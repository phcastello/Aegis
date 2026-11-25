package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

class NodeHandsFreeMediaFocusTest {
    private class Lease : AutoCloseable {
        var closes = 0
        override fun close() { closes++ }
    }

    @Test fun conversationKeepsOneFocusLeaseAcrossCueResponsesAndContinuation() {
        val lease = Lease()
        val outcomes = mutableListOf<String>()
        var requests = 0
        val focus = HandsFreeSessionMediaFocus(request = { requests++; lease },
            onLoss = { fail("unexpected loss") }, observe = { outcomes += it })
        assertEquals(HandsFreeSessionFocusResult.ACQUIRED, focus.acquire("conversation"))
        repeat(4) {
            assertEquals(HandsFreeSessionFocusResult.ACQUIRED, focus.acquire("conversation", retryUnavailable = true))
            assertEquals(0, lease.closes) // End of a response does not release the conversation.
        }
        assertEquals(1, requests)
        focus.release("old")
        assertEquals(0, lease.closes)
        focus.release("conversation")
        focus.release("conversation")
        focus.releaseAll()
        assertEquals(1, lease.closes)
        assertEquals(listOf("acquired", "released"), outcomes)
    }

    @Test fun firstDeniedRequestCanRetryOnceBeforePlayback() {
        val lease = Lease()
        val outcomes = mutableListOf<String>()
        var requests = 0
        val focus = HandsFreeSessionMediaFocus(request = { if (++requests == 1) null else lease },
            onLoss = { fail("unexpected loss") }, observe = { outcomes += it })
        assertEquals(HandsFreeSessionFocusResult.UNAVAILABLE, focus.acquire("conversation"))
        assertEquals(HandsFreeSessionFocusResult.UNAVAILABLE, focus.acquire("conversation"))
        assertEquals(1, requests)
        assertEquals(HandsFreeSessionFocusResult.ACQUIRED, focus.acquire("conversation", retryUnavailable = true))
        assertEquals(HandsFreeSessionFocusResult.ACQUIRED, focus.acquire("conversation", retryUnavailable = true))
        focus.release("conversation")
        assertEquals(2, requests)
        assertEquals(1, lease.closes)
        assertEquals(listOf("denied", "acquired", "released"), outcomes)
    }

    @Test fun failuresAreNonfatalAndBoundedWithoutLatentReacquisition() {
        var requests = 0
        val outcomes = mutableListOf<String>()
        val focus = HandsFreeSessionMediaFocus(request = { requests++; throw IllegalStateException("platform request failed") },
            onLoss = { fail("unexpected loss") }, observe = { outcomes += it })
        repeat(6) {
            assertEquals(HandsFreeSessionFocusResult.UNAVAILABLE, focus.acquire("conversation", retryUnavailable = it > 0))
        }
        focus.releaseAll()
        assertEquals(2, requests)
        assertEquals(listOf("failed", "failed"), outcomes)
    }

    @Test fun preemptedFocusCancelsOnlyItsOwnerAndDoesNotReacquire() {
        val losses = mutableListOf<String>()
        val callbacks = mutableListOf<() -> Unit>()
        val leases = mutableListOf<Lease>()
        val focus = HandsFreeSessionMediaFocus(
            request = { onLoss -> callbacks += onLoss; Lease().also { leases += it } },
            onLoss = { losses += it })
        focus.acquire("old")
        focus.acquire("new")
        assertEquals(1, leases[0].closes)
        callbacks[0]()
        assertTrue(losses.isEmpty())
        callbacks[1]()
        callbacks[1]()
        assertEquals(listOf("new"), losses)
        assertEquals(HandsFreeSessionFocusResult.LOST, focus.acquire("new", retryUnavailable = true))
        assertEquals(2, leases.size)
        focus.release("old")
        assertEquals(0, leases[1].closes)
        focus.release("new")
        assertEquals(1, leases[1].closes)
        callbacks[1]()
        assertEquals(listOf("new"), losses)
    }

    @Test fun focusLossDuringRequestDiscardsTheLateGrant() {
        val lease = Lease()
        val losses = mutableListOf<String>()
        val focus = HandsFreeSessionMediaFocus(
            request = { onLoss -> onLoss(); lease }, onLoss = { losses += it })
        assertEquals(HandsFreeSessionFocusResult.LOST, focus.acquire("conversation"))
        assertEquals(listOf("conversation"), losses)
        assertEquals(1, lease.closes)
        focus.releaseAll()
        assertEquals(1, lease.closes)
    }

