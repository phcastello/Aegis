package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

class NodeHandsFreePlaybackProgressTest {
    private val progress = HandsFreePlaybackProgress()

    private fun begin(id: String, index: Int, start: Long) {
        progress.begin("conversation", "turn", "assistant", id, index, start)
    }

    @Test fun usesAudibleHeadNotDownloadedOrEnqueuedUnits() {
        begin("one", 0, 0)
        progress.synthesized("one", 48000)
        progress.synthesized("two", 72000)
        begin("two", 1, 48000)
        val inFirst = checkNotNull(progress.at(24000))
        assertEquals("one", inFirst.segmentId)
        assertEquals(0, inFirst.sequence)
        assertEquals(24000L, inFirst.playedFramesInSegment)
        assertEquals(48000L, inFirst.segmentFrames)
        val onBoundary = checkNotNull(progress.at(48000))
        assertEquals("two", onBoundary.segmentId)
        assertEquals(0L, onBoundary.playedFramesInSegment)
        val inSecond = checkNotNull(progress.at(60000))
        assertEquals("two", inSecond.segmentId)
        assertEquals(12000L, inSecond.playedFramesInSegment)
        assertEquals(72000L, inSecond.segmentFrames)
    }

    @Test fun partialSynthesisKeepsAnExactOffsetWithoutInventingDuration() {
        begin("one", 0, 0)
        progress.synthesized("one", 12000)
        begin("two", 1, 12000)
        val position = checkNotNull(progress.at(24000))
        assertEquals(12000L, position.playedFramesInSegment)
        assertNull(position.segmentFrames)
        val json = position.toJson()
        assertEquals("assistant", json.getString("assistantMessageId"))
        assertEquals("turn", json.getString("turnId"))
        assertEquals(12000L, json.getLong("playedFramesInSegment"))
        assertTrue(json.isNull("segmentFrames"))
        progress.reset()
        assertNull(progress.at(24000))
        begin("new", 0, 0)
        assertEquals("new", progress.at(1)?.segmentId)
    }
}
