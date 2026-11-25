package com.aegis.node

import kotlin.math.PI
import kotlin.math.sin
import org.junit.Assert.*
import org.junit.Test

class NodeHandsFreePlaybackSpectrumTest {
    private val size = HandsFreePlaybackSpectrum.WINDOW_FRAMES
    private fun tone(bin: Int, amplitude: Int = 16000): ShortArray =
        ShortArray(size) { (amplitude * sin(2.0 * PI * bin * it / size)).toInt().toShort() }

    private fun asBytes(samples: ShortArray): ByteArray = ByteArray(samples.size * 2).also { bytes ->
        for (index in samples.indices) {
            bytes[2 * index] = samples[index].toByte()
            bytes[2 * index + 1] = (samples[index].toInt() shr 8).toByte()
        }
    }

    private fun analyze(samples: ShortArray): FloatArray {
        val analyzer = HandsFreePlaybackSpectrum.Analyzer()
        var result: FloatArray? = null
        for (sample in samples) result = analyzer.add(sample)
        return checkNotNull(result)
    }

    @Test fun fftShowsRealFrequencyAndAmplitude() {
        val low = analyze(tone(10))
        val high = analyze(tone(160))
        assertTrue(low.indices.maxBy { low[it] } < high.indices.maxBy { high[it] })
        assertTrue(low.max() > .9f)
        assertTrue(high.max() > .9f)
        assertTrue(analyze(tone(10, 800)).max() < low.max() - .25f)
        assertTrue(analyze(ShortArray(size)).all { it == 0f })
        assertTrue((low + high).all { it.isFinite() && it in 0f..1f })
    }

    @Test fun spectrumUsesPlayedFramesNotQueuedAudioAndClearsOnClose() {
        val owner = Any()
        val stream = HandsFreePlaybackSpectrum.Stream("session")
        val output = FloatArray(HandsFreePlaybackSpectrum.BAR_COUNT)
        var head = 0L
        var notifications = 0
        HandsFreePlaybackSpectrum.attach(owner, "session")
        HandsFreePlaybackSpectrum.setListener(owner) { notifications++ }
        try {
            stream.start(HandsFreePlaybackClock({ head }))
            val bytes = asBytes(tone(10))
            stream.offer(bytes, 0, bytes.size, 0)
            assertTrue(notifications > 0)
            assertTrue(HandsFreePlaybackSpectrum.render(owner, output, 10L))
            assertTrue(output.all { it == 0f })
            head = size.toLong()
            assertTrue(HandsFreePlaybackSpectrum.render(owner, output, 20L))
            assertArrayEquals(analyze(tone(10)), output, .0001f)
            assertFalse(HandsFreePlaybackSpectrum.render(owner, output, 600_000_020L))
            assertTrue(output.all { it == 0f })
            stream.close()
            assertFalse(HandsFreePlaybackSpectrum.render(owner, output))
            assertTrue(output.all { it == 0f })
        } finally {
            stream.close()
            HandsFreePlaybackSpectrum.detach(owner)
        }
    }

    @Test fun staleOrClosedStreamsCannotReplaceNewPlayback() {
        val owner = Any()
        val old = HandsFreePlaybackSpectrum.Stream("session")
        val closed = HandsFreePlaybackSpectrum.Stream("session")
        val current = HandsFreePlaybackSpectrum.Stream("session")
        val closedLater = HandsFreePlaybackSpectrum.Stream("session")
        val output = FloatArray(HandsFreePlaybackSpectrum.BAR_COUNT)
        val low = asBytes(tone(10))
        val high = asBytes(tone(160))
        HandsFreePlaybackSpectrum.attach(owner, "session")
        try {
            closed.close()
            current.start(HandsFreePlaybackClock({ size.toLong() }))
            current.offer(high, 0, high.size, 0)
            old.start(HandsFreePlaybackClock({ size.toLong() }))
            old.offer(low, 0, low.size, 0)
            closed.start(HandsFreePlaybackClock({ size.toLong() }))
            closed.offer(low, 0, low.size, 0)
            closedLater.close()
            closedLater.start(HandsFreePlaybackClock({ size.toLong() }))
            closedLater.offer(low, 0, low.size, 0)
            old.close()
            assertTrue(HandsFreePlaybackSpectrum.render(owner, output))
            assertArrayEquals(analyze(tone(160)), output, .0001f)
            current.close()
            // Even after the newer stream is gone, an old callback cannot
            // re-attach playback to the visible panel.
            old.start(HandsFreePlaybackClock({ size.toLong() }))
            assertFalse(HandsFreePlaybackSpectrum.render(owner, output))
            assertTrue(output.all { it == 0f })
        } finally {
            old.close()
            closed.close()
            closedLater.close()
            current.close()
            HandsFreePlaybackSpectrum.detach(owner)
        }
    }

