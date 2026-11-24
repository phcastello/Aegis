package com.aegis.node

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.atomic.AtomicReference
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.roundToInt
import kotlin.math.sin
import kotlin.math.sqrt

class NodeHandsFreeBargeInEchoTest {
    private fun reference(frames: Int): ShortArray = ShortArray(frames) { i ->
        val t = i / 24000.0
        val envelope = .75 + .20 * sin(2 * PI * 7.1 * t)
        (envelope * (5000 * sin(2 * PI * 211.3 * t) + 2800 * sin(2 * PI * 627.7 * t) +
            1300 * sin(2 * PI * 1171.9 * t) + 700 * sin(2 * PI * 2387.3 * t))).roundToInt().toShort()
    }
    private fun bytes(pcm: ShortArray): ByteArray = ByteArray(pcm.size * 2).also { out ->
        for (i in pcm.indices) { out[i * 2] = pcm[i].toByte(); out[i * 2 + 1] = (pcm[i].toInt() shr 8).toByte() }
    }
    private fun sample(pcm: ShortArray, halfFrame: Long): Double {
        val index = (halfFrame / 2).toInt()
        return if (halfFrame and 1L == 0L) pcm[index].toDouble() else (pcm[index].toDouble() + pcm[index + 1]) * .5
    }
    private fun echo(pcm: ShortArray, head: Long, delaySamples: Int, gain: Double): ShortArray =
        ShortArray(320) { i -> (sample(pcm, head * 2 - (320 - i + delaySamples) * 3L) * gain).roundToInt().toShort() }
    private fun rms(pcm: ShortArray): Double = sqrt(pcm.sumOf { it.toDouble() * it } / pcm.size)
    private fun projection(pcm: ShortArray, signal: ShortArray): Double =
        pcm.indices.sumOf { pcm[it].toDouble() * signal[it] } / signal.sumOf { it.toDouble() * it }

