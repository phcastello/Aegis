package com.aegis.node

import java.net.InetAddress
import java.net.ServerSocket
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class NodeHandsFreeWakeElectionTest {
    private val samples = ShortArray(24400) { if (it < 16000) 100 else 1000 }
    private val window = HandsFreeVerificationWindow(samples, 24000)

    @Test fun signalEstimateMatchesWindowsAndExcludesPostWakeCommand() {
        samples.fill(30000, 24000, samples.size)
        assertEquals(20.0, checkNotNull(HandsFreeWakeElection.signalToNoiseDb(window)), .00001)
        assertNull(HandsFreeWakeElection.signalToNoiseDb(HandsFreeVerificationWindow(samples, 23999)))
    }

    private class Coordinator(val timingHeaders: List<String> = emptyList(),
        val authHeaders: List<String> = emptyList(),
        val respond: (JSONObject) -> String) : AutoCloseable {
        private val socket = ServerSocket(0, 2, InetAddress.getByName("127.0.0.1"))
        private val worker = Executors.newSingleThreadExecutor()
        val session = NodeNativeSession.Session("http://127.0.0.1:${socket.localPort}", "node", "fixture")
        val payload = AtomicReference<JSONObject>()
        val requestHeaders = AtomicReference<Map<String, String>>()
        private val job = worker.submit {
            socket.accept().use { client ->
                client.soTimeout = 4000
                val input = client.getInputStream().bufferedReader(Charsets.UTF_8)
                assertEquals("POST /api/nodes/me/voice-activations/claim HTTP/1.1", input.readLine())
                val headers = generateSequence { input.readLine()?.takeIf { it.isNotEmpty() } }.associate {
                    it.substringBefore(':').lowercase() to it.substringAfter(':').trim()
                }
                assertEquals("AegisNode fixture", headers["authorization"])
                requestHeaders.set(headers)
                val chars = CharArray(headers.getValue("content-length").toInt())
                var read = 0
                while (read < chars.size) {
                    val n = input.read(chars, read, chars.size - read)
                    check(n > 0)
                    read += n
                }
                val request = JSONObject(String(chars))
                payload.set(request)
                val text = respond(request)
                val timing = timingHeaders.joinToString("") { "X-Aegis-Election-Wait-Ms: $it\r\n" } +
                    authHeaders.joinToString("") { "X-Aegis-Node-Auth-Ms: $it\r\n" }
                client.getOutputStream().write(("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" + timing +
                    "Connection: close\r\nContent-Length: ${text.toByteArray().size}\r\n\r\n$text").toByteArray())
            }
        }
        override fun close() {
            try { job.get(4, TimeUnit.SECONDS) } finally { socket.close(); worker.shutdownNow() }
        }
    }

    private fun result(request: JSONObject, decision: String, episodeId: Any? = null) =
        JSONObject().put("activationId", request.getString("activationId")).put("decision", decision)
            .put("episodeId", episodeId).toString()

    @Test fun grantIsPinnedToIdentityAndDeadlineAndOnlyScoresAreSent() {
        Coordinator { result(it, "granted") }.use { server ->
            val session = AtomicReference<NodeNativeSession.Session?>(server.session)
            val now = AtomicLong(1000)
            HandsFreeWakeElection(session::get, now::get, 3000, {true}, true).use { election ->
                assertEquals(HandsFreeWakeElection.Decision.GRANTED, election.claim(window, .75f))
                val sent = server.payload.get()
                assertEquals(setOf("activationId", "confidence", "snrDb"), sent.keys().asSequence().toSet())
                assertEquals(.75, sent.getDouble("confidence"), .00001)
                assertEquals(20.0, sent.getDouble("snrDb"), .00001)
                assertEquals(election.activationId, sent.getString("activationId"))
                assertEquals("0", server.requestHeaders.get()["x-aegis-detection-age-ms"])
                assertNull(election.coordinatorTiming) // Older servers omit the timing header.
                assertNull(election.episodeId) // Older servers omit the optional episode.
                assertTrue(election.canActivate())
                session.set(server.session.copy(credential = "replacement"))
                assertFalse(election.canActivate())
                session.set(server.session)
                now.set(3000)
                assertFalse(election.canActivate())
            }
        }
    }

    @Test fun onlyTrustedDecisionsProvideAnEpisodeForDatasetGrouping() {
        val episodeId = "e602008a-7283-43ae-97a0-ab3238c731c9"
        val foreignActivationId = "694b4db4-ff99-44a2-8a08-21bd2e326bc5"
        for ((expected, remote, group) in listOf(
            Triple(HandsFreeWakeElection.Decision.GRANTED, "granted", episodeId),
            Triple(HandsFreeWakeElection.Decision.SUPPRESSED, "suppressed", episodeId),
            Triple(HandsFreeWakeElection.Decision.GRANTED, "granted", "0-0-0-0-1"),
            Triple(HandsFreeWakeElection.Decision.SUPPRESSED, "suppressed", "00000000-0000-0000-0000-000000000000"),
            Triple(HandsFreeWakeElection.Decision.SUPPRESSED, "suppressed", JSONObject.NULL),
            Triple(HandsFreeWakeElection.Decision.UNAVAILABLE, "unknown", episodeId)
        )) {
            Coordinator { result(it, remote, group) }.use { server ->
                HandsFreeWakeElection({ server.session }, { 1000L }, 3000, { true }, false).use { election ->
                    assertEquals(expected, election.claim(window, .75f))
                    assertEquals(episodeId.takeIf { group == it && expected != HandsFreeWakeElection.Decision.UNAVAILABLE }, election.episodeId)
                }
            }
        }
        Coordinator { request ->
            JSONObject(result(request, "granted", episodeId)).put("activationId", foreignActivationId).toString()
        }.use { server ->
            HandsFreeWakeElection({ server.session }, { 1000L }, 3000, { true }, false).use { election ->
                assertEquals(HandsFreeWakeElection.Decision.UNAVAILABLE, election.claim(window, .75f))
                assertNull(election.episodeId)
            }
        }
    }

    @Test fun verifiedClaimCreditsOnlyElapsedMonotonicAgeAndSeparatesReportedServerTime() {
        val now = AtomicLong(1000)
        Coordinator(listOf("350")) { q ->
            now.set(1750)
            result(q, "granted")
        }.use { server ->
            HandsFreeWakeElection({server.session}, now::get, 2750, {true}, true).use { election ->
                assertEquals(HandsFreeWakeElection.Decision.GRANTED, election.claim(window, .75f))
                assertEquals("250", server.requestHeaders.get()["x-aegis-detection-age-ms"])
                assertEquals(setOf("activationId", "confidence", "snrDb"), server.payload.get().keys().asSequence().toSet())
                assertEquals(HandsFreeWakeElection.CoordinatorTiming(350, 400), election.coordinatorTiming)
                assertTrue(election.canActivate())
            }
        }
    }

    @Test fun missingInvalidOrImplausibleTimingDoesNotInventMetricsOrDelayValidGrant() {
        val cases = listOf(emptyList(), listOf("-1"), listOf("1.5"), listOf("x"), listOf("2001"),
            listOf("999999999999999999999"), listOf("751"), listOf("100", "101"))
        for (timing in cases) {
            val now = AtomicLong(1000)
            Coordinator(timing) { q ->
                now.set(1750)
                result(q, "granted")
            }.use { server ->
                HandsFreeWakeElection({server.session}, now::get, 2750, {true}, false).use { election ->
                    assertEquals(HandsFreeWakeElection.Decision.GRANTED, election.claim(window, .75f))
                    assertTrue(election.canActivate())
                    assertNull("invalid timing $timing", election.coordinatorTiming)
                }
            }
        }
    }

    @Test fun authenticationTimingIsOptionalBoundedAndIndependentOfGrant() {
        val cases = listOf(
            listOf("15") to HandsFreeWakeElection.CoordinatorTiming(0, 750, 15),
            listOf("15", "16") to HandsFreeWakeElection.CoordinatorTiming(0, 750),
            listOf("751") to HandsFreeWakeElection.CoordinatorTiming(0, 750),
            listOf("-5") to HandsFreeWakeElection.CoordinatorTiming(0, 750),
            emptyList<String>() to HandsFreeWakeElection.CoordinatorTiming(0, 750))
        for ((auth, expected) in cases) {
            val now = AtomicLong(1000)
            Coordinator(listOf("0"), auth) { q -> now.set(1750); result(q, "granted") }.use { server ->
                HandsFreeWakeElection({server.session}, now::get, 2750, {true}, true).use { election ->
                    assertEquals(HandsFreeWakeElection.Decision.GRANTED, election.claim(window, .75f))
                    assertEquals(expected, election.coordinatorTiming)
                    assertEquals(false, election.transportTiming?.reused)
                    assertNotNull(election.transportTiming?.connectMs)
                    assertNotNull(election.transportTiming?.requestToHeadersMs)
                    assertTrue(election.canActivate())
                }
            }
        }
    }

    @Test fun transportObservationReusesTheSamePoolWithoutCreatingIdleTraffic() {
        val socket = ServerSocket(0, 1, InetAddress.getByName("127.0.0.1"))
        val worker = Executors.newSingleThreadExecutor()
        val serverJob = worker.submit {
            socket.accept().use { client ->
                client.soTimeout = 4000
                val input = client.getInputStream().bufferedReader(Charsets.UTF_8)
                repeat(2) { index ->
                    assertEquals("POST /api/nodes/me/voice-activations/claim HTTP/1.1", input.readLine())
                    val headers = generateSequence { input.readLine()?.takeIf { it.isNotEmpty() } }.associate {
                        it.substringBefore(':').lowercase() to it.substringAfter(':').trim()
                    }
                    val chars = CharArray(headers.getValue("content-length").toInt())
                    var read = 0
                    while (read < chars.size) {
                        val n = input.read(chars, read, chars.size-read); check(n > 0); read += n
                    }
                    val text = result(JSONObject(String(chars)), "granted")
                    client.getOutputStream().write(("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                        "Connection: ${if(index == 0) "keep-alive" else "close"}\r\n" +
                        "Content-Length: ${text.toByteArray().size}\r\n\r\n$text").toByteArray())
                }
            }
        }
        try {
            val session = NodeNativeSession.Session("http://127.0.0.1:${socket.localPort}", "node", "fixture")
            repeat(2) { index ->
                HandsFreeWakeElection({session}, {1000L}, 3000, {true}, false).use { election ->
                    assertEquals(HandsFreeWakeElection.Decision.GRANTED, election.claim(window, .75f))
                    assertEquals(index == 1, election.transportTiming?.reused)
                }
            }
            serverJob.get(4, TimeUnit.SECONDS)
        } finally { socket.close(); worker.shutdownNow() }
    }

    @Test fun loserMalformedAndForeignDecisionsStaySilent() {
        val cases = listOf<Pair<(JSONObject) -> String, HandsFreeWakeElection.Decision>>(
            { q: JSONObject -> result(q, "suppressed") } to HandsFreeWakeElection.Decision.SUPPRESSED,
            { _: JSONObject -> """{"activationId":"other","decision":"granted"}""" } to HandsFreeWakeElection.Decision.UNAVAILABLE,
            { _: JSONObject -> "invalid" } to HandsFreeWakeElection.Decision.UNAVAILABLE
        )
        for ((response, expected) in cases) {
            val now = AtomicLong(1000)
            Coordinator(listOf("25")) { q -> now.set(1100); response(q) }.use { server ->
                HandsFreeWakeElection({server.session}, now::get, 3000, {true}, false).use { election ->
                    assertEquals(expected, election.claim(window, 1f))
                    assertFalse(server.payload.get().has("confidence"))
                    assertFalse(election.canActivate())
                    assertNull(election.coordinatorTiming)
                }
            }
        }
    }

    @Test fun unpairedOrExpiredCandidateCannotOpenAnElection() {
        val session = NodeNativeSession.Session("http://127.0.0.1:1", "node", "fixture")
        val expired = HandsFreeWakeElection({session}, {3000}, 3000, {true}, true)
        val unpaired = HandsFreeWakeElection({null}, {1000}, 3000, {true}, true)
        for (claim in listOf(expired, unpaired)) claim.use {
            assertEquals(HandsFreeWakeElection.Decision.UNAVAILABLE, it.claim(window, .75f))
            assertFalse(it.canActivate())
        }
    }

    @Test fun grantedPrimaryOnlyCandidateRetainsSpeechFromOriginalBoundary() {
        val entered = CountDownLatch(1)
        val finish = CountDownLatch(1)
        val executor = Executors.newSingleThreadExecutor()
        try {
            Coordinator { q ->
                entered.countDown()
                assertTrue(finish.await(2, TimeUnit.SECONDS))
                result(q, "granted")
            }.use { server ->
                val election = HandsFreeWakeElection({server.session}, {1000}, 2900, {true}, false)
                val ring = HandsFreeRing(48000).apply { append(samples, samples.size) }
                val candidate = HandsFreeWakeCandidate(24000, 0, floatArrayOf())
                HandsFreePendingWakeVerification(candidate, ring, 24400, executor, 1000, {1000L},
                    { _, _ -> 1f }, threshold=1f, election=election, withLookahead=false).use { pending ->
                    assertTrue(entered.await(2, TimeUnit.SECONDS))
                    assertTrue(pending.append(shortArrayOf(101,102), 2))
                    finish.countDown()
                    executor.submit {}.get(2, TimeUnit.SECONDS)
                    assertEquals(HandsFreePendingWakeVerification.Status.ACCEPTED, pending.poll(1200).status)
                    val command = ShortArray(402)
                    assertEquals(402, pending.copyCommandTo(command))
                    assertArrayEquals(samples.copyOfRange(24000,24400) + shortArrayOf(101,102), command)
                }
            }
        } finally { finish.countDown(); executor.shutdownNow() }
    }

    @Test fun pendingElectionKeepsReadingAndRejectsGrantTooLateForAudioThread() {
        val entered = CountDownLatch(1)
        val finish = CountDownLatch(1)
        val now = AtomicLong(1000)
        val executor = Executors.newSingleThreadExecutor()
        try {
            Coordinator { q ->
                entered.countDown()
                assertTrue(finish.await(2, TimeUnit.SECONDS))
                result(q, "granted")
            }.use { server ->
                val election = HandsFreeWakeElection({server.session}, now::get, 2500, {true}, true)
                val ring = HandsFreeRing(48000).apply { append(samples, samples.size) }
                val candidate = HandsFreeWakeCandidate(24000, 0, floatArrayOf())
                HandsFreePendingWakeVerification(candidate, ring, 24400, executor, 1000, now::get,
                    { _, _ -> .8f }, election=election, withLookahead=false).use { pending ->
                    assertTrue(entered.await(2, TimeUnit.SECONDS))
                    assertEquals(HandsFreePendingWakeVerification.Status.WAITING, pending.poll(now.get()).status)
                    assertTrue(pending.append(shortArrayOf(101,102), 2))
                    finish.countDown()
                    executor.submit {}.get(2, TimeUnit.SECONDS)
                    now.set(2500)
                    assertEquals(HandsFreePendingWakeVerification.Status.COORDINATOR_UNAVAILABLE, pending.poll(now.get()).status)
                }
            }
        } finally { finish.countDown(); executor.shutdownNow() }
    }
}
