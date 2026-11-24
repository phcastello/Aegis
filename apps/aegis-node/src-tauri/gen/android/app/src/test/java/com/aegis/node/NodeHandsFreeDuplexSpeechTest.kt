package com.aegis.node

import org.junit.Assert.*
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder

class NodeHandsFreeDuplexSpeechTest {
    private fun rendered(size: Int): ByteArray =
        ByteBuffer.allocate(size * 2).order(ByteOrder.LITTLE_ENDIAN).also { out ->
            repeat(size) { out.putShort(it.toShort()) }
        }.array()

    @Test fun captureAlignedReferenceResamplesAndNeverUsesUncommittedTail() {
        val echo = HandsFreeBargeInEcho()
        val data = rendered(960)
        echo.offer(data, 0, data.size, 0)
        val frame = ShortArray(320)
        assertTrue(echo.copyRender(frame, 480))
        assertArrayEquals(ShortArray(320) { (it * 3 / 2).toShort() }, frame)
        assertTrue(echo.copyRender(frame, 960))
        assertArrayEquals(ShortArray(320) { (480 + it * 3 / 2).toShort() }, frame)
        assertFalse(echo.copyRender(frame, 961))
        assertTrue(frame.all { it == 0.toShort() })
    }

    @Test fun prerenderSilenceAndMissingPositionsAreExplicit() {
        val echo = HandsFreeBargeInEcho()
        val data = rendered(480)
        echo.offer(data, 0, data.size, 0)
        val frame = ShortArray(320)
        assertTrue(echo.copyRender(frame, 240))
        assertTrue(frame.take(160).all { it == 0.toShort() })
        echo.clear()
        echo.offer(data, 0, data.size, 960)
        assertFalse(echo.copyRender(frame, 1000))
        assertTrue(frame.all { it == 0.toShort() })
    }

    @Test fun quantizedHeadKeepsContiguousCommittedReference() {
        val timeline = HandsFreeBargeInReferenceTimeline()
        assertEquals(960L, timeline.next(960, 1440))
        assertEquals(1440L, timeline.next(960, 1920))
        assertEquals(1920L, timeline.next(1920, 2400))
        assertEquals(2400L, timeline.next(1920, 2880))
        assertNull(timeline.next(1920, 2400))
        assertEquals(2880L,timeline.next(2880, 3360))
        assertNull(timeline.next(7200, 7680))
    }

    @Test fun discontinuousReferenceResetsTheTimeline() {
        val timeline = HandsFreeBargeInReferenceTimeline()
        assertEquals(480L,timeline.next(480,960))
        assertNull(timeline.next(null,960))
        assertEquals(960L,timeline.next(960,1440))
        timeline.reset()
        assertEquals(480L,timeline.next(480,960))
    }

    @Test fun nativeRelaxationRequiresObservedSuppressionAndRevokesOnCoherentEcho() {
        val gate = HandsFreeNativeAecQuality()
        val random = java.util.Random(1123)
        val render = ShortArray(320) { (random.nextInt(12001) - 6000).toShort() }
        val quiet = ShortArray(320)
        val voice = ShortArray(320) { (random.nextInt(3001) - 1500).toShort() }

        // An enabled effect, without evidence of attenuation, retains strict detection.
        assertFalse(gate.observe(voice,render,.71f,false))
        gate.reset()
        repeat(7) {
            assertFalse(gate.observe(quiet,render,.2f,false))
            assertFalse(gate.qualified)
        }
        assertFalse(gate.observe(quiet,render,.2f,false))
        assertTrue(gate.qualified)
        assertTrue(gate.clearVotes)
        assertTrue(gate.observe(voice,render,.71f,false))
        assertFalse(gate.clearVotes)

        // A return of highly correlated own playback must also veto a strict vote.
        assertFalse(gate.observe(render,render,.95f,true))
        assertTrue(gate.vetoed)
        assertFalse(gate.qualified)
        assertTrue(gate.clearVotes)
        repeat(7) { assertFalse(gate.observe(quiet,render,.2f,false)) }
        assertFalse(gate.qualified)
    }

    @Test fun confirmedNoiseQualityDoesNotLeaveStaleRelaxedVotes() {
        val onset = HandsFreeBargeInOnset(HandsFreeSpeechClassifier { true }, learnedEvidence = true,
            windowFrames = 12, requiredObservations = 6)
        val frame = ShortArray(320) { 1200 }
        repeat(5) { assertNull(onset.process(frame,HandsFreeBargeInEvidence.INDEPENDENT)) }
        onset.clearVotes()
        repeat(5) { assertNull(onset.process(frame,HandsFreeBargeInEvidence.INDEPENDENT)) }
        assertNotNull(onset.process(frame,HandsFreeBargeInEvidence.INDEPENDENT))
    }

