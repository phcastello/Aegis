package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

class NodeHandsFreePanelTest {
    @Test fun silenceAndLowNoiseDoNotInventVoiceActivity() {
        assertEquals(0f,handsFreePanelLevel(ShortArray(3200),3200),0f)
        assertEquals(0f,handsFreePanelLevel(ShortArray(3200){if(it%2==0)12 else -12},3200),0f)
    }
    @Test fun actualPcmEnergyControlsTheLevelWithoutAutomaticGain() {
        fun level(amplitude:Int)=handsFreePanelLevel(ShortArray(3200){if(it%2==0)amplitude.toShort() else (-amplitude).toShort()},3200)
        assertTrue(level(80)>0f);assertTrue(level(300)>level(80));assertTrue(level(1500)>level(300))
        assertEquals(1f,level(16000),0f)
        assertEquals(1f,handsFreePanelLevel(shortArrayOf(Short.MIN_VALUE),1),0f)
    }
    @Test fun partialBlocksUseOnlyValidSamplesAndNeverModifyPcm() {
        val samples=shortArrayOf(0,0,Short.MAX_VALUE);val copy=samples.copyOf()
        assertEquals(0f,handsFreePanelLevel(samples,2),0f)
        assertTrue(handsFreePanelLevel(samples,99)>0f)
        assertEquals(0f,handsFreePanelLevel(samples,-1),0f)
        assertEquals(0f,handsFreePanelLevel(shortArrayOf(),10),0f)
        assertArrayEquals(copy,samples)
    }
    @Test fun noVisiblePanelRetainsNoMeterAndLateCaptureCannotEnterANewSession() {
        val first=Any();val second=Any();val voice=ShortArray(3200){1500}
        HandsFreePanelSignal.attach(first,"old")
        HandsFreePanelSignal.capture("old",voice,voice.size,100)
        assertTrue(HandsFreePanelSignal.level(first,100)>0f)
        HandsFreePanelSignal.attach(second,"new")
        HandsFreePanelSignal.capture("old",voice,voice.size,110)
        assertEquals(0f,HandsFreePanelSignal.level(second,110),0f)
        HandsFreePanelSignal.capture("new",voice,voice.size,120)
        assertTrue(HandsFreePanelSignal.level(second,120)>0f)
        HandsFreePanelSignal.detach(first)
        assertTrue(HandsFreePanelSignal.level(second,120)>0f)
        HandsFreePanelSignal.detach(second)
        HandsFreePanelSignal.capture("new",voice,voice.size,130)
        assertEquals(0f,HandsFreePanelSignal.level(second,130),0f)
    }
    @Test fun oldPanelLifecycleCannotResetOrDetachAReplacementOfTheSameSession() {
        val first=Any();val replacement=Any();val voice=ShortArray(3200){1500}
        HandsFreePanelSignal.attach(first,"same")
        HandsFreePanelSignal.attach(replacement,"same")
        HandsFreePanelSignal.capture("same",voice,voice.size,100)
        HandsFreePanelSignal.reset(first);HandsFreePanelSignal.detach(first)
        assertTrue(HandsFreePanelSignal.level(replacement,100)>0f)
        assertEquals(0f,HandsFreePanelSignal.level(first,100),0f)
        HandsFreePanelSignal.detach(replacement)
    }
    @Test fun missingBlocksAndStateTransitionsReturnToAQuietMeter() {
        val owner=Any();val voice=ShortArray(3200){1500}
        HandsFreePanelSignal.attach(owner,"turn")
        HandsFreePanelSignal.capture("turn",voice,voice.size,100)
        assertTrue(HandsFreePanelSignal.level(owner,500)>0f)
        assertEquals(0f,HandsFreePanelSignal.level(owner,501),0f)
        assertEquals(0f,HandsFreePanelSignal.level(owner,99),0f)
        HandsFreePanelSignal.capture("turn",voice,voice.size,600)
        HandsFreePanelSignal.reset(owner)
        assertEquals(0f,HandsFreePanelSignal.level(owner,600),0f)
        HandsFreePanelSignal.detach(owner)
    }
    @Test fun userFacingStatesUseShortLabelsWithoutRuntimeDiagnostics() {
        assertEquals(HandsFreePanelState.CAPTURING,HandsFreePanelState.fromRuntime("capturing"))
        assertEquals(HandsFreePanelState.CONTINUATION,HandsFreePanelState.fromRuntime("continuation"))
        assertEquals(HandsFreePanelState.PROCESSING,HandsFreePanelState.fromRuntime("processing"))
        assertEquals(HandsFreePanelState.SPEAKING,HandsFreePanelState.fromRuntime("speaking"))
        assertEquals(HandsFreePanelState.ERROR,HandsFreePanelState.fromRuntime("HTTP 502: upstream detail"))
        assertFalse(HandsFreePanelState.ERROR.animated)
        assertEquals("Não consegui concluir. Tente novamente.",HandsFreePanelState.ERROR.label)
    }
    @Test fun explicitTerminalFailureSurvivesDroppedOrQueuedRuntimeStatus() {
        val presentation=HandsFreePanelPresentation()
        presentation.begin();presentation.refresh("processing")
        // The runtime may have already returned to listening before its UI callback runs.
        assertTrue(presentation.delayClose(immediate=false,failed=true))
        assertEquals(HandsFreePanelState.ERROR,presentation.state)
        assertFalse(presentation.refresh("listening"))
        assertFalse(presentation.refresh("session_failed"))
        assertEquals(HandsFreePanelState.ERROR,presentation.state)
        // Explicit cancel never waits for the short error presentation.
        assertFalse(presentation.delayClose(immediate=true,failed=false))
        presentation.begin()
        assertTrue(presentation.refresh("capturing"))
        assertEquals(HandsFreePanelState.CAPTURING,presentation.state)
    }

}
