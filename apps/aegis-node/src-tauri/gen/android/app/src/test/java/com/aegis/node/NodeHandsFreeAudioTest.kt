package com.aegis.node

import org.junit.Assert.*
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import kotlin.math.abs
import kotlin.math.sqrt

class NodeHandsFreeAudioTest {
    private val neverSpeech=HandsFreeSpeechClassifier{false}
    private val voice=HandsFreeSpeechClassifier{frame->frame.any{it.toInt()>30 || it.toInt() < -30}}
    private fun speech(count:Int,level:Int=1500)=ShortArray(count){if(it%30<15)level.toShort() else (-level).toShort()}

    @Test fun activationPreservesKeywordAndUnpausedCommandAcrossRingWrap() {
        val ring=HandsFreeRing(40000)
        val stream=ShortArray(64000){(it%30000).toShort()}
        for(offset in 0 until 64000 step 3200)ring.append(stream.copyOfRange(offset,offset+3200),3200)
        val command=ShortArray(360000);val size=ring.copyTo(command)
        assertEquals(40000,size)
        assertArrayEquals(stream.copyOfRange(24000,64000),command.copyOf(size))
        ring.clear();assertEquals(0,ring.copyTo(command))
    }
    @Test fun actualActivationPathDoesNotSeedSpeechForEightHundredMillisecondsOfSilence() {
        // Exact controller helper/initialization, including its full pre-roll. A keyword event is not evidence.
        val command=HandsFreeCommandCapture(ShortArray(360000),40000,neverSpeech)
        var ended=false
        repeat(4){ended=command.append(ShortArray(3200),3200) || ended}
        assertFalse(ended);assertFalse(command.hasSpeech);assertFalse(command.hasUsefulAudio)
        repeat(11){ended=command.append(ShortArray(3200),3200) || ended}
        assertTrue(ended)
    }
    @Test fun longQuietPreRollDoesNotSpendThePostActivationNoSpeechBudget() {
        val command=HandsFreeCommandCapture(ShortArray(360000),40000,voice)
        repeat(5){assertFalse(command.append(ShortArray(3200),3200))}
        assertFalse(command.append(speech(3200,120),3200))
        assertTrue(command.hasSpeech);assertTrue(command.hasUsefulAudio)
    }
    @Test fun shortQuietRequestEntirelyInPreRollIsPreservedEvenWhenClassifierIsUncertain() {
        val pcm=ShortArray(360000)
        speech(1600,12).copyInto(pcm,38000)
        val before=pcm.copyOf(40000)
        val command=HandsFreeCommandCapture(pcm,40000,neverSpeech)
        assertFalse(command.hasSpeech);assertTrue(command.hasUsefulAudio)
        repeat(5){command.append(ShortArray(3200),3200)}
        assertArrayEquals(before,pcm.copyOf(40000))
    }
    @Test fun commandAcrossActivationBlockBoundaryIsKeptAndEndpointedWithoutClipping() {
        val pcm=ShortArray(360000);speech(1280).copyInto(pcm,38720)
        val command=HandsFreeCommandCapture(pcm,40000,voice)
        val tail=ShortArray(3200);speech(1920).copyInto(tail)
        assertFalse(command.append(tail,tail.size))
        var ended=false
        repeat(4){ended=command.append(ShortArray(3200),3200) || ended}
        assertTrue(ended);assertTrue(command.hasSpeech);assertTrue(command.hasUsefulAudio)
        assertArrayEquals(speech(1280),pcm.copyOfRange(38720,40000))
        assertArrayEquals(tail,pcm.copyOfRange(40000,43200))
    }
    @Test fun singleTwentyMillisecondPulseAndCueDoNotConfirmAnOnsetThroughClassifierHangover() {
        val alwaysSpeech=HandsFreeSpeechClassifier{true} // Stronger adverse case than real VAD hangover.
        for(pcm in listOf(speech(320),HandsFreeCueSignal.samples)) {
            val vad=HandsFreeVad(alwaysSpeech)
            vad.process(pcm,pcm.size);vad.process(ShortArray(3200),3200)
            assertFalse(vad.hasVoiceStart);assertFalse(vad.hasSpeech)
            val command=HandsFreeCommandCapture(ShortArray(360000),40000,alwaysSpeech)
            command.append(pcm,pcm.size);repeat(4){command.append(ShortArray(3200),3200)}
            assertFalse(command.hasUsefulAudio)
        }
    }
    @Test fun louderCuePreservesApprovedPcmAndHasDigitalHeadroom() {
        val pcm=HandsFreeCueSignal.samples
        val bytes=ByteBuffer.allocate(pcm.size*2).order(ByteOrder.LITTLE_ENDIAN)
        for(sample in pcm)bytes.putShort(sample)
        // Digest captured from the approved v1 waveform before changing player gain.
        assertEquals("8249525e9eaf920fbcc1397dba075252b08b26dab5349188fa81f833c94ddbc6",
            MessageDigest.getInstance("SHA-256").digest(bytes.array()).joinToString(""){"%02x".format(it)})
        assertEquals(640,pcm.size);assertEquals(16000,HandsFreeCueSignal.SAMPLE_RATE)
        assertEquals(1.0f,HandsFreeCueSignal.PLAYER_GAIN,0.0f)
        assertEquals(4997,pcm.maxOf{abs(it.toInt())})
        val rms=sqrt(pcm.sumOf{it.toDouble()*it}/pcm.size)
        assertEquals(2231.3221164816164,rms,0.00001)
        assertTrue(pcm.all{abs(it.toInt())<32767})
        assertTrue(pcm.sliceArray(256 until 384).all{it.toInt()==0})
    }
    @Test fun cueOverUnpausedCommandDoesNotMuteOrDropTheCommandStart() {
        val pcm=ShortArray(360000)
        val before=speech(1280);before.copyInto(pcm,38720)
        val capture=HandsFreeCommandCapture(pcm,40000,voice)
        val mixed=speech(3200)
        for(i in HandsFreeCueSignal.samples.indices) {
            val sum=mixed[i].toInt()+HandsFreeCueSignal.samples[i]
            assertTrue(sum in -32768..32767)
            mixed[i]=sum.toShort()
        }
        assertFalse(capture.append(mixed,mixed.size))
        repeat(4){capture.append(ShortArray(3200),3200)}
        assertTrue(capture.hasSpeech);assertTrue(capture.hasUsefulAudio)
        assertEquals(56000,capture.length)
        assertArrayEquals(before,pcm.copyOfRange(38720,40000))
        assertArrayEquals(mixed,pcm.copyOfRange(40000,43200))
    }
    @Test fun continuationStartInsideWindowCanBeConfirmedAfterDeadlineAndLastBeyondTwoSeconds() {
        val gate=HandsFreeContinuationGate(voice);val silence=ShortArray(3200)
        repeat(9){assertEquals(HandsFreeContinuationGate.Decision.WAIT,gate.process(silence,3200,3200,false))}
        val boundary=ShortArray(3200);speech(320).copyInto(boundary,2880) // Start1.98s.
        assertEquals(HandsFreeContinuationGate.Decision.WAIT,gate.process(boundary,3200,3200,true))
        assertEquals(HandsFreeContinuationGate.Decision.STARTED,gate.process(speech(3200),3200,0,true))
        val vad=HandsFreeVad(voice);repeat(40){assertFalse(vad.process(speech(3200),3200))}
        repeat(3){assertFalse(vad.process(silence,3200))}
        assertTrue(vad.process(silence,3200))
    }
    @Test fun noContinuationOrIsolatedClickNearDeadlineTimesOutAndDoesNotExtendWindow() {
        for(click in listOf(false,true)) {
            val gate=HandsFreeContinuationGate(voice)
            repeat(9){gate.process(ShortArray(3200),3200,3200,false)}
            val boundary=ShortArray(3200);if(click)speech(320).copyInto(boundary,2880)
            val first=gate.process(boundary,3200,3200,true)
            if(click){assertEquals(HandsFreeContinuationGate.Decision.WAIT,first);assertEquals(HandsFreeContinuationGate.Decision.TIMED_OUT,gate.process(ShortArray(3200),3200,0,true))}
            else assertEquals(HandsFreeContinuationGate.Decision.TIMED_OUT,first)
        }
        val late=HandsFreeContinuationGate(voice)
        assertEquals(HandsFreeContinuationGate.Decision.TIMED_OUT,late.process(speech(3200),3200,0,true))
    }
    @Test fun sustainedActualVoiceStopsAtHardLimitAndDoesNotFreezeNoiseUpdates() {
        val vad=HandsFreeVad(voice);val pcm=speech(3200)
        repeat(99){assertFalse(vad.process(pcm,3200))}
        assertTrue(vad.process(pcm,3200));assertEquals(320000,vad.total)
    }
    @Test fun partialPcmBlocksRetainTheirEvidenceAndExactSamples() {
        val pcm=speech(40000,120)
        val a=HandsFreeVad(voice);a.process(pcm,pcm.size)
        val b=HandsFreeVad(voice);var offset=0
        for(size in generateSequence(17){when(it){17->257;257->89;89->480;else->17}}) {
            if(offset==pcm.size)break
            val n=minOf(size,pcm.size-offset);b.process(pcm.copyOfRange(offset,offset+n),n);offset+=n
        }
        assertEquals(a.total,b.total);assertEquals(a.hasSpeech,b.hasSpeech);assertEquals(a.confirmedStartSample,b.confirmedStartSample)
        assertEquals(a.lastActiveEndSample,b.lastActiveEndSample)
    }
    @Test fun lastActiveFrameObservationDoesNotExtendTheApprovedEndpoint() {
        val vad=HandsFreeVad(voice)
        assertNull(vad.lastActiveEndSample)
        assertFalse(vad.process(speech(3200),3200))
        assertEquals(3200,vad.lastActiveEndSample)
        repeat(3){assertFalse(vad.process(ShortArray(3200),3200))}
        assertTrue(vad.process(ShortArray(3200),3200))
        assertEquals(16000,vad.total);assertEquals(3200,vad.lastActiveEndSample)
    }
    @Test fun wavContainsExactLittleEndianPcmAndRealLength() {
        val samples=shortArrayOf(-32768,-1,0,1,32767)
        val wav=handsFreeWav(samples,samples.size);val data=ByteBuffer.wrap(wav).order(ByteOrder.LITTLE_ENDIAN)
        assertEquals("RIFF",String(wav.copyOfRange(0,4)));assertEquals(46,data.getInt(4));assertEquals(16000,data.getInt(24))
        assertEquals(10,data.getInt(40))
        for(i in samples.indices)assertEquals(samples[i],data.getShort(44+i*2))
    }
}
