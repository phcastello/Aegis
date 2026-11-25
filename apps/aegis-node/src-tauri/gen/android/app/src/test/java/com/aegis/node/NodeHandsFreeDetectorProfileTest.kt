package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

class NodeHandsFreeDetectorProfileTest {
    @Test fun samplesOneBlockPerIntervalWithoutChangingAudioCadence() {
        val sampler=HandsFreeDetectorProfileSampler(5000)
        assertTrue(sampler.sampleDue(0))
        for(at in 200L until 5000L step 200L)assertFalse(sampler.sampleDue(at))
        assertTrue(sampler.sampleDue(5000))
        assertFalse(sampler.sampleDue(5000))
        assertFalse(sampler.sampleDue(9999))
        assertTrue(sampler.sampleDue(10000))
    }
    @Test fun longGapDoesNotScheduleCatchupSamples() {
        val sampler=HandsFreeDetectorProfileSampler(5000)
        assertTrue(sampler.sampleDue(0))
        assertTrue(sampler.sampleDue(3600000))
        assertFalse(sampler.sampleDue(3600001))
    }
    @Test fun newClockOriginStartsANewInterval() {
        val sampler=HandsFreeDetectorProfileSampler(5000)
        assertTrue(sampler.sampleDue(10000))
        assertTrue(sampler.sampleDue(20))
        assertFalse(sampler.sampleDue(21))
        assertTrue(sampler.sampleDue(5020))
    }
    @Test(expected=IllegalArgumentException::class) fun rejectsInvalidInterval() {
        HandsFreeDetectorProfileSampler(0)
    }
}
