package com.aegis.node

import org.junit.Assert.*
import org.junit.Test
import kotlin.math.PI
import kotlin.math.sin
import kotlin.random.Random

class NodeHandsFreeMechanicalNoiseGateTest {
    private fun motor(frequency: Int, amplitude: Int, harmonic: Boolean, milliseconds: Int = 600): List<ShortArray> {
        val size = milliseconds * 16
        val samples = ShortArray(size) { i ->
            val phase = 2 * PI * frequency * i / 16000.0
            val ramp = minOf(1.0, i / 160.0, (size - 1 - i) / 160.0)
            val value = if (harmonic) (sin(phase) + .36 * sin(2 * phase) +
                .18 * sin(3 * phase) + .08 * sin(5 * phase)) / 1.62 else sin(phase)
            (value * amplitude * ramp).toInt().coerceIn(-32767, 32767).toShort()
        }
        return List(size / 320) { samples.copyOfRange(it * 320, (it + 1) * 320) }
    }

    private fun changingSpeechLikeFrame(block: Int, offset: Int = 0) = ShortArray(320) { i ->
        val t = (block * 320 + i + offset) / 16000.0
        val phase = 2 * PI * (175 * t + 70 * t * t)
        val formants = sin(2 * PI * (650 + block * 10) * t) + .75 * sin(2 * PI * 1350 * t) +
            .4 * sin(2 * PI * 2450 * t)
        (1500 * sin(phase) + 900 * formants).toInt().toShort()
    }

    @Test fun stationaryMotorVetoesEvenStrongDetectorVotesInBothPaths() {
        for (learned in listOf(false, true)) {
            for (frequency in listOf(140, 180, 220, 260)) {
                for (amplitude in listOf(1200, 24000, 48000)) {
                    for (harmonic in listOf(false, true)) {
                        val onset = HandsFreeBargeInOnset(HandsFreeSpeechClassifier { pcm -> pcm.any { it != 0.toShort() } },
                            learnedEvidence = learned, windowFrames = if (learned) 12 else 7,
                            requiredObservations = if (learned) 6 else 4)
                        repeat(15) { assertNull(onset.process(ShortArray(320), HandsFreeBargeInEvidence.AMBIGUOUS, false)) }
                        motor(frequency, amplitude, harmonic).forEach {
                            assertNull("learned=$learned f=$frequency amp=$amplitude harmonic=$harmonic",
                                onset.process(it, HandsFreeBargeInEvidence.INDEPENDENT))
                        }
                        repeat(8) { assertNull(onset.process(ShortArray(320), HandsFreeBargeInEvidence.AMBIGUOUS, false)) }
                        assertTrue(onset.mechanicalVetoFrames >= 20)
                        onset.clear()
                    }
                }
            }
        }
    }

    @Test fun briefBroadbandForegroundStillConfirmsAfterOrOverMotor() {
        for (learned in listOf(false, true)) {
            val votes = if (learned) 6 else 4
            for (overlap in listOf(false, true)) {
                val onset = HandsFreeBargeInOnset(HandsFreeSpeechClassifier { true }, learnedEvidence = learned,
                    windowFrames = if (learned) 12 else 7, requiredObservations = votes)
                val buzz = motor(180, 1200, true)
                repeat(15) { assertNull(onset.process(ShortArray(320), HandsFreeBargeInEvidence.AMBIGUOUS, false)) }
                buzz.take(15).forEach { assertNull(onset.process(it, HandsFreeBargeInEvidence.INDEPENDENT)) }
                val speech = List(votes) { block ->
                    changingSpeechLikeFrame(block).also { frame ->
                        if (overlap) for (i in frame.indices) frame[i] =
                            (frame[i] + buzz[15 + block][i]).toShort()
                    }
                }
                var prefix: ShortArray? = null
                speech.forEach { frame -> if (prefix == null) prefix = onset.process(frame, HandsFreeBargeInEvidence.INDEPENDENT) }
                val confirmed = checkNotNull(prefix)
                assertArrayEquals(speech.first(), confirmed.copyOfRange(confirmed.size - speech.size * 320,
                    confirmed.size - (speech.size - 1) * 320))
                // A retained lead-in may include the transition, but not every
                // screened, sustained mechanical frame before the speech.
                assertTrue(confirmed.take(confirmed.size - speech.size * 320).any { it == 0.toShort() })
            }
        }
    }

    @Test fun gateDoesNotTreatBroadbandContactOrOrdinaryHighFrequencyAsMotor() {
        val gate = HandsFreeMechanicalNoiseGate()
        val random = Random(123)
        val contact = motor(180, 14000, true).map { frame ->
            ShortArray(320) { i -> (frame[i] + random.nextInt(-11000, 11001)).coerceIn(-32767, 32767).toShort() }
        }
        assertTrue(contact.none { gate.observe(it) })
        gate.reset()
        val high = ShortArray(320) { (if (it % 2 == 0) 1500 else -1500).toShort() }
        repeat(30) { assertFalse(gate.observe(high)) }
    }

    @Test fun unavailableReferenceClearsMechanicalHistoryAndFreshSpeechUsesExistingCadence() {
        val gate = HandsFreeMechanicalNoiseGate()
        val frames = motor(180, 25000, false)
        assertTrue(frames.count { gate.observe(it) } >= 25)
        gate.reset()
        assertFalse(gate.observe(frames[10])) // no previous samples from the old acoustic path
        val onset = HandsFreeBargeInOnset(HandsFreeSpeechClassifier { true }, learnedEvidence = true,
            requiredObservations = 4)
        frames.forEach { assertNull(onset.process(it, HandsFreeBargeInEvidence.INDEPENDENT)) }
        assertNull(onset.process(ShortArray(320), HandsFreeBargeInEvidence.UNAVAILABLE))
        val speech = List(4) { changingSpeechLikeFrame(it) }
        speech.take(3).forEach { assertNull(onset.process(it, HandsFreeBargeInEvidence.INDEPENDENT)) }
        assertNotNull(onset.process(speech.last(), HandsFreeBargeInEvidence.INDEPENDENT))
    }

    @Test fun diagnosticCountsOnlyMechanicalFramesWithIndependentEvidence() {
        val onset = HandsFreeBargeInOnset(HandsFreeSpeechClassifier { true }, learnedEvidence = true,
            requiredObservations = 6, windowFrames = 12)
        val motor = motor(180, 3600, true)
        motor.take(15).forEach {
            assertNull(onset.process(it, HandsFreeBargeInEvidence.AMBIGUOUS, retain = true))
        }
        assertEquals(0, onset.mechanicalVetoFrames)
        motor.drop(15).forEach { assertNull(onset.process(it, HandsFreeBargeInEvidence.INDEPENDENT)) }
        assertTrue(onset.mechanicalVetoFrames >= 12)
        val total = onset.mechanicalVetoFrames
        onset.reset()
        assertEquals(total, onset.mechanicalVetoFrames) // Per-capture aggregate, not acoustic state.
    }
}