    @Test fun missingReferenceZeroHeadOrHeadBeyondOfferFailClosed() {
        val filter = HandsFreeBargeInEcho()
        val input = ShortArray(320) { 1000 }
        assertFalse(filter.suppress(input, 320, 480)); assertTrue(input.all { it == 0.toShort() })
        filter.offer(bytes(ShortArray(480)), 0, 960, 0)
        input.fill(1000); assertFalse(filter.suppress(input, 320, 0)); assertTrue(input.all { it == 0.toShort() })
        input.fill(1000); assertFalse(filter.suppress(input, 320, 481)); assertTrue(input.all { it == 0.toShort() })
    }
    @Test fun knownRenderedSilencePassesForegroundVoiceUnchanged() {
        val filter = HandsFreeBargeInEcho()
        filter.offer(bytes(ShortArray(24000)), 0, 48000, 0)
        val voice = ShortArray(320) { (3000 * sin(2 * PI * 1733 * it / 16000)).toInt().toShort() }
        val actual = voice.copyOf()
        assertTrue(filter.suppress(actual, actual.size, 20000))
        assertArrayEquals(voice, actual)
    }
    @Test fun scaledAndDelayedCoherentEchoIsRemovedAtFractionalResamplingPositions() {
        val pcm = reference(26400)
        for (head in listOf(23040L, 23041L)) for (delay in listOf(0, 13, 160, 799, 3999)) {
            val filter = HandsFreeBargeInEcho(); val data = bytes(pcm)
            filter.offer(data, 0, data.size, 0)
            val actual = echo(pcm, head, delay, .43)
            assertTrue(filter.suppress(actual, actual.size, head))
            assertTrue("head=$head delay=$delay rms=${rms(actual)}", rms(actual) < 3.0)
        }
    }
    @Test fun invertedEchoAndDcBiasAreRemovedWithoutASecondEchoEngine() {
        val pcm = reference(26400); val data = bytes(pcm)
        val filter = HandsFreeBargeInEcho(); filter.offer(data, 0, data.size, 0)
        val actual = echo(pcm, 23040, 413, -.52).also { for (i in it.indices) it[i] = (it[i] + 350).toShort() }
        assertTrue(filter.suppress(actual, actual.size, 23040))
        assertTrue(rms(actual) < 3.0)
    }
    @Test fun simultaneousVoiceWithDistinctSpectrumSurvivesEchoReduction() {
        val pcm = reference(26400); val data = bytes(pcm); val head = 23040L
        val voice = ShortArray(320) { (1500 * sin(2 * PI * 2781 * it / 16000) + 500 * sin(2 * PI * 3461 * it / 16000)).toInt().toShort() }
        val echo = echo(pcm, head, 421, .60)
        val mixed = ShortArray(320) { (voice[it] + echo[it]).toShort() }
        val beforeEcho = abs(projection(mixed, echo))
        val filter = HandsFreeBargeInEcho(); filter.offer(data, 0, data.size, 0)
        assertTrue(filter.suppress(mixed, mixed.size, head))
        assertTrue("voice retained", projection(mixed, voice) in .90..1.10)
        assertTrue("echo reduced", abs(projection(mixed, echo)) < beforeEcho * .12)
        assertTrue(rms(mixed) > rms(voice) * .90)
    }
    @Test fun dominantForegroundVoiceIsUsableWithoutDemandingAnEchoMatch() {
        val pcm = reference(26400); val data = bytes(pcm); val head = 23040L
        val voice = ShortArray(320) { (6000 * sin(2 * PI * 2917 * it / 16000)).toInt().toShort() }
        val echo = echo(pcm, head, 333, .12)
        val mixed = ShortArray(320) { (voice[it] + echo[it]).toShort() }
        val filter = HandsFreeBargeInEcho(); filter.offer(data, 0, data.size, 0)
        assertTrue(filter.suppress(mixed, mixed.size, head))
        assertTrue(projection(mixed, voice) > .90)
    }
    @Test fun aGapInTheRecentRenderedReferenceFailsClosedEvenIfAnOlderBlockMatches() {
        val pcm = reference(26400); val data = bytes(pcm); val filter = HandsFreeBargeInEcho()
        filter.offer(data, 0, 21000 * 2, 0)
        filter.offer(data, 22000 * 2, (pcm.size - 22000) * 2, 22000)
        val actual = echo(pcm, 22020, 1000, .5)
        assertFalse(filter.suppress(actual, actual.size, 22020))
        assertTrue(actual.all { it == 0.toShort() })
    }
    @Test fun partialWriteRetryOverwritesOfferedPositionsAndDoesNotUseFutureFrames() {
        val pcm = reference(26400); val data = bytes(pcm); val filter = HandsFreeBargeInEcho()
        val wrong = ByteArray(data.size)
        filter.offer(wrong, 0, wrong.size, 0)
        filter.offer(data, 0, 16000 * 2, 0)
        filter.offer(data, 16000 * 2, data.size - 16000 * 2, 16000)
        val actual = echo(pcm, 23040, 131, .5)
        assertTrue(filter.suppress(actual, actual.size, 23040)); assertTrue(rms(actual) < 3.0)
        val old = echo(pcm, 23040, 131, .5)
        filter.offer(bytes(ShortArray(2000) { Short.MAX_VALUE }), 0, 4000, 23040)
        assertTrue(filter.suppress(old, old.size, 23040)); assertTrue(rms(old) < 3.0)
    }
    @Test fun oversizedOffersStayBoundedAndAnOverwrittenHeadCannotBecomeUserSpeech() {
        val pcm = reference(70000); val data = bytes(pcm); val filter = HandsFreeBargeInEcho()
        filter.offer(data, 0, data.size, 0)
        val old = echo(pcm, 20000, 100, .5)
        assertFalse(filter.suppress(old, old.size, 20000))
        val current = echo(pcm, 69000, 200, .5)
        assertTrue(filter.suppress(current, current.size, 69000)); assertTrue(rms(current) < 3.0)
    }
    @Test fun frozenPlaybackHeadSupportsTheBoundedTailAndClearInvalidatesIt() {
        val pcm = reference(26400); val data = bytes(pcm); val filter = HandsFreeBargeInEcho()
        filter.offer(data, 0, data.size, 0)
        for (delay in listOf(0, 320, 1280, 3200)) {
            val tail = echo(pcm, 23040, delay, .3)
            assertTrue(filter.suppress(tail, tail.size, 23040)); assertTrue(rms(tail) < 3.0)
        }
        filter.clear()
        val old = echo(pcm, 23040, 300, .3)
        assertFalse(filter.suppress(old, old.size, 23040)); assertTrue(old.all { it == 0.toShort() })
    }
    @Test fun delayCanChangeBetweenBlocksWithoutResettingOrLosingTheNewReference() {
        val pcm = reference(26400); val data = bytes(pcm); val filter = HandsFreeBargeInEcho()
        filter.offer(data, 0, data.size, 0)
        for ((index, delay) in listOf(0, 111, 227, 803, 1577, 3999).withIndex()) {
            val head = 23040L + index * 480
            val actual = echo(pcm, head, delay, .4)
            assertTrue(filter.suppress(actual, actual.size, head))
            assertTrue("changing delay=$delay", rms(actual) < 3.0)
        }
    }
    @Test fun aSecondaryReflectionLeavesResidualButTheCoherentComponentIsReduced() {
        val pcm = reference(26400); val data = bytes(pcm); val filter = HandsFreeBargeInEcho()
        filter.offer(data, 0, data.size, 0)
        val direct = echo(pcm, 23040, 211, .60)
        val reflection = echo(pcm, 23040, 983, .18)
        val actual = ShortArray(320) { (direct[it] + reflection[it]).toShort() }
        val before = rms(actual)
        assertTrue(filter.suppress(actual, actual.size, 23040))
        assertTrue("dominant echo reduced", rms(actual) < before * .60)
        assertTrue("a single-tap reducer is not general AEC", rms(actual) > 5.0)
    }
    @Test fun aCoherentOutOfRangeGainFailsClosedInsteadOfPassingSelfSpeechToVad() {
        val pcm = reference(26400).also { for (i in it.indices) it[i] = (it[i] / 30).toShort() }
        val data = bytes(pcm); val filter = HandsFreeBargeInEcho()
        filter.offer(data, 0, data.size, 0)
        val actual = echo(pcm, 23040, 211, 8.0)
        assertFalse(filter.suppress(actual, actual.size, 23040))
        assertTrue(actual.all { it == 0.toShort() })
    }
    @Test fun concurrentOfferSuppressAndClearAreBoundedAndDoNotThrow() {
        val filter = HandsFreeBargeInEcho(); val data = bytes(reference(26400))
        val failure = AtomicReference<Throwable?>(); val start = CountDownLatch(1)
        val jobs = listOf(
            Thread { try { start.await(); repeat(40) { filter.offer(data, 0, data.size, 0) } } catch(t:Throwable) { failure.set(t) } },
            Thread { try { start.await(); repeat(40) { filter.suppress(ShortArray(320) { 2000 }, 320, 23040) } } catch(t:Throwable) { failure.set(t) } },
            Thread { try { start.await(); repeat(40) { filter.clear() } } catch(t:Throwable) { failure.set(t) } }
        )
        jobs.forEach { it.start() }; start.countDown()
        jobs.forEach { it.join(5000); assertFalse(it.isAlive) }
        assertNull(failure.get())
        filter.clear(); val input = ShortArray(320) { 1000 }
        assertFalse(filter.suppress(input, input.size, 23040)); assertTrue(input.all { it == 0.toShort() })
    }
}
