package com.aegis.node

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

class NodeHandsFreeCaptureStartupTest {
    private class Clock(var now:Long=1000L) {
        var broken=false
        fun read():Long {if(broken)throw IllegalStateException("clock");return now}
        fun startup()=HandsFreeCaptureStartup("detection",7,11,10000,::read)
    }
    @Test fun firstPcmReadyDoesNotMeasureUntilExplicitPublication() {
        val clock=Clock();val startup=clock.startup()
        assertEquals("pending",startup.state());assertFalse(startup.shouldMeasure())
        clock.now=1200
        assertTrue(startup.signalReady(12345));assertFalse(startup.signalReady(99999))
        assertTrue(startup.awaitReady());assertFalse(startup.shouldMeasure());assertNull(startup.measurementRunId())
        assertEquals(1200L,startup.readyWallMs());assertEquals(12345L,startup.readyThreadCpuNanos());assertEquals(200L,startup.preparationMs())
        assertTrue(startup.markMeasuring("run-1"));assertTrue(startup.markMeasuring("run-1"))
        assertFalse(startup.markMeasuring("another-run"));assertEquals("run-1",startup.measurementRunId())
        clock.now=80000;clock.broken=true
        assertTrue(startup.shouldMeasure());assertTrue(startup.awaitReady()) // No startup clock/wait after handoff.
    }
    @Test fun originalEightySecondSleepCannotBecomeAMeasuredPhase() {
        val clock=Clock();val startup=clock.startup();clock.now=81000
        assertFalse(startup.signalReady(100));assertFalse(startup.awaitReady())
        assertFalse(startup.markMeasuring("run-1"));assertNull(startup.measurementRunId())
        assertEquals("failed",startup.state());assertEquals("startup_timeout",startup.failure())
    }
    @Test fun readinessBeforeTimeoutDoesNotPermitLatePublication() {
        val clock=Clock();val startup=clock.startup();clock.now=1200;assertTrue(startup.signalReady(-1))
        assertNull(startup.readyThreadCpuNanos())
        clock.now=10000
        assertFalse(startup.markMeasuring("run-1"));assertFalse(startup.shouldMeasure())
        assertNull(startup.measurementRunId());assertEquals("startup_timeout",startup.failure())
    }
    @Test fun expiredAndInvalidClockConstructionCannotWait() {
        val expired=HandsFreeCaptureStartup("capture_only",1,1,1000,{1000})
        assertFalse(expired.awaitReady());assertEquals("startup_timeout",expired.failure())
        val invalid=HandsFreeCaptureStartup("capture_only",1,1,1000,{-1})
        assertFalse(invalid.awaitReady());assertEquals("clock_invalid",invalid.failure())
        assertThrows(IllegalArgumentException::class.java){HandsFreeCaptureStartup("off",1,1,1000,{1})}
    }
    @Test fun cancellationAndFailureWakeCoordinatorWithoutWaitingForDeadline() {
        for(cancel in listOf(true,false)) {
            val clock=Clock();val startup=clock.startup();val entered=CountDownLatch(1)
            val executor=Executors.newSingleThreadExecutor()
            try {
                val result=executor.submit<Boolean>{entered.countDown();startup.awaitReady()}
                assertTrue(entered.await(1,TimeUnit.SECONDS))
                if(cancel)startup.cancel("generation_changed") else startup.fail("microphone_unavailable")
                assertFalse(result.get(1,TimeUnit.SECONDS))
                assertFalse(startup.signalReady(10));assertFalse(startup.markMeasuring("run-1"))
                assertEquals(if(cancel)"cancelled" else "failed",startup.state())
                assertEquals(if(cancel)"generation_changed" else "microphone_unavailable",startup.failure())
            }finally{executor.shutdownNow()}
        }
    }
    @Test fun cancellationAfterReadyInvalidatesAnyLaterAcknowledgement() {
        val clock=Clock();val startup=clock.startup();assertTrue(startup.signalReady(10))
        startup.cancel("cancelled");assertFalse(startup.awaitReady());assertFalse(startup.markMeasuring("run-1"))
        assertFalse(startup.shouldMeasure());assertNull(startup.measurementRunId())
        startup.fail("later_failure");assertEquals("cancelled",startup.failure())
    }
    @Test fun firstConcurrentReadinessWinsAndContextRemainsImmutable() {
        val clock=Clock();val startup=clock.startup();val winners=AtomicInteger()
        val executor=Executors.newFixedThreadPool(4)
        try {
            val tasks=(0 until 32).map{n->executor.submit{if(startup.signalReady(n.toLong()))winners.incrementAndGet()}}
            tasks.forEach{it.get(2,TimeUnit.SECONDS)}
            assertEquals(1,winners.get());assertTrue(startup.awaitReady())
            assertEquals("detection",startup.mode);assertEquals(7,startup.controlGeneration);assertEquals(11,startup.audioEpoch)
            assertEquals(10000L,startup.deadlineElapsedMs)
        }finally{executor.shutdownNow()}
    }
    @Test fun backwardsOrUnavailableClockFailsBeforeMeasurement() {
        for(broken in listOf(false,true)) {
            val clock=Clock();val startup=clock.startup()
            if(broken)clock.broken=true else clock.now=999
            assertFalse(startup.shouldMeasure());assertFalse(startup.awaitReady());assertEquals("clock_invalid",startup.failure())
        }
    }
    @Test fun explicitStopCanEndAnAlreadyMeasuringTicket() {
        val clock=Clock();val startup=clock.startup();startup.signalReady(10);startup.markMeasuring("run-1")
        startup.cancel("phase_ended")
        assertFalse(startup.shouldMeasure());assertNull(startup.measurementRunId());assertEquals("phase_ended",startup.failure())
    }
}
