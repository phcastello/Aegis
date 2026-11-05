package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

class NodeHandsFreePanelMotionTest {
    @Test fun explicitDisabledScaleTakesEffectBeforePlatformCacheUpdates() {
        assertFalse(handsFreePanelMotionAllowed(0f,false,true))
        assertFalse(handsFreePanelMotionAllowed(0f,false,false))
    }
    @Test fun restoringSystemScaleDoesNotWaitForAnOldDisabledCache() {
        assertTrue(handsFreePanelMotionAllowed(1f,false,false))
        assertTrue(handsFreePanelMotionAllowed(.5f,false,true))
    }
    @Test fun powerSaverDisablesDecorationAndExitRestoresRequestedMotion() {
        assertFalse(handsFreePanelMotionAllowed(1f,true,true))
        assertFalse(handsFreePanelMotionAllowed(Float.NaN,true,true))
        assertTrue(handsFreePanelMotionAllowed(1f,false,true))
    }
    @Test fun unavailableScaleFallsBackToThePlatformAnimationPreference() {
        assertFalse(handsFreePanelMotionAllowed(Float.NaN,false,false))
        assertTrue(handsFreePanelMotionAllowed(Float.NaN,false,true))
    }
}
