package com.aegis.node

import org.junit.Assert.*
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import kotlin.math.abs
import com.aegis.node.HandsFreeBargeInEvidence.AMBIGUOUS
import com.aegis.node.HandsFreeBargeInEvidence.INDEPENDENT
import com.aegis.node.HandsFreeBargeInEvidence.UNAVAILABLE

class NodeHandsFreeBargeInCaptureTest {
    private fun frame(amplitude:Int)=ShortArray(320){if(it%2==0)amplitude.toShort() else (-amplitude).toShort()}
    private fun flatten(frames:List<ShortArray>):ShortArray {
        val result=ShortArray(frames.sumOf{it.size});var offset=0
        frames.forEach{it.copyInto(result,offset);offset+=it.size};return result
    }
    private class Classifier(
        private val voiced: (ShortArray) -> Boolean = { pcm -> pcm.any { abs(it.toInt()) >= 100 } }
    ):HandsFreeBargeInClassifier {
        val closes=AtomicInteger();val resets=AtomicInteger()
        override fun isSpeech(frame:ShortArray)=voiced(frame)
        override fun reset(){resets.incrementAndGet()}
        override fun close(){closes.incrementAndGet()}
    }
    private class Input(val pcm:ShortArray,val clock:AtomicLong,val chunk:Int=320):HandsFreeBargeInInput {
        val proceed=CountDownLatch(1);val exhausted=CountDownLatch(1)
        val stopped=CountDownLatch(1);val releasing=CountDownLatch(1)
        var releaseGate:CountDownLatch?=null
        var stopGate:CountDownLatch?=null
        var silenced=false
        var takeSample:(Int,Short)->Short={_,sample->sample}
        val starts=AtomicInteger();val stops=AtomicInteger();val closes=AtomicInteger()
        @Volatile var offset=0
        override fun start(){starts.incrementAndGet()}
        override fun read(samples:ShortArray,offset:Int,count:Int):Int {
            proceed.await()
            if(stopped.count==0L)return -1
            if(this.offset>=pcm.size){exhausted.countDown();stopped.await();return -1}
            val n=minOf(count,chunk,pcm.size-this.offset)
            for(i in 0 until n) {
                val index=this.offset+i
                samples[offset+i]=takeSample(index,pcm[index])
            }
            this.offset+=n
            clock.set(this.offset*1000L/16000);return n
        }
        override fun silenced()=silenced
        override fun stop(){stops.incrementAndGet();stopGate?.await();stopped.countDown();proceed.countDown()}
        override fun close(){releasing.countDown();releaseGate?.await();closes.incrementAndGet()}
    }
    private fun capture(input:Input,classifier:Classifier=Classifier(),head:()->Long?={24000L+input.offset*3L/2},
        current:()->Boolean={true},inspect:(ShortArray,Int,Long)->HandsFreeBargeInEvidence={_,_,_->INDEPENDENT},
        volume:()->Int={8},resetEvidence:()->Unit={},speech:()->Boolean={true},
        result: (String, Int, Int) -> Unit = { _, _, _ -> },
        onAvailabilityChanged: () -> Unit = {}, voicePathReady: () -> Boolean = { false },
        onCaptureSummary: (HandsFreeBargeInCaptureSummary) -> Unit = {}) =
        HandsFreeBargeInCapture(input,inspect,head,volume,resetEvidence,current,speech,{classifier},{input.clock.get()},
            result, onAvailabilityChanged, voicePathReady, onCaptureSummary = onCaptureSummary)
    private fun pcm(wav:ByteArray):ShortArray {
        val b=ByteBuffer.wrap(wav).order(ByteOrder.LITTLE_ENDIAN);b.position(44)
        return ShortArray((wav.size-44)/2){b.short}
    }
    private fun await(latch:CountDownLatch){assertTrue(latch.await(2,TimeUnit.SECONDS))}

