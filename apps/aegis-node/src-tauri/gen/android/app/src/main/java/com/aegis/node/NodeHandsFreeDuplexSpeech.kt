package com.aegis.node

import kotlin.math.abs
import kotlin.math.sqrt

/** Keep a continuous sequence of 20 ms reference blocks, including on routes where
 * presentation timestamps are unavailable and the playback head advances in bursts.
 * A projection may use only committed PCM and must stay close to the observed head.
 */
internal class HandsFreeBargeInReferenceTimeline {
    private var previous: Long? = null

    fun next(observed: Long?, committed: Long): Long? {
        if (observed == null || observed <= 0) {
            reset()
            return null
        }
        val expected = previous?.plus(480)
        val end = if (expected != null &&
            abs(expected - observed) <= 1200 && expected <= maxOf(committed, observed))
            expected else observed
        if (expected != null && end != expected) {
            reset()
            return null
        }
        previous = end
        return end
    }

    fun reset() { previous = null }
}

/** A mute confirmed at the start has no acoustic echo. A live mute still
 * allows the already emitted sound to decay before using a silent reference.
 */
internal class HandsFreeBargeInOutputMute {
    private var observed = false
    private var muted = false
    private var settleFrames = 0

    fun update(value: Boolean) {
        if (muted == value) return
        muted = value
        settleFrames = if (value && observed) 20 else 0
    }

    fun apply(render: ShortArray) {
        observed = true
        if (!muted) return
        if (settleFrames > 0) settleFrames-- else render.fill(0)
    }
}

/** Processing is scoped to the active reply and its interrupted command.
 * The learned detector sees the microphone and the synchronized playback, independently
 * of how much speech the acoustic canceler happens to retain in its output.
 */
internal class HandsFreeDuplexSpeech(
    private val reference: HandsFreeBargeInEcho,
    private val detector: HandsFreeDoubleTalkDetector,
    private val hardware: Boolean,
    private val referenceGain: Float = 1f
) : AutoCloseable {
    private val software = if (hardware) null else NativeHandsFreeEcho.create()
    private val nativeQuality = if (hardware) HandsFreeNativeAecQuality() else null
    private val mute = HandsFreeBargeInOutputMute()
    private val render = ShortArray(HandsFreeVad.FRAME_SAMPLES)
    private val cleaned = ShortArray(render.size)
    private val linear = ShortArray(render.size)
    private val linearDelay = ShortArray(NativeHandsFreeEcho.SUPPRESSED_LATENCY_SAMPLES - NativeHandsFreeEcho.LINEAR_LATENCY_SAMPLES)
    private val quietLevels = DoubleArray(50)
    private var quietCount = 0
    private var quietPosition = 0
    private var hasReference = false
    private var failed = !hardware && software == null
    private var closed = false
    var keepForOnset = false
        private set
    var peakSpeechScore = 0f
        private set
    var qualifiedNativeFrames = 0
        private set
    val clearOnsetVotes: Boolean get() = nativeQuality?.clearVotes == true

    val available: Boolean get() = !closed && !failed
    val confirmationVotes: Int get() = detector.requiredVotes
    val confirmationWindowFrames: Int get() = detector.windowFrames
    fun voicePathReady(): Boolean = available && hasReference && detector.ready
    fun setOutputMuted(value: Boolean) = mute.update(value)

    /** Seed only the command endpoint's noise estimate. The lower quartile of
     * recent unconfirmed microphone levels estimates the ambient floor after cut.
     * The cap prevents loud playback from setting an excessive speech threshold.
     */
    fun commandNoiseFloor(): Double {
        if (quietCount < 5) return 8.0
        val values = quietLevels.copyOf(quietCount).also { it.sort() }
        return values[quietCount / 4].coerceIn(8.0, 300.0)
    }

    fun inspect(frame: ShortArray, count: Int, head: Long): HandsFreeBargeInEvidence {
        require(count == render.size && frame.size == count)
        if (!available || !reference.copyRender(render, head)) {
            if (hasReference) reset()
            frame.fill(0)
            return HandsFreeBargeInEvidence.UNAVAILABLE
        }
        hasReference = true
        mute.apply(render)
        if (referenceGain != 1f) {
            for (i in render.indices) render[i] = (render[i] * referenceGain).toInt().toShort()
        }
        if (!hardware && !processSoftware(frame)) {
            frame.fill(0)
            return HandsFreeBargeInEvidence.UNAVAILABLE
        }
        val strict = if (hardware) detector.observe(frame, render)
            else detector.observe(frame, render, cleaned, linear)
        val independent = nativeQuality?.observe(frame, render, detector.lastScore, strict) ?: strict
        if (nativeQuality?.qualified == true) qualifiedNativeFrames++
        if (detector.ready) peakSpeechScore = maxOf(peakSpeechScore, detector.lastScore)
        keepForOnset = nativeQuality?.vetoed != true && detector.ready &&
            (independent || detector.lastScore >= 0.25f)
        if (detector.ready && !independent) {
            // Measure before software suppression. On handoff the speaker goes
            // quiet and we may preserve speech from the less-suppressed output.
            val mean = frame.sumOf { it.toDouble() } / frame.size
            quietLevels[quietPosition] = sqrt(maxOf(0.0, frame.sumOf { it.toDouble() * it } / frame.size - mean * mean))
            quietPosition = (quietPosition + 1) % quietLevels.size
            quietCount = minOf(quietCount + 1, quietLevels.size)
        }
        if (!hardware) copyProcessed(frame, preserveVoice = independent)
        return if (detector.ready && independent) HandsFreeBargeInEvidence.INDEPENDENT
            else HandsFreeBargeInEvidence.AMBIGUOUS
    }

    private fun processSoftware(frame: ShortArray): Boolean {
        val processor = software ?: return false
        if (!processor.process(render, frame, cleaned, linear = linear)) {
            failed = true
            return false
        }
        // The two AEC outputs have different pipeline delays. Keep both sample-aligned.
        for (i in linear.indices) {
            val slot = i % linearDelay.size
            val prior = linearDelay[slot]
            linearDelay[slot] = linear[i]
            linear[i] = prior
        }
        return true
    }

    private fun copyProcessed(frame: ShortArray, preserveVoice: Boolean) {
        // The nonlinear suppressor can attenuate the first syllable during convergence.
        // Use the linear output only when independent speech was confirmed by the model.
        val nonlinearEnergy = cleaned.sumOf { it.toDouble() * it }
        val linearEnergy = linear.sumOf { it.toDouble() * it }
        val output = if (preserveVoice && nonlinearEnergy < linearEnergy / 16.0) linear else cleaned
        output.copyInto(frame)
    }

    fun afterInterruption(frame: ShortArray) {
        if (hardware || !available) return
        render.fill(0)
        // Playback has been stopped. Consume the acoustic tail without feeding future TTS.
        if (processSoftware(frame)) copyProcessed(frame, preserveVoice = true)
    }

    fun reset() {
        if (closed) return
        hasReference = false
        keepForOnset = false
        detector.reset()
        nativeQuality?.reset()
        quietLevels.fill(0.0)
        quietPosition = 0
        quietCount = 0
        render.fill(0)
        cleaned.fill(0)
        linear.fill(0)
        linearDelay.fill(0)
        if (software != null && !software.reset()) failed = true
    }

    override fun close() {
        if (closed) return
        closed = true
        try { software?.close() } finally {
            detector.reset()
            nativeQuality?.reset()
            quietLevels.fill(0.0)
            render.fill(0)
            cleaned.fill(0)
            linear.fill(0)
            linearDelay.fill(0)
        }
    }
}
