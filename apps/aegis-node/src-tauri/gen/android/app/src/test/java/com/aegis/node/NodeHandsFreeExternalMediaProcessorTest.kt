package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

/** Streaming policy and ownership tests using injected AEC; actual JNI replay
 * is also covered by scripts/hands-free/benchmark-external-media.py.
 */
class NodeHandsFreeExternalMediaProcessorTest {
    private class FakeEcho : HandsFreeMediaEcho {
        var calls = 0
        var closed = false
        var fail = false
        var factor = 0.4
        override fun process(render: ShortArray, microphone: ShortArray, suppressed: ShortArray, linear: ShortArray): Boolean {
            check(!closed)
            calls++
            for (i in linear.indices) linear[i] = (microphone[i] * factor).toInt().toShort()
            suppressed.fill(0)
            return !fail
        }
        override fun close() { check(!closed); closed = true }
    }

    private class Fixture {
        var now = 1_000_000_000L
        var generation = 1L
        var available = true
        var renderLevel = 1200
        var factoryAvailable = true
        var attempts = 0
        val queried = ArrayList<Long>()
        val instances = ArrayList<FakeEcho>()
        val processor = HandsFreeExternalMediaProcessor(copyReference = { end, frame ->
            queried.add(end)
            frame.fill(renderLevel.toShort())
            available
        }, referenceGeneration = { generation }, openEcho = {
            attempts++
            if (factoryAvailable) FakeEcho().also { instances.add(it) } else null
        })
        fun append(samples: ShortArray): Boolean {
            now += samples.size * 62500L
            return processor.process(samples, samples.size, now)
        }
        fun frame() = ShortArray(320) { 1200 }
        fun qualify() {
            repeat(8) { assertFalse(append(frame())) }
            assertTrue(append(frame()))
        }
    }

    @Test fun noReferenceOrSilenceDoesNotOpenNativeOrModifyMic() {
        val f = Fixture()
        val samples = ShortArray(3200) { i -> (i % 2500 - 1250).toShort() }
        val original = samples.copyOf()
        f.available = false
        assertFalse(f.append(samples))
        assertArrayEquals(original, samples)
        f.available = true
        f.renderLevel = 0
        assertFalse(f.append(samples))
        assertArrayEquals(original, samples)
        assertEquals(0, f.attempts)
        assertEquals(20, f.queried.size)
        assertEquals(1_020_000_000L, f.queried.first())
        assertEquals(1_400_000_000L, f.queried.last())
        f.processor.close()
    }

    @Test fun linearOutputRequiresPriorEvidenceThenPreservesFrameLength() {
        val f = Fixture()
        f.qualify()
        val raw = f.frame()
        assertTrue(f.append(raw))
        assertTrue(raw.all { it.toInt() == 480 })
        assertEquals(1, f.attempts)
        assertEquals(10, f.instances.single().calls)
        f.processor.close()
        assertTrue(f.instances.single().closed)
    }

    @Test fun linearAmplificationIsVetoedAndMissingReferenceResets() {
        val f = Fixture()
        f.qualify()
        f.instances.single().factor = 1.5
        val unchanged = f.frame()
        assertFalse(f.append(unchanged))
        assertTrue(unchanged.all { it.toInt() == 1200 })
        f.available = false
        val isolatedGap = f.frame()
        assertFalse(f.append(isolatedGap))
        assertFalse(f.instances.single().closed)
        assertTrue(isolatedGap.all { it.toInt() == 1200 })
        val missing = f.frame()
        assertFalse(f.append(missing))
        assertTrue(f.instances.single().closed)
        assertTrue(missing.all { it.toInt() == 1200 })
        f.available = true
        assertFalse(f.append(f.frame()))
        assertEquals(1, f.attempts)
        repeat(60) { f.append(f.frame()) }
        assertEquals(2, f.attempts)
        assertTrue(f.append(f.frame()))
        f.processor.close()
    }

