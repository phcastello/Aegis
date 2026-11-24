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
import kotlin.math.roundToInt
import kotlin.math.sqrt

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
            if(received>0)reset()
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
    fun reset() {
        ring.clear()
        resetClassifier()
        vad=HandsFreeVad(classifier)
        received=0
        latched=false
    }

    fun clear(){ring.clear()}
    companion object { const val PRE_SAMPLES=1920 }
}

/** The fresh libfvad model can call stationary microphone noise voiced for its
 * first 80–100 ms. Learn the noise level independently of that early decision.
 * This gate applies only while listening for interruptions.
 */
private class HandsFreeBargeInLevelGate {
    private val calibration = DoubleArray(10) // 200 ms of samples with usable playback reference.
    private var calibratedFrames = 0
    var floorRms = 0.0
        private set

    fun observe(rms: Double, voiced: Boolean): Boolean {
        if (calibratedFrames < calibration.size) {
            calibration[calibratedFrames++] = rms
            if (calibratedFrames == calibration.size) {
                calibration.sort()
                floorRms = calibration[2]
            }
            return false
        }
        val loud = aboveFloor(rms)
        // Learn quieter levels even if mislabelled by the VAD, and allow a
        // change in background after the warmed VAD classifies it as non-speech.
        if (!loud || !voiced) floorRms += (rms - floorRms) * 0.05
        return loud
    }

    fun aboveFloor(rms: Double) = rms >= maxOf(64.0, floorRms * 2.5)

    companion object {
        fun rms(frame: ShortArray): Double {
            var sum = 0.0
            var squares = 0.0
            for (sample in frame) {
                val value = sample.toDouble()
                sum += value
                squares += value * value
            }
            val mean = sum / frame.size
            return sqrt((squares / frame.size - mean * mean).coerceAtLeast(0.0))
        }
    }
}

/** One microphone, owned only for the current response and its accepted new utterance.
 * close() requests stop without joining; awaitClosed() proves stop/release completed.
 */
