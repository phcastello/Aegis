package com.aegis.node

import org.junit.Assert.*
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import kotlin.math.abs
import kotlin.math.sqrt

class NodeHandsFreeAudioTest {
    @Test fun classroomSpeechKeepsTheLouderCommandAndEndsWhileTheRoomKeepsTalking() {
        // Synthetic mixed signals. The VAD sees speech in every frame,
        // exercising the relative level gate independently of ASR.
        val room = ShortArray(320) { index ->
            ((if(index % 17 < 8) 730 else -670) + (if(index % 29 < 14) 290 else -270)).toShort()
        }
        val foreground = ShortArray(320) { index ->
            (room[index] + if(index % 31 < 16) 3800 else -3600).toShort()
        }
        val ring = HandsFreeRing()
        val quietRoom = ShortArray(320) { (room[it]/4).toShort() }
        repeat(71) {
            val frame = if (it%2==0) quietRoom else room
            ring.append(frame,frame.size)
        }
        // The stronger recent keyword must not influence the background estimate.
        repeat(50) { ring.append(foreground,foreground.size) }
        val speechFloor = checkNotNull(ring.sustainedSpeechFloor(0))
        assertTrue(speechFloor in 900.0..1300.0)
        val command = ShortArray(320000)
        val capture = HandsFreeCommandCapture(command,0,HandsFreeSpeechClassifier{true},
            sustainedSpeechFloor=speechFloor)
        repeat(10) { assertFalse(capture.append(room,room.size)) }
        assertFalse(capture.hasUsefulAudio)
        repeat(20) { assertFalse(capture.append(foreground,foreground.size)) }
        assertTrue(capture.hasUsefulAudio)
        repeat(20) { assertFalse(capture.append(room,room.size)) }
        assertFalse(capture.append(foreground,foreground.size)) // a loud single background syllable
        repeat(18) { assertFalse(capture.append(room,room.size)) }
        assertTrue(capture.append(room,room.size))
        assertEquals(30*320,capture.lastActiveEndSample)
        assertEquals(4*320,capture.transcriptionStart)
        assertEquals(32*320,capture.transcriptionLength)
        assertArrayEquals(foreground,command.copyOfRange(10*320,11*320))
    }
    private val neverSpeech=HandsFreeSpeechClassifier{false}
    private val voice=HandsFreeSpeechClassifier{frame->frame.any{it.toInt()>30 || it.toInt() < -30}}
    private fun speech(count:Int,level:Int=1500)=ShortArray(count){if(it%30<15)level.toShort() else (-level).toShort()}

    @Test fun preTranscriptionCorrectionRetainsBothCommandsAndRebuildsBoundedPcmHeader() {
        val first=shortArrayOf(111,222,333)
        val correction=shortArrayOf(444,555)
        val oldWav=handsFreeWav(first,first.size)
        val newWav=handsFreeWav(correction,correction.size)
        val joined=handsFreeJoinUntranscribedCommands(oldWav,newWav)
        val header=ByteBuffer.wrap(joined).order(ByteOrder.LITTLE_ENDIAN)
        assertEquals(joined.size-8,header.getInt(4))
        assertEquals(joined.size-44,header.getInt(40))
        header.position(44)
        val samples=ShortArray((joined.size-44)/2){header.short}
        assertArrayEquals(first+ShortArray(4000)+correction,samples)
        assertArrayEquals(handsFreeWav(first,first.size),oldWav)
        assertArrayEquals(handsFreeWav(correction,correction.size),newWav)
        var many=oldWav
        val latest=handsFreeWav(ShortArray(320000){1234},320000)
        repeat(8){many=handsFreeJoinUntranscribedCommands(many,latest)}
        assertTrue(many.size<=44+90*16000*2)
        assertArrayEquals(latest.copyOfRange(44,latest.size),many.copyOfRange(many.size-latest.size+44,many.size))
    }

