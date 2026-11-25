package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

class NodeHandsFreeVerificationWindowTest {
    private fun pcm(size:Int)=ShortArray(size){(it%30000).toShort()}
    @Test fun onlyPendingNormalElectionUsesShorterCompleteMediaFrames() {
        assertEquals(3200,handsFreePendingWakeReadSamples(3200,false))
        assertEquals(640,handsFreePendingWakeReadSamples(3200,true))
        assertEquals(0,handsFreePendingWakeReadSamples(3200,true)%320)
        assertEquals(320,handsFreePendingWakeReadSamples(320,true))
        assertEquals(3200,handsFreePendingWakeReadSamples(3200,false,true))
        assertEquals(1280,handsFreePendingWakeReadSamples(3200,true,true))
        assertEquals(0,handsFreePendingWakeReadSamples(3200,true,true)%320)
    }
    @Test fun shorterReadsReduceLookaheadOvershootWithoutChangingVerifierPcm() {
        val samples=pcm(51200)
        val initialEnd=48000
        var largestSaving=0
        for(offset in 160..3200 step 160) {
            val decision=initialEnd-3200+offset
            val candidate=HandsFreeWakeCandidate(decision.toLong(),offset,floatArrayOf(.7f,.5f))
            fun observe(pending:Boolean,externalReference:Boolean=false):Int {
                val ring=HandsFreeRing(HandsFreeWakeCandidate.RING_CAPACITY)
                ring.append(samples,initialEnd)
                var end=initialEnd
                while(!candidate.ready(end.toLong())) {
                    val count=handsFreePendingWakeReadSamples(3200,pending,externalReference)
                    ring.append(samples.copyOfRange(end,end+count),count)
                    end+=count
                }
                val window=candidate.verificationWindow(ring,end.toLong())
                assertArrayEquals(samples.copyOfRange(decision-40000,decision+2560),window.samples)
                val command=ShortArray(320000)
                val length=candidate.copyCommandTo(ring,end.toLong(),command)
                assertArrayEquals(samples.copyOfRange(decision,end),command.copyOf(length))
                return end
            }
            val baseline=observe(false)
            val faster=observe(true)
            assertTrue(faster<=baseline)
            assertTrue(faster-decision-2560<640 || faster==initialEnd)
            assertTrue(observe(true,true)<=baseline)
            largestSaving=maxOf(largestSaving,baseline-faster)
        }
        assertEquals(2560,largestSaving) // 160ms less waiting in this boundary sweep.
    }
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
