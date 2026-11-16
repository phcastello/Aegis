package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

class NodeHandsFreeVerificationWindowTest {
    private fun pcm(size:Int)=ShortArray(size){(it%30000).toShort()}
    @Test fun lookaheadWaitsForSamplesAndPreservesOriginalDecision() {
        val candidate=HandsFreeWakeCandidate(40160,160,floatArrayOf(.7f,.5f))
        assertFalse(candidate.ready(42719))
        assertTrue(candidate.ready(42720))
        assertEquals(40160L,candidate.decisionSample)
    }
    @Test fun exactReferenceWindowSurvivesReadOvershootAndRingWrap() {
        val samples=pcm(46000);val ring=HandsFreeRing(HandsFreeWakeCandidate.RING_CAPACITY)
        ring.append(samples,samples.size)
        val candidate=HandsFreeWakeCandidate(40320,160,floatArrayOf(.7f,.5f))
        val window=candidate.verificationWindow(ring,samples.size.toLong())
        assertEquals(40000,window.candidateSample)
        assertArrayEquals(samples.copyOfRange(320,42880),window.samples)
        val command=ShortArray(320000)
        val count=candidate.copyCommandTo(ring,samples.size.toLong(),command)
        assertArrayEquals(samples.copyOfRange(40320,46000),command.copyOf(count))
        assertArrayEquals(samples.copyOfRange(8320,43520),candidate.context(ring,46000))
    }
    @Test fun earlyDecisionUsesOnlyCapturedAudioWithoutZeroPadding() {
        val samples=pcm(3200);val ring=HandsFreeRing(HandsFreeWakeCandidate.RING_CAPACITY)
        ring.append(samples,samples.size)
        val candidate=HandsFreeWakeCandidate(320,320,floatArrayOf(.7f,.5f))
        val window=candidate.verificationWindow(ring,3200)
        assertEquals(320,window.candidateSample)
        assertArrayEquals(samples.copyOfRange(0,2880),window.samples)
    }
    @Test(expected=IllegalArgumentException::class) fun missingReferenceCannotSilentlyChangeWindow() {
        val samples=pcm(46000);val ring=HandsFreeRing(40000)
        ring.append(samples,samples.size)
        HandsFreeWakeCandidate(40320,160,floatArrayOf(.7f,.5f)).verificationWindow(ring,46000)
    }
}
