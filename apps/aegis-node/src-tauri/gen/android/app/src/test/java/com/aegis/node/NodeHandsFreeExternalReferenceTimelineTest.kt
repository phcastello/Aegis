package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

class NodeHandsFreeExternalReferenceTimelineTest {
    private val sample = HandsFreeCaptureClock.NANOS_PER_SAMPLE
    private val start = 10_000_000_000L

    @Test fun copiesOnlyPastTimedReferenceAndHandlesRingWrap() {
        val timeline = HandsFreeExternalReferenceTimeline(capacity = 640)
        val a = ShortArray(320) { (1000 + it).toShort() }
        val b = ShortArray(320) { (2000 + it).toShort() }
        val c = ShortArray(320) { (3000 + it).toShort() }
        timeline.offer(a, a.size, start + 320 * sample)
        timeline.offer(b, b.size, start + 640 * sample)
        timeline.offer(c, c.size, start + 960 * sample)
        val end = start + 960 * sample
        val output = ShortArray(320)
        assertTrue(timeline.hasReference(end))
        assertTrue(timeline.copyFrame(end, output, end))
        assertArrayEquals(c, output)
        assertTrue(timeline.copyFrame(start + 640 * sample, output, end))
        assertArrayEquals(b, output)
        assertFalse(timeline.copyFrame(start + 320 * sample, output, end))
        assertTrue(output.all { it.toInt() == 0 })
        assertFalse(timeline.copyFrame(end + sample, output, end + sample))
        assertTrue(output.all { it.toInt() == 0 })
    }

    @Test fun identifiesOnlyAnActuallyPendingFutureFrameForTheBoundedWait() {
        val timeline = HandsFreeExternalReferenceTimeline()
        val pcm = ShortArray(320) { 1000 }
        val end = start + 320 * sample
        assertNull(timeline.aheadOfReference(end, end))
        timeline.offer(pcm, pcm.size, end)
        assertEquals(20_000_000L, timeline.aheadOfReference(end + 20_000_000L, end))
        assertNull(timeline.aheadOfReference(end, end))
        assertNull(timeline.aheadOfReference(end - sample, end))
        timeline.offer(pcm, pcm.size, end + 20_000_000L)
        assertNull(timeline.aheadOfReference(end + 20_000_000L, end + 20_000_000L))
        timeline.clear()
        assertNull(timeline.aheadOfReference(end + 40_000_000L, end + 20_000_000L))
    }

    @Test fun smallTimestampJitterDoesNotShiftAdjacentFrames() {
        val timeline = HandsFreeExternalReferenceTimeline()
        val pcm = ShortArray(320) { (500 + it).toShort() }
        val end = start + 320 * sample
        timeline.offer(pcm, pcm.size, end)
        timeline.offer(pcm, pcm.size, end + 320 * sample + 2_000_000)
        val output = ShortArray(160)
        assertTrue(timeline.copyFrame(end + 160 * sample, output, end + 320 * sample + 2_000_000))
        assertArrayEquals(pcm.copyOfRange(0, 160), output)
        assertTrue(timeline.copyFrame(end + 320 * sample, output, end + 320 * sample + 2_000_000))
        assertArrayEquals(pcm.copyOfRange(160, 320), output)
    }

    @Test fun invalidTimestampAndRouteJumpClearOldPcm() {
        val timeline = HandsFreeExternalReferenceTimeline()
        val pcm = ShortArray(320) { 1200 }
        timeline.offer(pcm, pcm.size, start + 320 * sample)
        val first = timeline.generation
        timeline.offer(pcm, pcm.size, null)
        assertTrue(timeline.generation > first)
        assertFalse(timeline.hasReference(start + 640 * sample))
        val newAnchor = start + 800_000_000
        timeline.offer(pcm, pcm.size, newAnchor)
        val stable = timeline.generation
        val jumped = newAnchor + 120_000_000
        timeline.offer(pcm, pcm.size, jumped)
        assertTrue(timeline.generation > stable)
        val output = ShortArray(320) { 99 }
        assertFalse(timeline.copyFrame(newAnchor, output, jumped))
        assertTrue(output.all { it.toInt() == 0 })
        assertTrue(timeline.copyFrame(jumped, output, jumped))
        assertArrayEquals(pcm, output)
    }

    @Test fun silentOrStaleReferenceDoesNotPretendCancellationIsAvailable() {
        val timeline = HandsFreeExternalReferenceTimeline()
        val silent = ShortArray(320)
        timeline.offer(silent, silent.size, start)
        assertFalse(timeline.hasReference(start))
        val output = ShortArray(320) { 77 }
        assertFalse(timeline.copyFrame(start, output, start))
        assertTrue(output.all { it.toInt() == 0 })
        val signal = ShortArray(320) { 5000 }
        timeline.offer(signal, signal.size, start + 20_000_000)
        assertTrue(timeline.hasReference(start + 20_000_000))
        // Zeros immediately after playback stops remain valid for the AEC tail.
        repeat(30) { timeline.offer(silent, silent.size, start + (it + 2) * 20_000_000L) }
        assertTrue(timeline.hasReference(start + 620_000_000))
        timeline.offer(silent, silent.size, start + 640_000_000)
        assertFalse(timeline.hasReference(start + 640_000_000))
        timeline.offer(signal, signal.size, start + 660_000_000)
        assertFalse(timeline.hasReference(start + 2_000_000_000))
        timeline.clear()
        assertFalse(timeline.hasReference(start + 660_000_000))
    }
}
