package com.aegis.node

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

class NodeHandsFreeContinuityTest {
    private class Fixture {
        var time = 1000L
        val continuity = HandsFreeConversationContinuity { time }
        fun response(id: String = "chat-A"): HandsFreeConversationContinuity.Activation {
            val activation = continuity.activate("identity-A")
            assertTrue(continuity.playbackCompleted(activation, id, "identity-A"))
            continuity.end(activation)
            return activation
        }
    }

    @Test fun wakeWithinTenSecondsReusesOnlyTheHandsFreeConversation() {
        val f = Fixture(); assertNull(f.continuity.activate("identity-A").conversationId)
        f.response(); f.time += 9999
        assertEquals("chat-A", f.continuity.activate("identity-A").conversationId)
    }

    @Test fun exactTenSecondsAndLaterHaveExpiredWithoutDeletingAnyConversation() {
        for (elapsed in listOf(10000L, 10001L, 60000L)) {
            val f = Fixture(); f.response(); f.time += elapsed
            assertNull(f.continuity.activate("identity-A").conversationId)
        }
    }

    @Test fun anAcceptedActivationPinsItsDestinationBeforeTheNextBlockingCaptureRead() {
        val f = Fixture(); f.response(); f.time += 9999
        val acceptedAt = f.time
        f.time += 200 // Authority verification/capture handoff must not change the acceptance time.
        val activation = f.continuity.activate("identity-A", acceptedAt)
        assertEquals("chat-A", activation.conversationId)
        f.time += 20000
        assertTrue(f.continuity.playbackCompleted(activation, "chat-A", "identity-A"))
    }

    @Test fun eachCompletedReplyRenewsTheEntireWindowForAnUnlimitedSequence() {
        val f = Fixture(); f.response()
        repeat(50) {
            f.time += 9000
            val activation = f.continuity.activate("identity-A")
            assertEquals("chat-A", activation.conversationId)
            f.time += 15000 // Responses and generation may exceed the original deadline.
            assertTrue(f.continuity.playbackCompleted(activation, "chat-A", "identity-A"))
            f.continuity.end(activation)
        }
        f.time += 9999
        assertEquals("chat-A", f.continuity.activate("identity-A").conversationId)
    }

    @Test fun longInitialResponseDoesNotStartTheWindowUntilPlaybackHasFinished() {
        val f = Fixture(); val activation = f.continuity.activate("identity-A")
        f.time += 45000
        assertTrue(f.continuity.playbackCompleted(activation, "chat-A", "identity-A"))
        f.continuity.end(activation); f.time += 7000
        assertEquals("chat-A", f.continuity.activate("identity-A").conversationId)
    }

    @Test fun automaticContinuationKeepsItsSessionAndRenewsBeforeTheNextWakeCall() {
        val f = Fixture(); val session = f.continuity.activate("identity-A")
        assertTrue(f.continuity.playbackCompleted(session, "chat-A", "identity-A"))
        f.time += 1500 // Accepted onset in the existing automatic continuation gate.
        f.time += 20000
        assertTrue(f.continuity.playbackCompleted(session, "chat-A", "identity-A"))
        f.continuity.end(session); f.time += 9000
        assertEquals("chat-A", f.continuity.activate("identity-A").conversationId)
    }

    @Test fun automaticContinuationTimeoutDoesNotRenewTheLastCompletedResponse() {
        val f = Fixture(); val session = f.continuity.activate("identity-A")
        assertTrue(f.continuity.playbackCompleted(session, "chat-A", "identity-A"))
        f.time += 2000; f.continuity.end(session); f.time += 8000
        assertNull(f.continuity.activate("identity-A").conversationId)
    }

    @Test fun cancellationDuringGenerationOrPlaybackAndTtsErrorsCannotRenewEligibility() {
        for (failure in listOf("generation_cancelled", "playback_cancelled", "tts_error")) {
            val f = Fixture(); f.response(); f.time += 7000
            val attempt = f.continuity.activate("identity-A")
            assertEquals(failure, "chat-A", attempt.conversationId)
            f.time += 2000; f.continuity.end(attempt)
            assertFalse(f.continuity.playbackCompleted(attempt, "chat-A", "identity-A"))
            f.time += 1000
            assertNull(f.continuity.activate("identity-A").conversationId)
        }
    }

    @Test fun falseWakeSttFailureAndEmptyCommandsDoNotCreateOrExtendEligibility() {
        for (failure in listOf("false_wake", "stt_error", "empty_command")) {
            val f = Fixture(); val empty = f.continuity.activate("identity-A")
            f.continuity.end(empty)
            assertNull(failure, f.continuity.activate("identity-A").conversationId)
            f.response(); f.time += 3000
            val attempt = f.continuity.activate("identity-A")
            f.continuity.end(attempt); f.time += 6999
            assertEquals(failure, "chat-A", f.continuity.activate("identity-A").conversationId)
            f.time += 1
            assertNull(failure, f.continuity.activate("identity-A").conversationId)
        }
    }

    @Test fun identityChangeCredentialRotationAndRevocationInvalidateEvenIfIdentityReturns() {
        for (next in listOf("identity-B", "identity-A-rotated-credential", null)) {
            val f = Fixture(); val old = f.response()
            f.continuity.identityChanged(next)
            assertFalse(f.continuity.playbackCompleted(old, "chat-A", "identity-A"))
            assertNull(f.continuity.activate("identity-A").conversationId)
        }
    }

    @Test fun changedIdentityAtPlaybackCompletionCannotAuthorizeTheOldConversation() {
        val f = Fixture(); val old = f.continuity.activate("identity-A")
        assertFalse(f.continuity.playbackCompleted(old, "chat-A", "identity-B"))
        assertNull(f.continuity.activate("identity-B").conversationId)
    }

