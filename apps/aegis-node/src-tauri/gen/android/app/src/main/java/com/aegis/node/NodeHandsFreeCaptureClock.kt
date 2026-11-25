package com.aegis.node

import kotlin.math.abs

/** AudioRecord timestamps and frame counts refer to the beginning of the
 * recording, not the time a blocking read returns. Never substitute read time
 * for the acoustic timeline when pairing independent capture streams.
 */
internal class HandsFreeCaptureClock {
    private var samplesRead = 0L
    private var previous: HandsFreeAudioTimestamp? = null
    private var stable = false

    fun advance(count: Int) {
        require(count >= 0)
        samplesRead += count
    }

    /** Keep the stream frame count when a reference temporarily disappears. */
    fun invalidate() {
        previous = null
        stable = false
    }

    fun endNanos(nowNanos: Long, timestamp: HandsFreeAudioTimestamp?): Long? {
        if (timestamp == null || timestamp.frame < 0 ||
            nowNanos - timestamp.nanoTime !in -20_000_000L..1_000_000_000L) {
            invalidate()
            return null
        }
        val delta = samplesRead - timestamp.frame
        if (delta !in -16_000L..16_000L) {
            invalidate()
            return null
        }
        val end = timestamp.nanoTime + delta * NANOS_PER_SAMPLE
        if (nowNanos - end !in -20_000_000L..600_000_000L) {
            invalidate()
            return null
        }
        val prior = previous
        if (prior != timestamp) {
            previous = timestamp
            if (prior == null || timestamp.frame <= prior.frame || timestamp.nanoTime <= prior.nanoTime) {
                stable = false
            } else {
                val elapsed = timestamp.nanoTime - prior.nanoTime
                val expected = (timestamp.frame - prior.frame) * NANOS_PER_SAMPLE
                // The two anchors must agree on the sample rate. Small timestamp
                // quantization and device clock drift are normal.
                stable = abs(elapsed - expected) <= 8_000_000L + expected / 500
            }
        }
        return end.takeIf { stable }
    }

    companion object {
        const val NANOS_PER_SAMPLE = 62_500L
    }
}
