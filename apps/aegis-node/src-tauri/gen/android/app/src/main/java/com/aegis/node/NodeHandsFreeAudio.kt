package com.aegis.node

import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.sqrt
import kotlin.math.PI
import kotlin.math.sin

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

/** Classifies one complete PCM16/16kHz/20ms frame. It does not own or change the PCM. */
internal fun interface HandsFreeSpeechClassifier { fun isSpeech(frame:ShortArray):Boolean }

/** Endpointing is independent of wake detection and never seeds evidence of a command. */
internal class HandsFreeVad(
    private val classifier:HandsFreeSpeechClassifier,
    private val silenceSamples:Int=12800,
    private val maxSamples:Int=320000,
    private val noSpeechSamples:Int=NO_SPEECH_SAMPLES
) {
    private val frame=ShortArray(FRAME_SAMPLES)
    private var pending=0
    private var voicedRun=0
    private var quiet=0
    private var noise=8.0
    var total=0;private set
    var hasSpeech=false;private set
    val hasVoiceStart:Boolean get()=hasSpeech
    var confirmedStartSample:Int?=null;private set
    val pendingStartSample:Int? get()=if(voicedRun>0)total-voicedRun else null
    var signalSamples=0;private set
    fun process(samples:ShortArray,count:Int):Boolean {
        require(count in 0..samples.size)
        var offset=0
        while(offset<count) {
            val n=minOf(FRAME_SAMPLES-pending,count-offset)
            samples.copyInto(frame,pending,offset,offset+n);pending+=n;offset+=n
            if(pending<FRAME_SAMPLES)continue
            pending=0;total+=FRAME_SAMPLES
            var sum=0.0;var square=0.0
            for(v in frame){sum+=v;square+=v.toDouble()*v}
            // DC and digital silence do not make a WebRTC hangover frame into fresh speech.
            val mean=sum/FRAME_SAMPLES
            val rms=sqrt(maxOf(0.0,square/FRAME_SAMPLES-mean*mean))
            if(rms>=8.0)signalSamples+=FRAME_SAMPLES
            val classified=classifier.isSpeech(frame)
            val active=classified && rms>=maxOf(8.0,noise*1.4)
            if(active) {
                voicedRun+=FRAME_SAMPLES;quiet=0
                if(voicedRun>=ONSET_SAMPLES && !hasSpeech) {
                    hasSpeech=true;confirmedStartSample=total-voicedRun
                }
            } else {
                voicedRun=0;quiet+=FRAME_SAMPLES
                // The floor can continue to adapt after confirmed speech. Wake state is irrelevant.
                if(!classified)noise=noise*.95+rms*.05
            }
        }
        return total>=maxSamples || (hasSpeech && quiet>=silenceSamples) || (!hasSpeech && total>=noSpeechSamples)
    }
    companion object {
        const val FRAME_SAMPLES=320
        const val ONSET_SAMPLES=1280 // 80ms; one click or the 40ms local cue is insufficient.
        const val NO_SPEECH_SAMPLES=48000
        const val VERSION="libfvad-532ab666-mode2-onset80ms"
    }
}

/** Same activation initialization used by the controller and regression tests. */
internal class HandsFreeCommandCapture(
    private val command:ShortArray,
    preRollCount:Int,
    classifier:HandsFreeSpeechClassifier
) {
    private val vad=HandsFreeVad(classifier,maxSamples=preRollCount+320000,noSpeechSamples=preRollCount+HandsFreeVad.NO_SPEECH_SAMPLES)
    private var afterActivation=0
    private val uncertainPreRoll:Boolean
    var length=preRollCount;private set
    val hasSpeech:Boolean get()=vad.hasSpeech
    // Energy cannot distinguish an isolated invocation from every short request. Preserve
    // uncertain real pre-roll; STT still rejects empty/name-only text before creating chat.
    val hasUsefulAudio:Boolean get()=vad.hasSpeech || uncertainPreRoll
    init {
        require(preRollCount in 0..command.size)
        vad.process(command,preRollCount)
        uncertainPreRoll=vad.signalSamples>=640 // At least40ms of variable pre-event audio.
    }
    fun append(samples:ShortArray,count:Int):Boolean {
        require(count in 0..samples.size)
        val accepted=minOf(count,command.size-length)
        samples.copyInto(command,length,0,accepted);length+=accepted;afterActivation+=accepted
        val endpoint=vad.process(samples,accepted)
        return length==command.size || afterActivation>=320000 || (afterActivation>=3200 && endpoint)
    }
}

/** A start inside2s may be confirmed afterwards; a start after the boundary is never accepted. */
internal class HandsFreeContinuationGate(classifier:HandsFreeSpeechClassifier) {
    private val vad=HandsFreeVad(classifier)
    private var received=0
    private var boundary:Int?=null
    fun process(samples:ShortArray,count:Int,beforeDeadlineSamples:Int,deadlineReached:Boolean):Decision {
        require(beforeDeadlineSamples in 0..count)
        if(boundary==null && (deadlineReached || beforeDeadlineSamples<count))boundary=received+beforeDeadlineSamples
        vad.process(samples,count);received+=count
        val start=vad.confirmedStartSample
        if(start!=null)return if(boundary==null || start<boundary!!)Decision.STARTED else Decision.TIMED_OUT
        val limit=boundary?:return Decision.WAIT
        val pending=vad.pendingStartSample
        return if(pending!=null && pending<limit && received-pending<=HandsFreeVad.ONSET_SAMPLES+HandsFreeVad.FRAME_SAMPLES)
            Decision.WAIT else Decision.TIMED_OUT
    }
    enum class Decision { WAIT, STARTED, TIMED_OUT }
}

/** Cached 40ms rising two-note cue: local PCM, no TTS/network and no microphone mute window. */
internal object HandsFreeCueSignal {
    const val SAMPLE_RATE=16000
    const val VERSION="aegis-dual-note-40ms-v1-gain1"
    // Keep the approved PCM unchanged. The old player gain of .18 attenuated it
    // by another 14.9dB; unity still leaves 16.3dB of digital peak headroom.
    const val PLAYER_GAIN=1.0f
    val samples:ShortArray=ShortArray(640).also { pcm->
        fun note(start:Int,frequency:Double) {
            val size=256 //16ms, separated by8ms; totalactive audio32ms.
            for(i in 0 until size) {
                val envelope=sin(PI*i/(size-1))
                pcm[start+i]=(5000.0*envelope*sin(2*PI*frequency*i/SAMPLE_RATE)).toInt().toShort()
            }
        }
        note(0,1046.5);note(384,1568.0)
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
