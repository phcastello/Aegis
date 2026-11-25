package com.aegis.node

/** Testable boundary around the per-microphone AEC3 instance. */
internal interface HandsFreeMediaEcho : AutoCloseable {
    fun process(render: ShortArray, microphone: ShortArray, suppressed: ShortArray, linear: ShortArray): Boolean
}

private fun openExternalMediaEcho(): HandsFreeMediaEcho? {
    val native = NativeHandsFreeEcho.create() ?: return null
    return object : HandsFreeMediaEcho {
        override fun process(render: ShortArray, microphone: ShortArray, suppressed: ShortArray, linear: ShortArray) =
            native.process(render, microphone, suppressed, linear = linear)
        override fun close() = native.close()
    }
}

/**
 * External-media AEC uses only a consented reference on the same monotonic
 * sample timeline as the microphone. Keep the original microphone audio
 * during convergence and whenever an acoustic echo path has not been proven.
 *
 * The nonlinear suppressor can remove short independent speech at low SNR.
 * Use the linear AEC output, after observing repeated real echo reduction,
 * preserving near-end speech and keeping the existing wakeword detector.
 * This class owns no recorder, timers, logging or reference persistence.
 */
internal class HandsFreeExternalMediaProcessor(
    private val copyReference: (Long, ShortArray) -> Boolean,
    private val referenceGeneration: () -> Long,
    private val openEcho: () -> HandsFreeMediaEcho? = ::openExternalMediaEcho
) : AutoCloseable {
    private val render = ShortArray(FRAME)
    private val raw = ShortArray(FRAME)
    private val suppressed = ShortArray(FRAME)
    private val linear = ShortArray(FRAME)
    private val rawTail = ShortArray(NativeHandsFreeEcho.LINEAR_LATENCY_SAMPLES)
    private val evidence = BooleanArray(EVIDENCE_FRAMES)
    private val referenceCoverage = BooleanArray(EVIDENCE_FRAMES)
    private var coveragePosition = 0
    private var coverageCount = 0
    private var coverageMissing = 0
    private var echo: HandsFreeMediaEcho? = null
    private var generation: Long? = null
    private var failedGeneration: Long? = null
    private var lastEndNanos: Long? = null
    private var evidencePosition = 0
    private var strongFrames = 0
    private var sinceStrong = EVIDENCE_FRAMES
    private var quietFrames = 0
    private var missingFrames = 0
    private var previousRaw = false
    private var usedLinear = false
    private var closed = false

    /** Mutates only complete, qualified 20ms microphone frames. Returns true
     * when any frame was treated; missing reference always preserves raw PCM.
     */
    fun process(samples: ShortArray, count: Int, endNanos: Long): Boolean {
        require(count in 0..samples.size)
        check(!closed)
        if (count == 0 || endNanos < count * SAMPLE_NANOS) {
            reset()
            return false
        }
        val currentGeneration = referenceGeneration()
        if (generation != currentGeneration) {
            clearEcho()
            resetCoverage()
            generation = currentGeneration
            failedGeneration = null
        }
        val beginning = endNanos - count * SAMPLE_NANOS
        if (lastEndNanos?.let { kotlin.math.abs(beginning - it) > MAX_GAP_NANOS } == true) { clearEcho(); resetCoverage() }
        lastEndNanos = endNanos
        if (failedGeneration == currentGeneration) return false

        var changed = false
        var offset = 0
        while (offset + FRAME <= count) {
            val frameEnd = beginning + (offset + FRAME) * SAMPLE_NANOS
            val referenceAvailable = copyReference(frameEnd, render)
            if (referenceGeneration() != currentGeneration) {
                clearEcho()
                resetCoverage()
                break
            }
            if (!observeCoverage(referenceAvailable)) {
                if (echo != null) clearEcho()
                offset += FRAME
                continue
            }
            if (!referenceAvailable) {
                // Independent AudioRecord reads may temporarily leave the
                // last 20–40ms of a microphone read ahead of the reference
                // ring. Feed a bounded silent reference only to preserve
                // native state. Never forward its output or score it as
                // evidence; a longer gap requires fresh convergence.
                val processor = echo
                if (processor != null && ++missingFrames <= MAX_MISSING_FRAMES) {
                    render.fill(0)
                    samples.copyInto(raw, 0, offset, offset + FRAME)
                    if (!processor.process(render, raw, suppressed, linear)) {
                        clearEcho()
                        failedGeneration = currentGeneration
                        break
                    }
                    rememberRawTail()
                    observeEvidence(false)
                    usedLinear = false
                } else if (processor != null) clearEcho()
                offset += FRAME
                continue
            }
            missingFrames = 0
            val activeRender = power(render) >= MIN_RENDER_POWER
            quietFrames = if (activeRender) 0 else minOf(quietFrames + 1, QUIET_TAIL_FRAMES)
            if (!activeRender && (echo == null || quietFrames >= QUIET_TAIL_FRAMES)) {
                if (echo != null) clearEcho()
                offset += FRAME
                continue
            }
            val processor = echo ?: openEcho()?.also { echo = it }
            if (processor == null) {
                failedGeneration = currentGeneration
                break
            }
            samples.copyInto(raw, 0, offset, offset + FRAME)
            if (!processor.process(render, raw, suppressed, linear)) {
                clearEcho()
                failedGeneration = currentGeneration
                break
            }
            // Linear AEC has a fixed 64-sample latency. Compare its power to
            // the same interval of the original microphone, not louder or
            // quieter adjacent syllables.
            val alignedPower = if (previousRaw) power(rawTail) + power(raw, FRAME - rawTail.size) else power(raw)
            rememberRawTail()
            val linearPower = power(linear)
            val wasQualified = strongFrames >= MIN_STRONG_FRAMES && sinceStrong <= MAX_STRONG_AGE
            val strong = activeRender && alignedPower >= MIN_MIC_POWER && linearPower * 2 <= alignedPower
            observeEvidence(strong)

            val qualified = wasQualified && alignedPower > 0 && linearPower * 10 <= alignedPower * 13
            if (qualified) {
                // On entry, fade once across the 4ms difference in pipeline
                // delay. Subsequent frames retain a continuous AEC timeline.
                for (i in 0 until FRAME) {
                    samples[offset + i] = if (!usedLinear && i < rawTail.size) {
                        ((raw[i].toInt() * (rawTail.size - i) + linear[i].toInt() * i) / rawTail.size).toShort()
                    } else linear[i]
                }
                changed = true
            }
            usedLinear = qualified
            offset += FRAME
        }
        // Do not silently advance the AEC timeline across an unsupported
        // fractional frame. A partial microphone read remains valid raw PCM.
        if (offset != count) { clearEcho(); resetCoverage() }
        return changed
    }

    /** Called when the microphone timestamp becomes unavailable or the
     * recording route/session changes. The next paired block can reconverge.
     */
    fun reset() {
        clearEcho()
        resetCoverage()
        generation = null
        failedGeneration = null
        lastEndNanos = null
    }

    private fun clearEcho() {
        echo?.close()
        echo = null
        evidence.fill(false)
        render.fill(0)
        raw.fill(0)
        suppressed.fill(0)
        linear.fill(0)
        rawTail.fill(0)
        evidencePosition = 0
        strongFrames = 0
        sinceStrong = EVIDENCE_FRAMES
        quietFrames = 0
        missingFrames = 0
        previousRaw = false
        usedLinear = false
    }

    override fun close() {
        if (closed) return
        reset()
        render.fill(0)
        raw.fill(0)
        suppressed.fill(0)
        linear.fill(0)
        closed = true
    }

    /** A partial reference every read can systematically distort the wake
     * signal even when the adaptive state survives. Fail open on poor recent
     * coverage and avoid native work until this same timeline recovers.
     */
    private fun observeCoverage(available: Boolean): Boolean {
        if (coverageCount == referenceCoverage.size) {
            if (referenceCoverage[coveragePosition]) coverageMissing--
        } else coverageCount++
        referenceCoverage[coveragePosition] = !available
        if (!available) coverageMissing++
        coveragePosition = (coveragePosition + 1) % referenceCoverage.size
        return coverageMissing * 10 <= coverageCount
    }

    private fun resetCoverage() {
        referenceCoverage.fill(false)
        coveragePosition = 0
        coverageCount = 0
        coverageMissing = 0
    }

    private fun rememberRawTail() {
        raw.copyInto(rawTail, 0, FRAME - rawTail.size, FRAME)
        previousRaw = true
    }

    private fun observeEvidence(strong: Boolean) {
        if (evidence[evidencePosition]) strongFrames--
        evidence[evidencePosition] = strong
        if (strong) strongFrames++
        evidencePosition = (evidencePosition + 1) % evidence.size
        sinceStrong = if (strong) 0 else minOf(sinceStrong + 1, EVIDENCE_FRAMES)
    }

    private fun power(samples: ShortArray, count: Int = samples.size): Long {
        var energy = 0L
        for (i in 0 until count) {
            val value = samples[i].toLong()
            energy += value * value
        }
        return energy
    }

    private companion object {
        const val FRAME = 320
        const val SAMPLE_NANOS = 62_500L
        const val MAX_GAP_NANOS = 20_000_000L
        const val QUIET_TAIL_FRAMES = 15
        const val MAX_MISSING_FRAMES = 2
        const val EVIDENCE_FRAMES = 50
        const val MIN_STRONG_FRAMES = 8
        const val MAX_STRONG_AGE = 40
        const val MIN_RENDER_POWER = 120L * 120L * FRAME
        const val MIN_MIC_POWER = 80L * 80L * FRAME
    }
}