    @Test fun onsetKeepsExact120msBeforeFirstSpeechAcrossRingWrap() {
        val classifier=Classifier();val onset=HandsFreeBargeInOnset(classifier)
        val quiet=(1..16).map{frame(it%5+1)};quiet.forEach{assertNull(onset.process(it,INDEPENDENT))}
        val speech=(0..3).map{frame(1000+it)}
        speech.take(3).forEach{assertNull(onset.process(it,INDEPENDENT))}
        val actual=onset.process(speech.last(),INDEPENDENT)
        assertArrayEquals(flatten(quiet.takeLast(6)+speech),actual)
        assertNull(onset.process(frame(1000),INDEPENDENT))
    }
    @Test fun unsafeReferenceBreaksConsecutiveOnsetAndErasesPreroll() {
        val classifier=Classifier();val onset=HandsFreeBargeInOnset(classifier){classifier.reset()}
        repeat(3){assertNull(onset.process(frame(1000),INDEPENDENT))}
        assertNull(onset.process(frame(1000),UNAVAILABLE))
        repeat(3){assertNull(onset.process(frame(2000),INDEPENDENT))}
        val actual=onset.process(frame(2000),INDEPENDENT)
        assertArrayEquals(flatten(List(4){frame(2000)}),actual)
        assertEquals(1,classifier.resets.get())
    }
    @Test fun isolatedClickAnd40msCueDoNotQualifySpeech() {
        val onset=HandsFreeBargeInOnset(Classifier())
        repeat(20){
            assertNull(onset.process(frame(3000),INDEPENDENT))
            assertNull(onset.process(frame(3000),INDEPENDENT))
            repeat(4){assertNull(onset.process(frame(0),INDEPENDENT))}
        }
    }
    @Test fun acceptedCommandPreservesEverySampleWithPartialReadsAndHeadDisappearing() {
        val quiet=List(12){frame(it%5+1)};val speech=List(22){frame(1000+it)}
        val frames=quiet+speech+List(40){frame(0)}
        val input=Input(flatten(frames),AtomicLong(),73);val approved=CountDownLatch(1)
        val classifier=Classifier();val calls=AtomicInteger()
        val capture=capture(input,classifier,head={if(approved.count>0)24000L+input.offset*3L/2 else null},speech={
            calls.incrementAndGet();approved.countDown();true
        })
        assertTrue(capture.start());input.proceed.countDown();await(approved)
        val command=checkNotNull(capture.awaitCommand())
        assertArrayEquals(flatten(quiet.takeLast(6)+speech+List(40){frame(0)}),pcm(command.wav))
        assertEquals(1,calls.get());assertEquals(1,classifier.closes.get())
        val marks=command.trace.report("complete").getJSONObject("milestonesFromEndpointMs")
        assertTrue(marks.has("barge_in_accepted"));assertFalse(marks.has("wake_accepted"))
        assertTrue(capture.awaitClosed(500));capture.close()
        assertEquals(1,input.stops.get());assertEquals(1,input.closes.get())
        assertNull(capture.awaitCommand())
    }
    @Test fun confirmedInterruptedSpeechEndpointsAfterEightHundredMillisecondsWithoutNewVoice() {
        val warmup = List(12) { frame(0) }
        val speech = List(7) { frame(2000 + it) }
        val silence = List(150) { frame(0) }
        val input = Input(flatten(warmup + speech + silence), AtomicLong())
        val approved = CountDownLatch(1)
        val capture = capture(input, inspect = { _, _, _ ->
            val speechFrame = input.offset / 320 - warmup.size - 1
            if (speechFrame in setOf(1, 3, 5)) AMBIGUOUS else INDEPENDENT
        }, speech = {
            approved.countDown()
            true
        })
        try {
            assertTrue(capture.start())
            input.proceed.countDown()
            await(approved)
            val command = checkNotNull(capture.awaitCommand())
            val prefix = speech.mapIndexed { index, frame ->
                if (index % 2 == 0) frame else silence.first()
            }
            assertArrayEquals(flatten(warmup.takeLast(6) + prefix + silence.take(40)), pcm(command.wav))
            assertEquals((warmup.size + speech.size + 40) * 20L, input.clock.get())
        } finally {
            capture.close()
            assertTrue(capture.awaitClosed(1000))
        }
    }