    @Test fun speakingPhasePreservesFirstQueuedPcmButBargeInClearsIt() {
        val owner = Any()
        val stream = HandsFreePlaybackSpectrum.Stream("session")
        val output = FloatArray(HandsFreePlaybackSpectrum.BAR_COUNT)
        val bytes = asBytes(tone(10))
        HandsFreePlaybackSpectrum.attach(owner, "session")
        try {
            stream.start(HandsFreePlaybackClock({ size.toLong() }))
            // The streaming backend publishes speaking AFTER the first PCM
            // write. Already measured audio must not disappear at that phase.
            stream.offer(bytes, 0, bytes.size, 0)
            HandsFreePlaybackSpectrum.phaseChanged(owner, HandsFreePanelState.PROCESSING, HandsFreePanelState.SPEAKING)
            assertTrue(HandsFreePlaybackSpectrum.render(owner, output))
            assertArrayEquals(analyze(tone(10)), output, .0001f)
            HandsFreePlaybackSpectrum.phaseChanged(owner, HandsFreePanelState.SPEAKING, HandsFreePanelState.CAPTURING)
            assertFalse(HandsFreePlaybackSpectrum.render(owner, output))
            assertTrue(output.all { it == 0f })
        } finally {
            stream.close()
            HandsFreePlaybackSpectrum.detach(owner)
        }
    }

    @Test fun regressedPlaybackHeadStaysNeutralUntilFreshStream() {
        val owner = Any()
        val stream = HandsFreePlaybackSpectrum.Stream("session")
        val output = FloatArray(HandsFreePlaybackSpectrum.BAR_COUNT)
        val bytes = asBytes(tone(10))
        var head = size.toLong()
        HandsFreePlaybackSpectrum.attach(owner, "session")
        try {
            stream.start(HandsFreePlaybackClock({ head }))
            stream.offer(bytes, 0, bytes.size, 0)
            stream.offer(bytes, 0, bytes.size, size.toLong())
            assertTrue(HandsFreePlaybackSpectrum.render(owner, output))
            head = 2L * size
            assertTrue(HandsFreePlaybackSpectrum.render(owner, output))
            head = size.toLong()
            assertFalse(HandsFreePlaybackSpectrum.render(owner, output))
            assertTrue(output.all { it == 0f })
            head = 3L * size
            stream.offer(bytes, 0, bytes.size, 2L * size)
            assertFalse(HandsFreePlaybackSpectrum.render(owner, output))
            assertTrue(output.all { it == 0f })
            val fresh = HandsFreePlaybackSpectrum.Stream("session")
            try {
                fresh.start(HandsFreePlaybackClock({ size.toLong() }))
                fresh.offer(bytes, 0, bytes.size, 0)
                assertTrue(HandsFreePlaybackSpectrum.render(owner, output))
                assertArrayEquals(analyze(tone(10)), output, .0001f)
            } finally { fresh.close() }
        } finally {
            stream.close()
            HandsFreePlaybackSpectrum.detach(owner)
        }
    }

    @Test fun partialWritesAndObserverGenerationCannotLeakOldAudio() {
        val owner = Any()
        val stream = HandsFreePlaybackSpectrum.Stream("session")
        val output = FloatArray(HandsFreePlaybackSpectrum.BAR_COUNT)
        var head = size.toLong()
        HandsFreePlaybackSpectrum.attach(owner, "session")
        try {
            stream.start(HandsFreePlaybackClock({ head }))
            val bytes = asBytes(tone(50))
            // The second offer partially overlaps a previous blocking device write.
            stream.offer(bytes, 0, 900, 0)
            stream.offer(bytes, 600, bytes.size - 600, 300)
            assertTrue(HandsFreePlaybackSpectrum.render(owner, output))
            assertArrayEquals(analyze(tone(50)), output, .0001f)
            HandsFreePlaybackSpectrum.reset(owner)
            assertFalse(HandsFreePlaybackSpectrum.render(owner, output))
            assertTrue(output.all { it == 0f })
            HandsFreePlaybackSpectrum.detach(owner)
            stream.offer(bytes, 0, bytes.size, size.toLong())
            HandsFreePlaybackSpectrum.attach(owner, "session")
            head = 2L * size
            assertFalse(HandsFreePlaybackSpectrum.render(owner, output))
            assertTrue(output.all { it == 0f })
        } finally {
            stream.close()
            HandsFreePlaybackSpectrum.detach(owner)
        }
    }
}
