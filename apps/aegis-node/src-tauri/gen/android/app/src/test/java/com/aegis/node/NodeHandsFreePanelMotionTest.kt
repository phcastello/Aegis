package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

class NodeHandsFreePanelMotionTest {
    @Test fun explicitDisabledScaleTakesEffectBeforePlatformCacheUpdates() {
        assertFalse(handsFreePanelMotionAllowed(0f,true))
        assertFalse(handsFreePanelMotionAllowed(0f,false))
    }
    @Test fun restoringSystemScaleDoesNotWaitForAnOldDisabledCache() {
        assertTrue(handsFreePanelMotionAllowed(1f,false))
        assertTrue(handsFreePanelMotionAllowed(.5f,true))
    }
    @Test fun batterySaverDoesNotOverrideExplicitEnabledAnimationScale() {
        // platformEnabled may be false due to battery saver or a stale global cache.
        assertTrue(handsFreePanelMotionAllowed(1f,false))
        assertFalse(handsFreePanelMotionAllowed(0f,true))
    }
    @Test fun unavailableScaleFallsBackToThePlatformAnimationPreference() {
        assertFalse(handsFreePanelMotionAllowed(Float.NaN,false))
        assertTrue(handsFreePanelMotionAllowed(Float.NaN,true))
    }
    @Test fun displayTimeoutIsPreventedOnlyForVisibleActiveSession() {
        for(state in HandsFreePanelState.entries) {
            val active=state in listOf(HandsFreePanelState.CAPTURING,HandsFreePanelState.CONTINUATION,
                HandsFreePanelState.PROCESSING,HandsFreePanelState.SPEAKING)
            assertEquals(active,handsFreePanelKeepsScreenOn(state,true,true))
            assertFalse(handsFreePanelKeepsScreenOn(state,false,true))
            assertFalse(handsFreePanelKeepsScreenOn(state,true,false))
        }
    }
}