    @Test fun stationaryBackgroundNeverConfirmsEvenWhenClassifierCallsEveryFrameSpeech() {
        for (amplitude in listOf(20, 50, 250)) {
            val input = Input(flatten(List(35) { frame(amplitude + it % 7) }),AtomicLong())
            val approvals = AtomicInteger()
            val capture = capture(input, Classifier { true },speech={approvals.incrementAndGet();true})
            try {
                assertTrue(capture.start());input.proceed.countDown();await(input.exhausted)
                assertEquals(0,approvals.get())
                assertNull(capture.awaitCommand())
            } finally { capture.close();assertTrue(capture.awaitClosed(1000)) }
        }
    }

    @Test fun ambiguousFramesCalibrateTheFloorWithoutQualifyingSpeech() {
        val input = Input(flatten(List(12){frame(120)}+List(6){frame(1000)}+
            List(25){frame(200)}), AtomicLong())
        val approved = AtomicInteger()
        val results = mutableListOf<Triple<String,Int,Int>>()
        val capture = capture(input, Classifier { true },
            inspect={_,_,_->if (input.offset <= 18*320) AMBIGUOUS else INDEPENDENT},
            speech={approved.incrementAndGet();true},
            result={reason,floor,peak->results.add(Triple(reason,floor,peak))})
        try {
            assertTrue(capture.start());input.proceed.countDown();await(input.exhausted)
            assertEquals(0,approved.get())
            assertNull(capture.awaitCommand())
            assertTrue(capture.awaitClosed(1000))
            assertEquals(listOf(Triple("ambiguous",120,1000)),results)
        } finally { capture.close();assertTrue(capture.awaitClosed(1000)) }
    }

    @Test fun louderBackgroundRaisesTheFloorWithoutBlockingRealSpeech() {
        val frames = List(12){frame(25)}+List(80){frame(300)}+List(6){frame(500)}+
            List(23){frame(2000)}+List(40){frame(300)}
        val input = Input(flatten(frames), AtomicLong())
        val confirmedAt = AtomicInteger()
        val approved = CountDownLatch(1)
        val results = mutableListOf<Triple<String,Int,Int>>()
        val capture = capture(input, Classifier { it[0] >= 500 },
            speech={confirmedAt.set(input.offset);approved.countDown();true},
            result={reason,floor,peak->results.add(Triple(reason,floor,peak))})
        try {
            assertTrue(capture.start());input.proceed.countDown();await(approved)
            assertNotNull(capture.awaitCommand())
            assertEquals((12+80+6+4)*320,confirmedAt.get())
            val (reason,floor,peak)=results.single()
            assertEquals("confirmed",reason)
            assertTrue(floor in 275..400)
            assertEquals(2000,peak)
        } finally { capture.close();assertTrue(capture.awaitClosed(1000)) }
    }

    @Test fun stalePlaybackHeadCannotSupplyConsecutiveIndependentEvidence() {
        val frames=List(12){frame(0)}+List(40){frame(1800)}
        val input=Input(flatten(frames),AtomicLong())
        val calls=AtomicInteger()
        val capture=capture(input,head={24000L+minOf(input.offset,13*320)*3L/2},
            speech={calls.incrementAndGet();true})
        assertTrue(capture.start());input.proceed.countDown();await(input.exhausted)
        assertEquals(0,calls.get())
        capture.close();assertTrue(capture.awaitClosed(1000));assertNull(capture.awaitCommand())
    }

    @Test fun volumeChangeAtDecisionDiscardsOnsetAndResetsEchoEvidence() {
        val quiet=List(12){frame(0)}
        val speech=List(18){frame(2200+it)}
        val input=Input(flatten(quiet+speech+List(40){frame(0)}),AtomicLong())
        val calls=AtomicInteger();val resets=AtomicInteger();val confirmedAt=AtomicInteger()
        val approved=CountDownLatch(1)
        val capture=capture(input,volume={if(input.offset < 16*320)8 else 13},
            resetEvidence={resets.incrementAndGet()},
            speech={calls.incrementAndGet();confirmedAt.set(input.offset);approved.countDown();true})
        assertTrue(capture.start());input.proceed.countDown();await(approved)
        val actual=pcm(checkNotNull(capture.awaitCommand()).wav)
        assertArrayEquals(flatten(speech.drop(4)+List(40){frame(0)}),actual)
        assertEquals(20*320,confirmedAt.get())
        assertEquals(1,resets.get());assertEquals(1,calls.get())
    }

