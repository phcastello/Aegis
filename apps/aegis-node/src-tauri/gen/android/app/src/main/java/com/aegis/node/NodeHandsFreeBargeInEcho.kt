package com.aegis.node

import kotlin.math.abs
import kotlin.math.roundToInt
import kotlin.math.sqrt

internal enum class HandsFreeBargeInEvidence { UNAVAILABLE, AMBIGUOUS, INDEPENDENT }

/** Bounded reduction of coherent self playback, not a general acoustic echo canceller.
 *
 * offer() borrows 24 kHz mono PCM16 LE immediately before an AudioTrack write.
 * startFrame is the absolute frame index in that stream (partial writes may overwrite
 * the same positions). suppress() consumes a <=20 ms 16 kHz mono capture block;
 * headFrame is the played-frame count observed at the END of that block. No offered
 * sample at/after that head is used, including bytes not yet accepted by a write.
 *
 * Only a delayed/scaled linear component is modeled; reverberation, clipping and
 * route-dependent processing can leave residual self speech. [inspect] separates
 * evidence of independent sound from usable reference. The caller still verifies speech.
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
    private var evidenceGeneration = 0L
    private var lastEvidenceHead: Long? = null
    private var quietReference = false
    private var profile: EchoProfile? = null

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
        resetEvidence()
    }

    /** A route/gain change invalidates the learned echo path, not the offered PCM. */
    fun resetEvidence() = synchronized(lock) {
        evidenceGeneration++
        lastEvidenceHead = null
        quietReference = false
        profile = null
    }

    /** The reference can support voice detection on the latest inspected frame. */
    fun voicePathReady(): Boolean = synchronized(lock) {
        val head = lastEvidenceHead ?: return@synchronized false
        quietReference || profile?.let {
            it.blocks >= PROFILE_BLOCKS && head in it.lastVerifiedHead..(it.lastVerifiedHead + PROFILE_RECENCY_FRAMES)
        } == true
    }

    fun inspect(input: ShortArray, count: Int, headFrame: Long): HandsFreeBargeInEvidence =
        reduce(input, count, headFrame, evaluateEvidence = true)

    fun suppress(input: ShortArray, count: Int, headFrame: Long): Boolean =
        reduce(input, count, headFrame, evaluateEvidence = false) != HandsFreeBargeInEvidence.UNAVAILABLE

    private fun reduce(input: ShortArray, count: Int, headFrame: Long, evaluateEvidence: Boolean): HandsFreeBargeInEvidence {
        require(count in 1..MAX_CAPTURE_FRAMES && count <= input.size)
        fun unavailable(): HandsFreeBargeInEvidence {
            input.fill(0, 0, count)
            if (evaluateEvidence) resetEvidence()
            return HandsFreeBargeInEvidence.UNAVAILABLE
        }
        if (headFrame <= 0 || headFrame > Long.MAX_VALUE / 2) return unavailable()
        val requiredFrames = ((count + MAX_DELAY_SAMPLES) * 3 + 1) / 2 + 1
        val snapshot = synchronized(lock) {
            val first = maxOf(0L, headFrame - requiredFrames)
            val size = (headFrame - first).toInt()
            if (headFrame > newestEnd || size !in 1..REFERENCE_FRAMES) return@synchronized null
            val data = ShortArray(size)
            val valid = BooleanArray(size)
            for (i in 0 until size) {
                val frame = first + i
                val slot = (frame % REFERENCE_FRAMES).toInt()
                if (positions[slot] == frame) { data[i] = pcm[slot]; valid[i] = true }
            }
            val trustedLag = profile?.takeIf {
                evaluateEvidence && it.blocks >= PROFILE_BLOCKS &&
                    headFrame in it.lastVerifiedHead..(it.lastVerifiedHead + PROFILE_RECENCY_FRAMES)
            }?.lag
            Snapshot(first, data, valid, generation, evidenceGeneration, trustedLag)
        } ?: return unavailable()
        val endHalfFrame = headFrame * 2
        // The most recent rendered block must be known, even when an older lag fits.
        // Otherwise an unoffered section could leak self speech as apparent user speech.
        for (i in 0 until count) {
            if (snapshot.sample(endHalfFrame - (count - i) * 3L).isNaN()) return unavailable()
        }
        val full = moments(input, count, 1)
        val output = input.copyOf(count)
        var fittedEcho: Fit? = null
        var fittedLag = 0
        var residualCoherence = 0.0
        var residualLag = 0
        var observedPath: Fit? = null
        if (full.energy > 1e-8 && count >= MIN_CORRELATION_SAMPLES && snapshot.data.any { it != 0.toShort() }) {
            // Interpolate once rather than recomputing phase/validity in every
            // inner product. Scan every delay, including high-frequency echoes
            // whose only coherent peak falls between older four-sample steps.
            val reference = DoubleArray(MAX_DELAY_SAMPLES + count) { i ->
                snapshot.sample(endHalfFrame - (count + MAX_DELAY_SAMPLES - i) * 3L)
            }
            val sparse = moments(input, count, COARSE_STRIDE)
            val fit = Fit()
            val peaks = ArrayList<Peak>(COARSE_PEAKS)
            for (lag in 0..MAX_DELAY_SAMPLES step COARSE_STEP) {
                computeFit(input, count, COARSE_STRIDE, sparse, reference, lag, fit)
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
                    computeFit(input, count, 1, full, reference, lag, fit)
                    if (!fit.score.isNaN() && (best == null || fit.score > best.score)) {
                        best = fit.copy(); bestLag = lag
                    }
                }
            }
            fittedEcho = best
            fittedLag = bestLag
            val trustedLag = snapshot.trustedLag
            var subtractionLag = bestLag
            if (trustedLag != null) {
                var bestLocalHit: Fit? = null
                for (lag in maxOf(0, trustedLag - MAX_PROFILE_LAG_DRIFT)..minOf(MAX_DELAY_SAMPLES, trustedLag + MAX_PROFILE_LAG_DRIFT)) {
                    computeFit(input, count, 1, full, reference, lag, fit)
                    if (!fit.score.isNaN() && (bestLocalHit == null || fit.score > bestLocalHit.score)) {
                        bestLocalHit = fit.copy()
                        subtractionLag = lag
                    }
                }
                observedPath = bestLocalHit
            }
            val match = if (trustedLag != null) observedPath else best
            if (match != null && match.score >= MIN_CORRELATION_SQUARED) {
                // A coherent but out-of-range fit must not become raw self speech.
                if (abs(match.gain) > MAX_ABS_GAIN) return unavailable()
                for (i in 0 until count) {
                    val renderedSample = reference[MAX_DELAY_SAMPLES - subtractionLag + i]
                    val residual = input[i] - full.mean - match.gain * (renderedSample - match.referenceMean)
                    output[i] = residual.roundToInt().coerceIn(Short.MIN_VALUE.toInt(), Short.MAX_VALUE.toInt()).toShort()
                }
                if (evaluateEvidence) {
                    val residualMoments = moments(output, count, 1)
                    for (peak in peaks) {
                        for (lag in maxOf(0, peak.lag - COARSE_STEP)..minOf(MAX_DELAY_SAMPLES, peak.lag + COARSE_STEP)) {
                            computeFit(output, count, 1, residualMoments, reference, lag, fit)
                            if (!fit.score.isNaN() && fit.score > residualCoherence) {
                                residualCoherence = fit.score
                                residualLag = lag
                            }
                        }
                    }
                }
            } else if (best != null) {
                residualCoherence = best.score
                residualLag = bestLag
            }
        }
        val renderEnvelope = if (evaluateEvidence) snapshot.renderEnvelope(requiredFrames) else null
        val residualRms = if (evaluateEvidence) sqrt(moments(output, count, 1).energy / count) else 0.0
        return synchronized(lock) {
            if (generation != snapshot.generation ||
                (evaluateEvidence && evidenceGeneration != snapshot.evidenceGeneration)) {
                input.fill(0, 0, count)
                HandsFreeBargeInEvidence.UNAVAILABLE
            } else {
                output.copyInto(input, 0, 0, count)
                if (evaluateEvidence) independentEvidence(
                    snapshot, renderEnvelope, count, headFrame, sqrt(full.energy / count),
                    residualRms, residualCoherence, residualLag, fittedEcho, fittedLag, observedPath
                ) else HandsFreeBargeInEvidence.AMBIGUOUS
            }
        }
    }

    /** Called under lock. Coherent far-end blocks train an upper echo envelope.
     * Suspected near speech cannot teach the profile. An unmodeled block must
     * exceed the envelope by a wide margin before it can reach speech detection.
     */
    private fun independentEvidence(
        snapshot: Snapshot, renderEnvelope: Double?, count: Int, headFrame: Long, micRms: Double,
        residualRms: Double, residualCoherence: Double, residualLag: Int, match: Fit?, lag: Int,
        observedPath: Fit?
    ): HandsFreeBargeInEvidence {
        val previous = lastEvidenceHead
        lastEvidenceHead = headFrame
        quietReference = false
        val advance = if (previous == null) 0L else headFrame - previous
        if (previous != null && advance == 0L) return HandsFreeBargeInEvidence.AMBIGUOUS
        if (advance < 0 || advance > count * 3L) {
            profile = null
            return HandsFreeBargeInEvidence.AMBIGUOUS
        }
        if (renderEnvelope == null) {
            profile = null
            return HandsFreeBargeInEvidence.AMBIGUOUS
        }
        if (renderEnvelope <= QUIET_RENDER_RMS) {
            quietReference = true
            return HandsFreeBargeInEvidence.INDEPENDENT
        }

        // Speech consists of intermittent voiced syllables. A well-observed
        // path survives short intervening unvoiced frames, but expires without
        // enough recent observations for calibration.
        val learned = profile?.takeIf {
            headFrame - it.lastVerifiedHead <= PROFILE_RECENCY_FRAMES &&
                (it.blocks >= PROFILE_BLOCKS || headFrame - it.firstVerifiedHead <= PROFILE_TRAIN_FRAMES)
        }
        profile = learned
        if (match != null && match.score >= PROFILE_CORRELATION_SQUARED &&
            match.referenceRms >= MIN_PROFILE_REFERENCE_RMS && micRms >= MIN_CALIBRATION_MIC_RMS) {
            val gain = abs(match.gain)
            val leakageGain = residualRms / match.referenceRms
            if (learned != null && learned.compatible(lag, match.gain)) {
                learned.maximumGain = maxOf(learned.maximumGain, gain)
                learned.maximumLeakageGain = maxOf(learned.maximumLeakageGain, leakageGain)
                learned.blocks = minOf(PROFILE_BLOCKS, learned.blocks + 1)
                learned.lastVerifiedHead = headFrame
                learned.alternateLag = null
                learned.alternateBlocks = 0
                return HandsFreeBargeInEvidence.AMBIGUOUS
            }
            if (learned == null || learned.blocks < PROFILE_BLOCKS) {
                profile = EchoProfile(lag, match.gain, gain, leakageGain, headFrame)
                return HandsFreeBargeInEvidence.AMBIGUOUS
            }
            // An unrelated voiced vowel can happen to fit a far-away render
            // lag in a single 20 ms block. Move the learned path only when a
            // changed lag persists, with close-fitting echo blocked meanwhile.
            val alternate = learned.alternateLag
            learned.alternateBlocks = if (alternate != null && abs(alternate - lag) <= MAX_PROFILE_LAG_DRIFT)
                learned.alternateBlocks + 1 else 1
            learned.alternateLag = lag
            if (learned.alternateBlocks >= ALTERNATE_PATH_BLOCKS) {
                profile = EchoProfile(lag, match.gain, gain, leakageGain, headFrame)
                return HandsFreeBargeInEvidence.AMBIGUOUS
            }
            if (match.score >= ALMOST_EXACT_ECHO_SQUARED) return HandsFreeBargeInEvidence.AMBIGUOUS
        } else if (learned != null) {
            learned.alternateLag = null
            learned.alternateBlocks = 0
        }
        if (learned == null || learned.blocks < PROFILE_BLOCKS) return HandsFreeBargeInEvidence.AMBIGUOUS
        // A coherent but louder copy at the calibrated delay may be a volume or
        // path change. Do not accept it as a foreground speaker.
        if (observedPath != null && observedPath.score >= PATH_CHANGE_CORRELATION_SQUARED &&
            abs(observedPath.gain) > learned.maximumGain * MAX_GAIN_CHANGE) {
            profile = null
            return HandsFreeBargeInEvidence.AMBIGUOUS
        }
        val pathEnvelope = snapshot.pathEnvelope(count, learned.lag)
        val echoBound = pathEnvelope * learned.maximumGain + renderEnvelope * learned.maximumLeakageGain
        // Real reflections stay at a physical delay. One coincidental match
        // to another vowel can occur at an unrelated lag, often rising by
        // nearly a frame every block as the reference and microphone advance.
        val persistentEcho = learned.observeResidual(residualLag, residualCoherence)
        if (!persistentEcho &&
            micRms >= maxOf(MIN_NEAR_RMS, echoBound * MIC_ECHO_MARGIN) &&
            residualRms >= maxOf(MIN_NEAR_RMS, echoBound * RESIDUAL_ECHO_MARGIN)) {
            return HandsFreeBargeInEvidence.INDEPENDENT
        }
        return HandsFreeBargeInEvidence.AMBIGUOUS
    }

    private data class EchoProfile(
        val lag: Int, val gain: Double, var maximumGain: Double, var maximumLeakageGain: Double,
        val firstVerifiedHead: Long, var lastVerifiedHead: Long = firstVerifiedHead, var blocks: Int = 1,
        var alternateLag: Int? = null, var alternateBlocks: Int = 0
    ) {
        private val residualLags = IntArray(RESIDUAL_HISTORY) { -1 }
        private var nextResidualLag = 0

        fun observeResidual(lag: Int, coherence: Double): Boolean {
            val suspected = coherence >= MAX_NEAR_CORRELATION_SQUARED
            val repeated = suspected && residualLags.any { it >= 0 && abs(it - lag) <= MAX_PROFILE_LAG_DRIFT }
            residualLags[nextResidualLag] = if (suspected) lag else -1
            nextResidualLag = (nextResidualLag + 1) % residualLags.size
            return repeated
        }


        fun compatible(lag: Int, gain: Double): Boolean {
            val ratio = gain / this.gain
            return abs(this.lag - lag) <= MAX_PROFILE_LAG_DRIFT &&
                ratio in (1.0 / MAX_GAIN_CHANGE)..MAX_GAIN_CHANGE
        }
    }

    private data class Snapshot(
        val firstFrame: Long, val data: ShortArray, val valid: BooleanArray,
        val generation: Long, val evidenceGeneration: Long, val trustedLag: Int?
    ) {
        /** Highest 10 ms RMS over the full possible echo path. Render must be
         * known for the whole horizon, including any late reflections.
         */
        fun renderEnvelope(requiredFrames: Int): Double? {
            if (data.size != requiredFrames || data.size < RENDER_ENVELOPE_FRAMES) return null
            if (!valid.all { it }) return null
            return envelope(0, data.size)
        }

        fun pathEnvelope(count: Int, lag: Int): Double {
            val first = (data.size - ((count + lag + MAX_PROFILE_LAG_DRIFT) * 3 + 1) / 2).coerceAtLeast(0)
            val end = data.size - (lag - MAX_PROFILE_LAG_DRIFT).coerceAtLeast(0) * 3 / 2
            return envelope(first, end)
        }

        private fun envelope(first: Int, end: Int): Double {
            var sum = 0.0
            var peak = 0.0
            val window = minOf(RENDER_ENVELOPE_FRAMES, end - first)
            for (i in first until end) {
                val sample = data[i].toDouble()
                sum += sample * sample
                if (i - first >= window) {
                    val old = data[i - window].toDouble()
                    sum -= old * old
                }
                if (i - first >= window - 1) peak = maxOf(peak, sum)
            }
            return sqrt(peak / window)
        }

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
    private data class Fit(
        var score: Double = Double.NaN, var gain: Double = 0.0,
        var referenceMean: Double = 0.0, var referenceRms: Double = 0.0
    )
    private fun moments(input: ShortArray, count: Int, stride: Int): Moments {
        var sum = 0.0; var squares = 0.0; var samples = 0
        for (i in 0 until count step stride) { val x = input[i].toDouble(); sum += x; squares += x * x; samples++ }
        return Moments(sum, sum / samples, (squares - sum * sum / samples).coerceAtLeast(0.0), samples)
    }
    private fun computeFit(
        input: ShortArray, count: Int, stride: Int, capture: Moments,
        reference: DoubleArray, lag: Int, result: Fit
    ) {
        result.score = Double.NaN
        if (capture.energy <= 1e-8) return
        var sum = 0.0; var squares = 0.0; var cross = 0.0
        val start = MAX_DELAY_SAMPLES - lag
        for (i in 0 until count step stride) {
            val y = reference[start + i]
            if (y.isNaN()) return
            sum += y; squares += y * y; cross += input[i] * y
        }
        val energy = squares - sum * sum / capture.samples
        if (energy <= 1e-8) return
        val dot = cross - capture.sum * sum / capture.samples
        result.score = (dot * dot / (capture.energy * energy)).coerceIn(0.0, 1.0)
        result.gain = dot / energy
        result.referenceMean = sum / capture.samples
        result.referenceRms = sqrt(energy / capture.samples)
    }

    companion object {
        internal const val REFERENCE_FRAMES = 28800 // 1.2 s of mono 24 kHz PCM.
        private const val MAX_CAPTURE_FRAMES = 320 // 20 ms at 16 kHz.
        private const val MIN_CORRELATION_SAMPLES = 64
        private const val MAX_DELAY_SAMPLES = 4000 // 250 ms at 16 kHz; never reads ahead of playback.
        private const val COARSE_STEP = 1
        private const val COARSE_STRIDE = 8
        private const val COARSE_PEAKS = 4
        private const val COARSE_PEAK_SEPARATION = 16
        private const val MIN_CORRELATION_SQUARED = 0.4225 // |correlation| >= .65.
        private const val MAX_ABS_GAIN = 4.0
        private const val RENDER_ENVELOPE_FRAMES = 240 // 10 ms at 24 kHz.
        private const val QUIET_RENDER_RMS = 8.0
        private const val PROFILE_BLOCKS = 6 // 120 ms of far-end-coherent capture.
        private const val PROFILE_TRAIN_FRAMES = 24000L // Collect observations over 1 second.
        private const val PROFILE_RECENCY_FRAMES = 36000L // Expire 1.5 seconds after last observation.
        private const val PROFILE_CORRELATION_SQUARED = .80
        private const val MAX_NEAR_CORRELATION_SQUARED = .20
        private const val PATH_CHANGE_CORRELATION_SQUARED = .22
        private const val ALMOST_EXACT_ECHO_SQUARED = .94
        private const val ALTERNATE_PATH_BLOCKS = 3
        private const val RESIDUAL_HISTORY = 3
        private const val MIN_PROFILE_REFERENCE_RMS = 256.0
        private const val MIN_CALIBRATION_MIC_RMS = 32.0
        private const val MAX_PROFILE_LAG_DRIFT = 64 // 4 ms of capture/playback-head jitter.
        private const val MAX_GAIN_CHANGE = 1.5
        private const val MIN_NEAR_RMS = 96.0
        private const val MIC_ECHO_MARGIN = 1.65
        private const val RESIDUAL_ECHO_MARGIN = 1.4
    }
}
