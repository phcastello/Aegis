package com.aegis.node

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.io.BufferedInputStream
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import java.util.concurrent.atomic.AtomicInteger

/** Real loopback HTTP bytes; no backend, model, microphone or Android identity store is contacted. */
class NodeHandsFreeBackendTest {
  private val session = UUID.randomUUID().toString()
  private val conversation = UUID.randomUUID().toString()
  private val message = UUID.randomUUID().toString()
  private fun wav() = handsFreeWav(ShortArray(1600) { 42 }, 1600)

  @Test fun immediateCancellationBlocksChatBeforeDeferredNetworkCleanupRuns() {
    val entered = CountDownLatch(1)
    val release = CountDownLatch(1)
    Fixture(respond = { request, normal ->
      if (request.path == "/api/voice/transcriptions") {
        entered.countDown()
        check(release.await(5, TimeUnit.SECONDS))
      }
      normal
    }).use { fixture ->
      val executor = Executors.newSingleThreadExecutor()
      val backend = fixture.backend()
      try {
        val pending = executor.submit<HandsFreeTurn> { backend.execute(wav(), session, null) }
        assertTrue(entered.await(5, TimeUnit.SECONDS))
        // Intentionally do not run cancel()/DELETE/disconnect yet, as if cleanup is queued.
        backend.requestCancellation()
        release.countDown()
        val failed = assertThrows(java.util.concurrent.ExecutionException::class.java) { pending.get(5, TimeUnit.SECONDS) }
        assertTrue(failed.cause is java.util.concurrent.CancellationException)
        assertEquals(listOf("/api/voice/transcriptions"), fixture.requests.map { it.path })
      } finally { release.countDown(); backend.cancel(); executor.shutdownNow() }
    }
  }

  @Test fun multipartChatAndPcmKeepOneExplicitVoiceDestination() {
    Fixture().use { fixture ->
      val stages = mutableListOf<String>()
      val backend = fixture.backend { stage, _ -> stages.add(stage) }
      val result = backend.execute(wav(), session, conversation)
      assertEquals(conversation, result.conversationId)
      assertArrayEquals(byteArrayOf(1, 2, 3, 4), result.audio)
      assertTrue(result.continueListening)
      assertEquals(listOf("stt", "transcribed", "chat", "tts_fetch"), stages)
      assertEquals(listOf("/api/voice/transcriptions", "/api/chat/messages", "/api/voice/speech"), fixture.requests.map { it.path })
      val transcription = fixture.requests[0]
      assertTrue(transcription.contentType.startsWith("multipart/form-data; boundary=aegis-"))
      val body = transcription.text()
      assertTrue(body.contains("name=\"clientDurationMilliseconds\"\r\n\r\n100\r\n"))
      assertTrue(body.contains("name=\"audio\"; filename=\"command.wav\"\r\nContent-Type: audio/wav\r\n\r\nRIFF"))
      assertTrue(body.endsWith("--\r\n"))
      val chat = JSONObject(fixture.requests[1].text())
      assertEquals("hands_free", chat.getString("origin"))
      assertEquals(session, chat.getString("voiceSessionId"))
      assertEquals(conversation, chat.getString("conversationId"))
      assertEquals("o que tem hoje?", chat.getString("content"))
      val speech = JSONObject(fixture.requests[2].text())
      assertEquals(chat.getString("turnId"), speech.getString("turnId"))
      assertEquals(message, speech.getString("assistantMessageId"))
      assertTrue(speech.getString("speechRequestId").isNotBlank())
      assertTrue(fixture.requests.all { it.authorization == "AegisNode synthetic-credential" })
    }
  }

  @Test fun emptyAndWakeOnlyTranscriptsNeverCreateChatOrRequestTts() {
    for (text in listOf("", "  ", "Aegis.", "AÉGIS,", "aéjis!", "égis")) {
      Fixture(transcript = text).use { fixture ->
        val stages = mutableListOf<String>()
        val result = fixture.backend { stage, _ -> stages.add(stage) }.execute(wav(), session, null)
        assertNull(result.conversationId)
        assertNull(result.audio)
        assertFalse(result.continueListening)
        assertEquals(listOf("/api/voice/transcriptions"), fixture.requests.map { it.path })
        assertEquals(listOf("stt", "empty"), stages)
      }
    }
  }

  @Test fun leadingInvocationDoesNotRemoveNamesInsideTheCommandOrPrefixOfWord() {
    assertEquals("responda Aegis agora", NodeHandsFreeBackend.stripWakeWord("Aegis, responda Aegis agora"))
    assertEquals("Aegisteste", NodeHandsFreeBackend.stripWakeWord("Aegisteste"))
    assertEquals("Fale com Aegis", NodeHandsFreeBackend.stripWakeWord("Fale com Aegis"))
    assertEquals("olá", NodeHandsFreeBackend.stripWakeWord(" Aégis—olá"))
  }