    @Test fun ringRetainsTheExactSuffixAcrossPartialWrappedAndOversizedBlocks() {
        for(capacity in listOf(1,17,160,40000)) {
            val ring=HandsFreeRing(capacity)
            var expected=shortArrayOf()
            val sizes=listOf(0,1,159,3200,capacity,capacity+1,capacity*2+19,17,0,89)
            for((step,length) in sizes.withIndex()) {
                // Padding is deliberately outside length and must never enter the ring.
                val source=ShortArray(length+3){(step*701+it).toShort()}
                ring.append(source,length)
                expected=(expected+source.copyOf(length)).takeLast(capacity).toShortArray()
                val actual=ShortArray(capacity+3){Short.MIN_VALUE}
                assertEquals(expected.size,ring.copyTo(actual))
                assertArrayEquals(expected,actual.copyOf(expected.size))
                assertTrue(actual.drop(expected.size).all{it==Short.MIN_VALUE})
                if(expected.isNotEmpty()) {
                    val tail=ShortArray(expected.size+2){Short.MAX_VALUE}
                    val from=expected.size/2
                    assertEquals(expected.size-from,ring.copyRangeTo(tail,from,expected.size-from,1))
                    assertArrayEquals(expected.copyOfRange(from,expected.size),tail.copyOfRange(1,1+expected.size-from))
                    assertEquals(Short.MAX_VALUE,tail[0])
                }
            }
            ring.clear();assertEquals(0,ring.size)
            ring.append(shortArrayOf(42),1)
            val fresh=ShortArray(1);assertEquals(1,ring.copyTo(fresh));assertArrayEquals(shortArrayOf(42),fresh)
        }
    }

