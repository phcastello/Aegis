package com.aegis.node

import kotlin.math.abs
import kotlin.math.roundToLong

/**
 * Bounded 16 kHz PCM history on the reference recorder's monotonic timeline.
 * A missing time, clock jump or route restart invalidates previously stored samples.
 * Small timestamp jitter does not shift the timeline between adjacent DSP blocks.
 */
internal class HandsFreeExternalReferenceTimeline(private val capacity: Int = 16000 * 2) {
    init { require(capacity >= 320) }
    private val pcm = ShortArray(capacity)
    private var writeIndex = 0
    private var size = 0
    private var endNanos: Long? = null
    private var lastSignalNanos: Long? = null
    var generation = 0L
        private set

    fun clear() {
        pcm.fill(0)
        writeIndex = 0
        size = 0
        endNanos = null
        lastSignalNanos = null
        generation++
    }

    fun offer(source: ShortArray, count: Int, observedEndNanos: Long?) {
        require(count in 1..source.size && count <= capacity)
        if (observedEndNanos == null || observedEndNanos <= 0) {
            if (size != 0) clear()
            return
        }
        val expected = endNanos?.plus(count * HandsFreeCaptureClock.NANOS_PER_SAMPLE)
        if (expected != null && abs(expected - observedEndNanos) > CLOCK_TOLERANCE_NANOS) {
            clear()
        }
        // Anchor once; update only on a verified discontinuity rather than inserting
        // and dropping arbitrary samples whenever AudioRecord timestamps jitter.
        val frameEnd = endNanos?.plus(count * HandsFreeCaptureClock.NANOS_PER_SAMPLE) ?: observedEndNanos
        for (i in 0 until count) {
            val sample = source[i]
            pcm[writeIndex] = sample
            writeIndex = (writeIndex + 1) % capacity
            if (sample.toInt() !in -32..32) lastSignalNanos = frameEnd
        }
        size = minOf(capacity, size + count)
        endNanos = frameEnd
    }

    /** Keep a limited recent quiet interval for the acoustic tail after playback pauses. */
    fun hasReference(nowNanos: Long): Boolean {
        val end = endNanos ?: return false
        val lastSignal = lastSignalNanos ?: return false
        return size >= 320 && nowNanos - end in -20_000_000L..STALE_NANOS &&
            end - lastSignal <= TAIL_NANOS
    }

    /** Positive distance if the desired microphone boundary is just ahead of the reference. */
    fun aheadOfReference(frameEndNanos: Long, nowNanos: Long): Long? {
        if (!hasReference(nowNanos)) return null
        return (frameEndNanos - checkNotNull(endNanos)).takeIf { it > HandsFreeCaptureClock.NANOS_PER_SAMPLE / 2 }
    }

    /** `frameEndNanos` is the end-exclusive microphone sample time, not the read return time. */
    fun copyFrame(frameEndNanos: Long, output: ShortArray, nowNanos: Long): Boolean {
        require(output.size == 160 || output.size == 320)
        output.fill(0)
        if (!hasReference(nowNanos)) return false
        val end = endNanos ?: return false
        if (nowNanos - frameEndNanos !in -20_000_000L..STALE_NANOS) return false
        // Never manufacture future or missing render samples.
        val lagNanos = end - frameEndNanos
        if (lagNanos < -HandsFreeCaptureClock.NANOS_PER_SAMPLE / 2) return false
        val lag = (lagNanos.toDouble() / HandsFreeCaptureClock.NANOS_PER_SAMPLE).roundToLong()
        if (lag !in 0L..(size - output.size).toLong()) return false
        val start = (writeIndex - lag.toInt() - output.size + capacity * 2) % capacity
        for (i in output.indices) output[i] = pcm[(start + i) % capacity]
        return true
    }

    companion object {
        private const val CLOCK_TOLERANCE_NANOS = 8_000_000L
        private const val STALE_NANOS = 850_000_000L
        private const val TAIL_NANOS = 600_000_000L
    }
}
