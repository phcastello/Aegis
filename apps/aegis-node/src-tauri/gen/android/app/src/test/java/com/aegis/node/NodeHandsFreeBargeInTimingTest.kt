package com.aegis.node

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.abs

class NodeHandsFreeBargeInTimingTest {
    private class Replay {
        private val captureOrigin = 5_000_000_000L
        private val playbackOrigin = captureOrigin + 130_000_000L
        var quantum = 720L
        var playbackRoute = 0L
        var micRoute = 0L
        var timestampsAvailable = true
        var timestampThrows = false
        var captureTimestampShift = 0L
        var headOverride: Long? = null
        var trackPolls = 0
        var enqueuedOverride: Long? = null
        var playbackDriftPpm = 0
        private var now = captureOrigin

        private fun clockTimestamp(origin: Long, rate: Double, blockSize: Long): HandsFreeAudioTimestamp? {
            check(!timestampThrows) { "device replaced" }
            val count = ((now - origin) * rate / 1_000_000_000L).toLong()
            if (count < blockSize || !timestampsAvailable) return null
            val frame = count / blockSize * blockSize
            return HandsFreeAudioTimestamp(frame, origin + (frame * 1_000_000_000L / rate).toLong())
        }

        private val playbackRate get() = 24_000 * (1 + playbackDriftPpm / 1_000_000.0)
        private fun playbackFrameAt(nanoTime: Long) =
            ((nanoTime - playbackOrigin) * playbackRate / 1_000_000_000).toLong()
        fun rawHead() = headOverride ?: (playbackFrameAt(now).coerceAtLeast(0) / quantum * quantum)
        fun actualPlaybackFrame(frame: Long) = playbackFrameAt(captureOrigin + frame * 20_000_000L)

        private val playback = HandsFreePlaybackClock(
            playedFrames = { rawHead() },
            timestamp = { trackPolls++; clockTimestamp(playbackOrigin, playbackRate, 1536L) },
            discontinuity = { playbackRoute },
            enqueuedFrames = { enqueuedOverride ?: (playbackFrameAt(now) + 4_800) }
        )
        private val timing = HandsFreeBargeInTiming(
            playback,
            { clockTimestamp(captureOrigin, 16_000.0, 512L)?.let {
                it.copy(nanoTime = it.nanoTime + captureTimestampShift)
            } },
            { micRoute },
            { now }
        )

        fun frame(frame: Long, readAfterMs: Long = 30): HandsFreeBargeInFramePosition {
            now = captureOrigin + (frame * 20 + readAfterMs) * 1_000_000L
            return timing.locate(frame * 320)
        }
    }

    @Test fun pairedClocksAlignEveryBlockWhenRawHeadRepeatsAndReadsAreDelayed() {
        for (quantum in listOf(512L, 576L, 720L, 960L, 1200L)) {
            val replay = Replay()
            replay.quantum = quantum
            var previousRaw = -1L
            var repeated = 0
            for (frame in 1L..400) {
                // Bursty reads may deliver multiple buffered microphone blocks
                // while the hardware-reported track head is unchanged.
                val delay = listOf(46L, 27L, 10L, 0L, 0L, 0L, 0L, 0L)[(frame % 8).toInt()]
                val result = replay.frame(frame, 50 + delay)
                val observed = replay.rawHead()
                if (observed == previousRaw) repeated++
                previousRaw = observed
                if (frame > 25L) {
                    assertEquals(HandsFreeBargeInTimingSource.TIMESTAMP, result.source)
                    assertEquals(replay.actualPlaybackFrame(frame), result.frame)
                    assertFalse(result.discontinuity)
                }
            }
            assertTrue(repeated > 30)
            assertTrue(replay.trackPolls < 8)
        }
    }

    @Test fun routeAndUnderrunInvalidateAnchorsBeforeTheNextDecision() {
        val replay = Replay()
        for (frame in 1L..80) replay.frame(frame)
        assertEquals(HandsFreeBargeInTimingSource.TIMESTAMP, replay.frame(81).source)
        replay.playbackRoute++
        val newOutput = replay.frame(82)
        assertTrue(newOutput.discontinuity)
        assertEquals(HandsFreeBargeInTimingSource.UNAVAILABLE, newOutput.source)
        assertNull(newOutput.frame)
        for (frame in 83L..100) replay.frame(frame)
        assertEquals(HandsFreeBargeInTimingSource.TIMESTAMP, replay.frame(101).source)
        replay.micRoute++
        val newInput = replay.frame(102)
        assertTrue(newInput.discontinuity)
        assertNull(newInput.frame)
    }