    @Test fun processRestartAndExplicitStopHaveNoReusableState() {
        val f = Fixture(); f.response()
        assertNull(HandsFreeConversationContinuity { f.time }.activate("identity-A").conversationId)
        f.continuity.invalidate()
        assertNull(f.continuity.activate("identity-A").conversationId)
    }

    @Test fun anOldCompletionCannotReplaceTheConversationOrDeadlineOfANewerSession() {
        val f = Fixture(); val old = f.continuity.activate("identity-A")
        val next = f.continuity.activate("identity-A")
        assertTrue(f.continuity.playbackCompleted(next, "chat-B", "identity-A"))
        f.time += 9000
        assertFalse(f.continuity.playbackCompleted(old, "chat-A", "identity-A"))
        f.continuity.end(old); f.continuity.end(next); f.time += 1000
        assertNull(f.continuity.activate("identity-A").conversationId)
    }

    @Test fun delayedConcurrentCompletionCannotRenewAfterCancellationAndANewActivation() {
        val f = Fixture(); val lock = Any(); val ready = CountDownLatch(1); val proceed = CountDownLatch(1)
        val old = f.continuity.activate("identity-A")
        val executor = Executors.newSingleThreadExecutor()
        try {
            val late = executor.submit<Boolean> {
                ready.countDown(); check(proceed.await(5, TimeUnit.SECONDS))
                synchronized(lock) { f.continuity.playbackCompleted(old, "chat-A", "identity-A") }
            }
            assertTrue(ready.await(5, TimeUnit.SECONDS))
            synchronized(lock) {
                f.continuity.end(old)
                val next = f.continuity.activate("identity-A")
                assertTrue(f.continuity.playbackCompleted(next, "chat-B", "identity-A"))
                f.continuity.end(next)
            }
            proceed.countDown(); assertFalse(late.get(5, TimeUnit.SECONDS))
            f.time += 1000
            assertEquals("chat-B", f.continuity.activate("identity-A").conversationId)
        } finally { proceed.countDown(); executor.shutdownNow() }
    }

    @Test fun actualPcmDrainStartsTheWindowEvenWhenHttpAndBookkeepingFinishAtOtherTimes() {
        val f = Fixture(); val activation = f.continuity.activate("identity-A")
        val armed = CountDownLatch(1); val played = AtomicLong(0)
        val marker = AtomicReference<(() -> Unit)?>(); val drainedAt = AtomicReference<Long?>()
        val device = object : HandsFreePcmDevice {
            override fun write(bytes: ByteArray, offset: Int, count: Int) = count
            override fun playedFrames() = played.get()
            override fun endMarker(frame: Int, reached: () -> Unit) { marker.set(reached); armed.countDown() }
            override fun play() = Unit
            override fun stop() = Unit
            override fun close() = Unit
        }
        val sink = HandsFreeStreamingPcmSink({ device }, onPlaybackEvent = {
            if (it == "player_drained") drainedAt.set(f.time)
        })
        val executor = Executors.newSingleThreadExecutor()
        try {
            sink.write(ByteArray(48), 0, 48) // HTTP has delivered every byte; device still buffered.
            val finishing = executor.submit { sink.finish() }
            assertTrue(armed.await(5, TimeUnit.SECONDS)); assertNull(drainedAt.get())
            f.time += 30000 // Generation/queued playback exceeds ten seconds, without eligibility.
            played.set(24); checkNotNull(marker.get()).invoke(); finishing.get(5, TimeUnit.SECONDS)
            assertEquals(f.time, drainedAt.get())
            f.time += 3000 // AudioTrack release and other bookkeeping cannot renew the anchor.
            assertTrue(f.continuity.playbackCompleted(activation, "chat-A", "identity-A", checkNotNull(drainedAt.get())))
            f.continuity.end(activation); f.time += 6999
            assertEquals("chat-A", f.continuity.activate("identity-A").conversationId)
            f.time += 1
            assertNull(f.continuity.activate("identity-A").conversationId)
        } finally { sink.abort(); sink.close(); executor.shutdownNow() }
    }

    @Test fun cancellingBufferedPlaybackCannotPublishADrainOrRenewThePreviousWindow() {
        val f = Fixture(); f.response(); f.time += 9000
        val activation = f.continuity.activate("identity-A")
        val armed = CountDownLatch(1); val drainedAt = AtomicReference<Long?>()
        val device = object : HandsFreePcmDevice {
            override fun write(bytes: ByteArray, offset: Int, count: Int) = count
            override fun playedFrames() = 0L
            override fun endMarker(frame: Int, reached: () -> Unit) { armed.countDown() }
            override fun play() = Unit
            override fun stop() = Unit
            override fun close() = Unit
        }
        val sink = HandsFreeStreamingPcmSink({ device }, onPlaybackEvent = {
            if (it == "player_drained") drainedAt.set(f.time)
        })
        val executor = Executors.newSingleThreadExecutor()
        try {
            sink.write(ByteArray(48), 0, 48)
            val finishing = executor.submit { sink.finish() }
            assertTrue(armed.await(5, TimeUnit.SECONDS)); f.continuity.end(activation); sink.abort()
            val error = assertThrows(java.util.concurrent.ExecutionException::class.java) { finishing.get(5, TimeUnit.SECONDS) }
            assertTrue(error.cause is java.util.concurrent.CancellationException)
            assertNull(drainedAt.get()); f.time += 1000
            assertNull(f.continuity.activate("identity-A").conversationId)
        } finally { sink.abort(); sink.close(); executor.shutdownNow() }
    }
}
