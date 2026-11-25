package com.aegis.node

import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.sin
import kotlin.math.sqrt

/** Reject evidence for a stable motor-like tone, not speech in general. The
 * 40 ms causal analysis is local to interruption candidates. No microphone or
 * playback gain, detector threshold, or command PCM is changed by this gate.
 * A vibration with broadband contact noise may not satisfy these narrow tests.
 */
internal class HandsFreeMechanicalNoiseGate {
    private val previous = ShortArray(320)
    private var ready = false
    private val waveform = DoubleArray(640)
    private val real = DoubleArray(1024)
    private val imaginary = DoubleArray(1024)
    private val priorSpectrum = DoubleArray(321)
    private val spectrum = DoubleArray(321)

    fun observe(frame: ShortArray): Boolean {
        require(frame.size == previous.size)
        if (!ready) {
            frame.copyInto(previous)
            ready = true
            return false
        }
        var mean = 0.0
        for (i in previous.indices) {
            waveform[i] = previous[i].toDouble()
            waveform[i + 320] = frame[i].toDouble()
            mean += waveform[i] + waveform[i + 320]
        }
        frame.copyInto(previous)
        mean /= waveform.size
        var power = 0.0
        for (i in waveform.indices) {
            waveform[i] -= mean
            power += waveform[i] * waveform[i]
        }
        if (power < waveform.size * 45.0 * 45.0) {
            priorSpectrum.fill(0.0)
            return false
        }

        // 40 ms Hann, 1024 point FFT; 15.625 Hz/bin at 16 kHz. Padding
        // permits narrow-band measurements without delaying the current vote.
        real.fill(0.0)
        imaginary.fill(0.0)
        for (i in waveform.indices) real[BIT_REVERSED[i]] = waveform[i] * HANN[i]
        var length = 2
        while (length <= real.size) {
            val half = length / 2
            val stride = real.size / length
            for (start in real.indices step length) for (j in 0 until half) {
                val left = start + j
                val right = left + half
                val k = j * stride
                val a = real[right] * COS[k] - imaginary[right] * SIN[k]
                val b = real[right] * SIN[k] + imaginary[right] * COS[k]
                real[right] = real[left] - a
                imaginary[right] = imaginary[left] - b
                real[left] += a
                imaginary[left] += b
            }
            length *= 2
        }
        for (k in spectrum.indices) spectrum[k] = real[k] * real[k] + imaginary[k] * imaginary[k]
        var total = 0.0
        var low = 0.0
        var high = 0.0
        for (k in 4..320) total += spectrum[k] // 60–5000 Hz
        for (k in 4..28) low += spectrum[k] // 60–450 Hz
        for (k in 39..256) high += spectrum[k] // 600–4000 Hz
        var dot = 0.0
        var currentPower = 0.0
        var priorPower = 0.0
        for (k in 4..256) {
            dot += spectrum[k] * priorSpectrum[k]
            currentPower += spectrum[k] * spectrum[k]
            priorPower += priorSpectrum[k] * priorSpectrum[k]
        }
        val stationary = currentPower > 0.0 && priorPower > 0.0 &&
            dot / sqrt(currentPower * priorPower) >= .999
        spectrum.copyInto(priorSpectrum)
        if (total <= 0.0 || low < .85 * total || high > .08 * total) return false

        // Either almost all energy lies in one narrow peak or the spectral
        // shape remains almost unchanged across adjacent observations.
        var peak = 4
        for (k in 5..25) if (spectrum[k] > spectrum[peak]) peak = k
        var narrow = 0.0
        for (k in peak - 3..peak + 3) narrow += spectrum[k]
        if (!stationary && narrow < .95 * total) return false

        // Require near-repetition of the actual waveform, not merely a
        // prominent low fundamental. Run only for the unusual frames above.
        for (lag in 40..200) {
            var cross = 0.0
            var a = 0.0
            var b = 0.0
            for (i in lag until waveform.size) {
                val u = waveform[i]
                val v = waveform[i - lag]
                cross += u * v
                a += u * u
                b += v * v
            }
            if (a > 0.0 && b > 0.0 && cross >= .985 * sqrt(a * b)) return true
        }
        return false
    }

    fun reset() {
        ready = false
        previous.fill(0)
        waveform.fill(0.0)
        real.fill(0.0)
        imaginary.fill(0.0)
        priorSpectrum.fill(0.0)
        spectrum.fill(0.0)
    }

    companion object {
        private val HANN = DoubleArray(640) { .5 - .5 * cos(2 * PI * it / 639) }
        private val BIT_REVERSED = IntArray(640) { Integer.reverse(it) ushr 22 }
        private val COS = DoubleArray(512) { cos(2 * PI * it / 1024) }
        private val SIN = DoubleArray(512) { -sin(2 * PI * it / 1024) }
    }
}
