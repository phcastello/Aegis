package com.aegis.node

import kotlin.math.abs

internal data class HandsFreeAudioTimestamp(val frame: Long, val nanoTime: Long)

internal class HandsFreePlaybackClock(
    private val playedFrames: () -> Long?,
    val timestamp: () -> HandsFreeAudioTimestamp? = { null },
    val discontinuity: () -> Long = { 0L },
    val enqueuedFrames: () -> Long = { 0L }
) : () -> Long? {
    override fun invoke(): Long? = playedFrames()
}

internal enum class HandsFreeBargeInTimingSource { TIMESTAMP, PLAYBACK_HEAD, UNAVAILABLE }

internal data class HandsFreeBargeInFramePosition(
    val frame: Long?,
    val source: HandsFreeBargeInTimingSource,
    val discontinuity: Boolean
)

/** Maps the end of each microphone block onto the playback stream. Android
 * timestamps carry a frame index and its monotonic time. Polling the raw head
 * after a read instead includes any delay from queued capture and scheduling.
 */
internal class HandsFreeBargeInTiming(
    private val playback: HandsFreePlaybackClock,
    private val captureTimestamp: () -> HandsFreeAudioTimestamp?,
    private val captureDiscontinuity: () -> Long = { 0L },
    private val nanoTime: () -> Long = System::nanoTime
) {
    private val output = FrameClock(PLAYBACK_RATE, 10_000 * MILLIS, 12_000 * MILLIS)
    private val input = FrameClock(CAPTURE_RATE, 400 * MILLIS, 1_200 * MILLIS)
    private var source = HandsFreeBargeInTimingSource.UNAVAILABLE
    private var captureGeneration: Long? = null
    private var playbackGeneration: Long? = null
    private var lastHead: Long? = null
    private var lastHeadAdvancedAt: Long? = null

    fun locate(captureEndFrame: Long): HandsFreeBargeInFramePosition {
        val now = nanoTime()
        val head = readAudio(playback)?.takeIf { it > 0 }
        val trackGeneration = readAudio(playback.discontinuity)
        val micGeneration = readAudio(captureDiscontinuity)
        val previousHead = lastHead
        val restarted = (playbackGeneration != null && playbackGeneration != trackGeneration) ||
            (captureGeneration != null && captureGeneration != micGeneration) ||
            (previousHead != null && head != null && head < previousHead)
        playbackGeneration = trackGeneration
        captureGeneration = micGeneration
        lastHead = head

        if (head == null || restarted || head != previousHead) lastHeadAdvancedAt = now
        val stalled = head != null && now - checkNotNull(lastHeadAdvancedAt) > 100 * MILLIS
        if (head == null || trackGeneration == null || micGeneration == null || restarted || stalled) {
            output.clear()
            input.clear()
            return result(null, HandsFreeBargeInTimingSource.UNAVAILABLE, restarted)
        }

        val clockChanged = output.poll(now, playback.timestamp)
        val captureChanged = input.poll(now, captureTimestamp)
        val captureTime = input.timeAt(captureEndFrame, now)
        val estimated = captureTime?.takeIf { now - it in -20 * MILLIS..450 * MILLIS }
            ?.let { output.frameAt(it, now) }
        // The head can be quantized, and queued capture may put the estimate
        // earlier. Looking ahead of the head still requires proof that those
        // frames were accepted by the track. The PCM ring can contain offers
        // that a blocking or short write has not accepted yet.
        val aligned = estimated?.takeIf {
            it > 0 && head - it in -1_200L..12_000L &&
                it <= maxOf(head, readAudio(playback.enqueuedFrames) ?: 0L, output.confirmedFrame(now))
        }
        return if (aligned != null) result(aligned, HandsFreeBargeInTimingSource.TIMESTAMP, clockChanged || captureChanged)
        else result(head, HandsFreeBargeInTimingSource.PLAYBACK_HEAD, clockChanged || captureChanged)
    }

    private fun result(
        frame: Long?,
        next: HandsFreeBargeInTimingSource,
        discontinuity: Boolean
    ): HandsFreeBargeInFramePosition {
        val changed = source != next
        source = next
        return HandsFreeBargeInFramePosition(frame, next, discontinuity || changed)
    }

    private class FrameClock(
        private val rate: Long,
        private val refreshInterval: Long,
        private val maxAge: Long
    ) {
        private var pending: HandsFreeAudioTimestamp? = null
        private var stable: HandsFreeAudioTimestamp? = null
        private var lastPollAt: Long? = null
        private var warmupPolls = 0

        fun clear() {
            pending = null
            stable = null
            lastPollAt = null
            warmupPolls = 0
        }

        /** Return true if an existing anchor became inconsistent. A route may
         * supply repeated or temporarily absent timestamps while warming up.
         */
        fun poll(now: Long, read: () -> HandsFreeAudioTimestamp?): Boolean {
            val active = activeAnchor(now)
            val interval = when {
                active != null -> refreshInterval
                warmupPolls >= 10 -> SECOND
                else -> 80 * MILLIS
            }
            if (lastPollAt?.let { now - it < interval } == true) return false
            lastPollAt = now
            if (active == null) warmupPolls++
            val next = readAudio(read)?.takeIf {
                it.frame in 0..(rate * 600) && now - it.nanoTime in -100 * MILLIS..1_000 * MILLIS
            }
                ?: return false
            val previous = pending
            if (previous == next) return false
            pending = next
            if (previous != null && next.frame > previous.frame && next.nanoTime > previous.nanoTime &&
                abs(next.nanoTime - timeAt(next.frame, previous)) <= 3 * MILLIS) {
                stable = next
                warmupPolls = 0
                return false
            }
            if (previous == null) warmupPolls = 0
            stable = null
            return active != null
        }

        private fun activeAnchor(now: Long) = stable?.takeIf { now - it.nanoTime <= maxAge }
        fun timeAt(frame: Long, now: Long): Long? = activeAnchor(now)?.let { timeAt(frame, it) }
        fun confirmedFrame(now: Long): Long = activeAnchor(now)?.frame ?: 0
        fun frameAt(nanos: Long, now: Long): Long? = activeAnchor(now)?.let {
            it.frame + (nanos - it.nanoTime) * rate / SECOND
        }

        private fun timeAt(frame: Long, anchor: HandsFreeAudioTimestamp): Long =
            anchor.nanoTime + (frame - anchor.frame) * SECOND / rate
    }

    private companion object {
        const val CAPTURE_RATE = 16_000L
        const val PLAYBACK_RATE = 24_000L
        const val MILLIS = 1_000_000L
        const val SECOND = 1_000 * MILLIS
        inline fun <T> readAudio(read: () -> T): T? = try { read() } catch (_: Exception) { null }
    }
}
