package com.aegis.node

import kotlin.math.abs
import kotlin.math.roundToInt

/** Bounded reduction of coherent self playback, not a general acoustic echo canceller.
 *
 * offer() borrows 24 kHz mono PCM16 LE immediately before an AudioTrack write.
 * startFrame is the absolute frame index in that stream (partial writes may overwrite
 * the same positions). suppress() consumes a <=20 ms 16 kHz mono capture block;
 * headFrame is the played-frame count observed at the END of that block. No offered
 * sample at/after that head is used, including bytes not yet accepted by a write.
 *
 * Only a delayed/scaled linear component is modeled; reverberation, clipping and
 * route-dependent processing can leave residual self speech. true is reference
 * usability, never proof of near-end speech. Do not stack another AEC untested.
 * A valid reference with no coherent echo passes through, preserving double-talk.
 * A missing/overwritten reference or clear() racing the work returns false and zeros
 * the borrowed input: the caller must not advance VAD/onset through that frame.
 * Keep one instance per playback stream; a stale stream must never offer into its
 * successor. The caller owns playback stop/echo-tail timing and microphone policy.
 */
internal class HandsFreeBargeInEcho {
    private val lock = Any()
    private val pcm = ShortArray(REFERENCE_FRAMES)
    private val positions = LongArray(REFERENCE_FRAMES) { -1L }
    private var newestEnd = 0L
    private var generation = 0L

    fun offer(bytes: ByteArray, offset: Int, count: Int, startFrame: Long) {
        require(offset >= 0 && count >= 0 && count % 2 == 0 && offset <= bytes.size - count)
        val frames = count / 2
        require(startFrame >= 0 && startFrame <= Long.MAX_VALUE - frames)
        if (frames == 0) return
        synchronized(lock) {
            val end = startFrame + frames
            newestEnd = maxOf(newestEnd, end)
            val retainedStart = maxOf(0L, newestEnd - REFERENCE_FRAMES)
            // Even an oversized offer copies at most the fixed ring capacity.
            val first = maxOf(startFrame, retainedStart, end - REFERENCE_FRAMES)
            var frame = first
            while (frame < end) {
                val at = offset + ((frame - startFrame) * 2).toInt()
                val slot = (frame % REFERENCE_FRAMES).toInt()
                pcm[slot] = ((bytes[at].toInt() and 255) or (bytes[at + 1].toInt() shl 8)).toShort()
                positions[slot] = frame
                frame++
            }
        }
    }

    fun clear() = synchronized(lock) {
        generation++
        pcm.fill(0)
        positions.fill(-1L)
        newestEnd = 0L
    }

    fun suppress(input: ShortArray, count: Int, headFrame: Long): Boolean {
        require(count in 1..MAX_CAPTURE_FRAMES && count <= input.size)
        fun unavailable(): Boolean { input.fill(0, 0, count); return false }
        if (headFrame <= 0 || headFrame > Long.MAX_VALUE / 2) return unavailable()
        val snapshot = synchronized(lock) {
            val first = maxOf(0L, headFrame - ((count + MAX_DELAY_SAMPLES) * 3L + 1) / 2 - 1)
            val size = (headFrame - first).toInt()
            if (headFrame > newestEnd || size !in 1..REFERENCE_FRAMES) return@synchronized null
            val data = ShortArray(size)
            val valid = BooleanArray(size)
            for (i in 0 until size) {
                val frame = first + i
                val slot = (frame % REFERENCE_FRAMES).toInt()
                if (positions[slot] == frame) { data[i] = pcm[slot]; valid[i] = true }
            }
            Snapshot(first, data, valid, generation)
        } ?: return unavailable()
        val endHalfFrame = headFrame * 2
        // The most recent rendered block must be known, even when an older lag fits.
        // Otherwise an unoffered section could leak self speech as apparent user speech.
        for (i in 0 until count) {
            if (snapshot.sample(endHalfFrame - (count - i) * 3L).isNaN()) return unavailable()
        }
        val full = moments(input, count, 1)
        val output = input.copyOf(count)
        if (full.energy > 1e-8 && count >= MIN_CORRELATION_SAMPLES && snapshot.data.any { it != 0.toShort() }) {
            val sparse = moments(input, count, COARSE_STRIDE)
            val fit = Fit()
            val peaks = ArrayList<Peak>(COARSE_PEAKS)
            for (lag in 0..MAX_DELAY_SAMPLES step COARSE_STEP) {
                computeFit(input, count, COARSE_STRIDE, sparse, snapshot, endHalfFrame, lag, fit)
                if (fit.score.isNaN()) continue
                val close = peaks.indexOfFirst { abs(it.lag - lag) < COARSE_PEAK_SEPARATION }
                if (close >= 0) {
                    if (fit.score > peaks[close].score) peaks[close] = Peak(lag, fit.score)
                } else if (peaks.size < COARSE_PEAKS) peaks.add(Peak(lag, fit.score))
                else {
                    val weakest = peaks.indices.minByOrNull { peaks[it].score }!!
                    if (fit.score > peaks[weakest].score) peaks[weakest] = Peak(lag, fit.score)
                }
            }
            var best: Fit? = null
            var bestLag = 0
            for (peak in peaks) {
                for (lag in maxOf(0, peak.lag - COARSE_STEP)..minOf(MAX_DELAY_SAMPLES, peak.lag + COARSE_STEP)) {
                    computeFit(input, count, 1, full, snapshot, endHalfFrame, lag, fit)
                    if (!fit.score.isNaN() && (best == null || fit.score > best.score)) {
                        best = fit.copy(); bestLag = lag
                    }
                }
            }
            val match = best
            if (match != null && match.score >= MIN_CORRELATION_SQUARED) {
                // A coherent but out-of-range fit must not become raw self speech.
                if (abs(match.gain) > MAX_ABS_GAIN) return unavailable()
                for (i in 0 until count) {
                    val reference = snapshot.sample(endHalfFrame - (count - i + bestLag) * 3L)
                    val residual = input[i] - full.mean - match.gain * (reference - match.referenceMean)
                    output[i] = residual.roundToInt().coerceIn(Short.MIN_VALUE.toInt(), Short.MAX_VALUE.toInt()).toShort()
                }
            }
        }
        return synchronized(lock) {
            if (generation != snapshot.generation) unavailable()
            else { output.copyInto(input, 0, 0, count); true }
        }
    }

