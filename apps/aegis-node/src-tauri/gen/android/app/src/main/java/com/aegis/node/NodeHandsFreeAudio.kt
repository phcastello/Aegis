package com.aegis.node

import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.sqrt

/** One bounded ring; snapshots happen only at activation, never per PCM frame. */
internal class HandsFreeRing(private val capacity:Int=40000) {
    private val samples=ShortArray(capacity)
    private var head=0
    private var count=0
    fun append(source:ShortArray,length:Int) {
        for(i in 0 until length){samples[head]=source[i];head=(head+1)%capacity;count=minOf(capacity,count+1)}
    }
    fun copyTo(destination:ShortArray):Int {
        val start=(head-count+capacity)%capacity
        val first=minOf(count,capacity-start)
        samples.copyInto(destination,0,start,start+first)
        if(first<count)samples.copyInto(destination,first,0,count-first)
        return count
    }
    fun clear(){head=0;count=0;samples.fill(0)}
}

/** Conservative local energy VAD, 20ms frames; only session endpointing, never STT. */
internal class HandsFreeVad(private val silenceSamples:Int=12800,private val maxSamples:Int=320000) {
    private var noise=90.0
    private var voiced=0
    private var quiet=0
    var total=0;private set
    var hasSpeech=false;private set
    var hasVoiceStart=false;private set
    fun seedSpeech(){hasSpeech=true}
    fun process(samples:ShortArray,count:Int):Boolean {
        var offset=0
        while(offset<count) {
            val n=minOf(320,count-offset)
            var square=0.0
            for(i in offset until offset+n){val v=samples[i].toDouble();square+=v*v}
            val rms=sqrt(square/n)
            val speech=rms>maxOf(240.0,noise*3.0)
            if(speech){hasVoiceStart=true;voiced+=n;quiet=0;if(voiced>=1280)hasSpeech=true}
            else {quiet+=n;if(!hasSpeech)noise=noise*.98+minOf(rms,800.0)*.02}
            total+=n;offset+=n
        }
        return total>=maxSamples || (hasSpeech && total>=8000 && quiet>=silenceSamples) || (!hasSpeech && total>=48000)
    }
}

internal fun handsFreeWav(samples:ShortArray,count:Int):ByteArray {
    require(count in 1..360000)
    val buffer=ByteBuffer.allocate(44+count*2).order(ByteOrder.LITTLE_ENDIAN)
    buffer.put("RIFF".toByteArray());buffer.putInt(36+count*2);buffer.put("WAVEfmt ".toByteArray())
    buffer.putInt(16);buffer.putShort(1);buffer.putShort(1);buffer.putInt(16000);buffer.putInt(32000);buffer.putShort(2);buffer.putShort(16)
    buffer.put("data".toByteArray());buffer.putInt(count*2)
    for(i in 0 until count)buffer.putShort(samples[i])
    return buffer.array()
}

/** JNI batches 200ms AudioRecord blocks; feature stride remains internal 10ms / 3 frames. */
internal object MicroWakeWordNative {
    init {System.loadLibrary("aegis_microwakeword")}
    external fun create(model:ByteArray):Long
    external fun destroy(handle:Long)
    external fun reset(handle:Long):Boolean
    external fun process(handle:Long,samples:ShortArray,count:Int):Long
}
