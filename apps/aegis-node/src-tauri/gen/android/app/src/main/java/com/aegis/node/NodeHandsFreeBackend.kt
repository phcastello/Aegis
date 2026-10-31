package com.aegis.node

import android.content.Context
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.net.HttpURLConnection
import java.net.URL
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID
import java.util.concurrent.CancellationException
import java.util.concurrent.ScheduledThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/** PCM uses the existing voice endpoint's format: signed little endian, mono, 24 kHz. */
data class HandsFreeTurn(val conversationId: String?, val audio: ByteArray?, val continueListening: Boolean)

/** One instance belongs to one native session; it never reads the WebView's draft or destination. */
class NodeHandsFreeBackend internal constructor(
  private val readSession: () -> NodeNativeSession.Session?,
  private val onStage: (String, Long) -> Unit = { _, _ -> }
) {
  constructor(context: Context, onStage: (String, Long) -> Unit = { _, _ -> }) :
    this({ NodeNativeSession.read(context.applicationContext) }, onStage)

  private val cancelled = AtomicBoolean(false)
  private val executing = AtomicBoolean(false)
  @Volatile private var active: HttpURLConnection? = null
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
                      audio: Boolean = false, method: String = "POST", cleanup: Boolean = false): ByteArray {
    val pinned = scope ?: throw NodeNativeSession.IdentityUnavailable()
    require(path.startsWith("/api/") && !path.contains("..") && !path.contains("?"))
    if (!cleanup) checkCurrent()
    val connection = URL(pinned.origin + path).openConnection() as HttpURLConnection
    if (!cleanup) {
      active = connection
    }
    val deadline = System.nanoTime() + timeoutSeconds * 1_000_000_000L
    // readTimeout does not bound a stalled streaming upload. Close the same request at its deadline too.
    val expiry = deadlines.schedule({ connection.disconnect() }, timeoutSeconds.toLong(), TimeUnit.SECONDS)
    fun remaining(): Int = ((deadline - System.nanoTime()) / 1_000_000L).coerceAtMost(timeoutSeconds * 1000L).toInt()
      .also { if (it <= 0) throw java.net.SocketTimeoutException() }
    try {
      // A cancel racing with assignment must also disconnect this request.
      if (!cleanup) checkCurrent()
      connection.instanceFollowRedirects = false
      connection.connectTimeout = if (cleanup) 3000 else 10_000
      connection.readTimeout = if (cleanup) 3000 else remaining()
      connection.requestMethod = method
      connection.useCaches = false
      connection.setRequestProperty("Authorization", "AegisNode ${pinned.credential}")
      connection.setRequestProperty("Accept", if (audio) "application/octet-stream" else "application/json")
      if (method != "DELETE") {
        connection.doOutput = true
        connection.setRequestProperty("Content-Type", contentType)
        connection.setFixedLengthStreamingMode(body.size)
        connection.outputStream.use { it.write(body) }
      }
      if (connection.responseCode !in 200..299) throw NodeNativeSession.HttpFailure(connection.responseCode)
      if (audio) require(connection.getHeaderField("X-Aegis-Audio-Format") == "pcm_s16le" &&
        connection.getHeaderField("X-Aegis-Sample-Rate") == "24000" && connection.getHeaderField("X-Aegis-Channels") == "1")
      val output = ByteArrayOutputStream()
      val block = ByteArray(8192)
      connection.inputStream.use { stream ->
        while (true) {
          if (!cleanup && cancelled.get()) throw CancellationException()
          connection.readTimeout = if (cleanup) 3000 else remaining()
          val count = stream.read(block)
          if (count < 0) break
          require(output.size() + count <= limit)
          output.write(block, 0, count)
        }
      }
      if (!cleanup) checkCurrent()
      return output.toByteArray()
    } finally {
      expiry.cancel(false)
      if (active === connection) active = null
      connection.disconnect()
    }
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
  fun execute(wav: ByteArray, sessionId: String, conversationId: String?): HandsFreeTurn {
    check(executing.compareAndSet(false, true)) { "A voice turn is already running." }
    try {
      return executeTurn(wav, sessionId, conversationId)
    } finally { executing.set(false) }
  }

  private fun executeTurn(wav: ByteArray, sessionId: String, conversationId: String?): HandsFreeTurn {
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
        return HandsFreeTurn(conversationId, null, false)
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
      val pcm = timed("tts_fetch") {
        val body = JSONObject().put("turnId", id).put("speechRequestId", speechRequest)
          .put("assistantMessageId", messageId).toString().toByteArray(Charsets.UTF_8)
        try { request("/api/voice/speech", body, "application/json", 24000 * 2 * 180, 120, audio = true) }
        finally { body.fill(0) }
      }
      require(pcm.isNotEmpty() && pcm.size % 2 == 0)
      checkCurrent()
      speechId = null
      turnId = null
      return HandsFreeTurn(destination, pcm, result.optString("voiceContinuation") == "continue")
    } catch (error: Exception) {
      if (!cancelled.get()) onStage("failed", 0)
      cleanupRemote()
      throw error
    } finally { multipart.fill(0) }
  }

  /** Immediate, nonblocking invalidation even while an older remote cleanup is queued. */
  fun requestCancellation() { cancelled.set(true) }

  /** Called off the UI thread. No retries; the disconnect cancels STT as well as chat/audio downloads. */
  fun cancel() {
    requestCancellation()
    active?.disconnect()
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
    private val deadlines = ScheduledThreadPoolExecutor(1) { task ->
      Thread(task, "aegis-voice-deadline").apply { isDaemon = true }
    }.apply {
      removeOnCancelPolicy = true
      setKeepAliveTime(30, TimeUnit.SECONDS)
      allowCoreThreadTimeOut(true)
    }

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
