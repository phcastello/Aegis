package com.aegis.node

import org.junit.Assert.*
import org.junit.Test
import java.io.File
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.Random

/** Explicit host JNI integration test, outside the ordinary Android/JVM mocked runtime suite. */
class NodeHandsFreeRealVadTest {
    private fun root()=File(System.getProperty("aegis.vad.privateRoot",System.getProperty("user.home")+"/.cache/aegis-wakeword-poc"))
    private fun classifier()=NativeHandsFreeSpeechClassifier()
    private fun frameNoise(count:Int)=ShortArray(count).also { pcm->val random=Random(20261031);for(i in pcm.indices)pcm[i]=(random.nextGaussian()*1000).toInt().coerceIn(-32768,32767).toShort() }
    private fun wav(file:File,tailSamples:Int?=null):ShortArray {
        RandomAccessFile(file,"r").use { input->
            fun integer():Int=Integer.reverseBytes(input.readInt())
            check(input.readInt()==0x52494646);integer();check(input.readInt()==0x57415645)
            var dataAt=0L;var bytes=0
            while(input.filePointer+8<=input.length()) {
                val chunk=input.readInt();val length=integer();check(length>=0)
                if(chunk==0x64617461){dataAt=input.filePointer;bytes=length;break}
                input.seek(input.filePointer+length+(length%2))
            }
            check(dataAt>0 && bytes%2==0)
            val count=minOf(bytes/2,tailSamples?:bytes/2);check(count<=16000*30)
            input.seek(dataAt+bytes-count*2)
            val raw=ByteArray(count*2);input.readFully(raw)
            val buffer=ByteBuffer.wrap(raw).order(ByteOrder.LITTLE_ENDIAN)
            return ShortArray(count){buffer.short}
        }
    }
    @Test fun generatedCueAndTwentyMillisecondPulseDoNotBecomeSpeechThroughRealVadHangover() {
        val pulse=ShortArray(320){if(it%30<15)2000 else -2000}
        for(offset in listOf(0,1,160,319))for(signal in listOf(HandsFreeCueSignal.samples,pulse))classifier().use { native->
            val command=HandsFreeCommandCapture(ShortArray(360000),40000,native)
            val first=ShortArray(3200);signal.copyInto(first,offset)
            command.append(first,first.size)
            repeat(15){command.append(ShortArray(3200),3200)}
            assertFalse("cue/click is not confirmed speech",command.hasSpeech)
            assertFalse("generated cue with silent pre-roll is not useful command audio",command.hasUsefulAudio)
        }
    }
    @Test fun sustainedNoiseAboveTheOldEnergyThresholdNoLongerRunsUntilTheTwentySecondLimit() {
        for(preroll in listOf(0,40000))classifier().use { native->
            val noise=frameNoise(360000)
            val command=HandsFreeCommandCapture(noise.copyOf(),preroll,native)
            var ended=false;var blocks=0
            while(!ended && blocks<100){ended=command.append(noise,3200);blocks++}
            assertTrue(ended);assertTrue("stationary noise should adapt/end before hard cap",blocks<20)
        }
    }
    @Test fun realNativeResetMatchesFreshAndRetainsTheSixteenKilohertzMode() {
        val noise=frameNoise(32000)
        classifier().use { dirty->classifier().use { fresh->
            for(offset in noise.indices step 320)dirty.isSpeech(noise.copyOfRange(offset,offset+320))
            dirty.reset()
            for(offset in noise.indices step 320) {
                val frame=noise.copyOfRange(offset,offset+320)
                assertEquals(fresh.isSpeech(frame),dirty.isSpeech(frame))
            }
        }}
    }
    @Test fun realQuietPreRollAndCueMixedWithSpeechKeepAllOriginalInputSamples() {
        val source=wav(File(root(),"data/wav/android-positive-01.wav")).map{(it*.08).toInt().toShort()}.toShortArray()
        classifier().use { native->
            val buffer=ShortArray(360000);source.copyInto(buffer,40000-source.size)
            val original=buffer.copyOf(40000)
            val capture=HandsFreeCommandCapture(buffer,40000,native)
            repeat(5){capture.append(ShortArray(3200),3200)}
            assertTrue(capture.hasUsefulAudio);assertArrayEquals(original,buffer.copyOf(40000))
        }
        classifier().use { native->
            val split=source.size-1600;val buffer=ShortArray(360000)
            source.copyInto(buffer,40000-split,0,split)
            val before=buffer.copyOf(40000)
            val capture=HandsFreeCommandCapture(buffer,40000,native)
            val mixed=ShortArray(3200);source.copyInto(mixed,0,split)
            for(i in HandsFreeCueSignal.samples.indices)mixed[i]=(mixed[i].toInt()+HandsFreeCueSignal.samples[i]).coerceIn(-32768,32767).toShort()
            capture.append(mixed,mixed.size)
            assertArrayEquals(before,buffer.copyOf(40000))
            assertArrayEquals(mixed,buffer.copyOfRange(40000,43200))
            assertTrue(capture.hasUsefulAudio)
        }
    }
    @Test fun availableRealEnvironmentAndOriginalNegativeRecordingsHaveBoundedCaptureAndNoMutation() {
        val privateFiles=File(root(),"data/wav").listFiles()!!.filter{it.name.contains("-negative-") && it.extension=="wav"}.sortedBy{it.name}
        assertEquals(8,privateFiles.size)
        val environment=wav(File(root(),"comparison/public/negatives.wav"),480000)
        val inputs=privateFiles.map{wav(it)}+listOf(environment)
        var hardLimited=0
        for(input in inputs)classifier().use { native->
            val initial=minOf(40000,input.size);val buffer=ShortArray(360000)
            input.copyInto(buffer,0,0,initial)
            val before=buffer.copyOf(initial)
            val capture=HandsFreeCommandCapture(buffer,initial,native)
            var offset=initial;var ended=false;var blocks=0
            while(!ended && blocks<101) {
                val block=ShortArray(3200)
                val count=minOf(3200,(input.size-offset).coerceAtLeast(0))
                if(count>0){input.copyInto(block,0,offset,offset+count);offset+=count}
                ended=capture.append(block,block.size);blocks++
            }
            assertTrue(ended);assertTrue(capture.length<=360000)
            assertArrayEquals(before,buffer.copyOf(initial))
            if(blocks>=100)hardLimited++
        }
        println("Real VAD replay:8 original negatives +30s public environment; bounded captures; hard-limit cases="+hardLimited)
    }
}