    @Test fun activationPreservesKeywordAndUnpausedCommandAcrossRingWrap() {
        val ring=HandsFreeRing(40000)
        val stream=ShortArray(64000){(it%30000).toShort()}
        for(offset in 0 until 64000 step 3200)ring.append(stream.copyOfRange(offset,offset+3200),3200)
        val command=ShortArray(360000);val size=ring.copyTo(command)
        assertEquals(40000,size)
        assertArrayEquals(stream.copyOfRange(24000,64000),command.copyOf(size))
        ring.clear();assertEquals(0,ring.copyTo(command))
    }
    @Test fun conversationBeforeDecisionRemainsOnlyInLocalContext() {
        val ring=HandsFreeRing()
        val conversation=ShortArray(40000){111}
        ring.append(conversation,conversation.size)
        val block=ShortArray(3200){if(it<1760)222 else 333}
        ring.append(block,block.size)
        val boundary=HandsFreeActivationBoundary(40000,3200,41760)
        val command=ShortArray(320000)
        assertEquals(1440,boundary.copyPostTo(block,command))
        assertTrue(command.take(1440).all{it.toInt()==333})
        val context=boundary.context(ring)
        assertEquals(33440,context.size)
        assertTrue(context.take(30240).all{it.toInt()==111})
        assertTrue(context.slice(30240 until 32000).all{it.toInt()==222})
        assertTrue(context.takeLast(1440).all{it.toInt()==333})
    }
    @Test fun isolatedKeywordOrFalseActivationNeverSeedsOldSpeechIntoCommand() {
        for(keyword in listOf(222,777)) {
            val ring=HandsFreeRing();ring.append(speech(40000),40000)
            val block=ShortArray(3200){if(it<3040)keyword.toShort() else 0}
            ring.append(block,block.size)
            val command=ShortArray(320000)
            val count=HandsFreeActivationBoundary(40000,3200,43040).copyPostTo(block,command)
            val capture=HandsFreeCommandCapture(command,count,voice)
            repeat(15){capture.append(ShortArray(3200),3200)}
            assertFalse(capture.hasSpeech)
            assertFalse(capture.hasUsefulAudio)
            assertTrue(command.take(capture.length).all{it.toInt()==0})
        }
    }
    @Test fun unpausedCommandAndNearBlockEdgesPreserveEverySampleAfterDecision() {
        for(offset in listOf(0,1,159,160,1760,3199,3200)) {
            val block=ShortArray(3200){(it+1).toShort()}
            val command=ShortArray(320000)
            val boundary=HandsFreeActivationBoundary(9600,3200,9600L+offset)
            val initial=boundary.copyPostTo(block,command)
            val capture=HandsFreeCommandCapture(command,initial,neverSpeech)
            val next=ShortArray(3200){(it+3201).toShort()}
            capture.append(next,next.size)
            assertArrayEquals(block.copyOfRange(offset,3200)+next,command.copyOf(capture.length))
        }
    }
    @Test fun cancellationAtDecisionBoundaryProducesValidEmptyPostWav() {
        val boundary=HandsFreeActivationBoundary(0,3200,3200)
        val command=ShortArray(320000)
        assertEquals(0,boundary.copyPostTo(speech(3200),command))
        val wav=handsFreeWav(command,0)
        val header=ByteBuffer.wrap(wav).order(ByteOrder.LITTLE_ENDIAN)
        assertEquals(44,wav.size);assertEquals(36,header.getInt(4));assertEquals(0,header.getInt(40))
        assertEquals(16000,header.getInt(24));assertEquals(1,header.getShort(22).toInt())
    }
    @Test fun prematureCancellationRetainsOnlyAlreadyCapturedPostSamples() {
        val block=ShortArray(3200){it.toShort()};val command=ShortArray(320000)
        val n=HandsFreeActivationBoundary(0,3200,3040).copyPostTo(block,command)
        val capture=HandsFreeCommandCapture(command,n,neverSpeech)
        val wav=handsFreeWav(command,capture.length)
        val data=ByteBuffer.wrap(wav).order(ByteOrder.LITTLE_ENDIAN)
        assertEquals(160,capture.length);assertEquals(320,data.getInt(40))
        for(i in 0 until 160)assertEquals(block[3040+i],data.getShort(44+i*2))
    }
    @Test fun invalidDetectorOffsetsAreRejectedInsteadOfSendingRingPreroll() {
        for(sample in listOf(3199L,6401L)) {
            try {HandsFreeActivationBoundary(3200,3200,sample);fail("outside_block_accepted")}
            catch(_:IllegalArgumentException){}
        }
    }
    @Test fun initialPostSuffixCountsTowardTheTwentySecondHardLimit() {
        val command=ShortArray(320000);speech(3040).copyInto(command)
        val capture=HandsFreeCommandCapture(command,3040,voice)
        repeat(99){assertFalse(capture.append(speech(3200),3200))}
        assertTrue(capture.append(speech(3200),3200))
        assertEquals(320000,capture.length)
        assertTrue(capture.append(speech(3200),3200))
        assertEquals(320000,capture.length)
    }
    @Test fun cancellationInsideReadRetainsTheExactPrefixBeforeItsClockBoundary() {
        val count=3200;val readAt=1000L
        for((cancelAt,expected) in listOf(799L to 0,800L to 0,801L to 16,900L to 1600,999L to 3184,1000L to 3200,1001L to 3200)) {
            val accepted=handsFreeSamplesBeforeCancellation(count,readAt,cancelAt)
            assertEquals(expected,accepted)
            val pcm=ShortArray(count){it.toShort()}
            val wav=handsFreeWav(pcm,accepted)
            val data=ByteBuffer.wrap(wav).order(ByteOrder.LITTLE_ENDIAN)
            assertEquals(accepted*2,data.getInt(40))
            for(i in 0 until accepted)assertEquals(pcm[i],data.getShort(44+i*2))
        }
    }
    @Test fun cancellationOfEmptyOrPartialReadCannotAddSamples() {
        assertEquals(0,handsFreeSamplesBeforeCancellation(0,1000,900))
        assertEquals(0,handsFreeSamplesBeforeCancellation(0,1000,1001))
        assertEquals(1,handsFreeSamplesBeforeCancellation(17,1000,999))
        assertEquals(0,handsFreeSamplesBeforeCancellation(17,1000,998))
        assertEquals(17,handsFreeSamplesBeforeCancellation(17,1000,1001))
        assertEquals(0,handsFreeSamplesBeforeCancellation(3200,Long.MAX_VALUE,Long.MIN_VALUE))
        try{handsFreeSamplesBeforeCancellation(-1,1000,999);fail("negative_count_accepted")}
        catch(_:IllegalArgumentException){}
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
    @Test fun wakeTailAndCueEchoCannotEndCaptureBeforePostCueSpeech() {
        val pcm=ShortArray(320000)
        speech(3200).copyInto(pcm) // 200ms tail of the wake word, already confirmed by VAD.
        val capture=HandsFreeCommandCapture(pcm,3200,voice,waitForActivationCue=true)
        var now=1000L
        fun append(samples:ShortArray,ready:Boolean=false):Boolean {
            now+=samples.size/16L
            return capture.append(samples,samples.size,ready,now)
        }
        repeat(5){assertFalse(append(ShortArray(3200)))} // Delayed cue, despite >800ms quiet.
        val cue=ShortArray(3200)
        HandsFreeCueSignal.samples.copyInto(cue)
        assertFalse(append(cue,true))
        assertTrue(capture.hasSpeech)
        assertFalse(append(speech(1920),true)) // Acoustic tail after the cue is observed.
        repeat(8){assertFalse(append(ShortArray(3200),true))}
        assertFalse(append(speech(3200),true)) // The user finally starts 1.7s after the cue.
        repeat(3){assertFalse(append(ShortArray(3200),true))}
        assertTrue(append(ShortArray(3200),true)) // Real command gets the ordinary 800ms endpoint.
        assertTrue(capture.hasUsefulAudio)
    }

    @Test fun initialListeningBudgetStartsAtObservedCueCompletionAndAlsoUsesWallClock() {
        val capture=HandsFreeCommandCapture(ShortArray(320000),0,neverSpeech,waitForActivationCue=true)
        val silence=ShortArray(3200)
        repeat(15){assertFalse(capture.append(silence,silence.size,false,200L*it))}
        assertFalse(capture.append(silence,silence.size,true,3000L))
        // Already-buffered reads cannot spend time faster than elapsed real time.
        repeat(15){assertFalse(capture.append(silence,silence.size,true,3500L))}
        assertFalse(capture.append(silence,silence.size,true,5999L))
        assertTrue(capture.append(silence,silence.size,true,6000L))
        assertFalse(capture.hasUsefulAudio)
    }

    @Test fun queuedVoiceBlocksCannotSpendThePostCueSpeechGuardBeforeRealTimePasses() {
        val capture=HandsFreeCommandCapture(ShortArray(320000),0,voice,waitForActivationCue=true)
        val silence=ShortArray(3200)
        assertFalse(capture.append(silence,silence.size,true,1000L))
        // Capture catches up with queued voice in two immediate reads. Their
        // sample count alone would have bypassed the settling guard.
        repeat(2){assertFalse(capture.append(speech(3200),3200,true,1001L))}
        repeat(4){assertFalse(capture.append(silence,silence.size,true,1200L+200L*it))}
        assertTrue(capture.hasSpeech)
        // The queued utterance is retained; wait for the user's new utterance.
        assertFalse(capture.append(speech(3200),3200,true,2300L))
        repeat(3){assertFalse(capture.append(silence,silence.size,true,2500L+200L*it))}
        assertTrue(capture.append(silence,silence.size,true,3100L))
    }

    @Test fun unpausedShortCommandBeforeCueIsPreservedAndCanReachTheEndpoint() {
        val pcm=ShortArray(320000)
        val earlySpeech=speech(3200)
        earlySpeech.copyInto(pcm)
        val capture=HandsFreeCommandCapture(pcm,earlySpeech.size,voice,waitForActivationCue=true)
        val silence=ShortArray(3200)
        assertFalse(capture.append(silence,silence.size,true,200L))
        repeat(14){assertFalse(capture.append(silence,silence.size,true,400L+200L*it))}
        assertTrue(capture.append(silence,silence.size,true,3200L))
        assertTrue(capture.hasUsefulAudio)
        assertArrayEquals(earlySpeech,pcm.copyOf(earlySpeech.size))
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