    private data class Snapshot(val firstFrame: Long, val data: ShortArray, val valid: BooleanArray, val generation: Long) {
        /** Half-frame coordinates make the 24k→16k interpolation deterministic. */
        fun sample(halfFrame: Long): Double {
            if (halfFrame < 0) return Double.NaN
            val relative = halfFrame / 2 - firstFrame
            if (relative < 0 || relative >= data.size) return Double.NaN
            val index = relative.toInt()
            if (!valid[index]) return Double.NaN
            if (halfFrame and 1L == 0L) return data[index].toDouble()
            if (index + 1 >= data.size || !valid[index + 1]) return Double.NaN
            return (data[index].toDouble() + data[index + 1]) * 0.5
        }
    }
    private data class Moments(val sum: Double, val mean: Double, val energy: Double, val samples: Int)
    private data class Peak(val lag: Int, val score: Double)
    private data class Fit(var score: Double = Double.NaN, var gain: Double = 0.0, var referenceMean: Double = 0.0)
    private fun moments(input: ShortArray, count: Int, stride: Int): Moments {
        var sum = 0.0; var squares = 0.0; var samples = 0
        for (i in 0 until count step stride) { val x = input[i].toDouble(); sum += x; squares += x * x; samples++ }
        return Moments(sum, sum / samples, (squares - sum * sum / samples).coerceAtLeast(0.0), samples)
    }
    private fun computeFit(input: ShortArray, count: Int, stride: Int, capture: Moments, reference: Snapshot,
                    endHalfFrame: Long, lag: Int, result: Fit) {
        result.score = Double.NaN
        if (capture.energy <= 1e-8) return
        var sum = 0.0; var squares = 0.0; var cross = 0.0
        for (i in 0 until count step stride) {
            val y = reference.sample(endHalfFrame - (count - i + lag) * 3L)
            if (y.isNaN()) return
            sum += y; squares += y * y; cross += input[i] * y
        }
        val energy = squares - sum * sum / capture.samples
        if (energy <= 1e-8) return
        val dot = cross - capture.sum * sum / capture.samples
        result.score = (dot * dot / (capture.energy * energy)).coerceIn(0.0, 1.0)
        result.gain = dot / energy
        result.referenceMean = sum / capture.samples
    }

    companion object {
        internal const val REFERENCE_FRAMES = 28800 // 1.2 s of mono 24 kHz PCM.
        private const val MAX_CAPTURE_FRAMES = 320 // 20 ms at 16 kHz.
        private const val MIN_CORRELATION_SAMPLES = 64
        private const val MAX_DELAY_SAMPLES = 4000 // 250 ms at 16 kHz; never reads ahead of playback.
        private const val COARSE_STEP = 4
        private const val COARSE_STRIDE = 4
        private const val COARSE_PEAKS = 4
        private const val COARSE_PEAK_SEPARATION = 16
        private const val MIN_CORRELATION_SQUARED = 0.4225 // |correlation| >= .65.
        private const val MAX_ABS_GAIN = 4.0
    }
}
