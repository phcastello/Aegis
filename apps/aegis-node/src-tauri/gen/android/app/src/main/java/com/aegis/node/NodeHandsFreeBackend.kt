package com.aegis.node

import android.content.Context
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
data class HandsFreeTurn(val conversationId: String?, val playedAudio: Boolean, val continueListening: Boolean)

/** One instance belongs to one native session; it never reads the WebView's draft or destination. */
class NodeHandsFreeBackend internal constructor(
  private val readSession: () -> NodeNativeSession.Session?,
  private val onStage: (String, Long) -> Unit = { _, _ -> },
  private val ttsDeadlineMilliseconds: Long = 300000
) {
  constructor(context: Context, onStage: (String, Long) -> Unit = { _, _ -> }) :
    this({ NodeNativeSession.read(context.applicationContext) }, onStage)

  private val cancelled = AtomicBoolean(false)
  private val executing = AtomicBoolean(false)
  @Volatile private var active: Call? = null
  @Volatile private var activePcm: HandsFreePcmSink? = null
  @Volatile private var turnId: String? = null
  @Volatile private var speechId: String? = null
  @Volatile private var scope: NodeNativeSession.Session? = null
  private var pinnedSessionId: String? = null
  private var pinnedConversationId: String? = null

  private fun checkCurrent() {
    if (cancelled.get()) throw CancellationException()
    val pinned = scope ?: throw NodeNativeSession.IdentityUnavailable()
    val current = readSession() ?: throw NodeNativeSession.IdentityUnavailable()
    if (current.nodeId != pinned.nodeId || current.origin != pinned.origin || current.credential != pinned.credential)
      throw NodeNativeSession.IdentityUnavailable()
  }

  private fun request(path: String, body: ByteArray, contentType: String, limit: Int, timeoutSeconds: Int,
                      pcm: HandsFreePcmSink? = null, onFirstPcm: () -> Unit = {}, method: String = "POST", cleanup: Boolean = false): ByteArray {
    val pinned = scope ?: throw NodeNativeSession.IdentityUnavailable()
    require(path.startsWith("/api/") && !path.contains("..") && !path.contains("?"))
    if (!cleanup) checkCurrent()
    val client = transport.newBuilder()
      .callTimeout(timeoutSeconds.toLong(), TimeUnit.SECONDS)
      .connectTimeout(if (cleanup) 3 else 10, TimeUnit.SECONDS)
      .readTimeout((if (cleanup) 3 else minOf(timeoutSeconds,120)).toLong(), TimeUnit.SECONDS)
      .writeTimeout(if (cleanup) 3 else 10, TimeUnit.SECONDS).build()
    val builder = Request.Builder().url(pinned.origin + path)
      .header("Authorization", "AegisNode ${pinned.credential}")
      .header("Accept", if (pcm != null) "application/octet-stream" else "application/json")
      .header("Cache-Control", "no-store")
    if (method == "DELETE") builder.delete() else builder.method(method,body.toRequestBody(contentType.toMediaType()))
    val call = client.newCall(builder.build())
    if (!cleanup) active = call
    try {
      if (!cleanup) checkCurrent()
      call.execute().use { response ->
        if (!response.isSuccessful) throw NodeNativeSession.HttpFailure(response.code)
        if (pcm != null) require(response.header("X-Aegis-Audio-Format") == "pcm_s16le" &&
          response.header("X-Aegis-Sample-Rate") == "24000" && response.header("X-Aegis-Channels") == "1")
        if (pcm != null) checkCurrent()
        val payload = response.body ?: error("empty_response_body")
        val advertised = payload.contentLength()
        require(advertised <= limit)
        val output = if (pcm == null) ByteArrayOutputStream() else null
        val framer = pcm?.let { HandsFreePcm16Framer(it) }
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
              if (!cleanup && cancelled.get()) throw CancellationException()
              // Check authority after a possibly slow read, before delivering its audio. This is
              // time-bounded rather than decrypting the vault for every network/audio block.
              if (!cleanup && pcm != null && System.nanoTime() - checkedIdentityAt >= 1_000_000_000L) {
                checkCurrent(); checkedIdentityAt = System.nanoTime()
              }
              require(total + count <= limit); total += count
              if (framer == null) output!!.write(block, 0, count)
              else {
                framer.accept(block, count)
                if (!firstDelivered && framer.samples > 0) { firstDelivered = true; onFirstPcm() }
              }
            }
          }
          require(advertised < 0 || total == advertised) { "incomplete_http_body" }
          framer?.verifyComplete()
          if (!cleanup) checkCurrent()
          return output?.toByteArray() ?: ByteArray(0)
        } finally { block.fill(0); framer?.clear() }
      }
    } finally { if (active === call) active = null; call.cancel() }
  }

  private fun json(path: String, body: JSONObject): JSONObject {
    val bytes = body.toString().toByteArray(Charsets.UTF_8)
    return try { JSONObject(String(request(path, bytes, "application/json", 262144, 120), Charsets.UTF_8)) }
      finally { bytes.fill(0) }
  }

  private inline fun <T> timed(stage: String, action: () -> T): T {
    val start = System.nanoTime()
    try { return action() } finally { onStage(stage, (System.nanoTime() - start) / 1_000_000L) }
  }

  /** Blocking: called only by the controller's bounded background executor. */
  fun execute(wav: ByteArray, sessionId: String, conversationId: String?, pcm: HandsFreePcmSink): HandsFreeTurn {
    check(executing.compareAndSet(false, true)) { "A voice turn is already running." }
    activePcm = pcm
    try {
      return executeTurn(wav, sessionId, conversationId, pcm)
    } catch (error: Exception) {
      pcm.abort()
      if (cancelled.get()) throw CancellationException()
      throw error
    }
    finally {
      if (activePcm === pcm) activePcm = null
      try { pcm.close() } finally { executing.set(false) }
    }
  }

  private fun executeTurn(wav: ByteArray, sessionId: String, conversationId: String?, pcm: HandsFreePcmSink): HandsFreeTurn {
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
    val transcriptionId = UUID.randomUUID().toString()
    val boundary = "aegis-${UUID.randomUUID()}"
    val prefix = ("--$boundary\r\nContent-Disposition: form-data; name=\"transcriptionRequestId\"\r\n\r\n$transcriptionId\r\n" +
      "--$boundary\r\nContent-Disposition: form-data; name=\"clientDurationMilliseconds\"\r\n\r\n${(wav.size - 44) * 1000L / 32000}\r\n" +
      "--$boundary\r\nContent-Disposition: form-data; name=\"audio\"; filename=\"command.wav\"\r\nContent-Type: audio/wav\r\n\r\n").toByteArray(Charsets.UTF_8)
    val suffix = "\r\n--$boundary--\r\n".toByteArray(Charsets.UTF_8)
    val multipart = ByteArray(prefix.size + wav.size + suffix.size)
    prefix.copyInto(multipart); wav.copyInto(multipart, prefix.size); suffix.copyInto(multipart, prefix.size + wav.size)
    try {
      val transcript = timed("stt") {
        val response = request("/api/voice/transcriptions", multipart, "multipart/form-data; boundary=$boundary", 65536, 90)
        try {
          val result = JSONObject(String(response, Charsets.UTF_8))
          require(result.getString("transcriptionRequestId") == transcriptionId)
          result.getString("text").trim()
        } finally { response.fill(0) }
      }
      // Wake word alone and empty STT never create a conversation or reach the model.
      val command = stripWakeWord(transcript)
      if (command.isBlank()) {
        onStage("empty", 0)
        return HandsFreeTurn(conversationId, false, false)
      }
      onStage("transcribed", 0)
      checkCurrent()
      val id = UUID.randomUUID().toString()
      turnId = id
      val result = timed("chat") {
        json("/api/chat/messages", JSONObject().put("turnId", id).put("conversationId", conversationId ?: JSONObject.NULL)
          .put("content", command).put("origin", "hands_free").put("voiceSessionId", sessionId))
      }
      val destination = result.getString("conversationId")
      require(UUID.fromString(destination).toString() == destination)
      require(conversationId == null || destination == conversationId)
      val message = result.getJSONObject("message")
      require(message.getString("conversationId") == destination && message.getString("role") == "assistant")
      pinnedConversationId = destination
      val messageId = message.getString("id")
      require(UUID.fromString(messageId).toString() == messageId)
      checkCurrent()
      val speechRequest = UUID.randomUUID().toString()
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
          try { request("/api/voice/speech", body, "application/json", 24000 * 2 * 180, 300, pcm = pcm,
            onFirstPcm = {
              firstAudioAt = System.nanoTime()
              onStage("tts_first_audio", (firstAudioAt - ttsStart) / 1_000_000L)
            }) }
          finally { body.fill(0) }
        }
        checkCurrent()
        pcm.finish()
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
    } finally { multipart.fill(0) }
  }

  /** Immediate, nonblocking invalidation even while an older remote cleanup is queued. */
  fun requestCancellation() {
    cancelled.set(true)
    activePcm?.abort()
    // Call.cancel closes the current exchange immediately, independently of queued remote DELETEs.
    active?.cancel()
  }

  /** Called off the UI thread. No retries; call cancellation covers STT, chat and audio downloads. */
  fun cancel() {
    requestCancellation()
    active?.cancel()
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
    private val ttsDeadlines = ScheduledThreadPoolExecutor(1) { task ->
      Thread(task, "AegisTtsDeadline").apply { isDaemon = true }
    }.apply { removeOnCancelPolicy = true }
    // Shared, bounded idle connection pool; no logging interceptor or response cache.
    private val transport = OkHttpClient.Builder()
      .connectionPool(ConnectionPool(1,30,TimeUnit.SECONDS))
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