    @Test fun anOldDeniedRequestsLateCallbackCannotCancelARetry() {
        val callbacks = mutableListOf<() -> Unit>()
        val losses = mutableListOf<String>()
        val lease = Lease()
        val focus = HandsFreeSessionMediaFocus(
            request = { callback -> callbacks += callback; if (callbacks.size == 1) null else lease },
            onLoss = { losses += it })
        focus.acquire("conversation")
        callbacks[0]()
        assertTrue(losses.isEmpty())
        focus.acquire("conversation", retryUnavailable = true)
        callbacks[0]()
        assertTrue(losses.isEmpty())
        callbacks[1]()
        assertEquals(listOf("conversation"), losses)
        focus.releaseAll()
        assertEquals(1, lease.closes)
    }

    @Test fun cancellationWaitsForNativeRouteThenReleasesExactlyOnce() {
        val releases = mutableListOf<String>()
        val timers = mutableListOf<() -> Unit>()
        val release = HandsFreeMediaFocusRelease({ releases += it }, { releases += "all" }, { timers += it })
        release.routePreparing("old")
        release.releaseWhenSafe("old")
        release.releaseWhenSafe("old")
        release.releaseWhenSafe(null) // A concurrent stop after sessionId was cleared.
        assertTrue(releases.isEmpty())
        assertEquals(1, timers.size)
        release.routeClosed("old")
        assertEquals(listOf("old"), releases)
        timers.single()()
        assertEquals(listOf("old"), releases)
    }

    @Test fun cancellationWithoutNativeRouteReleasesImmediatelyAndTimeoutIsTokenScoped() {
        val releases = mutableListOf<String>()
        val timers = mutableListOf<() -> Unit>()
        val release = HandsFreeMediaFocusRelease({ releases += it }, { releases += "all" }, { timers += it })
        release.releaseWhenSafe("processing")
        release.releaseWhenSafe(null)
        assertEquals(listOf("processing", "all"), releases)
        assertTrue(timers.isEmpty())

        release.routePreparing("old-route")
        release.releaseWhenSafe("old-route")
        // A blocked worker cannot retain the focus indefinitely.
        timers.single()()
        assertEquals(listOf("processing", "all", "old-route"), releases)
        release.routeClosed("old-route")
        release.routePreparing("new-route")
        release.releaseWhenSafe("new-route")
        timers[0]()
        assertEquals(listOf("processing", "all", "old-route"), releases)
        release.routeClosed("new-route")
        assertEquals(listOf("processing", "all", "old-route", "new-route"), releases)
    }

    private class Device : HandsFreePcmDevice {
        private var frames = 0L
        override fun write(bytes: ByteArray, offset: Int, count: Int): Int { frames += count / 2; return count }
        override fun playedFrames() = frames
        override fun endMarker(frame: Int, reached: () -> Unit) { reached() }
        override fun play() = Unit
        override fun stop() = Unit
        override fun close() = Unit
    }

    @Test fun bothNativeAndSoftwareRoutesBorrowFocusAndManualPlaybackDoesNot() {
        val requests = mutableListOf<Pair<Boolean, Boolean>>()
        var sessionChecks = 0
        val playback = HandsFreePlayback { nativeCapture: Boolean, borrowedFocus: Boolean, _: () -> Unit ->
            requests += nativeCapture to borrowedFocus
            if (nativeCapture) throw IllegalStateException("simulate native route failure")
            Device()
        }
        val first = playback.openStream(stillCurrent = { true }, onPlaybackEvent = {},
            prepareCommunication = { true }, sessionFocus = { sessionChecks++; true })
        first.write(byteArrayOf(1, 2), 0, 2)
        first.finish()
        first.close()
        assertEquals(1, sessionChecks)
        assertEquals(listOf(true to true, false to true), requests)

        val manual = playback.openStream { true }
        manual.write(byteArrayOf(1, 2), 0, 2)
        manual.finish()
        manual.close()
        assertEquals(1, sessionChecks)
        assertEquals(false to false, requests.last())

        val denied = playback.openStream(stillCurrent = { true }, onPlaybackEvent = {},
            sessionFocus = { sessionChecks++; false })
        denied.write(byteArrayOf(1, 2), 0, 2)
        denied.finish()
        denied.close()
        assertEquals(2, sessionChecks)
        assertEquals(false to false, requests.last())
    }
}
