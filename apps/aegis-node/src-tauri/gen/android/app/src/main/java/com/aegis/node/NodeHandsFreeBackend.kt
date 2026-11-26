package com.aegis.node

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import okhttp3.Call
import okhttp3.ConnectionPool
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.toRequestBody
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID
import java.util.concurrent.CancellationException
import java.util.concurrent.ScheduledThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/** PCM uses the existing voice endpoint's format: signed little endian, mono, 24 kHz. */
data class HandsFreeTurn(val conversationId: String?, val playedAudio: Boolean, val continueListening: Boolean,
                         val ignored: Boolean = false, val readingStopped: Boolean = false)

internal data class VoiceInterruptionContext(
  val voiceSessionId: String,
  val conversationId: String?,
  val generation: HandsFreeGenerationInterruption?,
  val playback: HandsFreePlaybackInterruption?,
  val researchJobId: String?
) {
  fun applyTo(request: JSONObject): JSONObject = request.put("voiceSessionId", voiceSessionId)
    .put("conversationId", conversationId ?: JSONObject.NULL).apply {
      generation?.let { put("interruptedGeneration", it.toJson()) }
      playback?.let { put("interruptedPlayback", it.toJson()) }
      researchJobId?.let { put("researchJobId", it) }
    }
}

internal enum class VoiceInterruptionDisposition { RESUME, STOP, REPLACE }

internal data class PreparedVoiceInterruption(val context: VoiceInterruptionContext, val transcript: String, val receipt: String)

internal data class VoiceInterruptionDecision(
  val disposition: VoiceInterruptionDisposition, val replacement: PreparedVoiceInterruption? = null
)