    @Test fun ambiguousEvidenceBreaksOnsetAndDoesNotLeakEchoIntoTheCommand() {
        val speech=List(20){frame(2200+it)}
        val frames=List(12){frame(0)}+List(3){frame(1700)}+List(6){frame(1800)}+speech+List(40){frame(0)}
        val input=Input(flatten(frames),AtomicLong())
        val approved=CountDownLatch(1)
        val calls=AtomicInteger()
        val capture=capture(input,inspect={_,_,_->
            if(input.offset in 16*320..21*320)AMBIGUOUS else INDEPENDENT
        },speech={calls.incrementAndGet();approved.countDown();true})
        assertTrue(capture.start());input.proceed.countDown();await(approved)
        val actual=pcm(checkNotNull(capture.awaitCommand()).wav)
        assertArrayEquals(flatten(List(6){frame(0)}+speech+List(40){frame(0)}),actual)
        assertEquals(1,calls.get())
    }

    @Test fun oneRepeatedHeadBetweenIndependentFramesDoesNotLoseAnInterruption() {
        val speech = List(24) { frame(2000) }
        val input = Input(flatten(List(24) { frame(20) } + speech + List(41) { frame(0) }), AtomicLong())
        val approved = CountDownLatch(1)
        val confirmedAt = AtomicInteger()
        val capture = capture(input, head = { 24000L + (input.offset / 640) * 960L },
            speech = { confirmedAt.set(input.offset); approved.countDown(); true })
        try {
            assertTrue(capture.start()); input.proceed.countDown(); await(approved)
            assertEquals(32 * 320, confirmedAt.get())
            assertNotNull(capture.awaitCommand())
        } finally {
            capture.close()
            assertTrue(capture.awaitClosed(1000))
        }
    }

    @Test fun voiceAvailabilityRequiresBothCalibrationAndUsableEchoPath() {
        val input = Input(flatten(List(15) { frame(20) }), AtomicLong())
        val observed = mutableListOf<Pair<Int, HandsFreeBargeInPanelAvailability>>()
        lateinit var capture: HandsFreeBargeInCapture
        capture = capture(input, voicePathReady = { input.offset in 7 * 320..12 * 320 },
            onAvailabilityChanged = { observed.add(input.offset to capture.panelAvailability()) })
        try {
            assertTrue(capture.start()); input.proceed.countDown(); await(input.exhausted)
        } finally {
            capture.close()
            assertTrue(capture.awaitClosed(1000))
        }
        assertEquals(listOf(
            3200 to HandsFreeBargeInPanelAvailability.VOICE,
            4160 to HandsFreeBargeInPanelAvailability.PREPARING,
            4800 to HandsFreeBargeInPanelAvailability.UNAVAILABLE
        ), observed)
    }

    @Test fun closingDuringAnIncompleteOnsetDoesNotSubmit() {
        val input=Input(flatten(List(12){frame(0)}+List(3){frame(1700)}),AtomicLong())
        val calls=AtomicInteger()
        val capture=capture(input,speech={calls.incrementAndGet();true})
        assertTrue(capture.start());input.proceed.countDown();await(input.exhausted)
        capture.close();assertTrue(capture.awaitClosed(1000))
        assertEquals(0,calls.get())
        assertNull(capture.awaitCommand())
    }

