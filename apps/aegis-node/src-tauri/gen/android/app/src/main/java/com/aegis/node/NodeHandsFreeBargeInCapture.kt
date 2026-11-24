package com.aegis.node

import android.media.AudioRecord
import android.media.AudioRecordingConfiguration
import android.media.AudioManager
import android.os.Build
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference

internal data class BargeInCommand(val wav:ByteArray,val trace:HandsFreeTurnLatency)

internal interface HandsFreeBargeInInput:AutoCloseable {
    fun start()
    fun read(samples:ShortArray,offset:Int,count:Int):Int
    fun stop()
    fun silenced():Boolean=false
}
internal interface HandsFreeBargeInClassifier:HandsFreeSpeechClassifier,AutoCloseable { fun reset() }

/** Only safely cleaned PCM can qualify an onset. At acceptance retain120ms before
 * the confirmed80ms onset, including exact samples across frame/ring boundaries.
 */
internal class HandsFreeBargeInOnset(
    private val classifier:HandsFreeSpeechClassifier,
    private val resetClassifier:()->Unit={}
) {
    private val ring=HandsFreeRing(PRE_SAMPLES+HandsFreeVad.ONSET_SAMPLES)
    private var vad=HandsFreeVad(classifier)
    private var received=0L
    private var latched=false
    fun process(frame:ShortArray,safe:Boolean):ShortArray? {
        require(frame.size==HandsFreeVad.FRAME_SAMPLES)
        if(latched)return null
        if(!safe) {
            ring.clear()
            if(received>0){resetClassifier();vad=HandsFreeVad(classifier);received=0}
            return null
        }
        ring.append(frame,frame.size);received+=frame.size
        vad.process(frame,frame.size)
        val onset=vad.confirmedStartSample?:return null
        val retainedStart=received-ring.size
        val begin=maxOf(retainedStart,(onset-PRE_SAMPLES).coerceAtLeast(0).toLong())
        val size=(received-begin).toInt()
        latched=true
        return ShortArray(size).also{ring.copyRangeTo(it,(begin-retainedStart).toInt(),size)}
    }
    fun clear(){ring.clear()}
    companion object { const val PRE_SAMPLES=1920 }
}

/** One microphone, owned only for the current response and its accepted new utterance.
 * close() requests stop without joining; awaitClosed() proves stop/release completed.
 */