  @Test fun absentOrInvalidContinuationClosesAfterTheExistingVoice() {
    for (decision in listOf("close", "invalid", "", null)) Fixture(decision = decision).use { fixture ->
      val result = fixture.backend().execute(wav(), session, null)
      assertFalse(result.continueListening)
      assertNotNull(result.audio)
    }
  }

  @Test fun invalidWavAndIdentifiersFailBeforeSendingAnything() {
    Fixture().use { fixture ->
      for (audio in listOf(ByteArray(44), ByteArray(100), wav().copyOf(45), wav().also { it[24] = 0 }))
        assertThrows(IllegalArgumentException::class.java) { fixture.backend().execute(audio, session, null) }
      assertThrows(IllegalArgumentException::class.java) { fixture.backend().execute(wav(), "1-1-1-1-1", null) }
      assertThrows(IllegalArgumentException::class.java) { fixture.backend().execute(wav(), session, "not-a-conversation") }
      assertTrue(fixture.requests.isEmpty())
    }
  }

  @Test fun staleTranscriptionAndWrongConversationNeverRequestSpeech() {
    for (failure in listOf("stt-id", "conversation", "message-conversation", "role")) {
      Fixture(respond = { request, normal ->
        if (failure == "stt-id" && request.path.endsWith("transcriptions"))
          Reply.json(JSONObject(String(normal.bytes)).put("transcriptionRequestId", UUID.randomUUID().toString()))
        else if (request.path == "/api/chat/messages" && failure != "stt-id") {
          val json = JSONObject(String(normal.bytes))
          when (failure) {
            "conversation" -> json.put("conversationId", UUID.randomUUID().toString())
            "message-conversation" -> json.getJSONObject("message").put("conversationId", UUID.randomUUID().toString())
            "role" -> json.getJSONObject("message").put("role", "user")
          }
          Reply.json(json)
        } else normal
      }).use { fixture ->
        assertThrows(IllegalArgumentException::class.java) { fixture.backend().execute(wav(), session, conversation) }
        assertFalse(fixture.requests.any { it.method == "POST" && it.path == "/api/voice/speech" })
      }
    }
  }

  @Test fun ttsErrorsInvalidFormatsAndOversizedBodiesFailAndCancelTheTurn() {
    for (failure in listOf("http", "format", "empty", "odd", "large", "redirect")) {
      Fixture(respond = { request, normal ->
        if (request.path != "/api/voice/speech" || request.method != "POST") normal
        else when (failure) {
          "http" -> Reply(503)
          "format" -> normal.copy(headers = normal.headers + ("X-Aegis-Sample-Rate" to "16000"))
          "empty" -> normal.copy(bytes = byteArrayOf())
          "odd" -> normal.copy(bytes = byteArrayOf(1))
          "large" -> normal.copy(bytes = ByteArray(24000 * 2 * 180 + 2))
          else -> Reply(302, headers = mapOf("Location" to "/forbidden"))
        }
      }).use { fixture ->
        val failureResult = runCatching { fixture.backend().execute(wav(), session, conversation) }
        assertTrue(failureResult.isFailure)
        assertTrue(fixture.requests.any { it.method == "DELETE" && it.path.startsWith("/api/chat/turns/") })
        assertFalse(fixture.requests.any { it.path == "/forbidden" })
      }
    }
  }

  @Test fun sttAndChatFailuresNeverAdvanceToTheNextStage() {
    for (path in listOf("/api/voice/transcriptions", "/api/chat/messages")) Fixture(respond = { request, normal ->
      if (request.path == path) Reply(503) else normal
    }).use { fixture ->
      assertThrows(NodeNativeSession.HttpFailure::class.java) { fixture.backend().execute(wav(), session, null) }
      assertFalse(fixture.requests.any { it.method == "POST" && it.path == "/api/voice/speech" })
      if (path.endsWith("transcriptions")) assertEquals(1, fixture.requests.size)
    }
  }

  @Test fun transcriptionAndChatResponsesHaveIndependentByteLimits() {
    for (path in listOf("/api/voice/transcriptions", "/api/chat/messages")) Fixture(respond = { request, normal ->
      if (request.path == path) normal.copy(bytes = ByteArray(if (path.endsWith("transcriptions")) 65537 else 262145) { 32 }) else normal
    }).use { fixture ->
      assertThrows(IllegalArgumentException::class.java) { fixture.backend().execute(wav(), session, null) }
      assertFalse(fixture.requests.any { it.method == "POST" && it.path == "/api/voice/speech" })
    }
  }

