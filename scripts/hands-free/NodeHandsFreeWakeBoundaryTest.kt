package com.aegis.node

import org.junit.Assert.*
import org.junit.Assume.assumeNotNull
import org.junit.Test
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest

/** Host JVM invokes the same JNI entrypoints as Android; no microphone/device is used. */
class NodeHandsFreeWakeBoundaryTest {
    private fun model():ByteArray {
        val filename=System.getProperty("aegis.wake.model")
        assumeNotNull(filename)
        return File(filename!!).readBytes().also {
            assertEquals(39552,it.size)
            assertEquals("f9be8104b6a82dc91ed38febd1601e07eca0ef51f2ebbec4899e06f6e3aea514",
                MessageDigest.getInstance("SHA-256").digest(it).joinToString(""){b->"%02x".format(b)})
        }
    }
    private fun wav(file:File):ShortArray {
        val bytes=file.readBytes();val data=ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        check(String(bytes.copyOfRange(0,4))=="RIFF" && String(bytes.copyOfRange(8,12))=="WAVE")
        var offset=12
        while(offset+8<=bytes.size) {
            val count=data.getInt(offset+4);check(count>=0 && count<=bytes.size-offset-8)
            if(String(bytes.copyOfRange(offset,offset+4))=="data") {
                check(count%2==0)
                return ShortArray(count/2+8000){if(it<count/2)data.getShort(offset+8+it*2) else 0}
            }
            offset+=8+count+(count%2)
        }
        error("wav_data_missing")
    }
    @Test fun jniBoundsAndResetNeverReturnInventedScores() {
        val model=model();val handle=MicroWakeWordNative.create(model);model.fill(0)
        assertTrue(handle!=0L)
        try {
            assertTrue(MicroWakeWordNative.activationScores(handle).all{it.isNaN()})
            assertEquals(-2L,MicroWakeWordNative.process(handle,ShortArray(1),2))
            assertEquals(-2L,MicroWakeWordNative.process(handle,ShortArray(1),-1))
            assertEquals(-1L,MicroWakeWordNative.process(handle,ShortArray(3200),3200))
            assertTrue(MicroWakeWordNative.activationScores(handle).all{it.isNaN()})
            assertTrue(MicroWakeWordNative.reset(handle))
            assertTrue(MicroWakeWordNative.activationScores(handle).all{it.isNaN()})
        } finally {MicroWakeWordNative.destroy(handle)}
    }
    @Test fun jniEventIndexAndScoresRemainExactInsideTwoHundredMillisecondReads() {
        val model=model();val handle=MicroWakeWordNative.create(model);model.fill(0)
        assertTrue(handle!=0L)
        try {
            val root=File(System.getProperty("aegis.vad.privateRoot"),"data/wav")
            val files=root.listFiles()!!.filter{it.name.contains("positive") && it.extension=="wav"}.sortedBy{it.name}
            var activations=0
            for(file in files) {
                val pcm=wav(file)
                var expected:List<Triple<Long,Float,Float>>?=null
                for(chunks in listOf(listOf(160),listOf(3200),listOf(17,257,89,480,3200))) {
                    assertTrue(MicroWakeWordNative.reset(handle))
                    val observed=mutableListOf<Triple<Long,Float,Float>>()
                    var offset=0;var index=0
                    while(offset<pcm.size) {
                        val count=minOf(chunks[index++%chunks.size],pcm.size-offset)
                        val event=MicroWakeWordNative.process(handle,pcm.copyOfRange(offset,offset+count),count)
                        assertTrue(event>=-1)
                        val scores=MicroWakeWordNative.activationScores(handle)
                        if(event>=0) {
                            val boundary=HandsFreeActivationBoundary(offset.toLong(),count,event)
                            assertEquals(event-offset,boundary.offset.toLong())
                            assertEquals(0L,event%160)
                            assertTrue(scores.all{it.isFinite()})
                            observed.add(Triple(event,scores[0],scores[1]))
                        } else assertTrue(scores.all{it.isNaN()})
                        offset+=count
                    }
                    if(expected==null){expected=observed;activations+=observed.size}
                    else assertEquals(expected,observed)
                }
            }
            assertTrue("positive replay must exercise an accepted event",activations>0)
        } finally {MicroWakeWordNative.destroy(handle)}
    }
}
