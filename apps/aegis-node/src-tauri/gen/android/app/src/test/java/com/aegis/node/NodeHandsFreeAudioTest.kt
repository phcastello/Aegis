package com.aegis.node

import org.junit.Assert.*
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder

class NodeHandsFreeAudioTest {
    @Test fun activationPreservesKeywordAndUnpausedCommandAcrossRingWrap() {
        val ring=HandsFreeRing(40000)
        val stream=ShortArray(64000){(it%30000).toShort()}
        for(offset in 0 until 64000 step 3200)ring.append(stream.copyOfRange(offset,offset+3200),3200)
        val command=ShortArray(360000);val size=ring.copyTo(command)
        assertEquals(40000,size)
        assertArrayEquals(stream.copyOfRange(24000,64000),command.copyOf(size))
        ring.clear();assertEquals(0,ring.copyTo(command))
    }
    @Test fun silenceTimesOutWithoutInventingSpeech() {
        val vad=HandsFreeVad();val silence=ShortArray(3200)
        repeat(14){assertFalse(vad.process(silence,silence.size))}
        assertTrue(vad.process(silence,silence.size));assertFalse(vad.hasSpeech)
    }
    @Test fun startInsideContinuationCanLastBeyondTwoSecondsThenEndpoint() {
        val vad=HandsFreeVad();val speech=ShortArray(3200){if(it%30<15)2500 else -2500}
        vad.process(speech,3200);assertTrue(vad.hasVoiceStart);assertTrue(vad.hasSpeech)
        repeat(40){assertFalse(vad.process(speech,3200))} // More than8s, not restricted to2s.
        val silence=ShortArray(3200)
        repeat(3){assertFalse(vad.process(silence,3200))}
        assertTrue(vad.process(silence,3200))
    }
    @Test fun sustainedVoiceStopsAtHardLimit() {
        val vad=HandsFreeVad();val speech=ShortArray(3200){1500}
        repeat(99){assertFalse(vad.process(speech,3200))}
        assertTrue(vad.process(speech,3200));assertEquals(320000,vad.total)
    }
    @Test fun wavContainsExactLittleEndianPcmAndRealLength() {
        val samples=shortArrayOf(-32768,-1,0,1,32767)
        val wav=handsFreeWav(samples,samples.size);val data=ByteBuffer.wrap(wav).order(ByteOrder.LITTLE_ENDIAN)
        assertEquals("RIFF",String(wav.copyOfRange(0,4)));assertEquals(46,data.getInt(4));assertEquals(16000,data.getInt(24))
        assertEquals(10,data.getInt(40))
        for(i in samples.indices)assertEquals(samples[i],data.getShort(44+i*2))
    }
}
