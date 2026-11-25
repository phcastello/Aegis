package com.aegis.node

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.atomic.AtomicInteger

class NodeHandsFreeBatteryAlarmTest {
    private val token = HandsFreeBatteryAutomation.Token("seq-1", 0, "run-1", 1801000)
    private class Backend: HandsFreeBatteryAlarmBackend {
        var permitted = true
        var checkFailure = false
        var exactFailure = false
        var inexactFailure = false
        var checks = 0
        var exactCalls = 0
        var inexactCalls = 0
        val scheduled = HashMap<String, String>()
        override fun canScheduleExactAlarms(): Boolean {
            checks++
            if (checkFailure) throw SecurityException("denied")
            return permitted
        }
        override fun setExact(token: HandsFreeBatteryAutomation.Token) {
            exactCalls++
            if (exactFailure) throw SecurityException("revoked")
            scheduled[HandsFreeBatteryAlarmIntents.data(token)] = "exact"
        }
        override fun setInexact(token: HandsFreeBatteryAutomation.Token) {
            inexactCalls++
            if (inexactFailure) throw SecurityException("denied")
            scheduled[HandsFreeBatteryAlarmIntents.data(token)] = "inexact"
        }
        override fun cancel(token: HandsFreeBatteryAutomation.Token) {
            scheduled.remove(HandsFreeBatteryAlarmIntents.data(token))
        }
    }

    @Test fun oldApiUsesExactWithoutCallingTheNewPermissionApi() {
        val backend = Backend().apply { checkFailure = true }
        assertEquals("exact", HandsFreeBatteryAlarm(24, backend).schedule(token))
        assertEquals(0, backend.checks)
        assertEquals(1, backend.exactCalls)
        assertEquals(0, backend.inexactCalls)
    }
    @Test fun permissionChoosesExactOrInexactOnApi31AndLater() {
        for (sdk in listOf(31, 33, 35, 36, 37)) {
            val backend = Backend()
            val alarms = HandsFreeBatteryAlarm(sdk, backend)
            assertEquals("exact", alarms.schedule(token))
            backend.permitted = false
            assertEquals("inexact", alarms.schedule(token))
            assertEquals(2, backend.checks)
            assertEquals(1, backend.exactCalls)
            assertEquals(1, backend.inexactCalls)
        }
    }
    @Test fun revokedPermissionFallsBackToInexactUsingTheSameToken() {
        val backend = Backend().apply { exactFailure = true }
        assertEquals("inexact", HandsFreeBatteryAlarm(33, backend).schedule(token))
        assertEquals(1, backend.exactCalls)
        assertEquals(1, backend.inexactCalls)
        assertEquals(mapOf(HandsFreeBatteryAlarmIntents.data(token) to "inexact"), backend.scheduled)
        backend.checkFailure = true
        assertEquals("inexact", HandsFreeBatteryAlarm(35, backend).schedule(token))
        assertEquals(1, backend.exactCalls)
    }
    @Test fun failureOfInexactSchedulingIsReportedToTheSupervisor() {
        val backend = Backend().apply { permitted = false; inexactFailure = true }
        assertThrows(SecurityException::class.java) { HandsFreeBatteryAlarm(33, backend).schedule(token) }
        assertTrue(backend.scheduled.isEmpty())
    }
    @Test fun repeatedTokenReplacesItsAlarmAndCancellationDoesNotTouchAnotherBoundary() {
        val backend = Backend()
        val alarms = HandsFreeBatteryAlarm(33, backend)
        val next = token.copy(stageIndex = 1, runId = "run-2", deadlineElapsedMs = 3601000)
        alarms.schedule(token); alarms.schedule(token)
        assertEquals(1, backend.scheduled.size)
        alarms.schedule(next)
        assertEquals(2, backend.scheduled.size)
        alarms.cancel(token); alarms.cancel(token)
        assertEquals(setOf(HandsFreeBatteryAlarmIntents.data(next)), backend.scheduled.keys)
    }
    @Test fun canonicalDataRoundTripsEveryIdentityFieldAndAllowsBoundedIds() {
        for (stage in 0..2) {
            val original = HandsFreeBatteryAutomation.Token("a".repeat(128), stage, "Z_9-".repeat(32), Long.MAX_VALUE)
            val data = HandsFreeBatteryAlarmIntents.data(original)
            assertEquals(original, HandsFreeBatteryAlarmIntents.parse(HandsFreeBatteryAlarmIntents.ACTION, data))
        }
        val changed = listOf(token.copy(sequenceId = "seq-2"), token.copy(stageIndex = 1),
            token.copy(runId = "run-2"), token.copy(deadlineElapsedMs = token.deadlineElapsedMs + 1))
        assertEquals(5, (changed + token).map(HandsFreeBatteryAlarmIntents::data).toSet().size)
    }
    @Test fun malformedOrNonCanonicalDataCannotBecomeATransition() {
        val good = HandsFreeBatteryAlarmIntents.data(token)
        assertNull(HandsFreeBatteryAlarmIntents.parse(null, good))
        assertNull(HandsFreeBatteryAlarmIntents.parse("other", good))
        for (bad in listOf<String?>(null, "", good + "?extra=1", good + "#fragment", good + "/",
            good.replace("transition", "other"), good.replace("seq-1", "a".repeat(129)),
            good.replace("seq-1", "%73eq-1"), good.replace("seq-1", "../seq-1"),
            good.replace("/0/", "/3/"), good.replace("/0/", "/00/"), good.replace("/0/", "/+0/"),
            good.substringBeforeLast('/') + "/0", good.substringBeforeLast('/') + "/-1",
            good.substringBeforeLast('/') + "/01801000", good.substringBeforeLast('/') + "/9223372036854775808",
            good.replace("transition", "user@transition"), good.replace("transition", "transition:123"),
            good.replace("aegis-battery-study", "other"), "x".repeat(513))) {
            assertNull(bad, HandsFreeBatteryAlarmIntents.parse(HandsFreeBatteryAlarmIntents.ACTION, bad))
        }
    }
    @Test fun callbackAndTimeoutFinishAndReleaseOnlyOnce() {
        var cancelled = 0; var released = 0; var finished = 0
        val completion = HandsFreeBatteryAlarmCompletion({ cancelled++ }, { released++ }, { finished++ })
        completion.run(); completion.run(); completion.run()
        assertEquals(1, cancelled); assertEquals(1, released); assertEquals(1, finished)
        assertEquals(10000L, NodeHandsFreeBatteryAlarmReceiver.TRANSITION_TIMEOUT_MS)
    }
    @Test fun cleanupStillFinishesTheBroadcastWhenLeaseCleanupFails() {
        var finished = 0
        val completion = HandsFreeBatteryAlarmCompletion({ throw IllegalStateException() },
            { throw SecurityException() }, { finished++ })
        completion.run(); completion.run()
        assertEquals(1, finished)
    }
    @Test fun concurrentTimeoutAndWorkerCompletionCannotDoubleRelease() {
        val started = CountDownLatch(1)
        val cancelled = AtomicInteger(); val released = AtomicInteger(); val finished = AtomicInteger()
        val completion = HandsFreeBatteryAlarmCompletion({ cancelled.incrementAndGet() },
            { released.incrementAndGet() }, { finished.incrementAndGet() })
        val threads = List(8) { Thread { started.await(); completion.run() }.apply { start() } }
        started.countDown(); threads.forEach { it.join(1000); assertFalse(it.isAlive) }
        assertEquals(1, cancelled.get()); assertEquals(1, released.get()); assertEquals(1, finished.get())
    }
}
