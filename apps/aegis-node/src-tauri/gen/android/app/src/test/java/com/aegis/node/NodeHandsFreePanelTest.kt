package com.aegis.node

import org.junit.Assert.*
import org.junit.Test
import kotlin.concurrent.thread
import java.util.concurrent.atomic.AtomicReference

class NodeHandsFreePanelTest {
    private fun pcm(vararg levels:Int):ShortArray=ShortArray(levels.size*320){index->
        val value=levels[index/320];(if(index%2==0)value else -value).toShort()
    }
    private fun waveform(owner:Any,now:Long)=FloatArray(HandsFreePanelSignal.BAR_COUNT).also{
        HandsFreePanelSignal.render(owner,now,it)
    }
    private fun withSignal(body:(Any)->Unit) {
        val owner=Any();HandsFreePanelSignal.attach(owner,"turn")
        try{body(owner)}finally{HandsFreePanelSignal.detach(owner)}
    }
    @Test fun silenceAndLowNoiseDoNotInventVoiceActivity()=withSignal{owner->
        val quiet=pcm(0,12,20,30,0,0,12,0,0,0)
        HandsFreePanelSignal.capture("turn",quiet,quiet.size,200)
        val out=FloatArray(32)
        assertFalse(HandsFreePanelSignal.render(owner,400,out))
        assertTrue(out.all{it==0f})
        assertEquals(0f,handsFreePanelLevel(quiet,quiet.size),0f)
    }
    @Test fun fixedDisplayScalePreservesLowNormalAndLoudDifferences() {
        fun level(amplitude:Int)=handsFreePanelLevel(pcm(amplitude),320)
        assertTrue(level(80)>0f);assertTrue(level(300)>level(80));assertTrue(level(1500)>level(300))
        assertTrue(level(1500)<0.8f);assertTrue(level(6000)>level(1500))
        assertEquals(1f,level(16000),0f)
        assertEquals(1f,handsFreePanelLevel(shortArrayOf(Short.MIN_VALUE),1),0f)
    }
    @Test fun partialBlocksUseOnlyValidSamplesAndNeverModifyPcm()=withSignal{owner->
        val samples=ShortArray(321);samples[320]=Short.MAX_VALUE;val copy=samples.copyOf()
        HandsFreePanelSignal.capture("turn",samples,320,20)
        assertTrue(waveform(owner,220).all{it==0f})
        assertEquals(0f,handsFreePanelLevel(samples,320),0f)
        assertEquals(0f,handsFreePanelLevel(samples,-1),0f)
        assertEquals(0f,handsFreePanelLevel(shortArrayOf(),10),0f)
        assertArrayEquals(copy,samples)
    }
    @Test fun positionsRepresentDifferentRealTemporalWindowsInsteadOfFixedCenterWeights()=withSignal{owner->
        for(block in 0 until 7) {
            val levels=IntArray(10){window->60+((block*10+window)*83)%2200}
            val samples=pcm(*levels);HandsFreePanelSignal.capture("turn",samples,samples.size,(block+1)*200L)
        }
        val out=waveform(owner,1600)
        assertTrue(out.maxOrNull()!!-out.minOrNull()!!>0.18f)
        assertTrue(out.map{(it*1000).toInt()}.distinct().size>24)
        assertNotEquals(out.first(),out.last())
        assertTrue((0 until 16).any{kotlin.math.abs(out[it]-out[31-it])>0.08f})
    }
    @Test fun aSingle200msDeliveryMovesAcrossManyIntermediatePresentationFrames()=withSignal{owner->
        val samples=pcm(0,80,300,0,1800,400,0,2500,200,0)
        HandsFreePanelSignal.capture("turn",samples,samples.size,200)
        val values=(200L..390L step 10).map{waveform(owner,it).last()}
        assertTrue(values.map{it.toBits()}.distinct().size>12)
        assertTrue(values.any{it>0.1f})
        assertTrue(values.zipWithNext().count{it.first!=it.second}>12)
        // History is not revealed at once at receipt: it advances behind a bounded delay.
        assertEquals(0f,values.first(),0f)
    }
    @Test fun presentationUsesOnlyAlreadyReceivedPcmAndDoesNotAdvanceToFutureInput()=withSignal{owner->
        val voice=pcm(1500,1500,1500,1500,1500,1500,1500,1500,1500,1500)
        HandsFreePanelSignal.capture("turn",voice,voice.size,200)
        assertTrue(waveform(owner,200).all{it==0f})
        assertTrue(waveform(owner,240).last()>0f)
        assertEquals(0f,waveform(owner,199).maxOrNull()!!,0f)
    }
    @Test fun silenceAfterSpeechRelaxesAndHistoryClearsWithoutFrozenBars()=withSignal{owner->
        val voice=pcm(*IntArray(10){1500});HandsFreePanelSignal.capture("turn",voice,voice.size,200)
        val before=waveform(owner,400).last()
        val silence=ShortArray(3200);HandsFreePanelSignal.capture("turn",silence,silence.size,400)
        val after=waveform(owner,500).last()
        assertTrue(before>after);assertTrue(after>0f)
        assertEquals(0f,waveform(owner,600).last(),0f)
        for(block in 3..11)HandsFreePanelSignal.capture("turn",silence,silence.size,block*200L)
        assertTrue(waveform(owner,2400).all{it==0f})
    }
    @Test fun captureDropoutFadesAndExpiresInsteadOfFreezingLastSpeech()=withSignal{owner->
        val voice=pcm(*IntArray(10){1500});HandsFreePanelSignal.capture("turn",voice,voice.size,200)
        val peak=waveform(owner,500).maxOrNull()!!
        assertTrue(peak>0f)
        assertTrue(waveform(owner,1400).maxOrNull()!!<peak)
        val out=FloatArray(32)
        assertFalse(HandsFreePanelSignal.render(owner,1800,out));assertTrue(out.all{it==0f})
    }
    @Test fun shortSyllableSurvivesTemporalPresentation()=withSignal{owner->
        val samples=pcm(0,0,0,1800,0,0,0,0,0,0)
        HandsFreePanelSignal.capture("turn",samples,samples.size,200)
        val peaks=(200L..500L step 5).map{waveform(owner,it).maxOrNull()!!}
        assertTrue(peaks.maxOrNull()!!>0.2f)
        assertTrue(peaks.count{it>0.1f}>10)
    }
    @Test fun fragmentedReadsPreserveTheSameWindowEnergiesAndPcm() {
        val samples=pcm(80,300,1400,0,700,90,2200,600,0,100)
        val copy=samples.copyOf();val one=Any();val fragmented=Any()
        HandsFreePanelSignal.attach(one,"turn");HandsFreePanelSignal.capture("turn",samples,samples.size,200)
        val expected=waveform(one,400)
        HandsFreePanelSignal.attach(fragmented,"turn")
        for(index in 0 until 20) {
            val part=samples.copyOfRange(index*160,(index+1)*160)
            HandsFreePanelSignal.capture("turn",part,part.size,(index+1)*10L)
        }
        assertArrayEquals(expected,waveform(fragmented,400),0f)
        assertArrayEquals(copy,samples);HandsFreePanelSignal.detach(fragmented)
    }
    @Test fun duplicateOrOutOfOrderBatchIsNotCountedTwice()=withSignal{owner->
        val voice=pcm(*IntArray(10){1200});HandsFreePanelSignal.capture("turn",voice,voice.size,200)
        val before=waveform(owner,400)
        HandsFreePanelSignal.capture("turn",voice,voice.size,200)
        HandsFreePanelSignal.capture("turn",voice,voice.size,190)
        assertArrayEquals(before,waveform(owner,400),0f)
    }
    @Test fun ownerAndSessionScopeRejectOldCallbacksAndCanReopenWithoutHistory() {
        val old=Any();val fresh=Any();val voice=pcm(*IntArray(10){1500})
        HandsFreePanelSignal.attach(old,"old");HandsFreePanelSignal.capture("old",voice,voice.size,200)
        assertTrue(waveform(old,400).any{it>0f})
        HandsFreePanelSignal.attach(fresh,"new")
        HandsFreePanelSignal.capture("old",voice,voice.size,400)
        assertTrue(waveform(fresh,600).all{it==0f})
        HandsFreePanelSignal.capture("new",voice,voice.size,600)
        HandsFreePanelSignal.reset(old);HandsFreePanelSignal.detach(old)
        assertTrue(waveform(fresh,800).any{it>0f})
        assertTrue(waveform(old,800).all{it==0f})
        HandsFreePanelSignal.detach(fresh)
        HandsFreePanelSignal.capture("new",voice,voice.size,800)
        assertTrue(waveform(fresh,1000).all{it==0f})
    }
    @Test fun resetDiscardsPartialWindowsAndPreTransitionTimestamps()=withSignal{owner->
        HandsFreePanelSignal.capture("turn",ShortArray(160){1500},160,10)
        HandsFreePanelSignal.reset(owner,100)
        val voice=pcm(*IntArray(10){1500});HandsFreePanelSignal.capture("turn",voice,voice.size,100)
        assertTrue(waveform(owner,300).all{it==0f})
        HandsFreePanelSignal.capture("turn",ShortArray(160){1500},160,110)
        assertTrue(waveform(owner,310).all{it==0f}) // Only half of a new 20ms window.
        HandsFreePanelSignal.capture("turn",ShortArray(160){1500},160,120)
        assertTrue(waveform(owner,320).any{it>0f})
    }
    @Test fun longBackgroundGapDoesNotJoinOldAndNewPartialWindows()=withSignal{owner->
        HandsFreePanelSignal.capture("turn",ShortArray(160){1500},160,10)
        HandsFreePanelSignal.capture("turn",ShortArray(160){1500},160,1010)
        assertTrue(waveform(owner,1210).all{it==0f})
        HandsFreePanelSignal.capture("turn",ShortArray(160){1500},160,1020)
        assertTrue(waveform(owner,1220).any{it>0f})
    }
    @Test fun captureNotifiesAtMostOncePerInputBatchAndResetRetainsOnlyCurrentListener()=withSignal{owner->
        var calls=0;HandsFreePanelSignal.setListener(owner){calls++}
        val voice=pcm(*IntArray(10){1500})
        HandsFreePanelSignal.capture("turn",voice,voice.size,200);assertEquals(1,calls)
        HandsFreePanelSignal.reset(owner,200)
        HandsFreePanelSignal.capture("turn",voice,voice.size,400);assertEquals(2,calls)
        HandsFreePanelSignal.setListener(owner,null)
        HandsFreePanelSignal.capture("turn",voice,voice.size,600);assertEquals(2,calls)
    }
    @Test fun longStreamsHaveBoundedHistoryAndNoOldPeakCanReturnAfterSilence()=withSignal{owner->
        val voice=pcm(*IntArray(10){1500});val silence=ShortArray(3200)
        for(block in 1..300)HandsFreePanelSignal.capture("turn",if(block<20)voice else silence,3200,block*200L)
        val out=FloatArray(32);assertFalse(HandsFreePanelSignal.render(owner,60200,out));assertTrue(out.all{it==0f})
        assertEquals(80,HandsFreePanelSignal.CAPACITY)
    }
    @Test fun producerAndVisualConsumerDoNotWaitForEachOtherOrPublishNonfiniteFrames()=withSignal{owner->
        val error=AtomicReference<Throwable?>();val voice=pcm(60,0,1400,20,700,0,1200,900,0,80)
        val producer=thread {
            try{for(block in 1..3000)HandsFreePanelSignal.capture("turn",voice,voice.size,block*200L)}
            catch(t:Throwable){error.set(t)}
        }
        val out=FloatArray(32)
        for(frame in 1..6000) {
            HandsFreePanelSignal.render(owner,frame*100L,out)
            assertTrue(out.all{it.isFinite() && it in 0f..1f})
        }
        producer.join(3000);assertFalse(producer.isAlive);error.get()?.let{throw it}
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

    @Test fun genericCancellationCanDismissTheStillCurrentTerminalErrorImmediately() {
        val presentation=HandsFreePanelPresentation()
        presentation.begin("failed-session")
        assertTrue(presentation.delayClose(immediate=false,failed=true))
        val captured=checkNotNull(presentation.snapshot())
        assertEquals("failed-session",captured.session)
        assertTrue(presentation.isCurrent(captured))
        // Generic cancel dismisses this presentation without waiting for its error timer.
        presentation.end()
        assertFalse(presentation.isCurrent(captured))
        assertNull(presentation.snapshot())
    }
    @Test fun anOldCancellationSnapshotCannotDismissANewOrReopenedPresentation() {
        val presentation=HandsFreePanelPresentation()
        presentation.begin("old-session")
        presentation.delayClose(immediate=false,failed=true)
        val captured=checkNotNull(presentation.snapshot())
        presentation.begin("new-session")
        assertFalse(presentation.isCurrent(captured))
        val fresh=checkNotNull(presentation.snapshot())
        assertTrue(presentation.isCurrent(fresh))
        presentation.begin("new-session") // The system can reuse the same panel and session.
        assertFalse(presentation.isCurrent(fresh))
        assertFalse(presentation.isCurrent(captured))
    }

}