internal class HandsFreeBargeInCapture internal constructor(
    private val input:HandsFreeBargeInInput,
    private val clean:(ShortArray,Int,Long)->Boolean,
    private val playedFrames:()->Long?,
    private val stillCurrent:()->Boolean,
    private val onSpeech:()->Boolean,
    private val classifierFactory:()->HandsFreeBargeInClassifier,
    private val clock:()->Long={HandsFreeTurnLatency.now()}
):AutoCloseable {
    constructor(audio:AudioRecord,echo:HandsFreeBargeInEcho,playedFrames:()->Long?,stillCurrent:()->Boolean,onSpeech:()->Boolean):
        this(AndroidInput(audio),{pcm,count,head->echo.suppress(pcm,count,head)},playedFrames,stillCurrent,onSpeech,{
            val native=NativeHandsFreeSpeechClassifier()
            object:HandsFreeBargeInClassifier {
                override fun isSpeech(frame:ShortArray)=native.isSpeech(frame)
                override fun reset()=native.reset()
                override fun close()=native.close()
            }
        })
    private val lifecycle=AtomicInteger(0) // new, started, closing, released
    private val stopping=AtomicBoolean(false)
    private val accepted=AtomicBoolean(false)
    private val discard=AtomicBoolean(false)
    private val stopStarted=AtomicBoolean(false)
    private val releaseStarted=AtomicBoolean(false)
    private val releaseSucceeded=AtomicBoolean(false)
    private val ready=CountDownLatch(1)
    private val stoppedInput=CountDownLatch(1)
    private val closed=CountDownLatch(1)
    private val completed=CountDownLatch(1)
    private val result=AtomicReference<BargeInCommand?>()
    private val deliveryLock=Any()
    @Volatile private var readySucceeded=false
    @Volatile private var decisionAt:Long?=null

    fun start():Boolean {
        if(!lifecycle.compareAndSet(0,1))return readySucceeded && !stopping.get()
        try{Thread({capture()},"AegisBargeInAudio").apply{isDaemon=true;start()}}
        catch(_:Exception){
            stopping.set(true);lifecycle.set(2)
            Thread({releaseInput();ready.countDown();completed.countDown()},"AegisBargeInRelease").apply{isDaemon=true;start()}
            return false
        }
        val success=try{ready.await(START_WAIT_MS,TimeUnit.MILLISECONDS) && readySucceeded && !stopping.get()}
            catch(_:InterruptedException){Thread.currentThread().interrupt();false}
        if(!success)close()
        return success
    }
    fun awaitCommand():BargeInCommand? {
        // Root calls only after its approved interruption. A decision callback can
        // still be returning while cancellation has already unblocked the old job.
        if(decisionAt==null && !accepted.get()){close();return null}
        val remaining=(decisionAt?.let{COMMAND_WAIT_MS-(clock()-it).coerceAtLeast(0)}?:COMMAND_WAIT_MS).coerceIn(0,COMMAND_WAIT_MS)
        val done=try{completed.await(remaining,TimeUnit.MILLISECONDS)}
            catch(_:InterruptedException){Thread.currentThread().interrupt();false}
        if(!done){discardResult();close();return null}
        return synchronized(deliveryLock) {
            if(accepted.get() && releaseSucceeded.get() && !discard.get())result.getAndSet(null)
            else {discard.set(true);result.getAndSet(null)?.wav?.fill(0);null}
        }
    }
    private fun discardResult()=synchronized(deliveryLock){discard.set(true);result.getAndSet(null)?.wav?.fill(0)}
    fun awaitClosed(timeoutMs:Long):Boolean {
        require(timeoutMs>=0)
        return try{closed.await(timeoutMs,TimeUnit.MILLISECONDS) && releaseSucceeded.get()}
            catch(_:InterruptedException){Thread.currentThread().interrupt();false}
    }
    override fun close() {
        stopping.set(true)
        val previous=lifecycle.getAndUpdate{if(it<2)2 else it}
        if(previous>=2)return
        // AudioRecord.stop may perform IPC. Never execute it while the caller's
        // runtime lock is held or wait for the audio worker from that caller.
        Thread({
            stopInput()
            if(previous==0)releaseInput()
        },"AegisBargeInStop").apply{isDaemon=true;start()}
    }
    private fun current():Boolean=!stopping.get() && try{stillCurrent()}catch(_:Exception){false}
    private fun capture() {
        val frame=ShortArray(HandsFreeVad.FRAME_SAMPLES)
        val original=ShortArray(frame.size)
        var classifier:HandsFreeBargeInClassifier?=null
        var onset:HandsFreeBargeInOnset?=null
        var command:ShortArray?=null
        var state:HandsFreeCommandCapture?=null
        var candidate:BargeInCommand?=null
        var trace:HandsFreeTurnLatency?=null
        var lastActiveAt:Long?=null
        var frozenHead:Long?=null
        var lastHead:Long?=null
        var tailSamples=0
        try {
            if(!current())return
            val vad=classifierFactory();classifier=vad
            onset=HandsFreeBargeInOnset(vad){vad.reset()}
            input.start()
            if(!current())return
            readySucceeded=true;ready.countDown()
            var pending=0
            while(current()) {
                val count=input.read(frame,pending,frame.size-pending)
                if(!current())break
                check(count>0 && count<=frame.size-pending){"barge_microphone_unavailable"}
                if(input.silenced())break
                pending+=count
                if(pending<frame.size)continue
                pending=0
                val now=clock()
                val observed=try{playedFrames()?.takeIf{it>=0}}catch(_:Exception){null}
                if(!accepted.get()) {
                    val monotonic=observed!=null && (lastHead==null || observed>=checkNotNull(lastHead))
                    if(monotonic)lastHead=observed
                    val safe=monotonic && observed!=null && clean(frame,frame.size,observed)
                    val prefix=onset.process(frame,safe)
                    if(prefix==null)continue
                    decisionAt=now
                    val approved=try{onSpeech()}catch(_:Exception){false}
                    if(!approved){prefix.fill(0);break}
                    accepted.set(true);frozenHead=lastHead
                    val pcm=ShortArray(320000);command=pcm
                    prefix.copyInto(pcm);val initial=prefix.size;prefix.fill(0)
                    vad.reset();state=HandsFreeCommandCapture(pcm,initial,vad)
                    trace=HandsFreeTurnLatency(clock=clock).also{it.markAt("barge_in_accepted",now)}
                    state.lastActiveEndSample?.let{lastActiveAt=now-(state.length-it)*1000L/16000}
                    continue
                }
                // Preserve the user's PCM even when the old player/head disappears.
                // Attempt a short residual-tail cleanup, then capture raw user audio.
                if(tailSamples<TAIL_SAMPLES) {
                    frame.copyInto(original)
                    val head=observed?.takeIf{frozenHead==null || it>=checkNotNull(frozenHead)}?:frozenHead
                    if(head==null || !clean(frame,frame.size,head))original.copyInto(frame)
                    tailSamples+=frame.size
                }
                val capture=checkNotNull(state)
                val ended=capture.append(frame,frame.size)
                capture.lastActiveEndSample?.let{lastActiveAt=now-(capture.length-it)*1000L/16000}
                if(ended || now-checkNotNull(decisionAt)>=20000L) {
                    if(capture.hasUsefulAudio) {
                        val timing=checkNotNull(trace)
                        lastActiveAt?.let{timing.markAt("last_vad_active_frame_estimated",it)}
                        timing.markAt("endpoint_completed",now);timing.mark("wav_prepare_started")
                        val wav=handsFreeWav(checkNotNull(command),capture.length);timing.mark("wav_ready")
                        candidate=BargeInCommand(wav,timing)
                    }
                    break
                }
            }
        }catch(_:Exception) {
            // No partial or failed capture becomes a command.
        }finally {
            stopping.set(true);ready.countDown()
            try{classifier?.close()}catch(_:Exception){}
            onset?.clear();frame.fill(0);original.fill(0);command?.fill(0)
            releaseInput()
            synchronized(deliveryLock) {
                if(!discard.get() && accepted.get() && releaseSucceeded.get())result.set(candidate)
                else candidate?.wav?.fill(0)
            }
            completed.countDown()
        }
    }
    private fun stopInput() {
        if(!stopStarted.compareAndSet(false,true))return
        try{input.stop()}catch(_:Exception){}finally{stoppedInput.countDown()}
    }
    private fun releaseInput() {
        if(!releaseStarted.compareAndSet(false,true))return
        stopInput()
        var interrupted=false
        while(true){try{stoppedInput.await();break}catch(_:InterruptedException){interrupted=true}}
        try{input.close();releaseSucceeded.set(true)}catch(_:Exception){}
        lifecycle.set(3);closed.countDown()
        if(interrupted)Thread.currentThread().interrupt()
    }
    private class AndroidInput(private val audio:AudioRecord):HandsFreeBargeInInput {
        private val silenced=AtomicBoolean(false)
        private var registered=false
        private val callback=if(Build.VERSION.SDK_INT>=29)object:AudioManager.AudioRecordingCallback(){
            override fun onRecordingConfigChanged(configs:MutableList<AudioRecordingConfiguration>){silenced.set(configs.any{it.isClientSilenced})}
        } else null
        override fun start(){
            if(Build.VERSION.SDK_INT>=29 && callback!=null){audio.registerAudioRecordingCallback({it.run()},callback);registered=true}
            audio.startRecording();check(audio.recordingState==AudioRecord.RECORDSTATE_RECORDING)
        }
        override fun read(samples:ShortArray,offset:Int,count:Int)=audio.read(samples,offset,count,AudioRecord.READ_BLOCKING)
        override fun silenced()=silenced.get()
        override fun stop()=audio.stop()
        override fun close(){
            if(Build.VERSION.SDK_INT>=29 && registered && callback!=null)try{audio.unregisterAudioRecordingCallback(callback)}catch(_:Exception){}
            audio.release()
        }
    }
    companion object {
        const val START_WAIT_MS=750L
        const val COMMAND_WAIT_MS=22000L
        const val TAIL_SAMPLES=4800
    }
}