internal class HandsFreeBargeInCapture internal constructor(
    private val input:HandsFreeBargeInInput,
    private val clean:(ShortArray,Int,Long)->Boolean,
    private val playedFrames:()->Long?,
    private val pauseResponse:()->Boolean,
    private val resumeResponse:()->Unit,
    private val stillCurrent:()->Boolean,
    private val onSpeech:()->Boolean,
    private val classifierFactory:()->HandsFreeBargeInClassifier,
    private val clock:()->Long={HandsFreeTurnLatency.now()},
    private val onProbeResult: (String, Int, Int) -> Unit = { _, _, _ -> }
):AutoCloseable {
    constructor(
        audio:AudioRecord,
        echo:HandsFreeBargeInEcho,
        playedFrames:()->Long?,
        pauseResponse:()->Boolean,
        resumeResponse:()->Unit,
        stillCurrent:()->Boolean,
        onSpeech:()->Boolean,
        onProbeResult: (String, Int, Int) -> Unit = { _, _, _ -> }
    ):this(AndroidInput(audio),{pcm,count,head->echo.suppress(pcm,count,head)},playedFrames,pauseResponse,resumeResponse,stillCurrent,onSpeech,{
            val native=NativeHandsFreeSpeechClassifier()
            object:HandsFreeBargeInClassifier {
                override fun isSpeech(frame:ShortArray)=native.isSpeech(frame)
                override fun reset()=native.reset()
                override fun close()=native.close()
            }
        }, onProbeResult = onProbeResult)
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
    private class SpeechProbe(prefix:ShortArray,val pausedAt:Long,val floorRms:Int) {
        private val prefixSamples=prefix.size
        val pcm=prefix.copyOf(prefixSamples+PROBE_SAMPLES)
        var length=prefixSamples;private set
        val samplesSincePause:Int get()=length-prefixSamples
        var confirmation:HandsFreeVad?=null
        var peakRms=0
        var reported=false

        fun append(frame:ShortArray) {
            frame.copyInto(pcm,length)
            length+=frame.size
        }
    }

    private fun reportProbe(probe:SpeechProbe, reason:String) {
        if (probe.reported) return
        probe.reported = true
        try { onProbeResult(reason, probe.floorRms, probe.peakRms) } catch (_: Exception) { }
    }

    private fun capture() {
        val frame=ShortArray(HandsFreeVad.FRAME_SAMPLES)
        var classifier:HandsFreeBargeInClassifier?=null
        var onset:HandsFreeBargeInOnset?=null
        var command:ShortArray?=null
        var state:HandsFreeCommandCapture?=null
        var candidate:BargeInCommand?=null
        var trace:HandsFreeTurnLatency?=null
        var lastActiveAt:Long?=null
        var lastHead:Long?=null
        var cooldownSamples=0
        var probe:SpeechProbe?=null
        var paused=false
        try {
            if(!current())return
            val vad=classifierFactory();classifier=vad
            val levels = HandsFreeBargeInLevelGate()
            val onsetClassifier = HandsFreeSpeechClassifier { samples ->
                val voiced = vad.isSpeech(samples)
                val loud = levels.observe(HandsFreeBargeInLevelGate.rms(samples), voiced)
                loud && voiced
            }
            onset=HandsFreeBargeInOnset(onsetClassifier) { vad.reset() }
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
                if(!accepted.get()) {
                    val pendingProbe=probe
                    if(pendingProbe==null) {
                        val coolingDown = cooldownSamples > 0
                        if (coolingDown) cooldownSamples -= frame.size
                        val observed=try{playedFrames()?.takeIf{it>=0}}catch(_:Exception){null}
                        val monotonic=observed!=null && (lastHead==null || observed>=checkNotNull(lastHead))
                        if(monotonic)lastHead=observed
                        val safe=monotonic && clean(frame,frame.size,checkNotNull(observed))
                        if (coolingDown) {
                            // Adapt to new background after a rejected probe, without
                            // accumulating an onset or losing the original PCM cadence.
                            if (safe) onsetClassifier.isSpeech(frame)
                            continue
                        }
                        val prefix=onset.process(frame,safe)?:continue
                        try {
                            if(!current()) break
                            paused=pauseResponse()
                            if(paused) {
                                val pausedAt = clock()
                                probe=SpeechProbe(prefix,pausedAt,levels.floorRms.roundToInt())
                                // Warm the freshly reset classifier on guard frames,
                                // including stationary noise. None can count as speech.
                                vad.reset()
                            } else {
                                onset.reset()
                                cooldownSamples=PROBE_COOLDOWN_SAMPLES
                            }
                        } finally {
                            prefix.fill(0)
                        }
                        continue
                    }

                    pendingProbe.append(frame)
                    val elapsed=now-pendingProbe.pausedAt
                    // A delayed capture thread can drain buffered microphone data in
                    // a burst. Require both real time and samples since pausing.
                    val frameEndsAfterTail=elapsed>=ECHO_TAIL_MS+FRAME_MS &&
                        pendingProbe.samplesSincePause>ECHO_TAIL_SAMPLES
                    if(frameEndsAfterTail && elapsed<=PROBE_DEADLINE_MS) {
                        val confirmation=pendingProbe.confirmation ?: HandsFreeVad(HandsFreeSpeechClassifier { samples ->
                            val rms = HandsFreeBargeInLevelGate.rms(samples)
                            pendingProbe.peakRms = maxOf(pendingProbe.peakRms, rms.roundToInt())
                            val voiced = vad.isSpeech(samples)
                            voiced && levels.aboveFloor(rms)
                        }).also { pendingProbe.confirmation=it }
                        confirmation.process(frame,frame.size)
                        if(confirmation.hasSpeech) {
                            if(!current()) break
                            decisionAt=now
                            val approved=try{onSpeech()}catch(_:Exception){false}
                            if(!approved) {
                                reportProbe(pendingProbe,"stale")
                                break
                            }
                            // The approved handoff now owns the paused playback and aborts it.
                            accepted.set(true)
                            paused=false
                            reportProbe(pendingProbe,"confirmed")
                            val pcm=ShortArray(320000);command=pcm
                            pendingProbe.pcm.copyInto(pcm,endIndex=pendingProbe.length)
                            val initial=pendingProbe.length
                            pendingProbe.pcm.fill(0)
                            probe=null
                            vad.reset()
                            state=HandsFreeCommandCapture(pcm,initial,vad)
                            trace=HandsFreeTurnLatency(clock=clock).also{it.markAt("barge_in_accepted",now)}
                            state.lastActiveEndSample?.let{lastActiveAt=now-(state.length-it)*1000L/16000}
                            continue
                        }
                    } else if (!frameEndsAfterTail) {
                        vad.isSpeech(frame)
                    }
                    if(pendingProbe.samplesSincePause==PROBE_SAMPLES || elapsed>PROBE_DEADLINE_MS) {
                        pendingProbe.pcm.fill(0)
                        probe=null
                        resumeResponse()
                        paused=false
                        onset.reset()
                        cooldownSamples=PROBE_COOLDOWN_SAMPLES
                        reportProbe(pendingProbe,"no_speech")
                    }
                    continue
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
            if(paused)try{resumeResponse()}catch(_:Exception){}
            try{classifier?.close()}catch(_:Exception){}
            probe?.let { pending ->
                pending.pcm.fill(0)
                reportProbe(pending,"cancelled")
            }
            onset?.clear();frame.fill(0);command?.fill(0)
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
        private const val FRAME_MS=20L
        private const val ECHO_TAIL_MS=200L
        const val ECHO_TAIL_SAMPLES=3200
        const val PROBE_SAMPLES=6080 // 200ms for echo travel and 180ms to confirm live speech.
        private const val PROBE_DEADLINE_MS=420L // Allows read scheduling jitter without extending the PCM window.
        const val PROBE_COOLDOWN_SAMPLES=32000
    }
}