/** One instance belongs to one native session; it never reads the WebView's draft or destination. */
class NodeHandsFreeBackend internal constructor(
  private val readSession: () -> NodeNativeSession.Session?,
  private val onStage: (String, Long) -> Unit = { _, _ -> },
  private val ttsDeadlineMilliseconds: Long = 300000,
  private val onConversationChanged: (NodeNativeSession.Session, String) -> Unit = NodeConversationChanges::changed,
  private val onVoiceSearchProgress: (NodeNativeSession.Session, JSONObject) -> Unit = NodeConversationChanges::voiceSearchProgress,
  private val onResearchDelegated: (NodeNativeSession.Session, String, String, String) -> Unit = { _, _, _, _ -> }
) {
  constructor(context: Context, onStage: (String, Long) -> Unit = { _, _ -> }) :
    this({ NodeNativeSession.read(context.applicationContext) }, onStage)

  private val cancelled = AtomicBoolean(false)
  private val executing = AtomicBoolean(false)
  private val activeCalls = java.util.concurrent.ConcurrentHashMap.newKeySet<Call>()
  private val audioCalls = java.util.concurrent.ConcurrentHashMap.newKeySet<Call>()
  private val readingStopped = AtomicBoolean(false)
  @Volatile private var independentResearchPlayback = false
  @Volatile private var realtime: HandsFreeRealtimeTranscription? = null
  private val segmentDownloads = java.util.concurrent.ConcurrentHashMap.newKeySet<SegmentDownload>()
  @Volatile private var activePcm: HandsFreePcmSink? = null
  @Volatile private var turnId: String? = null
  @Volatile private var speechId: String? = null
  @Volatile private var scope: NodeNativeSession.Session? = null
  @Volatile private var latencyTrace:HandsFreeTurnLatency?=null
  @Volatile private var correlationId:String?=null
  private var pinnedSessionId: String? = null
  private var pinnedConversationId: String? = null
  private val playbackProgress = HandsFreePlaybackProgress()
  @Volatile private var generationSnapshot:HandsFreeGenerationInterruption?=null
  @Volatile private var originalTranscription = java.util.concurrent.CountDownLatch(0)

  internal fun playbackInterruption(playedFrames: Long) = playbackProgress.at(playedFrames)
  /** Present only after the prior command was actually transcribed. */
  internal fun generationInterruption()=generationSnapshot
  internal fun awaitOriginalTranscription(): Boolean = originalTranscription.await(12, TimeUnit.SECONDS)
  internal fun processingInProgress()=executing.get() && !cancelled.get()

  private fun conversationChanged(id: String) {
    requireUuid(id)
    // Serialize authority validation with journal binding/replay. An old worker can
    // never bind its scope after a newer identity has already published changes.
    synchronized(NodeConversationChanges) {
      checkCurrent()
      onConversationChanged(checkNotNull(scope), id)
    }
  }

  /** Transient presentation can fail without changing the authoritative voice turn. */
  private fun voiceSearchProgress(event:JSONObject, terminal:Boolean=false) {
    try {
      synchronized(NodeConversationChanges) {
        val pinned=scope ?: return
        val current=readSession() ?: return
        if(current.nodeId!=pinned.nodeId || current.origin!=pinned.origin || current.credential!=pinned.credential ||
          !terminal && cancelled.get()) return
        onVoiceSearchProgress(pinned,event)
      }
    }catch(_:Exception){}
  }

  private fun webResearchSources(event: JSONObject): JSONArray? {
    val sources = event.optJSONArray("sources") ?: return null
    if (sources.length() !in 1..8) return null
    val valid = JSONArray()
    for (index in 0 until sources.length()) {
      val source = sources.optJSONObject(index) ?: continue
      val url = source.opt("url") as? String ?: continue
      val count = source.opt("count") as? Int ?: continue
      if (count < 1 || url.length !in 1..1024 || url.contains('\\') ||
          url.any { it.isWhitespace() || it.isISOControl() }) continue
      val parsed = runCatching { java.net.URI(url) }.getOrNull() ?: continue
      val authority = parsed.rawAuthority ?: continue
      if (parsed.scheme !in listOf("http", "https") || authority.isBlank() || '@' in authority) continue
      valid.put(JSONObject().put("url", url).put("count", count))
    }
    return valid.takeIf { it.length() > 0 }
  }

  private fun checkNotCancelled() {if(cancelled.get())throw CancellationException()}

  private fun checkCurrent() {
    if (cancelled.get()) throw CancellationException()
    val pinned = scope ?: throw NodeNativeSession.IdentityUnavailable()
    val current = readSession() ?: throw NodeNativeSession.IdentityUnavailable()
    if (current.nodeId != pinned.nodeId || current.origin != pinned.origin || current.credential != pinned.credential)
      throw NodeNativeSession.IdentityUnavailable()
  }

  private fun request(path: String, body: ByteArray, contentType: String, limit: Int?, timeoutSeconds: Int,
                      pcm: HandsFreePcmSink? = null, onFirstPcm: () -> Unit = {}, method: String = "POST", cleanup: Boolean = false, onLine: ((JSONObject) -> Unit)? = null, pcmDeliveryMetrics:Boolean=true, onFirstByte:()->Unit={}, onPcmAvailable:()->Unit={}, onNoContent:(()->Unit)?=null): ByteArray {
    val pinned = scope ?: throw NodeNativeSession.IdentityUnavailable()
    require(path.startsWith("/api/") && !path.contains("..") && !path.contains("?"))
    if (!cleanup) checkCurrent()
    val trace=if(cleanup)null else latencyTrace
    val requestStage=when(path){"/api/voice/transcriptions"->"stt";"/api/chat/messages","/api/chat/messages/stream"->"chat";"/api/voice/speech","/api/voice/speech/segments"->"tts";else->null}
    val client = transport.newBuilder()
      .callTimeout(timeoutSeconds.toLong(), TimeUnit.SECONDS)
      .connectTimeout(if (cleanup) 3 else 10, TimeUnit.SECONDS)
      .readTimeout((if (cleanup) 3 else if (timeoutSeconds == 0) 120 else minOf(timeoutSeconds,120)).toLong(), TimeUnit.SECONDS)
      .writeTimeout(if (cleanup) 3 else 10, TimeUnit.SECONDS)
      .apply {
        if(trace!=null && requestStage!=null)eventListener(object:okhttp3.EventListener() {
          override fun requestHeadersStart(call:Call){trace.mark("${requestStage}_headers_started")}
          override fun requestBodyStart(call:Call){trace.mark("${requestStage}_upload_started")}
          override fun requestBodyEnd(call:Call,byteCount:Long){trace.mark("${requestStage}_upload_completed")}
          override fun responseHeadersEnd(call:Call,response:okhttp3.Response){trace.mark("${requestStage}_response_headers")}
        })
      }.build()
    val builder = Request.Builder().url(pinned.origin + path)
      .header("Authorization", "AegisNode ${pinned.credential}")
      .header("Accept", if (pcm != null) "application/octet-stream" else "application/json")
      .header("Cache-Control", "no-store")
    correlationId?.let{builder.header("X-Aegis-Turn-Id",it)}
    when (method) {
      "GET" -> builder.get()
      "DELETE" -> builder.delete()
      else -> builder.method(method,body.toRequestBody(contentType.toMediaType()))
    }
    val call = client.newCall(builder.build())
    if (!cleanup) activeCalls.add(call)
    if (pcm != null) audioCalls.add(call)
    try {
      if (!cleanup) checkCurrent()
      if (pcm != null && readingStopped.get()) throw CancellationException()
      call.execute().use { response ->
        if (!response.isSuccessful) throw NodeNativeSession.HttpFailure(response.code)
        if (response.code == 204 && onNoContent != null) {
          checkCurrent(); onNoContent(); return ByteArray(0)
        }
        if (pcm != null) require(response.header("X-Aegis-Audio-Format") == "pcm_s16le" &&
          response.header("X-Aegis-Sample-Rate") == "24000" && response.header("X-Aegis-Channels") == "1")
        if (pcm != null) checkCurrent()
        val payload = response.body ?: error("empty_response_body")
        val advertised = payload.contentLength()
        require(limit == null || advertised <= limit)
        val output = if (pcm == null) ByteArrayOutputStream() else null
        val line = if(onLine != null) ByteArrayOutputStream() else null
        val pcmTrace=if(pcmDeliveryMetrics)trace else null
        val framer = pcm?.let { HandsFreePcm16Framer(it,
          onFirstPcmAvailable={pcmTrace?.mark("tts_first_pcm_available");onPcmAvailable()},
          onFirstUsefulPcmAvailable={frame->pcmTrace?.usefulFrame(frame);pcmTrace?.mark("tts_first_useful_pcm_available")},
          onFirstUsefulPcmDelivered={pcmTrace?.mark("tts_first_useful_pcm_delivered")}) }
        val block = ByteArray(8192)
        var total = 0L
        var firstDelivered = false
        var checkedIdentityAt = System.nanoTime()
        try {
          payload.byteStream().use { stream ->
            while (true) {
              if (!cleanup && cancelled.get()) throw CancellationException()
              val count = stream.read(block)
              if (count < 0) break
              check(count > 0) { "empty_stream_read" }
              if(pcm!=null && total==0L){pcmTrace?.mark("tts_first_byte");onFirstByte()}
              if (!cleanup && cancelled.get()) throw CancellationException()
              // Check authority after a possibly slow read, before delivering its audio. This is
              // time-bounded rather than decrypting the vault for every network/audio block.
              if (!cleanup && pcm != null && System.nanoTime() - checkedIdentityAt >= 1_000_000_000L) {
                checkCurrent(); checkedIdentityAt = System.nanoTime()
              }
              require(limit == null || total + count <= limit); total += count
              if (onLine != null) {
                for (index in 0 until count) {
                  if (block[index] == 10.toByte()) {
                    if (line!!.size() > 0) { onLine(JSONObject(line.toString("UTF-8").trimEnd('\r'))); line.reset() }
                  } else { require(line!!.size() < 65536); line.write(block[index].toInt()) }
                }
              } else if (framer == null) output!!.write(block, 0, count)
              else {
                framer.accept(block, count)
                if (!firstDelivered && framer.samples > 0) { firstDelivered = true; onFirstPcm() }
              }
            }
          }
          require(advertised < 0 || total == advertised) { "incomplete_http_body" }
          if (onLine != null && line!!.size() > 0) onLine(JSONObject(line.toString("UTF-8")))
          framer?.verifyComplete()
          if (!cleanup) checkCurrent()
          return output?.toByteArray() ?: ByteArray(0)
        } finally { block.fill(0); framer?.clear() }
      }
    } finally { activeCalls.remove(call); audioCalls.remove(call); call.cancel() }
  }

  private fun chatJson(body: JSONObject): JSONObject? {
    val bytes = body.toString().toByteArray(Charsets.UTF_8)
    return try {
      var ignored = false
      val response = request("/api/chat/messages", bytes, "application/json", 262144, 120, onNoContent = { ignored = true })
      try { if (ignored) null else JSONObject(String(response, Charsets.UTF_8)) }
      finally { response.fill(0) }
    } finally { bytes.fill(0) }
  }

  private inline fun <T> timed(stage: String, action: () -> T): T {
    val start = System.nanoTime()
    try { return action() } finally { onStage(stage, (System.nanoTime() - start) / 1_000_000L) }
  }

  internal fun beginCapture(trace: HandsFreeTurnLatency, onPartial: (String) -> Unit = {}): HandsFreeRealtimeTranscription {
    check(!executing.get())
    if (scope == null) scope = readSession() ?: throw NodeNativeSession.IdentityUnavailable()
    checkCurrent()
    realtime?.close()
    return HandsFreeRealtimeTranscription(transport, checkNotNull(scope), { checkCurrent() }, trace = trace, checkActive = {if(cancelled.get())throw CancellationException()}, onPartial = onPartial).also { realtime = it }
  }
  internal fun abortCapture() { realtime?.close(); realtime = null }

  /** Blocking: called only by the controller's bounded background executor. */
  fun execute(wav: ByteArray, sessionId: String, conversationId: String?, pcm: HandsFreePcmSink): HandsFreeTurn =
    execute(wav,sessionId,conversationId,pcm,null)

  internal fun execute(wav:ByteArray,sessionId:String,conversationId:String?,pcm:HandsFreePcmSink,trace:HandsFreeTurnLatency?, incremental:Boolean=false,
                       interruptedPlayback: HandsFreePlaybackInterruption? = null,
                       interruptedGeneration: HandsFreeGenerationInterruption? = null,
                       onProcessing:()->Unit={},
                       closeResearchOnAcknowledgement:Boolean=false,
                       preparedInterruption: PreparedVoiceInterruption? = null,
                       onConversation:(String)->Unit={}):HandsFreeTurn {
    check(executing.compareAndSet(false, true)) { "A voice turn is already running." }
    readingStopped.set(false)
    playbackProgress.reset()
    generationSnapshot=null
    originalTranscription=java.util.concurrent.CountDownLatch(1)
    latencyTrace=trace
    activePcm = pcm
    val conversationReportLock = Any()
    var reportedConversation:String? = null
    val reportConversation:(String)->Unit = { destination ->
      synchronized(conversationReportLock) {
        checkCurrent();requireUuid(destination)
        require(conversationId == null || destination == conversationId)
        require(reportedConversation == null || reportedConversation == destination)
        if (reportedConversation == null) {
          reportedConversation = destination
          onConversation(destination)
        }
      }
    }
    try {
      require(interruptedPlayback==null || interruptedGeneration==null)
      onProcessing()
      return executeTurn(wav, sessionId, conversationId, pcm, incremental, interruptedPlayback, interruptedGeneration,
        closeResearchOnAcknowledgement, preparedInterruption, reportConversation)
    } catch (error: Exception) {
      pcm.abort()
      if (cancelled.get()) throw CancellationException()
      throw error
    }
    finally {
      if (activePcm === pcm) activePcm = null
      originalTranscription.countDown()
      try { pcm.close() } finally { abortCapture();latencyTrace=null;correlationId=null;executing.set(false) }
    }
  }

  private fun executeTurn(wav: ByteArray, sessionId: String, conversationId: String?, pcm: HandsFreePcmSink, incremental: Boolean,
                          interruptedPlayback: HandsFreePlaybackInterruption?, interruptedGeneration:HandsFreeGenerationInterruption?,
                          closeResearchOnAcknowledgement:Boolean,
                          preparedInterruption:PreparedVoiceInterruption?,
                          reportConversation:(String)->Unit): HandsFreeTurn {
    require(UUID.fromString(sessionId).toString() == sessionId)
    conversationId?.let { require(UUID.fromString(it).toString() == it) }
    validateWav(wav)
    require(pinnedSessionId == null || pinnedSessionId == sessionId)
    require(pinnedConversationId == null || pinnedConversationId == conversationId)
    if (pinnedSessionId == null) {
      pinnedSessionId = sessionId
      pinnedConversationId = conversationId
    }
    if (scope == null) scope = readSession() ?: throw NodeNativeSession.IdentityUnavailable()
    checkCurrent()
    val trace=latencyTrace
    trace?.mark("turn_execution_started")
    val id=trace?.turnId?:UUID.randomUUID().toString()
    correlationId=id
    try {
      onStage("transcription_started", 0)
      val realtimeAttempt = realtime
      val streamed = try { realtimeAttempt?.awaitFinal() } finally { realtimeAttempt?.close();if(realtime===realtimeAttempt)realtime=null }
      val transcript = preparedInterruption?.transcript ?: streamed ?: run {
        if (realtimeAttempt != null) trace?.mark("stt_batch_fallback")
        transcribeBatch(wav)
      }
      onStage("transcription_final", 0)
      // Wake word alone and empty STT never create a conversation or reach the model.
      val command = if (preparedInterruption == null) stripWakeWord(transcript) else transcript
      if (command.isBlank() || (closeResearchOnAcknowledgement && handsFreeClosesResearchReply(command))) {
        onStage("empty", 0)
        return HandsFreeTurn(conversationId, false, false)
      }
      onStage("transcribed", 0)
      checkCurrent()
      turnId = id
      generationSnapshot=HandsFreeGenerationInterruption(id,command)
      originalTranscription.countDown()
      if (incremental) return executeSegmented(command, sessionId, conversationId, id, pcm, interruptedPlayback,
        interruptedGeneration, preparedInterruption, reportConversation)
      val result = timed("chat") {
        trace?.mark("chat_request_started")
        chatJson(JSONObject().put("turnId", id).put("conversationId", conversationId ?: JSONObject.NULL)
          .put("content", command).put("origin", "hands_free").put("voiceSessionId", sessionId).put("allowSilentIgnore", true))
      }
      trace?.mark("chat_response_received")
      if (result == null) return ignoredTurn(conversationId)
      val destination = result.getString("conversationId")
      require(UUID.fromString(destination).toString() == destination)
      require(conversationId == null || destination == conversationId)
      val message = result.getJSONObject("message")
      require(message.getString("conversationId") == destination && message.getString("role") == "assistant")
      pinnedConversationId = destination
      val messageId = message.getString("id")
      require(UUID.fromString(messageId).toString() == messageId)
      conversationChanged(destination)
      reportConversation(destination)
      val speechRequest = UUID.randomUUID().toString()
      trace?.speech(speechRequest)
      speechId = speechRequest
      val ttsStart = System.nanoTime()
      var firstAudioAt = 0L
      // HTTP's call timeout cannot interrupt a producer blocked inside AudioTrack.write.
      // One task owns the whole TTS stage, including its final playback drain.
      val deadlineArmed = AtomicBoolean(true)
      val deadline = ttsDeadlines.schedule({
        if (deadlineArmed.compareAndSet(true, false)) requestCancellation()
      }, ttsDeadlineMilliseconds, TimeUnit.MILLISECONDS)
      try {
        timed("tts_fetch") {
          val body = JSONObject().put("turnId", id).put("speechRequestId", speechRequest)
            .put("assistantMessageId", messageId).toString().toByteArray(Charsets.UTF_8)
          // 120s idle network budget, plus at most 180s AudioTrack backpressure/drain.
          try { trace?.mark("tts_request_started");request("/api/voice/speech", body, "application/json", 24000 * 2 * 180, 300, pcm = pcm,
            onFirstPcm = {
              firstAudioAt = System.nanoTime()
              onStage("tts_first_audio", (firstAudioAt - ttsStart) / 1_000_000L)
            }) }
          finally { body.fill(0) }
        }
        checkCurrent()
        pcm.finish()
        trace?.mark("playback_finished")
        checkCurrent()
      } finally {
        val completedBeforeDeadline = deadlineArmed.getAndSet(false)
        deadline.cancel(false)
        // A deadline task may have claimed the flag but not yet executed cancellation.
        // Invalidate here as well so a completed turn cannot race past the deadline.
        if (!completedBeforeDeadline) requestCancellation()
        val ended = System.nanoTime()
        onStage("tts_total", (ended - ttsStart) / 1_000_000L)
        if (firstAudioAt > 0) onStage("tts_playback", (ended - firstAudioAt) / 1_000_000L)
      }
      checkCurrent()
      speechId = null
      turnId = null
      return HandsFreeTurn(destination, true, result.optString("voiceContinuation") == "continue")
    } catch (error: Exception) {
      if (!cancelled.get()) onStage("failed", 0)
      pcm.abort()
      cleanupRemote()
      throw error
    }
  }

  private fun ignoredTurn(conversationId: String?): HandsFreeTurn {
    checkCurrent()
    turnId = null
    latencyTrace?.mark("chat_ignored")
    onStage("ignored", 0)
    return HandsFreeTurn(conversationId, false, false, ignored = true)
  }

  internal fun classifyInterruption(wav: ByteArray, predecessor: () -> VoiceInterruptionContext?): VoiceInterruptionDecision {
    validateWav(wav)
    if (scope == null) scope = readSession() ?: throw NodeNativeSession.IdentityUnavailable()
    val transcript = stripWakeWord(transcribeBatch(wav, timeoutSeconds = 12))
    if (transcript.isBlank()) return VoiceInterruptionDecision(VoiceInterruptionDisposition.RESUME)
    val context = predecessor() ?: return VoiceInterruptionDecision(VoiceInterruptionDisposition.RESUME)
    requireUuid(context.voiceSessionId)
    val body = context.applyTo(JSONObject().put("content", transcript)).toString().toByteArray(Charsets.UTF_8)
    try {
      val result = request("/api/voice/interruptions/classify", body, "application/json", 8192, 12)
      return try {
        val response = JSONObject(String(result, Charsets.UTF_8))
        when (response.getString("disposition")) {
          "resume" -> VoiceInterruptionDecision(VoiceInterruptionDisposition.RESUME)
          "stop" -> VoiceInterruptionDecision(VoiceInterruptionDisposition.STOP)
          "replace" -> {
            val receipt = response.getString("receipt")
            requireUuid(receipt)
            VoiceInterruptionDecision(VoiceInterruptionDisposition.REPLACE, PreparedVoiceInterruption(context, transcript, receipt))
          }
          else -> error("invalid_voice_interruption_disposition")
        }
      } finally { result.fill(0) }
    } finally { body.fill(0) }
  }

  private fun transcribeBatch(wav:ByteArray, timeoutSeconds: Int = 90):String {
    val trace=latencyTrace
    val transcriptionId = UUID.randomUUID().toString()
    trace?.transcription(transcriptionId);trace?.mark("stt_multipart_started")
    val boundary = "aegis-${UUID.randomUUID()}"
    val prefix = ("--$boundary\r\nContent-Disposition: form-data; name=\"transcriptionRequestId\"\r\n\r\n$transcriptionId\r\n" +
      "--$boundary\r\nContent-Disposition: form-data; name=\"clientDurationMilliseconds\"\r\n\r\n${(wav.size - 44) * 1000L / 32000}\r\n" +
      "--$boundary\r\nContent-Disposition: form-data; name=\"audio\"; filename=\"command.wav\"\r\nContent-Type: audio/wav\r\n\r\n").toByteArray(Charsets.UTF_8)
    val suffix = "\r\n--$boundary--\r\n".toByteArray(Charsets.UTF_8)
    val multipart = ByteArray(prefix.size + wav.size + suffix.size)
    prefix.copyInto(multipart); wav.copyInto(multipart, prefix.size); suffix.copyInto(multipart, prefix.size + wav.size)
    try { return timed("stt") {
      trace?.mark("stt_request_started")
      val response=request("/api/voice/transcriptions",multipart,"multipart/form-data; boundary=$boundary",65536,timeoutSeconds)
      try {
        val result=JSONObject(String(response,Charsets.UTF_8))
        require(result.getString("transcriptionRequestId")==transcriptionId)
        trace?.mark("stt_response_received");result.getString("text").trim()
      }finally{response.fill(0)}
    } }finally{multipart.fill(0)}
  }

  private class SegmentDownload(val event:JSONObject) : AutoCloseable {
    // Half a second per unit. At most current+one ahead exist, independent of reply length.
    val input=java.io.PipedInputStream(24000)
    val output=java.io.PipedOutputStream(input)
    val completed=java.util.concurrent.CountDownLatch(1)
    val firstByte=java.util.concurrent.CountDownLatch(1)
    val failure=java.util.concurrent.atomic.AtomicReference<Throwable?>()
    var worker:Thread?=null
    override fun close() {try{input.close()}catch(_:Exception){};try{output.close()}catch(_:Exception){}}
  }

  /** Two synthesis slots, bounded PCM pipes and one writer keep playback continuous. */
  private fun executeSegmented(command:String, sessionId:String, conversationId:String?, id:String, pcm:HandsFreePcmSink,
                               interruptedPlayback: HandsFreePlaybackInterruption?, interruptedGeneration:HandsFreeGenerationInterruption?,
                               preparedInterruption: PreparedVoiceInterruption?,
                               reportConversation:(String)->Unit):HandsFreeTurn {
    val trace=latencyTrace
    val events=java.util.concurrent.ArrayBlockingQueue<JSONObject>(2)
    val units=java.util.concurrent.ArrayBlockingQueue<SegmentDownload>(2)
    val permits=java.util.concurrent.Semaphore(2)
    val readerFailure=java.util.concurrent.atomic.AtomicReference<Throwable?>()
    val schedulerFailure=java.util.concurrent.atomic.AtomicReference<Throwable?>()
    val stopped=AtomicBoolean(false)
    val searchDestination=java.util.concurrent.atomic.AtomicReference<String?>()
    fun presentation(type:String,destination:String,messageId:String?=null)=JSONObject()
      .put("type",type).put("turnId",id).put("conversationId",destination).put("assistantMessageId",messageId?:JSONObject.NULL)
    fun reportSearchPresentation(destination: String, event: JSONObject) {
      if (searchDestination.compareAndSet(null, destination))
        voiceSearchProgress(presentation("conversation", destination))
      voiceSearchProgress(event)
    }
    fun finishSearchPresentation(type:String,messageId:String?=null) {
      searchDestination.getAndSet(null)?.let { destination ->
        voiceSearchProgress(presentation(type,destination,messageId),terminal=true)
      }
    }
    fun failPipeline(error:Throwable) {
      if(stopped.get())return
      schedulerFailure.compareAndSet(null,error)
      activePcm?.abort();segmentDownloads.forEach{it.close()};activeCalls.forEach{it.cancel()}
    }
    val bytes=JSONObject().put("turnId",id).put("conversationId",conversationId?:JSONObject.NULL)
      .put("content",command).put("origin","hands_free").put("voiceSessionId",sessionId).put("voiceSegments",true)
      .put("allowSilentIgnore",true).put("includeWebResearchSources",true)
      .apply { interruptedPlayback?.takeIf { it.conversationId == conversationId }?.let { put("interruptedPlayback", it.toJson()) } }
      .apply { interruptedGeneration?.let { put("interruptedGeneration",it.toJson()) } }
      .apply { preparedInterruption?.let { it.context.applyTo(this); put("interruptionReceipt",it.receipt) } }
      .toString().toByteArray(Charsets.UTF_8)
    val reader=Thread({
      var persistedDestination=conversationId
      var persistedMessage:String?=null
      var persistedSequence=0
      var hasEvent=false
      var accepted=false
      var terminalSeen=false
      var ignored=false
      try {
        trace?.mark("chat_request_started")
        request("/api/chat/messages/stream",bytes,"application/json",1048576,180,onLine={ event ->
          checkCurrent();require(event.getString("turnId")==id)
          require(!ignored){"events_after_ignored"}
          require(!terminalSeen){"events_after_done"}
          val firstEvent = !hasEvent
          hasEvent = true
          when(event.getString("type")) {
            "ignored" -> {
              // A discarded utterance is the entire stream, before any persistent boundary.
              require(firstEvent && event.length() == 2){"invalid_ignored_event"}
              ignored = true
              while(!stopped.get() && !events.offer(event,200,TimeUnit.MILLISECONDS)) checkNotCancelled()
              if(stopped.get())throw CancellationException()
            }
            "conversation" -> {
              val destination=event.getString("conversationId");requireUuid(destination)
              require(persistedDestination==null || persistedDestination==destination)
              persistedDestination=destination
              accepted=true
              conversationChanged(destination)
              reportConversation(destination)
            }
            "final_segment","done","error" -> {
              // These stream boundaries follow SaveChanges, independently of speech backpressure.
              if(event.getString("type")!="error") {
                val destination=event.getString("conversationId");requireUuid(destination)
                require(persistedDestination==null || persistedDestination==destination)
                val message=event.getString("assistantMessageId");requireUuid(message)
                require(persistedMessage==null || persistedMessage==message)
                if(event.getString("type")=="final_segment") require(event.getInt("sequence")==persistedSequence++)
                else {
                  require(persistedSequence>0)
                  val jobId = event.opt("researchJobId") as? String
                  if (jobId != null && isUuid(jobId)) {
                    // The durable job is independent of this announcement's playback.
                    // Scheduling is allowed to fail without breaking the spoken turn.
                    runCatching { onResearchDelegated(checkNotNull(scope), jobId,
                      preparedInterruption?.context?.voiceSessionId ?: sessionId, destination) }
                  }
                }
                persistedDestination=destination;persistedMessage=message
                accepted=true
                conversationChanged(destination)
              }
              terminalSeen=event.getString("type")!="final_segment"
              while(!stopped.get() && !events.offer(event,200,TimeUnit.MILLISECONDS)) checkNotCancelled()
              if(stopped.get())throw CancellationException()
            }
            "tool_status" -> {
              // A pinned destination is not acceptance; speculative or unrelated status stays private.
              val destination=persistedDestination
              val state=event.opt("state")
              val statusMessage=event.opt("message")
              if(accepted && !stopped.get() && destination!=null && event.opt("category")=="web_search" &&
                (event.isNull("conversationId") || event.opt("conversationId")==destination) &&
                state is String && state in listOf("started","completed","failed") && statusMessage is String &&
                statusMessage.isNotBlank() && statusMessage.length<=256 && statusMessage.none{it.isISOControl()}) {
                reportSearchPresentation(destination, presentation("tool_status",destination).put("toolStatus",
                  JSONObject().put("category","web_search").put("state",state).put("message",statusMessage)))
              }
            }
            "web_research_sources" -> {
              val destination = persistedDestination
              if (accepted && !stopped.get() && destination != null && event.opt("conversationId") == destination) {
                webResearchSources(event)?.let { sources ->
                  reportSearchPresentation(destination, presentation("web_research_sources", destination).put("sources", sources))
                }
              }
            }
            "memory_activity","token" -> Unit
            else -> error("invalid_chat_event")
          }
        })
      }catch(error:Throwable){if(!stopped.get())readerFailure.set(error)}
      finally{bytes.fill(0)}
    },"AegisFinalSegments").apply{isDaemon=true;start()}
    val started=System.nanoTime()
    val deadlineArmed=AtomicBoolean(true)
    val deadline=ttsDeadlines.schedule({if(deadlineArmed.compareAndSet(true,false))requestCancellation()},ttsDeadlineMilliseconds,TimeUnit.MILLISECONDS)
    val scheduler=Thread({
      var destination=conversationId
      var messageId:String?=null
      var sequence=0
      val seen=hashSetOf<String>()
      var previous:SegmentDownload?=null
      try {
        while(!stopped.get()) {
          checkNotCancelled();readerFailure.get()?.let{throw it}
          val event=events.poll(200,TimeUnit.MILLISECONDS)
          if(event==null){check(reader.isAlive || events.isNotEmpty()){"incomplete_chat_stream"};continue}
          when(event.getString("type")) {
            "error" -> error("chat_stream_failed")
            "done","ignored" -> {
              if(event.getString("type")=="ignored") require(sequence==0 && messageId==null)
              else require(sequence>0 && event.getString("conversationId")==destination && event.getString("assistantMessageId")==messageId)
              trace?.mark("chat_response_received")
              // A terminal event cannot conceal a truncated HTTP body or later stream failure.
              reader.join(3000);check(!reader.isAlive){"chat_stream_did_not_finish"}
              readerFailure.get()?.let{throw it};require(events.isEmpty()){"events_after_done"}
              val terminal=SegmentDownload(event)
              while(!stopped.get() && !units.offer(terminal,200,TimeUnit.MILLISECONDS))checkNotCancelled()
              break
            }
            "final_segment" -> {
              require(sequence<128 && event.getInt("sequence")==sequence)
              val nextDestination=event.getString("conversationId");requireUuid(nextDestination)
              require(destination==null || nextDestination==destination);destination=nextDestination
              val nextMessage=event.getString("assistantMessageId");requireUuid(nextMessage)
              require(messageId==null || nextMessage==messageId);messageId=nextMessage
              val segmentId=event.getString("segmentId");requireUuid(segmentId);require(seen.add(segmentId))
              require(event.getString("content").isNotBlank() && event.getString("content").length<=8000)
              // Streams without a separate conversation marker still bind before synthesis.
              reportConversation(nextDestination)
              trace?.mark("first_final_segment_received")
              // Prior acceptance orders claims. Next synthesis begins while current PCM plays.
              previous?.let {prior->
                while(!stopped.get() && !readingStopped.get() && !prior.firstByte.await(200,TimeUnit.MILLISECONDS)) {
                  checkNotCancelled();prior.failure.get()?.let{throw it}
                  check(prior.completed.count>0){"empty_previous_segment"}
                }
              }
              if (readingStopped.get()) { sequence++; continue }
              var acquired = false
              while(!stopped.get() && !readingStopped.get()) {
                if (permits.tryAcquire(200,TimeUnit.MILLISECONDS)) { acquired = true; break }
                checkNotCancelled();readerFailure.get()?.let{throw it}
              }
              if (readingStopped.get()) {
                if (acquired) permits.release()
                sequence++
                continue
              }
              if(stopped.get())throw CancellationException()
              val unit=SegmentDownload(event);segmentDownloads.add(unit);previous=unit
              val speech=UUID.randomUUID().toString();trace?.speech(speech)
              val unitSequence=sequence
              val body=JSONObject().put("turnId",id).put("speechRequestId",speech).put("assistantMessageId",messageId)
                .put("conversationId",destination).put("segmentId",segmentId).put("sequence",sequence).toString().toByteArray(Charsets.UTF_8)
              unit.worker=Thread({
                var synthesizedFrames = 0L
                val pipe=object:HandsFreePcmSink {
                  override fun write(bytes:ByteArray,offset:Int,count:Int){unit.output.write(bytes,offset,count);synthesizedFrames += count / 2}
                  override fun finish()=Unit
                  override fun abort()=unit.close()
                  override fun close()=Unit
                }
                try {
                  trace?.mark("tts_request_started")
                  request("/api/voice/speech/segments",body,"application/json",24000*2*180,300,pcm=pipe,
                    pcmDeliveryMetrics=false,onFirstByte={unit.firstByte.countDown();if(unitSequence==0)trace?.mark("tts_first_byte")},
                    onPcmAvailable={if(unitSequence==0)trace?.mark("tts_first_pcm_available")})
                  playbackProgress.synthesized(segmentId, synthesizedFrames)
                }catch(error:Throwable){unit.failure.set(error);if(!readingStopped.get())failPipeline(error)}
                finally{body.fill(0);try{unit.output.close()}catch(_:Exception){};unit.completed.countDown()}
              },"AegisSegmentPcm-$sequence").apply{isDaemon=true;start()}
              while(!stopped.get() && !units.offer(unit,200,TimeUnit.MILLISECONDS))checkNotCancelled()
              sequence++
            }
          }
        }
      }catch(error:Throwable){failPipeline(error)}
    },"AegisSpeechPrefetch").apply{isDaemon=true;start()}
    var totalPcmBytes=0L
    var firstAudioAt=0L
    var identityCheckedAt=System.nanoTime()
    val boundedSink=object:HandsFreePcmSink {
      override fun write(bytes:ByteArray,offset:Int,count:Int) {
        checkNotCancelled();schedulerFailure.get()?.let{throw it}
        if(System.nanoTime()-identityCheckedAt>=1000000000L){checkCurrent();identityCheckedAt=System.nanoTime()}
        require(totalPcmBytes+count<=24000L*2*180);pcm.write(bytes,offset,count);totalPcmBytes+=count
        if(firstAudioAt==0L){firstAudioAt=System.nanoTime();onStage("tts_first_audio",(firstAudioAt-started)/1000000)}
      }
      override fun finish()=pcm.finish()
      override fun abort()=pcm.abort()
      override fun close()=Unit
    }
    // Prefetched bytes are never counted as delivered to the native player.
    val framer=HandsFreePcm16Framer(boundedSink,
      onFirstUsefulPcmAvailable={frame->trace?.usefulFrame(frame);trace?.mark("tts_first_useful_pcm_available")},
      onFirstUsefulPcmDelivered={trace?.mark("tts_first_useful_pcm_delivered")})
    val block=ByteArray(8192)
    var ignored = false
    try {
      while(true) {
        checkNotCancelled();schedulerFailure.get()?.let{throw it}
        if(System.nanoTime()-identityCheckedAt>=1000000000L){checkCurrent();identityCheckedAt=System.nanoTime()}
        val unit=units.poll(200,TimeUnit.MILLISECONDS)
        if(unit==null){check(scheduler.isAlive || units.isNotEmpty()){"incomplete_segment_queue"};continue}
        try {
          if(unit.event.getString("type")=="ignored") {
            return ignoredTurn(conversationId).also { ignored = true }
          }
          if(unit.event.getString("type")=="done") {
            if(!readingStopped.get()) {
              try { framer.verifyComplete();pcm.finish();trace?.mark("playback_finished") }
              catch(error:Exception){if(!readingStopped.get())throw error}
            }
            checkCurrent()
            val destination=unit.event.getString("conversationId")
            // Best effort acknowledgement after the real playback head, without delaying onset.
            try{completionCleanup.execute{try{request("/api/chat/turns/$id/complete",ByteArray(0),"application/json",65536,3,cleanup=true)}catch(_:Exception){}}}
            catch(_:java.util.concurrent.RejectedExecutionException){}
            pinnedConversationId=destination;speechId=null;turnId=null
            finishSearchPresentation("done",unit.event.getString("assistantMessageId"))
            val speechStopped = readingStopped.get()
            return HandsFreeTurn(destination,!speechStopped,!speechStopped && unit.event.optString("voiceContinuation")=="continue",
              readingStopped=speechStopped)
          }
          if (!readingStopped.get()) playbackProgress.begin(unit.event.getString("conversationId"), id,
            unit.event.getString("assistantMessageId"),unit.event.getString("segmentId"),
            unit.event.getInt("sequence"),framer.samples)
          // Closing an already-open legacy segment request can cancel its parent
          // server turn. Drain its bounded PCM pipe silently and fetch no successor.
          while(true) {
            checkNotCancelled();schedulerFailure.get()?.let{throw it}
            if(!readingStopped.get())unit.failure.get()?.let{throw it}
            if(System.nanoTime()-identityCheckedAt>=1000000000L){checkCurrent();identityCheckedAt=System.nanoTime()}
            val count=unit.input.read(block)
            if(count<0)break
            if (!readingStopped.get()) {
              try { framer.accept(block,count) }
              catch(error:Exception){if(!readingStopped.get() || cancelled.get())throw error}
            }
          }
          check(unit.completed.await(3000,TimeUnit.MILLISECONDS)){"segment_download_did_not_finish"}
          if(!readingStopped.get())unit.failure.get()?.let{throw it}
        } finally {unit.close();segmentDownloads.remove(unit);if(unit.event.getString("type")=="final_segment")permits.release()}
      }
    } finally {
      block.fill(0);framer.clear();stopped.set(true)
      reader.interrupt();scheduler.interrupt();segmentDownloads.forEach{it.close()};activeCalls.forEach{it.cancel()}
      reader.join(3000);scheduler.join(3000)
      segmentDownloads.forEach{it.worker?.join(3000);it.close()};segmentDownloads.clear()
      while(true){val abandoned=units.poll()?:break;abandoned.close()}
      val withinDeadline=deadlineArmed.getAndSet(false);deadline.cancel(false)
      if(!withinDeadline)requestCancellation()
      finishSearchPresentation(if(cancelled.get())"interrupted" else "error")
      if(!ignored)onStage("tts_total",(System.nanoTime()-started)/1000000)
    }
  }

  /** Manual Android chat uses exactly the same validated PCM transport and native sink. */
  internal fun speakExisting(id:String,messageId:String,speech:String,pcm:HandsFreePcmSink) {
    requireUuid(id);requireUuid(messageId);requireUuid(speech)
    check(executing.compareAndSet(false,true));activePcm=pcm
    if(scope==null)scope=readSession()?:throw NodeNativeSession.IdentityUnavailable()
    speechId=speech;correlationId=id
    val body=JSONObject().put("turnId",id).put("assistantMessageId",messageId).put("speechRequestId",speech).toString().toByteArray(Charsets.UTF_8)
    val armed=AtomicBoolean(true)
    val deadline=ttsDeadlines.schedule({if(armed.compareAndSet(true,false))requestCancellation()},ttsDeadlineMilliseconds,TimeUnit.MILLISECONDS)
    try {request("/api/voice/speech",body,"application/json",24000*2*180,300,pcm=pcm);checkCurrent();pcm.finish();checkCurrent()}
    catch(error:Exception){pcm.abort();cleanupRemote();throw error}
    finally {armed.set(false);deadline.cancel(false);body.fill(0);pcm.close();activePcm=null;executing.set(false);turnId=null;speechId=null}
  }

  internal fun researchJobState(jobId: String): String {
    requireUuid(jobId)
    if (scope == null) scope = readSession() ?: throw NodeNativeSession.IdentityUnavailable()
    val bytes = request("/api/chat/research-jobs/$jobId", ByteArray(0), "application/json", 262144, 12, method = "GET")
    return try {
      val record = JSONObject(String(bytes, Charsets.UTF_8))
      require(record.opt("id") == jobId)
      record.getString("state")
    } finally { bytes.fill(0) }
  }

  internal fun speakResearch(jobId: String, voiceSessionId: String, pcm: HandsFreePcmSink,
                             onFirstPcm: () -> Unit = {}, onProcessing: () -> Unit = {}): Boolean {
    requireUuid(jobId); requireUuid(voiceSessionId)
    check(executing.compareAndSet(false, true))
    readingStopped.set(false)
    independentResearchPlayback = true
    activePcm = pcm
    val body = JSONObject().put("voiceSessionId", voiceSessionId).toString().toByteArray(Charsets.UTF_8)
    try {
      if (scope == null) scope = readSession() ?: throw NodeNativeSession.IdentityUnavailable()
      onProcessing()
      // The backend streams the whole answer in bounded synthesis chunks. Retain the
      // ordinary idle network deadline and cancellation, without a whole-answer cutoff.
      request("/api/chat/research-jobs/$jobId/speech", body, "application/json", null, 0, pcm = pcm,
        onFirstPcm = { onStage("tts_first_audio", 0); onFirstPcm() })
      checkCurrent()
      pcm.finish()
      checkCurrent()
    } catch (error: Exception) {
      pcm.abort()
      if (!readingStopped.get() || cancelled.get()) throw error
    } finally {
      body.fill(0)
      try { pcm.close() } finally {
        independentResearchPlayback = false
        activePcm = null
        executing.set(false)
      }
    }
    return !readingStopped.get()
  }

  private fun isUuid(value: String): Boolean =
    try { UUID.fromString(value).toString() == value } catch (_: IllegalArgumentException) { false }

  private fun requireUuid(value:String) {require(isUuid(value))}

  /** The reply can finish saving its transcript and facts without a PCM consumer. */
  internal fun stopReading() {
    readingStopped.set(true)
    activePcm?.abort()
    if (independentResearchPlayback) audioCalls.forEach { it.cancel() }
  }

  /** Immediate, nonblocking invalidation even while an older remote cleanup is queued. */
  fun requestCancellation() {
    cancelled.set(true)
    activePcm?.abort()
    segmentDownloads.forEach{it.close()}
    // Call.cancel closes the current exchange immediately, independently of queued remote DELETEs.
    realtime?.close()
    activeCalls.forEach { it.cancel() }
  }

  /** Called off the UI thread. No retries; call cancellation covers STT, chat and audio downloads. */
  fun cancel() {
    requestCancellation()
    realtime?.close()
    activeCalls.forEach { it.cancel() }
    cleanupRemote()
  }

  private fun cleanupRemote() {
    val speech = speechId.also { speechId = null }
    val turn = turnId.also { turnId = null }
    for (path in listOfNotNull(speech?.let { "/api/voice/speech/$it" }, turn?.let { "/api/chat/turns/$it" })) {
      try { request(path, ByteArray(0), "application/json", 65536, 3, method = "DELETE", cleanup = true) } catch (_: Exception) { }
    }
  }

  companion object {
    // The controller permits one active voice turn. At most one delayed task is retained
    // for it; cancelling a completed turn removes its task immediately. No polling.
    private val completionCleanup = java.util.concurrent.ThreadPoolExecutor(1,1,30,TimeUnit.SECONDS,
      java.util.concurrent.ArrayBlockingQueue<Runnable>(2),{task->Thread(task,"AegisPlaybackComplete").apply{isDaemon=true}},
      java.util.concurrent.ThreadPoolExecutor.AbortPolicy()).apply{allowCoreThreadTimeOut(true)}
    private val ttsDeadlines = ScheduledThreadPoolExecutor(1) { task ->
      Thread(task, "AegisTtsDeadline").apply { isDaemon = true }
    }.apply { removeOnCancelPolicy = true }
    // Shared, bounded idle connection pool. Reuse TLS sessions between nearby calls
    // without introducing idle network probes, a logging interceptor or response cache.
    internal val transport = OkHttpClient.Builder()
      .connectionPool(ConnectionPool(1,5,TimeUnit.MINUTES))
      .followRedirects(false).followSslRedirects(false).retryOnConnectionFailure(false).build()

    private fun validateWav(wav: ByteArray) {
      require(wav.size in 46..(44 + 16000 * 2 * 90) && (wav.size - 44) % 2 == 0)
      fun ascii(offset: Int, value: String) = value.indices.all { wav[offset + it] == value[it].code.toByte() }
      val header = ByteBuffer.wrap(wav).order(ByteOrder.LITTLE_ENDIAN)
      require(ascii(0, "RIFF") && ascii(8, "WAVEfmt ") && ascii(36, "data") &&
        header.getInt(4) == wav.size - 8 && header.getInt(16) == 16 && header.getShort(20).toInt() == 1 &&
        header.getShort(22).toInt() == 1 && header.getInt(24) == 16000 && header.getInt(28) == 32000 &&
        header.getShort(32).toInt() == 2 && header.getShort(34).toInt() == 16 && header.getInt(40) == wav.size - 44)
    }

    // Only a leading invocation is removed; quoted names elsewhere remain part of the request.
    internal fun stripWakeWord(text: String): String = text.replaceFirst(
      Regex("^\\s*(?:aegis|aégis|aéjis|égis)(?=$|[\\s,.:;!?–—-])[\\s,.:;!?–—-]*", RegexOption.IGNORE_CASE), "").trim()
  }
}

private val researchAcknowledgements = setOf(
  "ok", "okay", "beleza", "valeu", "obrigado", "obrigada", "tá bom", "ta bom", "está bom", "esta bom",
  "ah não", "ah nao", "pode parar", "já deu", "ja deu", "é só isso", "e so isso"
)
private val acknowledgementDelimiter = Regex("[^\\p{L}\\p{N}]+")
private val acknowledgementPrefixes = listOf("ok ", "okay ", "beleza ", "ah não ", "ah nao ")

internal fun handsFreeClosesResearchReply(content: String): Boolean {
  val normalized = content.lowercase().replace(acknowledgementDelimiter, " ").trim()
  return normalized in researchAcknowledgements || acknowledgementPrefixes.any { prefix ->
    normalized.startsWith(prefix) && normalized.removePrefix(prefix) in researchAcknowledgements
  }
}