    @Test fun knownMuteSilencesReferenceOnlyAfterPreviouslyEmittedSoundCanDecay() {
        val mutedAtStart = HandsFreeBargeInOutputMute()
        val frame = ShortArray(320) { 1000 }
        mutedAtStart.update(true)
        mutedAtStart.apply(frame)
        assertTrue(frame.all { it == 0.toShort() })

        val live = HandsFreeBargeInOutputMute()
        frame.fill(1000)
        live.apply(frame)
        live.update(true)
        repeat(20) {
            live.apply(frame)
            assertTrue(frame.all { it == 1000.toShort() })
        }
        live.apply(frame)
        assertTrue(frame.all { it == 0.toShort() })
        live.update(false)
        frame.fill(1000)
        live.apply(frame)
        assertTrue(frame.all { it == 1000.toShort() })
    }

    @Test fun failedNativeCapturePlaybackRestoresBeforeRetryingMediaDevice() {
        val events = mutableListOf<String>()
        val device = object : HandsFreePcmDevice {
            override fun write(bytes: ByteArray, offset: Int, count: Int) = count
            override fun playedFrames() = 1L
            override fun endMarker(frame: Int, reached: () -> Unit) = Unit
            override fun play() { events += "play" }
            override fun stop() = Unit
            override fun close() { events += "close" }
        }
        val playback = HandsFreePlayback { nativeCapture: Boolean, _: () -> Unit ->
            if (nativeCapture) {
                events += "native_media"
                throw IllegalStateException("media route incompatible with AEC")
            }
            events += "software_media"
            device
        }
        val stream = playback.openStream(stillCurrent = { true }, onPlaybackEvent = {},
            prepareCommunication = { events += "prepare"; true },
            onCommunicationFailed = { events += "restore" })
        assertTrue(events.isEmpty())
        stream.write(ByteArray(4), 0, 4)
        stream.close()
        assertEquals(listOf("prepare", "native_media", "restore", "software_media", "play", "close"), events)
    }

    @Test fun cancellingWhilePreparingNativeAudioReleasesBeforeAnyDeviceIsOpened() {
        var opened = false
        var restored = false
        val playback = HandsFreePlayback { _: Boolean, _: () -> Unit ->
            opened = true
            error("a cancelled response cannot open a device")
        }
        val stream = playback.openStream(stillCurrent = { true }, onPlaybackEvent = {},
            prepareCommunication = { playback.cancel(); true },
            onCommunicationFailed = { restored = true })
        try {
            stream.write(ByteArray(2), 0, 2)
            fail("cancelled playback should not write")
        } catch (_: java.util.concurrent.CancellationException) { }
        finally { stream.close() }
        assertFalse(opened)
        assertTrue(restored)
    }

    @Test fun confirmedCommandEndpointsWithPreviouslyMeasuredStationaryNoise() {
        val command = ShortArray(320000)
        // The screened 300 ms lead-in is digital silence and must not erode the seed.
        for (i in 4800 until 6080) command[i] = (if (i % 2 == 0) 1200 else -1200).toShort()
        val capture = HandsFreeCommandCapture(command, 6080, HandsFreeSpeechClassifier { samples -> samples.any { it != 0.toShort() } },
            initialSpeechConfirmed = true, initialNoiseRms = 240.0)
        val renewedSpeech = ShortArray(320) { (if (it % 2 == 0) 800 else -800).toShort() }
        repeat(5) { assertFalse(capture.append(renewedSpeech, renewedSpeech.size)) }
        val noise = ShortArray(320) { (if (it % 2 == 0) 240 else -240).toShort() }
        repeat(39) { assertFalse(capture.append(noise, noise.size)) }
        assertTrue(capture.append(noise, noise.size))
        assertTrue(capture.hasUsefulAudio)
    }

    @Test fun learnedModelConfirmationWindowRequiresItsOwnNumberOfVotes() {
        val onset = HandsFreeBargeInOnset(HandsFreeSpeechClassifier { true }, learnedEvidence = true,
            windowFrames = 10, requiredObservations = 5)
        val signal = ShortArray(320) { 600 }
        repeat(4) {
            assertNull(onset.process(signal, HandsFreeBargeInEvidence.INDEPENDENT))
            assertNull(onset.process(signal, HandsFreeBargeInEvidence.AMBIGUOUS))
        }
        assertNotNull(onset.process(signal, HandsFreeBargeInEvidence.INDEPENDENT))
    }

    @Test fun learnedOnsetRetainsTentativeSpeechWithoutTreatingItAsConfirmation() {
        val onset = HandsFreeBargeInOnset(HandsFreeSpeechClassifier { false }, learnedEvidence = true,
            preSamples = HandsFreeBargeInOnset.DUPLEX_PRE_SAMPLES)
        val first = ShortArray(320) { 320 }
        val second = ShortArray(320) { 640 }
        repeat(15) { assertNull(onset.process(first, HandsFreeBargeInEvidence.AMBIGUOUS, retain = true)) }
        repeat(3) { assertNull(onset.process(second, HandsFreeBargeInEvidence.INDEPENDENT)) }
        val result = checkNotNull(onset.process(second, HandsFreeBargeInEvidence.INDEPENDENT))
        assertEquals(19 * 320, result.size)
        assertTrue(result.take(15 * 320).all { it == 320.toShort() })
        assertTrue(result.takeLast(4 * 320).all { it == 640.toShort() })
    }
}
