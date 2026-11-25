package com.aegis.node

import org.json.JSONObject

/** No assistant audio was heard. The prior request is resolved by its turn ID,
 * including a conversation persisted before the client received its marker. */
internal data class HandsFreeGenerationInterruption(val turnId:String,val previousContent:String?) {
    fun toJson()=JSONObject().put("turnId",turnId).apply {
        previousContent?.let { put("previousContent",it) }
    }
}

/** A position in a committed speech unit, measured against the native playback head. */
internal data class HandsFreePlaybackInterruption(
    val conversationId: String,
    val turnId: String,
    val assistantMessageId: String,
    val segmentId: String,
    val sequence: Int,
    val playedFramesInSegment: Long,
    val segmentFrames: Long?
) {
    fun toJson() = JSONObject()
        .put("turnId", turnId)
        .put("assistantMessageId", assistantMessageId)
        .put("segmentId", segmentId)
        .put("sequence", sequence)
        .put("playedFramesInSegment", playedFramesInSegment)
        .put("segmentFrames", segmentFrames ?: JSONObject.NULL)
}

/**
 * The writer registers each unit before delivering PCM. Synthesis can complete ahead of
 * playback, but neither downloaded nor written frames count as heard.
 */
internal class HandsFreePlaybackProgress {
    private data class Segment(
        val conversationId: String,
        val turnId: String,
        val assistantMessageId: String,
        val id: String,
        val sequence: Int,
        val startFrame: Long
    )

    private val segments = mutableListOf<Segment>()
    private val lengths = mutableMapOf<String, Long>()

    @Synchronized
    fun reset() {
        segments.clear()
        lengths.clear()
    }

    @Synchronized
    fun begin(
        conversationId: String, turnId: String, assistantMessageId: String,
        segmentId: String, sequence: Int, startFrame: Long
    ) {
        check(sequence == segments.size && startFrame >= 0)
        val previous = segments.lastOrNull()
        if (previous == null) check(startFrame == 0L)
        else {
            check(previous.conversationId == conversationId && previous.turnId == turnId && previous.assistantMessageId == assistantMessageId)
            check(startFrame == previous.startFrame + checkNotNull(lengths[previous.id]))
        }
        segments.add(Segment(conversationId, turnId, assistantMessageId, segmentId, sequence, startFrame))
    }

    @Synchronized
    fun synthesized(segmentId: String, frames: Long) {
        check(frames > 0)
        val previous = lengths.put(segmentId, frames)
        check(previous == null || previous == frames)
    }

    @Synchronized
    fun at(playedFrames: Long): HandsFreePlaybackInterruption? {
        if (playedFrames !in 0L..(24000L * 180)) return null
        val unit = segments.lastOrNull { it.startFrame <= playedFrames } ?: return null
        val length = lengths[unit.id]
        val relative = playedFrames - unit.startFrame
        return HandsFreePlaybackInterruption(
            unit.conversationId, unit.turnId, unit.assistantMessageId, unit.id, unit.sequence,
            if (length == null) relative else minOf(relative, length), length
        )
    }
}
