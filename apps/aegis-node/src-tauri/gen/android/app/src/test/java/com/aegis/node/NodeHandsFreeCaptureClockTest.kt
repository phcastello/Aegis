package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

class NodeHandsFreeCaptureClockTest {
    @Test fun tracksFramesInsteadOfDelayedReadReturn() {
        val clock = HandsFreeCaptureClock()
        val start = 1_000_000_000L
        clock.advance(3200)
        assertNull(clock.endNanos(start + 290_000_000L, HandsFreeAudioTimestamp(4000, start + 250_000_000L)))
        clock.advance(3200)
        assertEquals(start + 400_000_000L,
            clock.endNanos(start + 490_000_000L, HandsFreeAudioTimestamp(7200, start + 450_000_000L)))
        clock.advance(320)
        // A repeated hardware timestamp is usable after the timeline has been verified.
        assertEquals(start + 420_000_000L,
            clock.endNanos(start + 510_000_000L, HandsFreeAudioTimestamp(7200, start + 450_000_000L)))
    }

    @Test fun requiresFreshConsistentTimestampsAfterReferenceLossOrClockJump() {
        val clock = HandsFreeCaptureClock()
        val start = 5_000_000_000L
        clock.advance(3200)
        assertNull(clock.endNanos(start + 200_000_000L, HandsFreeAudioTimestamp(3200, start + 200_000_000L)))
        clock.advance(3200)
        assertNotNull(clock.endNanos(start + 400_000_000L, HandsFreeAudioTimestamp(6400, start + 400_000_000L)))
        clock.invalidate()
        clock.advance(3200)
        assertNull(clock.endNanos(start + 600_000_000L, HandsFreeAudioTimestamp(9600, start + 600_000_000L)))
        clock.advance(3200)
        assertNull(clock.endNanos(start + 900_000_000L, HandsFreeAudioTimestamp(12800, start + 890_000_000L)))
        clock.advance(3200)
        assertNotNull(clock.endNanos(start + 1_100_000_000L, HandsFreeAudioTimestamp(16000, start + 1_090_000_000L)))
        assertNull(clock.endNanos(start + 1_100_000_000L, null))
        assertNull(clock.endNanos(start + 1_100_000_000L, HandsFreeAudioTimestamp(5, start + 1_100_000_000L)))
    }

    @Test fun idleSamplesAreStillCountedWhenReferenceStartsLater() {
        val clock = HandsFreeCaptureClock()
        val start = 20_000_000_000L
        clock.advance(160_000)
        clock.advance(3200)
        assertNull(clock.endNanos(start + 10_240_000_000L,
            HandsFreeAudioTimestamp(163200, start + 10_200_000_000L)))
        clock.advance(3200)
        assertEquals(start + 10_400_000_000L, clock.endNanos(start + 10_440_000_000L,
            HandsFreeAudioTimestamp(166400, start + 10_400_000_000L)))
    }
}