    @Test fun shortReadOrderGapsPreserveConvergenceButNeverForwardUnpairedOutput() {
        val f = Fixture()
        f.qualify()
        repeat(8) {
            repeat(9) { assertTrue(f.append(f.frame())) }
            f.available = false
            val raw = f.frame()
            assertFalse(f.append(raw))
            assertTrue(raw.all { it.toInt() == 1200 })
            f.available = true
        }
        assertEquals(1, f.attempts)
        assertTrue(f.append(f.frame()))
        f.processor.close()
    }

    @Test fun persistentlyIncompleteReferenceFailsOpenUntilCoverageRecovers() {
        val f = Fixture()
        repeat(10) {
            repeat(8) {
                f.available = true
                val raw = f.frame()
                assertFalse(f.append(raw))
                assertTrue(raw.all { it.toInt() == 1200 })
            }
            f.available = false
            repeat(2) {
                val raw = f.frame()
                assertFalse(f.append(raw))
                assertTrue(raw.all { it.toInt() == 1200 })
            }
        }
        assertEquals(1, f.attempts)
        assertTrue(f.instances.single().closed)
        f.available = true
        repeat(60) { f.append(f.frame()) }
        assertEquals(2, f.attempts)
        assertTrue(f.append(f.frame()))
        f.processor.close()
    }

    @Test fun generationAndMicClockGapsRequireNewConvergence() {
        val f = Fixture()
        f.qualify()
        f.generation++
        assertFalse(f.append(f.frame()))
        assertTrue(f.instances[0].closed)
        assertEquals(2, f.attempts)
        repeat(7) { assertFalse(f.append(f.frame())) }
        assertTrue(f.append(f.frame()))
        f.now += 80_000_000L
        assertFalse(f.append(f.frame()))
        assertTrue(f.instances[1].closed)
        assertEquals(3, f.attempts)
        f.processor.close()
    }

    @Test fun mediaPauseProcessesOnlyBoundedTailThenReleasesNative() {
        val f = Fixture()
        f.qualify()
        f.renderLevel = 0
        repeat(14) { f.append(f.frame()) }
        assertFalse(f.instances.single().closed)
        val quiet = f.frame()
        assertFalse(f.append(quiet))
        assertTrue(quiet.all { it.toInt() == 1200 })
        assertTrue(f.instances.single().closed)
        repeat(20) { assertFalse(f.append(f.frame())) }
        assertEquals(1, f.attempts)
        f.processor.close()
    }

    @Test fun fractionalMicReadLeavesTailRawAndDoesNotJoinAcrossGap() {
        val f = Fixture()
        f.qualify()
        val partial = ShortArray(480) { 1200 }
        assertTrue(f.append(partial))
        assertTrue(partial.copyOfRange(0, 320).all { it.toInt() == 480 })
        assertTrue(partial.copyOfRange(320, 480).all { it.toInt() == 1200 })
        assertTrue(f.instances.single().closed)
        assertFalse(f.append(f.frame()))
        assertEquals(2, f.attempts)
        f.processor.close()
    }

    @Test fun unavailableNativeDoesNotRetryEveryFrameAndResetAllowsRecovery() {
        val f = Fixture()
        f.factoryAvailable = false
        repeat(10) { assertFalse(f.append(f.frame())) }
        assertEquals(1, f.attempts)
        f.factoryAvailable = true
        f.processor.reset()
        assertFalse(f.append(f.frame()))
        assertEquals(2, f.attempts)
        f.processor.close()
    }

    @Test fun failedNativeOutputIsNeverForwarded() {
        val f = Fixture()
        f.qualify()
        f.instances.single().fail = true
        val raw = f.frame()
        assertFalse(f.append(raw))
        assertTrue(raw.all { it.toInt() == 1200 })
        assertTrue(f.instances.single().closed)
        repeat(5) { assertFalse(f.append(f.frame())) }
        assertEquals(1, f.attempts)
        f.generation++
        assertFalse(f.append(f.frame()))
        assertEquals(2, f.attempts)
        f.processor.close()
    }
}
