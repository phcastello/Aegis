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
import java.io.OutputStream
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ExecutionException
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

  private class RecordingSink : HandsFreePcmSink {
    val bytes=ByteArrayOutputStream()
    var finished=false
    var aborted=false
    var closed=false
    override fun write(data:ByteArray,offset:Int,count:Int) { check(!aborted && !closed);bytes.write(data,offset,count) }
    override fun finish(){check(!aborted && !closed);finished=true}
    override fun abort(){aborted=true}
    override fun close(){closed=true}
  }
  @Volatile private var lastSink=RecordingSink()
  // Existing protocol cases now stream into a simulated output instead of collecting PCM in production.
  private fun NodeHandsFreeBackend.runTurn(wav:ByteArray,sessionId:String,conversationId:String?):HandsFreeTurn {
    val sink=RecordingSink();lastSink=sink
    return execute(wav,sessionId,conversationId,sink)
  }

  @Test fun persistedChatInvalidatesBeforeSpeechEvenWhenTtsFails() {
    val changes=mutableListOf<String>()
    Fixture(respond={request,normal->
      if(request.path=="/api/voice/speech") {assertEquals(listOf(conversation),changes);Reply(503)} else normal
    }).use { fixture ->
      val backend=fixture.backend(onChanged={_,id->changes.add(id)})
      assertThrows(Exception::class.java){backend.runTurn(wav(),session,null)}
      assertEquals(listOf(conversation),changes)
    }
  }

  @Test fun delayedOldIdentityResponseCannotEraseNewIdentityJournal() {
    val entered=CountDownLatch(1);val release=CountDownLatch(1)
    Fixture(respond={request,normal->
      if(request.path=="/api/chat/messages"){entered.countDown();check(release.await(5,TimeUnit.SECONDS))};normal
    }).use {fixture->
      val authority=AtomicReference(fixture.identity)
      val backend=NodeHandsFreeBackend({authority.get()})
      val executor=Executors.newSingleThreadExecutor()
      try {
        val old=executor.submit<HandsFreeTurn>{backend.runTurn(wav(),session,null)}
        assertTrue(entered.await(5,TimeUnit.SECONDS))
        val replacement=fixture.identity.copy(nodeId="replacement-node",credential="replacement-credential")
        authority.set(replacement)
        val destination=UUID.randomUUID().toString()
        NodeConversationChanges.changed(replacement,destination)
        release.countDown()
        assertThrows(ExecutionException::class.java){old.get(5,TimeUnit.SECONDS)}
        val replay=JSONObject(NodeConversationChanges.snapshot(replacement)).getJSONArray("changes")
        assertEquals(1,replay.length());assertEquals(destination,replay.getJSONObject(0).getString("conversationId"))
      }finally{release.countDown();backend.cancel();executor.shutdownNow()}
    }
  }

  @Test fun emptySttAndInvalidChatNeverInvalidateAnUnpersistedDestination() {
    for(empty in listOf(true,false)) {
      val changes=mutableListOf<String>()
      Fixture(transcript=if(empty) "Aegis" else "Aegis, diga oi",respond={request,normal->
        if(!empty && request.path=="/api/chat/messages") Reply.json(JSONObject(String(normal.bytes)).put("conversationId","invalid")) else normal
      }).use {fixture->
        val backend=fixture.backend(onChanged={_,id->changes.add(id)})
        if(empty)backend.runTurn(wav(),session,null) else assertThrows(Exception::class.java){backend.runTurn(wav(),session,null)}
        assertTrue(changes.isEmpty())
      }
    }
  }

  @Test fun streamingPersistenceInvalidatesBeforePlaybackDrainAndSurvivesTtsFailure() {
    val changes=java.util.concurrent.CopyOnWriteArrayList<String>()
    val firstPersisted=CountDownLatch(1)
    Fixture(respond={request,normal->
      if(request.path=="/api/chat/messages/stream") {
        val turn=JSONObject(request.text()).getString("turnId")
        val accepted=JSONObject().put("type","conversation").put("turnId",turn).put("conversationId",conversation).toString()+"\n"
        val segment=JSONObject().put("type","final_segment").put("turnId",turn).put("conversationId",conversation)
          .put("assistantMessageId",message).put("segmentId",UUID.randomUUID().toString()).put("sequence",0).put("content","Oi.").toString()+"\n"
        val done=JSONObject().put("type","done").put("turnId",turn).put("conversationId",conversation).put("assistantMessageId",message).toString()+"\n"
        Reply(bytes=(accepted+segment+done).toByteArray(),stream={out->
          out.write(accepted.toByteArray());out.flush();assertTrue(firstPersisted.await(5,TimeUnit.SECONDS))
          out.write((segment+done).toByteArray());out.flush()
        })
      } else if(request.path=="/api/voice/speech/segments") Reply(503) else normal
    }).use {fixture->
      val backend=fixture.backend(onChanged={_,id->changes.add(id);firstPersisted.countDown()})
      assertThrows(Exception::class.java){backend.execute(wav(),session,null,RecordingSink(),HandsFreeTurnLatency(),incremental=true)}
      assertEquals(listOf(conversation,conversation,conversation),changes)
    }
  }

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
        val pending = executor.submit<HandsFreeTurn> { backend.runTurn(wav(), session, null) }
        assertTrue(entered.await(5, TimeUnit.SECONDS))
        // Intentionally do not run deferred cancel()/DELETE cleanup yet.
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
      val result = backend.runTurn(wav(), session, conversation)
      assertEquals(conversation, result.conversationId)
      assertTrue(result.playedAudio)
      assertArrayEquals(byteArrayOf(1, 2, 3, 4), lastSink.bytes.toByteArray())
      assertTrue(result.continueListening)
      assertEquals(listOf("transcription_started", "stt", "transcription_final", "transcribed", "chat", "tts_first_audio", "tts_fetch", "tts_total", "tts_playback"), stages)
      assertEquals(listOf("/api/voice/transcriptions", "/api/chat/messages", "/api/voice/speech"), fixture.requests.map { it.path })
      val transcription = fixture.requests[0]
      assertTrue(transcription.contentType.startsWith("multipart/form-data; boundary=aegis-"))
      val body = transcription.text()
      assertTrue(body.contains("name=\"clientDurationMilliseconds\"\r\n\r\n100\r\n"))
      assertTrue(body.contains("name=\"audio\"; filename=\"command.wav\"\r\nContent-Type: audio/wav\r\n\r\nRIFF"))
      assertTrue(body.endsWith("--\r\n"))
      val chat = JSONObject(fixture.requests[1].text())
      assertTrue(fixture.requests.all{it.turnCorrelation==chat.getString("turnId")})
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
        val result = fixture.backend { stage, _ -> stages.add(stage) }.runTurn(wav(), session, null)
        assertNull(result.conversationId)
        assertFalse(result.playedAudio)
        assertEquals(0,lastSink.bytes.size())
        assertFalse(result.continueListening)
        assertEquals(listOf("/api/voice/transcriptions"), fixture.requests.map { it.path })
        assertEquals(listOf("transcription_started", "stt", "transcription_final", "empty"), stages)
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
      val result = fixture.backend().runTurn(wav(), session, null)
      assertFalse(result.continueListening)
      assertTrue(result.playedAudio)
    }
  }

  @Test fun invalidWavAndIdentifiersFailBeforeSendingAnything() {
    Fixture().use { fixture ->
      for (audio in listOf(ByteArray(44), ByteArray(100), wav().copyOf(45), wav().also { it[24] = 0 }))
        assertThrows(IllegalArgumentException::class.java) { fixture.backend().runTurn(audio, session, null) }
      assertThrows(IllegalArgumentException::class.java) { fixture.backend().runTurn(wav(), "1-1-1-1-1", null) }
      assertThrows(IllegalArgumentException::class.java) { fixture.backend().runTurn(wav(), session, "not-a-conversation") }
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
        assertThrows(IllegalArgumentException::class.java) { fixture.backend().runTurn(wav(), session, conversation) }
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
        val failureResult = runCatching { fixture.backend().runTurn(wav(), session, conversation) }
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
      assertThrows(NodeNativeSession.HttpFailure::class.java) { fixture.backend().runTurn(wav(), session, null) }
      assertFalse(fixture.requests.any { it.method == "POST" && it.path == "/api/voice/speech" })
      if (path.endsWith("transcriptions")) assertEquals(1, fixture.requests.size)
    }
  }

  @Test fun transcriptionAndChatResponsesHaveIndependentByteLimits() {
    for (path in listOf("/api/voice/transcriptions", "/api/chat/messages")) Fixture(respond = { request, normal ->
      if (request.path == path) normal.copy(bytes = ByteArray(if (path.endsWith("transcriptions")) 65537 else 262145) { 32 }) else normal
    }).use { fixture ->
      assertThrows(IllegalArgumentException::class.java) { fixture.backend().runTurn(wav(), session, null) }
      assertFalse(fixture.requests.any { it.method == "POST" && it.path == "/api/voice/speech" })
    }
  }

  @Test fun missingOrChangedIdentityPreventsSendingAndCannotRepinContinuation() {
    Fixture().use { fixture ->
      val identity = AtomicReference<NodeNativeSession.Session?>(null)
      val backend = NodeHandsFreeBackend({ identity.get() })
      assertThrows(NodeNativeSession.IdentityUnavailable::class.java) { backend.runTurn(wav(), session, null) }
      assertTrue(fixture.requests.isEmpty())
      identity.set(fixture.identity)
      backend.runTurn(wav(), session, conversation)
      val count = fixture.requests.size
      identity.set(fixture.identity.copy(credential = "changed-credential"))
      assertThrows(NodeNativeSession.IdentityUnavailable::class.java) { backend.runTurn(wav(), session, conversation) }
      assertEquals(count, fixture.requests.size)
    }
  }

  @Test fun backendInstanceCannotMoveToAnotherSessionOrConversation() {
    Fixture().use { fixture ->
      val backend = fixture.backend()
      backend.runTurn(wav(), session, null)
      val count = fixture.requests.size
      assertThrows(IllegalArgumentException::class.java) { backend.runTurn(wav(), UUID.randomUUID().toString(), conversation) }
      assertThrows(IllegalArgumentException::class.java) { backend.runTurn(wav(), session, UUID.randomUUID().toString()) }
      assertThrows(IllegalArgumentException::class.java) { backend.runTurn(wav(), session, null) }
      assertEquals(count, fixture.requests.size)
      backend.runTurn(wav(), session, conversation)
      assertEquals(count + 3, fixture.requests.size)
    }
  }

  @Test fun largeVoiceChecksVaultAtRequestBoundariesRatherThanEveryAudioBlock() {
    Fixture(respond = { request, normal ->
      if (request.method == "POST" && request.path == "/api/voice/speech") normal.copy(bytes = ByteArray(512000)) else normal
    }).use { fixture ->
      val reads = AtomicInteger()
      val backend = NodeHandsFreeBackend({ reads.incrementAndGet(); fixture.identity })
      assertTrue(backend.runTurn(wav(), session, null).playedAudio)
      assertEquals(512000, lastSink.bytes.size())
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
        NodeHandsFreeBackend({ authority.get() }).runTurn(wav(), session, null)
      }
      assertEquals(0,lastSink.bytes.size())
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
        val future = executor.submit<HandsFreeTurn> { backend.runTurn(wav(), session, null) }
        assertTrue(entered.await(5, TimeUnit.SECONDS))
        assertThrows(IllegalStateException::class.java) { backend.runTurn(wav(), session, null) }
        backend.cancel()
        release.countDown()
        assertThrows(java.util.concurrent.ExecutionException::class.java) { future.get(5, TimeUnit.SECONDS) }
        assertTrue(fixture.requests.any { it.method == "DELETE" && it.path.startsWith("/api/chat/turns/") })
        assertFalse(fixture.requests.any { it.method == "POST" && it.path == "/api/voice/speech" })
        val count = fixture.requests.size
        assertThrows(java.util.concurrent.CancellationException::class.java) { backend.runTurn(wav(), session, null) }
        assertEquals(count, fixture.requests.size)
      } finally { release.countDown(); executor.shutdownNow() }
    }
  }

  @Test fun slowOddFragmentsReachThePlayerBeforeHttpEofWithBoundedWrites() {
    val device=NodeHandsFreePcmStreamTest.Device(autoPlay=true)
    val releaseRemainder=CountDownLatch(1)
    val firstDelivered=CountDownLatch(1)
    val expected=ByteArray(50000){(it%251).toByte()}
    val stages=CopyOnWriteArrayList<String>()
    val durations=java.util.concurrent.ConcurrentHashMap<String,Long>()
    Fixture(respond={request,normal->
      if(request.method!="POST" || request.path!="/api/voice/speech")normal
      else normal.copy(bytes=expected,stream={output->
        output.write(expected,0,3);output.flush()
        check(releaseRemainder.await(5,TimeUnit.SECONDS))
        var offset=3
        for(size in listOf(1,5,3,101,7)) {
          output.write(expected,offset,size);output.flush();offset+=size;Thread.sleep(10)
        }
        output.write(expected,offset,expected.size-offset);output.flush()
      })
    }).use {fixture->
      val backend=fixture.backend{stage,duration->stages.add(stage);durations[stage]=duration;if(stage=="tts_first_audio")firstDelivered.countDown()}
      val sink=HandsFreeStreamingPcmSink({device})
      val executor=Executors.newSingleThreadExecutor()
      try {
        val result=executor.submit<HandsFreeTurn>{backend.execute(wav(),session,null,sink)}
        assertTrue(firstDelivered.await(5,TimeUnit.SECONDS))
        assertFalse(result.isDone)
        assertEquals(2,device.recorded.size())
        assertEquals(1,device.playedFrames()) // Simulated playback already advanced before the response can finish.
        releaseRemainder.countDown()
        assertTrue(result.get(5,TimeUnit.SECONDS).playedAudio)
        assertArrayEquals(expected,device.recorded.toByteArray())
        assertTrue(device.maxWrite<=8192)
        assertTrue(stages.indexOf("tts_first_audio")<stages.indexOf("tts_fetch"))
        assertTrue(stages.containsAll(listOf("tts_total","tts_playback")))
        assertTrue(durations.getValue("tts_first_audio")<durations.getValue("tts_total"))
      } finally {releaseRemainder.countDown();backend.cancel();executor.shutdownNow()}
    }
  }

  @Test fun turnTraceCorrelatesSttBeforeChatAndDistinguishesBytesSilenceAndUsefulDelivery() {
    val silentPrefix=CountDownLatch(1);val releaseUseful=CountDownLatch(1)
    val trace=HandsFreeTurnLatency()
    val pcm=byteArrayOf(0,0,1,0,-63,-1,63,0,-64,-1)
    Fixture(respond={request,normal->
      if(request.method=="POST" && request.path=="/api/voice/speech")normal.copy(bytes=pcm,stream={output->
        output.write(pcm,0,9);output.flush();silentPrefix.countDown()
        check(releaseUseful.await(5,TimeUnit.SECONDS))
        output.write(pcm,9,1);output.flush()
      })else normal
    }).use { fixture->
      val backend=fixture.backend()
      val recorded=RecordingSink()
      val delivered=CountDownLatch(1)
      val sink=object:HandsFreePcmSink by recorded {
        override fun write(bytes:ByteArray,offset:Int,count:Int){recorded.write(bytes,offset,count);delivered.countDown()}
      }
      val executor=Executors.newSingleThreadExecutor()
      try {
        val result=executor.submit<HandsFreeTurn>{backend.execute(wav(),session,null,sink,trace)}
        assertTrue(silentPrefix.await(5,TimeUnit.SECONDS));assertTrue(delivered.await(5,TimeUnit.SECONDS))
        var points=trace.report("running").getJSONObject("milestonesFromEndpointMs")
        assertTrue(points.has("tts_first_byte"));assertTrue(points.has("tts_first_pcm_available"))
        assertFalse(points.has("tts_first_useful_pcm_available"));assertFalse(points.has("tts_first_useful_pcm_delivered"))
        releaseUseful.countDown();assertTrue(result.get(5,TimeUnit.SECONDS).playedAudio)
        val report=trace.report("played");points=report.getJSONObject("milestonesFromEndpointMs")
        assertTrue(points.has("tts_first_useful_pcm_available"));assertTrue(points.has("tts_first_useful_pcm_delivered"))
        assertEquals(4,report.getLong("firstUsefulPcmFrame"))
        assertTrue(points.getLong("tts_first_byte")<=points.getLong("tts_first_useful_pcm_delivered"))
        assertTrue(points.has("stt_upload_started"));assertTrue(points.has("stt_upload_completed"));assertTrue(points.has("stt_response_received"))
        assertTrue(fixture.requests.all{it.turnCorrelation==trace.turnId})
        assertEquals(trace.turnId,JSONObject(fixture.requests[1].text()).getString("turnId"))
        assertTrue(fixture.requests[0].text().contains(report.getString("transcriptionRequestId")))
        assertEquals(JSONObject(fixture.requests[2].text()).getString("speechRequestId"),report.getString("speechRequestId"))
        assertArrayEquals(pcm,recorded.bytes.toByteArray())
      }finally{releaseUseful.countDown();backend.cancel();executor.shutdownNow()}
    }
  }

  @Test fun downloadedVoiceCannotReturnContinuationUntilTheSimulatedPlaybackHeadFinishes() {
    val device=NodeHandsFreePcmStreamTest.Device(autoPlay=false)
    Fixture().use {fixture->
      val backend=fixture.backend()
      val sink=HandsFreeStreamingPcmSink({device})
      val executor=Executors.newSingleThreadExecutor()
      try {
        val result=executor.submit<HandsFreeTurn>{backend.execute(wav(),session,null,sink)}
        assertTrue(device.markerArmed.await(5,TimeUnit.SECONDS))
        assertFalse(result.isDone)
        assertEquals(0,device.playedFrames())
        device.advance(2)
        val finished=result.get(5,TimeUnit.SECONDS)
        assertTrue(finished.playedAudio);assertTrue(finished.continueListening)
      }finally{backend.cancel();executor.shutdownNow()}
    }
  }

  @Test fun prematureNetworkEofAbortsAlreadyQueuedSamplesAndNeverCompletesTheTurn() {
    val device=NodeHandsFreePcmStreamTest.Device(autoPlay=false)
    Fixture(respond={request,normal->
      if(request.method=="POST" && request.path=="/api/voice/speech")
        normal.copy(bytes=ByteArray(100),stream={output->output.write(byteArrayOf(1,2,3,4));output.flush()})
      else normal
    }).use {fixture->
      val result=runCatching{fixture.backend().execute(wav(),session,null,HandsFreeStreamingPcmSink({device}))}
      assertTrue(result.isFailure)
      assertEquals(4,device.recorded.size())
      assertEquals(0,device.queuedFrames)
      assertEquals(1L,device.markerArmed.count)
      assertTrue(device.closed)
      assertTrue(fixture.requests.any{it.method=="DELETE" && it.path.startsWith("/api/chat/turns/")})
    }
  }

  @Test fun cancellingAStreamStopsDownloadAndQueuedAudioWithoutRunningRemoteCleanupFirst() {
    val device=NodeHandsFreePcmStreamTest.Device(autoPlay=false)
    val release=CountDownLatch(1)
    Fixture(respond={request,normal->
      if(request.method=="POST" && request.path=="/api/voice/speech")normal.copy(bytes=ByteArray(100),stream={output->
        output.write(byteArrayOf(1,2,3,4));output.flush()
        check(release.await(5,TimeUnit.SECONDS))
        output.write(ByteArray(96));output.flush()
      })else normal
    }).use {fixture->
      val backend=fixture.backend()
      val executor=Executors.newSingleThreadExecutor()
      try {
        val result=executor.submit<HandsFreeTurn>{backend.execute(wav(),session,null,HandsFreeStreamingPcmSink({device}))}
        assertTrue(device.firstWrite.await(5,TimeUnit.SECONDS))
        backend.requestCancellation()
        assertEquals(0,device.queuedFrames)
        // Immediate request invalidation and socket close do not wait behind remote DELETE cleanup.
        assertThrows(ExecutionException::class.java){result.get(5,TimeUnit.SECONDS)}
        assertEquals(4,device.recorded.size());assertTrue(device.closed)
      }finally{release.countDown();backend.cancel();executor.shutdownNow()}
    }
  }

  @Test fun playerFailureAfterHttpEofCancelsTheTurnInsteadOfReturningContinuation() {
    Fixture().use {fixture->
      val recorded=RecordingSink()
      val failedOutput=object:HandsFreePcmSink by recorded {
        override fun finish(){error("simulated_playback_failure")}
      }
      val stages=mutableListOf<String>()
      val result=runCatching{fixture.backend{stage,_->stages.add(stage)}.execute(wav(),session,null,failedOutput)}
      assertTrue(result.isFailure)
      assertArrayEquals(byteArrayOf(1,2,3,4),recorded.bytes.toByteArray())
      assertTrue(recorded.aborted);assertTrue(recorded.closed);assertFalse(recorded.finished)
      assertTrue(stages.contains("tts_fetch"));assertTrue(stages.contains("failed"))
      assertEquals(2,fixture.requests.count{it.method=="DELETE"})
    }
  }

  @Test fun ttsDeadlineAbortsAPlayerWriteBlockedByBackpressure() {
    val device=NodeHandsFreePcmStreamTest.Device(autoPlay=false,capacityFrames=2)
    Fixture(respond={request,normal->
      if(request.method=="POST" && request.path=="/api/voice/speech")normal.copy(bytes=ByteArray(50000))else normal
    }).use {fixture->
      val backend=fixture.backend(ttsDeadlineMilliseconds=1000)
      val executor=Executors.newSingleThreadExecutor()
      try {
        val result=executor.submit<HandsFreeTurn>{backend.execute(wav(),session,null,HandsFreeStreamingPcmSink({device}))}
        assertTrue(device.backpressured.await(5,TimeUnit.SECONDS))
        assertFalse(result.isDone);assertEquals(2,device.queuedFrames)
        val failed=assertThrows(ExecutionException::class.java){result.get(5,TimeUnit.SECONDS)}
        assertTrue(failed.cause is java.util.concurrent.CancellationException)
        assertEquals(0,device.queuedFrames);assertTrue(device.closed)
        assertEquals(1L,device.markerArmed.count)
      }finally{backend.cancel();executor.shutdownNow()}
    }
  }

  @Test fun ttsDeadlineAlsoAbortsFinalPlaybackDrainAfterHttpEof() {
    val device=NodeHandsFreePcmStreamTest.Device(autoPlay=false)
    Fixture().use {fixture->
      val backend=fixture.backend(ttsDeadlineMilliseconds=1000)
      val executor=Executors.newSingleThreadExecutor()
      try {
        val result=executor.submit<HandsFreeTurn>{backend.execute(wav(),session,null,HandsFreeStreamingPcmSink({device}))}
        assertTrue(device.markerArmed.await(5,TimeUnit.SECONDS))
        assertFalse(result.isDone);assertEquals(2,device.queuedFrames)
        val failed=assertThrows(ExecutionException::class.java){result.get(5,TimeUnit.SECONDS)}
        assertTrue(failed.cause is java.util.concurrent.CancellationException)
        assertEquals(0,device.queuedFrames);assertTrue(device.closed)
      }finally{backend.cancel();executor.shutdownNow()}
    }
  }

  @Test fun realtimeFailureFallsBackOnceUsingCapturedWavBeforeExactlyOneChatTurn() {
    Fixture().use {fixture->
      val backend=fixture.backend();val trace=HandsFreeTurnLatency()
      val realtime=backend.beginCapture(trace)
      realtime.append(ShortArray(40000),40000);realtime.commit()
      val recorded=RecordingSink()
      val result=backend.execute(wav(),session,null,recorded,trace)
      assertTrue(result.playedAudio)
      assertEquals(1,fixture.requests.count{it.path=="/api/voice/transcriptions"})
      assertEquals(1,fixture.requests.count{it.path=="/api/chat/messages"})
      assertEquals(1,fixture.requests.count{it.path=="/api/voice/speech"})
      assertTrue(trace.report("played").getJSONObject("milestonesFromEndpointMs").has("stt_batch_fallback"))
    }
  }

  @Test fun finalizedSegmentsPlayBeforeTextEndsInOneContinuousNativeStream() {
    val firstPlayed=CountDownLatch(1);var textEnded=false
    val device=NodeHandsFreePcmStreamTest.Device(autoPlay=true)
    var devices=0
    val player=HandsFreePlayback { _ -> devices++;device }
    Fixture(respond={request,normal->
      if(request.path=="/api/chat/messages/stream") {
        val turn=JSONObject(request.text()).getString("turnId")
        fun segment(sequence:Int)=JSONObject().put("type","final_segment").put("turnId",turn).put("conversationId",conversation)
          .put("assistantMessageId",message).put("segmentId",UUID.randomUUID().toString()).put("sequence",sequence).put("content","Frase $sequence.").toString()+"\n"
        val first=segment(0);val remaining=segment(1)+JSONObject().put("type","done").put("turnId",turn).put("conversationId",conversation)
          .put("assistantMessageId",message).put("voiceContinuation","continue").toString()+"\n"
        Reply(bytes=(first+remaining).toByteArray(),stream={out->
          out.write(first.toByteArray());out.flush();check(firstPlayed.await(5,TimeUnit.SECONDS));textEnded=true
          out.write(remaining.toByteArray());out.flush()
        })
      } else if(request.path=="/api/voice/speech/segments") Reply(bytes=byteArrayOf(1,2,3,4),headers=mapOf(
        "X-Aegis-Audio-Format" to "pcm_s16le","X-Aegis-Sample-Rate" to "24000","X-Aegis-Channels" to "1"))
      else normal
    }).use {fixture->
      val backend=fixture.backend()
      val native=player.openStream{true}
      val sink=object:HandsFreePcmSink by native {
        override fun write(bytes:ByteArray,offset:Int,count:Int) {
          if(firstPlayed.count>0)assertFalse(textEnded)
          native.write(bytes,offset,count);firstPlayed.countDown()
        }
      }
      val result=backend.execute(wav(),session,null,sink,HandsFreeTurnLatency(),incremental=true)
      assertTrue(result.continueListening);assertTrue(textEnded);assertEquals(1,devices)
      assertArrayEquals(byteArrayOf(1,2,3,4,1,2,3,4),device.recorded.toByteArray())
      val requests=fixture.requests.filter{it.path=="/api/voice/speech/segments"}.map{JSONObject(it.text())}
      assertEquals(listOf(0,1),requests.map{it.getInt("sequence")})
      assertEquals(2,requests.map{it.getString("speechRequestId")}.distinct().size)
      assertTrue(requests.all{!it.has("text") && !it.has("content")})
    }
  }

  @Test fun duplicateOrOutOfOrderSegmentsAbortTheRemainingAudioAndCannotContinue() {
    for(bad in listOf(0,2)) Fixture(respond={request,normal->
      if(request.path=="/api/chat/messages/stream") {
        val turn=JSONObject(request.text()).getString("turnId")
        val events=listOf(0,bad).map{sequence->JSONObject().put("type","final_segment").put("turnId",turn).put("conversationId",conversation)
          .put("assistantMessageId",message).put("segmentId",UUID.randomUUID().toString()).put("sequence",sequence).put("content","Frase.").toString()}
        Reply(bytes=(events.joinToString("\n")+"\n").toByteArray())
      } else if(request.path=="/api/voice/speech/segments") Reply(bytes=byteArrayOf(1,2),headers=mapOf(
        "X-Aegis-Audio-Format" to "pcm_s16le","X-Aegis-Sample-Rate" to "24000","X-Aegis-Channels" to "1"))
      else normal
    }).use{fixture->
      val sink=RecordingSink()
      assertThrows(Exception::class.java){fixture.backend().execute(wav(),session,null,sink,null,incremental=true)}
      assertTrue(sink.aborted);assertFalse(sink.finished)
      assertTrue(fixture.requests.count{it.path=="/api/voice/speech/segments"}<=1)
      assertTrue(fixture.requests.any{it.method=="DELETE" && it.path.startsWith("/api/chat/turns/")})
    }
  }

  @Test fun oneAheadSynthesisStartsDuringCurrentPlaybackButThirdWaitsAndCancellationAbortsBoth() {
    val secondStarted=CountDownLatch(1)
    val device=NodeHandsFreePcmStreamTest.Device(autoPlay=false,capacityFrames=2)
    Fixture(respond={request,normal->
      if(request.path=="/api/chat/messages/stream") {
        val turn=JSONObject(request.text()).getString("turnId")
        val segments=(0..2).joinToString("\n"){seq->JSONObject().put("type","final_segment").put("turnId",turn).put("conversationId",conversation)
          .put("assistantMessageId",message).put("segmentId",UUID.randomUUID().toString()).put("sequence",seq).put("content","Frase $seq.").toString()}
        val done=JSONObject().put("type","done").put("turnId",turn).put("conversationId",conversation).put("assistantMessageId",message).put("voiceContinuation","continue")
        Reply(bytes=(segments+"\n"+done.toString()+"\n").toByteArray())
      } else if(request.path=="/api/voice/speech/segments") {
        val seq=JSONObject(request.text()).getInt("sequence");if(seq==1)secondStarted.countDown()
        Reply(bytes=ByteArray(if(seq==0)100000 else 4){1},headers=mapOf(
          "X-Aegis-Audio-Format" to "pcm_s16le","X-Aegis-Sample-Rate" to "24000","X-Aegis-Channels" to "1"))
      }else normal
    }).use{fixture->
      val backend=fixture.backend();val executor=Executors.newSingleThreadExecutor()
      try {
        val running=executor.submit<HandsFreeTurn>{backend.execute(wav(),session,null,HandsFreeStreamingPcmSink({device}),null,incremental=true)}
        assertTrue(device.backpressured.await(5,TimeUnit.SECONDS));assertTrue(secondStarted.await(5,TimeUnit.SECONDS))
        assertFalse(running.isDone);assertEquals(0,device.playedFrames())
        assertEquals(2,fixture.requests.count{it.path=="/api/voice/speech/segments"})
        backend.requestCancellation()
        assertThrows(ExecutionException::class.java){running.get(5,TimeUnit.SECONDS)}
        assertEquals(0,device.queuedFrames);assertTrue(device.closed)
        assertEquals(2,fixture.requests.count{it.path=="/api/voice/speech/segments"})
      } finally {backend.cancel();executor.shutdownNow()}
    }
  }

  @Test fun manualAndHandsFreeResponsesDeliverTheIdenticalSavedPcmThroughTheSameNativePlayer() {
    Fixture().use{fixture->
      val devices=mutableListOf<NodeHandsFreePcmStreamTest.Device>()
      val player=HandsFreePlayback{_->NodeHandsFreePcmStreamTest.Device(autoPlay=true).also{devices.add(it)}}
      fixture.backend().execute(wav(),session,null,player.openStream{true})
      fixture.backend().speakExisting(UUID.randomUUID().toString(),message,UUID.randomUUID().toString(),player.openStream{true})
      assertEquals(2,devices.size)
      assertArrayEquals(devices[0].recorded.toByteArray(),devices[1].recorded.toByteArray())
      assertArrayEquals(byteArrayOf(1,2,3,4),devices[0].recorded.toByteArray())
      assertTrue(devices.all{it.closed})
      assertEquals(1,fixture.requests.count{it.path=="/api/chat/messages"})
    }
  }

  private data class Request(val method: String, val path: String, val bytes: ByteArray, val contentType: String, val authorization: String,val turnCorrelation:String) {
    fun text() = String(bytes, Charsets.UTF_8)
  }
  private data class Reply(val status: Int = 200, val bytes: ByteArray = byteArrayOf(), val headers: Map<String, String> = emptyMap(),
                           val stream: ((OutputStream) -> Unit)? = null) {
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
          // OkHttp may cancel a just-connected prefetched exchange before sending
          // any request byte. Partial headers still fail the strict parser below.
          input.mark(1)
          if(input.read()<0)return
          input.reset()
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
          val request = Request(start[0], start[1], body, headers["content-type"] ?: "", headers["authorization"] ?: "",headers["x-aegis-turn-id"]?:"")
          requests.add(request)
          val reply = respond(request, normal(request))
          val responseHeaders = buildString {
            append("HTTP/1.1 ${reply.status} Test\r\nContent-Length: ${reply.bytes.size}\r\nConnection: close\r\n")
            reply.headers.forEach { (key, value) -> append("$key: $value\r\n") }
            append("\r\n")
          }
          socket.getOutputStream().use { output ->
            output.write(responseHeaders.toByteArray(Charsets.US_ASCII)); output.flush()
            if (reply.stream != null) reply.stream.invoke(output) else output.write(reply.bytes)
            output.flush()
          }
        } catch (_: IOException) { /* Cancellation and size limits intentionally disconnect the client. */ }
        catch (error: Throwable) {
          serverErrors.add(error)
          try { socket.getOutputStream().write("HTTP/1.1 500 Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".toByteArray(Charsets.US_ASCII)) } catch (_: IOException) { }
        }
      }
    }
    fun backend(ttsDeadlineMilliseconds:Long=300000,onChanged:(NodeNativeSession.Session,String)->Unit=NodeConversationChanges::changed,onStage: (String, Long) -> Unit = { _, _ -> }) =
      NodeHandsFreeBackend({ identity }, onStage,ttsDeadlineMilliseconds,onChanged)
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