  @Test fun missingOrChangedIdentityPreventsSendingAndCannotRepinContinuation() {
    Fixture().use { fixture ->
      val identity = AtomicReference<NodeNativeSession.Session?>(null)
      val backend = NodeHandsFreeBackend({ identity.get() })
      assertThrows(NodeNativeSession.IdentityUnavailable::class.java) { backend.execute(wav(), session, null) }
      assertTrue(fixture.requests.isEmpty())
      identity.set(fixture.identity)
      backend.execute(wav(), session, conversation)
      val count = fixture.requests.size
      identity.set(fixture.identity.copy(credential = "changed-credential"))
      assertThrows(NodeNativeSession.IdentityUnavailable::class.java) { backend.execute(wav(), session, conversation) }
      assertEquals(count, fixture.requests.size)
    }
  }

  @Test fun backendInstanceCannotMoveToAnotherSessionOrConversation() {
    Fixture().use { fixture ->
      val backend = fixture.backend()
      backend.execute(wav(), session, null)
      val count = fixture.requests.size
      assertThrows(IllegalArgumentException::class.java) { backend.execute(wav(), UUID.randomUUID().toString(), conversation) }
      assertThrows(IllegalArgumentException::class.java) { backend.execute(wav(), session, UUID.randomUUID().toString()) }
      assertThrows(IllegalArgumentException::class.java) { backend.execute(wav(), session, null) }
      assertEquals(count, fixture.requests.size)
      backend.execute(wav(), session, conversation)
      assertEquals(count + 3, fixture.requests.size)
    }
  }

  @Test fun largeVoiceChecksVaultAtRequestBoundariesRatherThanEveryAudioBlock() {
    Fixture(respond = { request, normal ->
      if (request.method == "POST" && request.path == "/api/voice/speech") normal.copy(bytes = ByteArray(512000)) else normal
    }).use { fixture ->
      val reads = AtomicInteger()
      val backend = NodeHandsFreeBackend({ reads.incrementAndGet(); fixture.identity })
      assertEquals(512000, backend.execute(wav(), session, null).audio!!.size)
      assertTrue("Vault reads must be bounded by requests, not audio chunks", reads.get() <= 20)
    }
  }

  @Test fun authorityRevokedDuringVoiceDownloadDiscardsReturnedAudio() {
    val authority = AtomicReference<NodeNativeSession.Session?>()
    Fixture(respond = { request, normal ->
      if (request.method == "POST" && request.path == "/api/voice/speech") authority.set(null)
      normal
    }).use { fixture ->
      authority.set(fixture.identity)
      assertThrows(NodeNativeSession.IdentityUnavailable::class.java) {
        NodeHandsFreeBackend({ authority.get() }).execute(wav(), session, null)
      }
      assertTrue(fixture.requests.any { it.method == "DELETE" && it.path.startsWith("/api/chat/turns/") })
    }
  }

  @Test fun cancellationDuringChatDiscardsLateResultsAndSendsBoundTurnCancellation() {
    val entered = CountDownLatch(1)
    val release = CountDownLatch(1)
    Fixture(respond = { request, normal ->
      if (request.method == "POST" && request.path == "/api/chat/messages") {
        entered.countDown()
        check(release.await(5, TimeUnit.SECONDS))
      }
      normal
    }).use { fixture ->
      val executor = Executors.newSingleThreadExecutor()
      try {
        val backend = fixture.backend()
        val future = executor.submit<HandsFreeTurn> { backend.execute(wav(), session, null) }
        assertTrue(entered.await(5, TimeUnit.SECONDS))
        assertThrows(IllegalStateException::class.java) { backend.execute(wav(), session, null) }
        backend.cancel()
        release.countDown()
        assertThrows(java.util.concurrent.ExecutionException::class.java) { future.get(5, TimeUnit.SECONDS) }
        assertTrue(fixture.requests.any { it.method == "DELETE" && it.path.startsWith("/api/chat/turns/") })
        assertFalse(fixture.requests.any { it.method == "POST" && it.path == "/api/voice/speech" })
        val count = fixture.requests.size
        assertThrows(java.util.concurrent.CancellationException::class.java) { backend.execute(wav(), session, null) }
        assertEquals(count, fixture.requests.size)
      } finally { release.countDown(); executor.shutdownNow() }
    }
  }