    @Test fun staleTimestampExpiresAndFallsBackToSupportedHead() {
        val replay = Replay()
        for (frame in 1L..80) replay.frame(frame)
        assertEquals(HandsFreeBargeInTimingSource.TIMESTAMP, replay.frame(81).source)
        replay.timestampsAvailable = false
        assertEquals(HandsFreeBargeInTimingSource.TIMESTAMP, replay.frame(82).source)
        var transition: HandsFreeBargeInFramePosition? = null
        for (frame in 83L..175) {
            val result = replay.frame(frame)
            if (result.discontinuity) transition = result
        }
        assertEquals(HandsFreeBargeInTimingSource.PLAYBACK_HEAD, transition?.source)
        assertEquals(replay.rawHead(), replay.frame(175).frame)
        replay.timestampsAvailable = true
        for (frame in 176L..237) replay.frame(frame)
        assertEquals(HandsFreeBargeInTimingSource.TIMESTAMP, replay.frame(238).source)
    }

    @Test fun changingTimestampEpochDiscardsThePreviouslyAlignedModel() {
        val replay = Replay()
        for (frame in 1L..80) replay.frame(frame)
        assertEquals(HandsFreeBargeInTimingSource.TIMESTAMP, replay.frame(81).source)
        replay.captureTimestampShift = 12_000_000L
        val events = (82L..120).map { replay.frame(it) }
        assertTrue(events.any { it.discontinuity && it.source == HandsFreeBargeInTimingSource.PLAYBACK_HEAD })
        assertTrue(events.any { it.discontinuity && it.source == HandsFreeBargeInTimingSource.TIMESTAMP })
    }

    @Test fun stalledOrReversedHeadCannotBeExtrapolatedAsPlayback() {
        val replay = Replay()
        for (frame in 1L..80) replay.frame(frame)
        assertEquals(HandsFreeBargeInTimingSource.TIMESTAMP, replay.frame(81).source)
        replay.headOverride = replay.rawHead()
        val pausedFrames = (82L..87).map { replay.frame(it) }
        assertEquals(HandsFreeBargeInTimingSource.UNAVAILABLE, pausedFrames.last().source)
        assertTrue(pausedFrames.any { it.source == HandsFreeBargeInTimingSource.UNAVAILABLE && it.discontinuity })
        replay.headOverride = checkNotNull(replay.headOverride) - 720
        val reversed = replay.frame(88)
        assertEquals(HandsFreeBargeInTimingSource.UNAVAILABLE, reversed.source)
        assertTrue(reversed.discontinuity)
    }

    @Test fun timingCannotReferenceOfferedButUnacceptedFrames() {
        val replay = Replay()
        replay.quantum = 1_200
        for (frame in 1L..80) replay.frame(frame)
        assertEquals(HandsFreeBargeInTimingSource.TIMESTAMP, replay.frame(81).source)
        replay.enqueuedOverride = 0
        var unprovenFrames = 0
        for (frame in 82L..122) {
            val result = replay.frame(frame, readAfterMs = 10)
            if (replay.actualPlaybackFrame(frame) > replay.rawHead()) {
                unprovenFrames++
                assertEquals(HandsFreeBargeInTimingSource.PLAYBACK_HEAD, result.source)
                assertEquals(replay.rawHead(), result.frame)
            }
        }
        assertTrue(unprovenFrames > 0)
    }

    @Test fun slowHardwareDriftKeepsAnAlignedClockAcrossRefresh() {
        val replay = Replay()
        replay.playbackDriftPpm = 200
        for (frame in 1L..1_200) {
            val result = replay.frame(frame)
            if (frame > 25) {
                assertEquals(HandsFreeBargeInTimingSource.TIMESTAMP, result.source)
                assertTrue(abs(checkNotNull(result.frame) - replay.actualPlaybackFrame(frame)) <= 50)
                assertFalse(result.discontinuity)
            }
        }
        assertTrue(replay.trackPolls in 5..10)
    }

    @Test fun failedPlatformTimestampLeavesRawHeadUsable() {
        val replay = Replay()
        replay.timestampThrows = true
        for (frame in 1L..80) replay.frame(frame)
        val result = replay.frame(81)
        assertEquals(HandsFreeBargeInTimingSource.PLAYBACK_HEAD, result.source)
        assertEquals(replay.rawHead(), result.frame)
    }

    @Test fun unsupportedTimestampUsesRawHeadAndSlowsFurtherProbes() {
        val replay = Replay()
        replay.timestampsAvailable = false
        for (frame in 1L..600) {
            val result = replay.frame(frame)
            if (frame > 25) {
                assertEquals(HandsFreeBargeInTimingSource.PLAYBACK_HEAD, result.source)
                assertEquals(replay.rawHead(), result.frame)
            }
        }
        assertTrue(replay.trackPolls <= 22)
    }
}
