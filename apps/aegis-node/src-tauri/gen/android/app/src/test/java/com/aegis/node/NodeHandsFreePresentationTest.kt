package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

class NodeHandsFreePresentationTest {
    @Test fun initiallyOffCanWakeOnlyOnceWithoutRearmingForContinuationOrWindowRecreation() {
        val policy=HandsFreePresentationPolicy()
        assertEquals(HandsFreePresentationPolicy.Decision(true,true),policy.begin("one",true))
        assertEquals(HandsFreePresentationPolicy.Decision(false,true),policy.begin("one",true))
        policy.screenOff()
        assertEquals(HandsFreePresentationPolicy.Decision(false,false),policy.begin("one",true))
        assertEquals(HandsFreePresentationPolicy.Decision(true,true),policy.begin("two",true))
    }
    @Test fun initiallyInteractiveNeverWakesForAnotherPresentationAndScreenOffIsHonored() {
        val policy=HandsFreePresentationPolicy()
        assertEquals(HandsFreePresentationPolicy.Decision(false,true),policy.begin("one",false))
        policy.screenOff()
        assertEquals(HandsFreePresentationPolicy.Decision(false,false),policy.begin("one",false))
        assertEquals(HandsFreePresentationPolicy.Decision(false,false),policy.begin("one",true))
        assertEquals(HandsFreePresentationPolicy.Decision(false,true),policy.begin("two",false))
    }
    @Test fun screenOffBetweenActivationAndMainThreadPresentationPreventsThePendingWake() {
        val policy=HandsFreePresentationPolicy()
        policy.accepted("one");policy.screenOff()
        assertEquals(HandsFreePresentationPolicy.Decision(false,false),policy.begin("one",true))
    }
    @Test fun temporaryPartialsBoundMemoryAndUpdatesWithoutHoldingAHistory() {
        var time=0L
        val limiter=HandsFreePartialDisplayLimiter{time}
        assertEquals("a".repeat(240),limiter.offer("a".repeat(10000)))
        assertNull(limiter.offer("second"))
        time=100
        assertEquals("second",limiter.offer("second"))
        time=200
        assertNull(limiter.offer("second"))
        assertEquals("line one line two",limiter.offer("line one\nline two"))
        limiter.clear()
        assertEquals("new session",limiter.offer("new session"))
    }
}
