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

class NodeHandsFreeBargeInCaptureTest {
    private fun frame(amplitude:Int)=ShortArray(320){if(it%2==0)amplitude.toShort() else (-amplitude).toShort()}
    private fun flatten(frames:List<ShortArray>):ShortArray {
        val result=ShortArray(frames.sumOf{it.size});var offset=0
        frames.forEach{it.copyInto(result,offset);offset+=it.size};return result
    }
    private class Classifier:HandsFreeBargeInClassifier {
        val closes=AtomicInteger();val resets=AtomicInteger()
        override fun isSpeech(frame:ShortArray)=frame.any{abs(it.toInt())>=100}
        override fun reset(){resets.incrementAndGet()}
        override fun close(){closes.incrementAndGet()}
    }
    private class Input(val pcm:ShortArray,val clock:AtomicLong,val chunk:Int=320):HandsFreeBargeInInput {
        val proceed=CountDownLatch(1);val exhausted=CountDownLatch(1)
        val stopped=CountDownLatch(1);val releasing=CountDownLatch(1)
        var releaseGate:CountDownLatch?=null
        var stopGate:CountDownLatch?=null
        var silenced=false
        var timestampAt:(Int)->Long={it*1000L/16000}
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
            clock.set(timestampAt(this.offset));return n
        }
        override fun silenced()=silenced
        override fun stop(){stops.incrementAndGet();stopGate?.await();stopped.countDown();proceed.countDown()}
        override fun close(){releasing.countDown();releaseGate?.await();closes.incrementAndGet()}
    }
    private fun capture(input:Input,classifier:Classifier=Classifier(),head:()->Long?={24000L},
        current:()->Boolean={true},clean:(ShortArray,Int,Long)->Boolean={_,_,_->true},
        pause:()->Boolean={true},resume:()->Unit={},speech:()->Boolean={true})=
        HandsFreeBargeInCapture(input,clean,head,pause,resume,current,speech,{classifier},{input.clock.get()})
    private fun pcm(wav:ByteArray):ShortArray {
        val b=ByteBuffer.wrap(wav).order(ByteOrder.LITTLE_ENDIAN);b.position(44)
        return ShortArray((wav.size-44)/2){b.short}
    }
    private fun await(latch:CountDownLatch){assertTrue(latch.await(2,TimeUnit.SECONDS))}

    @Test fun onsetKeepsExact120msBeforeFirstSpeechAcrossRingWrap() {
        val classifier=Classifier();val onset=HandsFreeBargeInOnset(classifier)
        val quiet=(1..16).map{frame(it%5+1)};quiet.forEach{assertNull(onset.process(it,true))}
        val speech=(0..3).map{frame(1000+it)}
        speech.take(3).forEach{assertNull(onset.process(it,true))}
        val actual=onset.process(speech.last(),true)
        assertArrayEquals(flatten(quiet.takeLast(6)+speech),actual)
        assertNull(onset.process(frame(1000),true))
    }
    @Test fun unsafeReferenceBreaksConsecutiveOnsetAndErasesPreroll() {
        val classifier=Classifier();val onset=HandsFreeBargeInOnset(classifier){classifier.reset()}
        repeat(3){assertNull(onset.process(frame(1000),true))}
        assertNull(onset.process(frame(1000),false))
        repeat(3){assertNull(onset.process(frame(2000),true))}
        val actual=onset.process(frame(2000),true)
        assertArrayEquals(flatten(List(4){frame(2000)}),actual)
        assertEquals(1,classifier.resets.get())
    }
    @Test fun isolatedClickAnd40msCueDoNotQualifySpeech() {
        val onset=HandsFreeBargeInOnset(Classifier())
        repeat(20){
            assertNull(onset.process(frame(3000),true))
            assertNull(onset.process(frame(3000),true))
            repeat(4){assertNull(onset.process(frame(0),true))}
        }
    }
    @Test fun acceptedCommandPreservesEverySampleWithPartialReadsAndHeadDisappearing() {
        val quiet=List(12){frame(it%5+1)};val speech=List(22){frame(1000+it)}
        val frames=quiet+speech+List(40){frame(0)}
        val input=Input(flatten(frames),AtomicLong(),73);val approved=CountDownLatch(1)
        val classifier=Classifier();val calls=AtomicInteger()
        val capture=capture(input,classifier,head={if(approved.count>0)24000L else null},speech={
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
    @Test fun bufferedEchoCannotConfirmBeforeMonotonicGuardHasElapsed() {
        val echo=List(4+HandsFreeBargeInCapture.PROBE_SAMPLES/320){frame(1800)}
        val input=Input(flatten(echo),AtomicLong()).apply{timestampAt={0L}}
        val pauses=AtomicInteger();val resumes=AtomicInteger();val calls=AtomicInteger()
        val capture=capture(input,pause={pauses.incrementAndGet();true},resume={resumes.incrementAndGet()},
            speech={calls.incrementAndGet();true})
        assertTrue(capture.start());input.proceed.countDown();await(input.exhausted)
        assertEquals(1,pauses.get());assertEquals(1,resumes.get());assertEquals(0,calls.get())
        capture.close();assertTrue(capture.awaitClosed(1000));assertNull(capture.awaitCommand())
    }

    @Test fun failedProbeResumesAndCanConfirmADistinctLaterInterruption() {
        val cooldown=HandsFreeBargeInCapture.PROBE_COOLDOWN_SAMPLES/320
        val speech=List(24){frame(2200+it)}
        val frames=List(8){frame(1500)}+List(15+cooldown){frame(0)}+speech+List(40){frame(0)}
        val input=Input(flatten(frames),AtomicLong())
        val pauses=AtomicInteger();val resumes=AtomicInteger();val calls=AtomicInteger()
        val approved=CountDownLatch(1)
        val capture=capture(input,pause={pauses.incrementAndGet();true},resume={resumes.incrementAndGet()},
            speech={calls.incrementAndGet();approved.countDown();true})
        assertTrue(capture.start());input.proceed.countDown();await(approved)
        val actual=pcm(checkNotNull(capture.awaitCommand()).wav)
        assertArrayEquals(flatten(speech+List(40){frame(0)}),actual)
        assertEquals(2,pauses.get());assertEquals(1,resumes.get());assertEquals(1,calls.get())
    }

    @Test fun closingDuringProbeReturnsTheHeldPlaybackWithoutSubmitting() {
        val input=Input(flatten(List(6){frame(1700)}),AtomicLong())
        val pauses=AtomicInteger();val resumes=AtomicInteger();val calls=AtomicInteger()
        val capture=capture(input,pause={pauses.incrementAndGet();true},resume={resumes.incrementAndGet()},
            speech={calls.incrementAndGet();true})
        assertTrue(capture.start());input.proceed.countDown();await(input.exhausted)
        capture.close();assertTrue(capture.awaitClosed(1000))
        assertEquals(1,pauses.get());assertEquals(1,resumes.get());assertEquals(0,calls.get())
        assertNull(capture.awaitCommand())
    }
    @Test fun missingOrUnsafePlaybackReferenceNeverCallsOnSpeech() {
        for(missing in listOf(true,false)) {
            val input=Input(flatten(List(60){frame(2000)}),AtomicLong());val calls=AtomicInteger()
            val capture=capture(input,head={if(missing)null else 24000L},
                clean={_,_,_->false},speech={calls.incrementAndGet();true})
            assertTrue(capture.start());input.proceed.countDown();await(input.exhausted)
            assertEquals(0,calls.get());capture.close();assertTrue(capture.awaitClosed(1000))
            assertNull(capture.awaitCommand())
        }
    }
    @Test fun rejectedOrStaleTurnNeverProducesCommand() {
        for(reject in listOf(true,false)) {
            val input=Input(flatten(List(22){frame(2000)}),AtomicLong());val calls=AtomicInteger()
            val current={reject || input.offset<640}
            val capture=capture(input,current=current,speech={calls.incrementAndGet();false})
            assertTrue(capture.start());input.proceed.countDown()
            assertTrue(capture.awaitClosed(1000));assertNull(capture.awaitCommand())
            assertEquals(if(reject)1 else 0,calls.get())
            assertEquals(1,input.closes.get())
        }
    }
    @Test fun silencedRecorderNeverQualifiesSpeech() {
        val input=Input(flatten(List(8){frame(2000)}),AtomicLong());input.silenced=true
        val calls=AtomicInteger();val capture=capture(input,speech={calls.incrementAndGet();true})
        assertTrue(capture.start());input.proceed.countDown()
        assertTrue(capture.awaitClosed(1000));assertNull(capture.awaitCommand());assertEquals(0,calls.get())
    }
    @Test fun maxCommandIs20SecondsIncludingPreroll() {
        val input=Input(flatten(List(1100){frame(2000)}),AtomicLong());val approved=CountDownLatch(1)
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
        val input=Input(flatten(List(24){frame(2000)}+List(40){frame(0)}),AtomicLong())
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
                    val voice=if(withVoice && block>=12)1500*kotlin.math.sin(2*Math.PI*2781*(block*320+i)/16000.0) else 0.0
                    (rendered*.60+voice).toInt().toShort()
                }
            }}
            val input=Input(flatten(frames),AtomicLong());val filter=HandsFreeBargeInEcho()
            var pauseAt:Int?=null
            val approved=CountDownLatch(1);val calls=AtomicInteger()
            input.takeSample={i,sample->
                if(pauseAt?.let {i >= it+640}==true && i<45*320) {
                    (1500*kotlin.math.sin(2*Math.PI*2781*i/16000.0)).toInt().toShort()
                } else sample
            }
            val capture=capture(input,head={12000L+input.offset*3L/2},
                clean={samples,count,head->
                    filter.offer(bytes,0,head.toInt()*2,0)
                    filter.suppress(samples,count,head)
                },pause={pauseAt=input.offset;true},speech={calls.incrementAndGet();approved.countDown();true})
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

    @Test fun uncorrelatedSpeakerEchoResumesAfterPauseAndDoesNotReturnACommand() {
        val reference=ByteArray(24000)
        for(i in 0 until reference.size/2) {
            val value=(8000*kotlin.math.sin(2*Math.PI*311.2*i/24000)).toInt()
            reference[i*2]=value.toByte();reference[i*2+1]=(value shr 8).toByte()
        }
        val filter=HandsFreeBargeInEcho().also{it.offer(reference,0,reference.size,0)}
        // Distortion introduces harmonics absent from the render reference.
        val echo=ShortArray(36*320){i->
            val t=i/16000.0
            val phase=2*Math.PI*311.2*t
            val envelope=.65+.2*kotlin.math.sin(2*Math.PI*5.0*t)
            ((2600*kotlin.math.sin(2*phase)+600*kotlin.math.sin(3*phase))*envelope).toInt().toShort()
        }
        val input=Input(echo,AtomicLong())
        var pauseAt:Int?=null
        val pauses=AtomicInteger();val resumes=AtomicInteger();val calls=AtomicInteger()
        input.takeSample={i,sample->
            val sincePause=pauseAt?.let {i-it}
            if(sincePause!=null && resumes.get()==0) {
                (sample*(1920-sincePause).coerceIn(0,1920)/1920).toShort()
            } else sample
        }
        val capture=capture(input,head={12000L},
            clean={samples,count,head->filter.suppress(samples,count,head)},
            pause={pauses.incrementAndGet();pauseAt=input.offset;true},
            resume={resumes.incrementAndGet()},speech={calls.incrementAndGet();true})
        assertTrue(capture.start());input.proceed.countDown();await(input.exhausted)
        assertEquals(1,pauses.get());assertEquals(1,resumes.get());assertEquals(0,calls.get())
        capture.close();assertTrue(capture.awaitClosed(1000));assertNull(capture.awaitCommand())
    }
}