    @Test fun missingOrUnsafePlaybackReferenceNeverCallsOnSpeech() {
        for(missing in listOf(true,false)) {
            val input=Input(flatten(List(60){frame(2000)}),AtomicLong());val calls=AtomicInteger()
            val capture=capture(input,head={if(missing)null else 24000L},
                inspect={_,_,_->UNAVAILABLE},speech={calls.incrementAndGet();true})
            assertTrue(capture.start());input.proceed.countDown();await(input.exhausted)
            assertEquals(0,calls.get());capture.close();assertTrue(capture.awaitClosed(1000))
            assertNull(capture.awaitCommand())
        }
    }
    @Test fun rejectedOrStaleTurnNeverProducesCommand() {
        for(reject in listOf(true,false)) {
            val input=Input(flatten(List(12){frame(0)}+List(22){frame(2000)}),AtomicLong());val calls=AtomicInteger()
            val current={reject || input.offset<640}
            val capture=capture(input,current=current,speech={calls.incrementAndGet();false})
            assertTrue(capture.start());input.proceed.countDown()
            assertTrue(capture.awaitClosed(1000));assertNull(capture.awaitCommand())
            assertEquals(if(reject)1 else 0,calls.get())
            assertEquals(1,input.closes.get())
        }
    }
    @Test fun silencedRecorderNeverQualifiesSpeechAndReportsTheReason() {
        val input=Input(flatten(List(8){frame(2000)}),AtomicLong());input.silenced=true
        val calls = AtomicInteger()
        val reported = CountDownLatch(1)
        var summary: HandsFreeBargeInCaptureSummary? = null
        val capture = capture(input, speech = { calls.incrementAndGet(); true }, onCaptureSummary = {
            summary = it
            reported.countDown()
        })
        assertTrue(capture.start());input.proceed.countDown()
        await(reported)
        assertTrue(capture.awaitClosed(1000));assertNull(capture.awaitCommand());assertEquals(0,calls.get())
        val recorded = checkNotNull(summary)
        assertEquals(HandsFreeBargeInCaptureOutcome.RECORDER_SILENCED, recorded.outcome)
        assertFalse(recorded.accepted)
        assertEquals(0, recorded.commandFrames)
    }
    @Test fun maxCommandIs20SecondsIncludingPreroll() {
        val input=Input(flatten(List(12){frame(0)}+List(1100){frame(2000)}),AtomicLong());val approved=CountDownLatch(1)
        val capture=capture(input,speech={approved.countDown();true})
        assertTrue(capture.start());input.proceed.countDown();await(approved)
        val result=checkNotNull(capture.awaitCommand())
        assertEquals(320000,pcm(result.wav).size);assertTrue(capture.awaitClosed(500))
    }
    @Test fun closeNeverWaitsForStopAndClosedMeansActualRelease() {
        val input=Input(ShortArray(0),AtomicLong());val stopGate=CountDownLatch(1);val releaseGate=CountDownLatch(1)
        input.stopGate=stopGate;input.releaseGate=releaseGate
        val capture=capture(input);assertTrue(capture.start())
        val returned=CountDownLatch(1);Thread{capture.close();returned.countDown()}.start();await(returned)
        assertFalse(capture.awaitClosed(10));stopGate.countDown();await(input.releasing)
        assertFalse(capture.awaitClosed(10));releaseGate.countDown();assertTrue(capture.awaitClosed(1000))
        repeat(3){capture.close()}
        assertEquals(1,input.stops.get());assertEquals(1,input.closes.get())
    }
    @Test fun closeBeforeStartReleasesExactlyOnce() {
        val input=Input(ShortArray(0),AtomicLong());val capture=capture(input)
        capture.close();assertTrue(capture.awaitClosed(1000));assertFalse(capture.start());capture.close()
        assertEquals(0,input.starts.get());assertEquals(1,input.stops.get());assertEquals(1,input.closes.get())
    }
    @Test fun timedOutWaitDiscardsLateCommandPublication() {
        val input=Input(flatten(List(12){frame(0)}+List(24){frame(2000)}+List(40){frame(0)}),AtomicLong())
        val releaseGate=CountDownLatch(1);input.releaseGate=releaseGate
        val approved=CountDownLatch(1);val capture=capture(input,speech={approved.countDown();true})
        assertTrue(capture.start());input.proceed.countDown();await(approved);await(input.releasing)
        input.clock.set(100000L) // Endpoint WAV exists; final release/publication is deliberately blocked.
        assertNull(capture.awaitCommand());releaseGate.countDown()
        assertTrue(capture.awaitClosed(1000));assertNull(capture.awaitCommand())
        assertEquals(1,input.closes.get())
    }
    @Test fun realEchoFilterDoesNotInterruptOwnPcmButPreservesConcurrentSpeech() {
        for(withVoice in listOf(false,true)) {
            val blocks=if(withVoice)85 else 24
            val reference=ShortArray(12000+blocks*480+2){i->
                val t=i/24000.0
                (5000*kotlin.math.sin(2*Math.PI*211.3*t)+2800*kotlin.math.sin(2*Math.PI*627.7*t)+
                    1300*kotlin.math.sin(2*Math.PI*1171.9*t)).toInt().toShort()
            }
            val bytes=ByteArray(reference.size*2)
            reference.forEachIndexed{i,v->bytes[i*2]=v.toByte();bytes[i*2+1]=(v.toInt() shr 8).toByte()}
            val frames=List(blocks){block->ShortArray(320){i->
                if(withVoice && block>=45)0 else {
                    val half=(12000+(block+1)*480)*2-(320-i+160)*3
                    val at=half/2
                    val rendered=if(half%2==0)reference[at].toDouble() else (reference[at]+reference[at+1])*.5
                    val voice=if(withVoice && block>=14)8500*kotlin.math.sin(2*Math.PI*2781*(block*320+i)/16000.0) else 0.0
                    (rendered*.60+voice).toInt().toShort()
                }
            }}
            val input=Input(flatten(frames),AtomicLong());val filter=HandsFreeBargeInEcho()
            var acceptedAt:Int?=null
            val approved=CountDownLatch(1);val calls=AtomicInteger()
            input.takeSample={i,sample->
                if(acceptedAt?.let {i >= it}==true && i<45*320) {
                    (8500*kotlin.math.sin(2*Math.PI*2781*i/16000.0)).toInt().toShort()
                } else sample
            }
            val capture=capture(input,head={12000L+input.offset*3L/2},
                inspect={samples,count,head->
                    filter.offer(bytes,0,head.toInt()*2,0)
                    filter.inspect(samples,count,head)
                },speech={acceptedAt=input.offset;calls.incrementAndGet();approved.countDown();true})
            assertTrue(capture.start());input.proceed.countDown()
            if(withVoice) {
                await(approved);val result=checkNotNull(capture.awaitCommand())
                assertEquals(1,calls.get())
                assertTrue("foreground speech retained",pcm(result.wav).count{abs(it.toInt())>500}>1000)
            } else {
                await(input.exhausted);assertEquals(0,calls.get());capture.close()
                assertTrue(capture.awaitClosed(1000));assertNull(capture.awaitCommand())
            }
        }
    }