  private data class Request(val method: String, val path: String, val bytes: ByteArray, val contentType: String, val authorization: String) {
    fun text() = String(bytes, Charsets.UTF_8)
  }
  private data class Reply(val status: Int = 200, val bytes: ByteArray = byteArrayOf(), val headers: Map<String, String> = emptyMap()) {
    companion object { fun json(value: JSONObject) = Reply(bytes = value.toString().toByteArray(Charsets.UTF_8), headers = mapOf("Content-Type" to "application/json")) }
  }
  private inner class Fixture(
    val transcript: String = "Aegis, o que tem hoje?",
    val decision: String? = "continue",
    val respond: (Request, Reply) -> Reply = { _, response -> response }
  ) : AutoCloseable {
    val requests = CopyOnWriteArrayList<Request>()
    private val executor = Executors.newCachedThreadPool()
    private val server = ServerSocket(0, 50, InetAddress.getByName("127.0.0.1"))
    private val serverErrors = CopyOnWriteArrayList<Throwable>()
    @Volatile private var running = true
    private val acceptor = Thread({
      while (running) {
        try { val socket = server.accept(); executor.execute { serve(socket) } }
        catch (error: IOException) { if (running) serverErrors.add(error) }
      }
    }, "aegis-loopback-test").apply { isDaemon = true; start() }
    val identity = NodeNativeSession.Session("http://127.0.0.1:${server.localPort}", "synthetic-node", "synthetic-credential")
    private fun serve(socket: Socket) {
      socket.use {
        try {
          socket.soTimeout = 5000
          val input = BufferedInputStream(socket.getInputStream())
          fun line(): String {
            val bytes = ByteArrayOutputStream()
            while (true) {
              val next = input.read()
              check(next >= 0) { "Unexpected end of HTTP header" }
              if (next == 10) return String(bytes.toByteArray(), Charsets.US_ASCII).trimEnd('\r')
              check(bytes.size() < 8192)
              bytes.write(next)
            }
          }
          val start = line().split(' ')
          check(start.size == 3)
          val headers = mutableMapOf<String, String>()
          while (true) {
            val header = line(); if (header.isEmpty()) break
            check(headers.size < 64)
            val parts = header.split(':', limit = 2); check(parts.size == 2)
            headers[parts[0].lowercase(java.util.Locale.ROOT)] = parts[1].trim()
          }
          val length = headers["content-length"]?.toInt() ?: 0
          check(length in 0..3000000)
          val body = ByteArray(length)
          var offset = 0
          while (offset < length) { val count = input.read(body, offset, length - offset); check(count > 0); offset += count }
          val request = Request(start[0], start[1], body, headers["content-type"] ?: "", headers["authorization"] ?: "")
          requests.add(request)
          val reply = respond(request, normal(request))
          val responseHeaders = buildString {
            append("HTTP/1.1 ${reply.status} Test\r\nContent-Length: ${reply.bytes.size}\r\nConnection: close\r\n")
            reply.headers.forEach { (key, value) -> append("$key: $value\r\n") }
            append("\r\n")
          }
          socket.getOutputStream().use { output ->
            output.write(responseHeaders.toByteArray(Charsets.US_ASCII)); output.write(reply.bytes); output.flush()
          }
        } catch (_: IOException) { /* Cancellation and size limits intentionally disconnect the client. */ }
        catch (error: Throwable) {
          serverErrors.add(error)
          try { socket.getOutputStream().write("HTTP/1.1 500 Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".toByteArray(Charsets.US_ASCII)) } catch (_: IOException) { }
        }
      }
    }
    fun backend(onStage: (String, Long) -> Unit = { _, _ -> }) = NodeHandsFreeBackend({ identity }, onStage)
    private fun normal(request: Request): Reply {
      if (request.method == "DELETE") return Reply.json(JSONObject())
      return when (request.path) {
        "/api/voice/transcriptions" -> Reply.json(JSONObject().put("text", transcript).put("transcriptionRequestId",
          Regex("name=\"transcriptionRequestId\"\\r\\n\\r\\n([^\\r\\n]+)").find(request.text())!!.groupValues[1]))
        "/api/chat/messages" -> Reply.json(JSONObject().put("conversationId", conversation).put("message",
          JSONObject().put("id", message).put("conversationId", conversation).put("role", "assistant")).apply {
            decision?.let { put("voiceContinuation", it) }
          })
        "/api/voice/speech" -> Reply(bytes = byteArrayOf(1, 2, 3, 4), headers = mapOf(
          "X-Aegis-Audio-Format" to "pcm_s16le", "X-Aegis-Sample-Rate" to "24000", "X-Aegis-Channels" to "1"))
        else -> Reply(404)
      }
    }
    override fun close() {
      running = false; server.close(); acceptor.join(1000); executor.shutdownNow()
      assertTrue("Loopback fixture failed: $serverErrors", serverErrors.isEmpty())
    }
  }
}
