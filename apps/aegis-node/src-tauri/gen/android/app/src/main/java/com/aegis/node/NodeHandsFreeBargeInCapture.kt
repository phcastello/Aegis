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
    private var independent = false
    private val independentClassifier = HandsFreeSpeechClassifier { frame ->
        val voiced = classifier.isSpeech(frame)
        independent && voiced
    }
    private var vad = HandsFreeVad(independentClassifier)
    private var received=0L
    private var latched=false

    fun process(frame: ShortArray, evidence: HandsFreeBargeInEvidence): ShortArray? {
        require(frame.size==HandsFreeVad.FRAME_SAMPLES)
        if(latched)return null
        if(evidence==HandsFreeBargeInEvidence.UNAVAILABLE) {
            if(received>0)reset()
            return null
        }
        independent = evidence==HandsFreeBargeInEvidence.INDEPENDENT
        // Give the classifier time to adapt without forwarding uncertain echo to STT.
        ring.append(if (independent) frame else SILENCE,frame.size)
        received+=frame.size
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
        vad=HandsFreeVad(independentClassifier)
        received=0
        latched=false
    }

    fun clear(){ring.clear()}
    companion object {
        const val PRE_SAMPLES=1920
        private val SILENCE = ShortArray(HandsFreeVad.FRAME_SAMPLES)
    }
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
        fun rms(samples: ShortArray, offset: Int = 0, count: Int = samples.size): Double {
            var sum = 0.0
            var squares = 0.0
            for (i in offset until offset + count) {
                val value = samples[i].toDouble()
                sum += value
                squares += value * value
            }
            val mean = sum / count
            return sqrt((squares / count - mean * mean).coerceAtLeast(0.0))
        }
    }
}

/** One microphone, owned only for the current response and its accepted new utterance.
 * close() requests stop without joining; awaitClosed() proves stop/release completed.
 */
internal class HandsFreeBargeInCapture internal constructor(
    private val input:HandsFreeBargeInInput,
    private val inspect: (ShortArray, Int, Long) -> HandsFreeBargeInEvidence,
    private val playedFrames: () -> Long?,
    private val mediaVolume: () -> Int,
    private val resetEvidence: () -> Unit,
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
        mediaVolume: () -> Int,
        stillCurrent:()->Boolean,
        onSpeech:()->Boolean,
        onProbeResult: (String, Int, Int) -> Unit = { _, _, _ -> }
    ):this(AndroidInput(audio),echo::inspect,playedFrames,mediaVolume,echo::resetEvidence,stillCurrent,onSpeech,{
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
    private fun reportDetection(reason: String, floorRms: Double, prefix: ShortArray) {
        val frameSamples = HandsFreeVad.FRAME_SAMPLES
        val start = prefix.size - HandsFreeVad.ONSET_SAMPLES
        var peakRms = 0.0
        for (offset in start until prefix.size step frameSamples) {
            peakRms = maxOf(peakRms, HandsFreeBargeInLevelGate.rms(prefix, offset, frameSamples))
        }
        reportDetection(reason, floorRms, peakRms)
    }

    private fun reportDetection(reason: String, floorRms: Double, peakRms: Double) {
        try { onProbeResult(reason, floorRms.roundToInt(), peakRms.roundToInt()) } catch (_: Exception) { }
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
        var volume: Int? = null
        var framesUntilVolumeRead = 0
        var evidence = HandsFreeBargeInEvidence.UNAVAILABLE
        var ambiguousRun = 0
        var ambiguousRunPeak = 0.0
        var ambiguousDetection: Pair<Double, Double>? = null
        fun clearAmbiguousRun() {
            ambiguousRun = 0
            ambiguousRunPeak = 0.0
        }
        try {
            if(!current())return
            val vad=classifierFactory();classifier=vad
            val levels = HandsFreeBargeInLevelGate()
            val onsetClassifier = HandsFreeSpeechClassifier { samples ->
                val voiced = vad.isSpeech(samples)
                val rms = HandsFreeBargeInLevelGate.rms(samples)
                val loud = levels.observe(rms, voiced)
                if (ambiguousDetection==null) {
                    if (loud && voiced && evidence==HandsFreeBargeInEvidence.AMBIGUOUS) {
                        ambiguousRun += samples.size
                        ambiguousRunPeak = maxOf(ambiguousRunPeak, rms)
                        if (ambiguousRun>=HandsFreeVad.ONSET_SAMPLES)
                            ambiguousDetection = levels.floorRms to ambiguousRunPeak
                    } else {
                        clearAmbiguousRun()
                    }
                }
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
                    if (framesUntilVolumeRead==0) {
                        val observedVolume=mediaVolume()
                        if (volume!=null && observedVolume!=volume) {
                            resetEvidence()
                            onset.reset()
                            clearAmbiguousRun()
                        }
                        volume=observedVolume
                        framesUntilVolumeRead=VOLUME_READ_INTERVAL
                    }
                    framesUntilVolumeRead--
                    val observed=try{playedFrames()?.takeIf{it>0}}catch(_:Exception){null}
                    val advancing=observed!=null && observed>(lastHead?:0L)
                    if (advancing) lastHead=observed
                    evidence=if(advancing)inspect(frame,frame.size,checkNotNull(observed))
                        else HandsFreeBargeInEvidence.UNAVAILABLE
                    if (evidence==HandsFreeBargeInEvidence.UNAVAILABLE) clearAmbiguousRun()
                    val prefix=onset.process(frame,evidence)?:continue
                    try {
                        if(!current()) break
                        // Recheck at handoff so a recent gain change cannot reuse the old profile.
                        val observedVolume=mediaVolume()
                        if (observedVolume!=volume) {
                            volume=observedVolume
                            resetEvidence()
                            onset.reset()
                            clearAmbiguousRun()
                            framesUntilVolumeRead=VOLUME_READ_INTERVAL
                            continue
                        }
                        decisionAt=now
                        val approved=try{onSpeech()}catch(_:Exception){false}
                        if(!approved) {
                            reportDetection("stale",levels.floorRms,prefix)
                            break
                        }
                        accepted.set(true)
                        reportDetection("confirmed",levels.floorRms,prefix)
                        val pcm=ShortArray(320000);command=pcm
                        prefix.copyInto(pcm)
                        vad.reset()
                        state=HandsFreeCommandCapture(pcm,prefix.size,vad)
                        trace=HandsFreeTurnLatency(clock=clock).also{it.markAt("barge_in_accepted",now)}
                        state.lastActiveEndSample?.let{lastActiveAt=now-(state.length-it)*1000L/16000}
                    } finally {
                        prefix.fill(0)
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
            try{classifier?.close()}catch(_:Exception){}
            if (!accepted.get()) ambiguousDetection?.let { (floorRms, peakRms) ->
                reportDetection("ambiguous", floorRms, peakRms)
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
        private const val VOLUME_READ_INTERVAL = 4 // Poll at 80 ms and again at the decision boundary.
    }
}