    @Test fun uncorrelatedSpeakerEchoNeverInterruptsOrReturnsACommand() {
        val base=12000L
        val reference=ByteArray((base.toInt()+48*480)*2)
        for(i in 0 until reference.size/2) {
            val value=(8000*kotlin.math.sin(2*Math.PI*311.2*i/24000)).toInt()
            reference[i*2]=value.toByte();reference[i*2+1]=(value shr 8).toByte()
        }
        val filter=HandsFreeBargeInEcho()
        // Distortion introduces harmonics absent from the render reference.
        val echo=ShortArray(36*320){i->
            val t=i/16000.0
            val phase=2*Math.PI*311.2*t
            val envelope=.65+.2*kotlin.math.sin(2*Math.PI*5.0*t)
            ((2600*kotlin.math.sin(2*phase)+600*kotlin.math.sin(3*phase))*envelope).toInt().toShort()
        }
        val input=Input(flatten(List(12){frame(20)}+listOf(echo)),AtomicLong())
        val calls=AtomicInteger();val ambiguous=AtomicInteger()
        val capture=capture(input,head={base+input.offset*3L/2},
            inspect={samples,count,head->
                filter.offer(reference,0,head.toInt()*2,0)
                filter.inspect(samples,count,head).also { if (it==AMBIGUOUS) ambiguous.incrementAndGet() }
            },speech={calls.incrementAndGet();true})
        assertTrue(capture.start());input.proceed.countDown();await(input.exhausted)
        assertTrue(ambiguous.get()>30)
        assertEquals(0,calls.get())
        capture.close();assertTrue(capture.awaitClosed(1000));assertNull(capture.awaitCommand())
    }
}
