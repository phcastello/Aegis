package com.aegis.node

import android.media.AudioRecord
import android.media.AudioRecordingConfiguration
import android.media.AudioManager
import android.media.AudioRouting
import android.media.AudioTimestamp
import android.os.Build
import android.os.Handler
import android.os.Looper
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference
import kotlin.math.roundToInt
import kotlin.math.sqrt

internal data class BargeInCommand(
    val wav: ByteArray, val trace: HandsFreeTurnLatency,
    val preparedInterruption: PreparedVoiceInterruption? = null
)

internal interface HandsFreeBargeInInput:AutoCloseable {
    fun start()
    fun read(samples:ShortArray,offset:Int,count:Int):Int
    fun timestamp(): HandsFreeAudioTimestamp? = null
    fun preprocessFrame(frame: ShortArray) = Unit
    fun discontinuity(): Long = 0
    fun stop()
    fun silenced():Boolean=false
}
internal interface HandsFreeBargeInClassifier:HandsFreeSpeechClassifier,AutoCloseable { fun reset() }

/** Confirm independent speech within the configured voting window. Retain the
 * screened lead-in (300 ms for duplex speech), including tentative voice.
 */
internal class HandsFreeBargeInOnset(
    private val classifier:HandsFreeSpeechClassifier,
    private val learnedEvidence: Boolean = false,
    private val preSamples: Int = PRE_SAMPLES,
    private val windowFrames: Int = DEFAULT_WINDOW_FRAMES,
    private val requiredObservations: Int = DEFAULT_VOTES,
    private val resetClassifier:()->Unit={}
) {
    init { require(windowFrames in 1..30 && requiredObservations in 1..windowFrames) }
    private val windowMask = (1 shl windowFrames) - 1
    private val ring=HandsFreeRing(preSamples+windowFrames*HandsFreeVad.FRAME_SAMPLES)
    private var independent = false
    private val mechanicalNoiseGate = HandsFreeMechanicalNoiseGate()
    var mechanicalVetoFrames = 0
        private set
    private val independentClassifier = HandsFreeSpeechClassifier { frame ->
        val voiced = classifier.isSpeech(frame)
        independent && voiced
    }
    private var vad = HandsFreeVad(independentClassifier)
    private var received=0L
    private var history=0
    private var latched=false

    fun process(frame: ShortArray, evidence: HandsFreeBargeInEvidence,
                retain: Boolean = evidence == HandsFreeBargeInEvidence.INDEPENDENT): ShortArray? {
        require(frame.size==HandsFreeVad.FRAME_SAMPLES)
        if(latched)return null
        if(evidence==HandsFreeBargeInEvidence.UNAVAILABLE) {
            if(received>0)reset()
            return null
        }
        val mechanicalNoise = mechanicalNoiseGate.observe(frame)
        if (mechanicalNoise && evidence==HandsFreeBargeInEvidence.INDEPENDENT) mechanicalVetoFrames++
        independent = evidence==HandsFreeBargeInEvidence.INDEPENDENT && !mechanicalNoise
        // Give the classifier time to adapt without forwarding uncertain echo to STT.
        ring.append(if (retain && !mechanicalNoise) frame else SILENCE,frame.size)
        received+=frame.size
        val active = if (learnedEvidence) independent else {
            vad.process(frame,frame.size)
            vad.lastActiveEndSample == vad.total
        }
        addObservation(active)
        if (!active || Integer.bitCount(history) < requiredObservations) return null
        val onsetFrames = Int.SIZE_BITS - Integer.numberOfLeadingZeros(history)
        val onset = received - onsetFrames * HandsFreeVad.FRAME_SAMPLES
        val retainedStart=received-ring.size
        val begin=maxOf(retainedStart,(onset-preSamples).coerceAtLeast(0))
        val size=(received-begin).toInt()
        latched=true
        return ShortArray(size).also{ring.copyRangeTo(it,(begin-retainedStart).toInt(),size)}
    }
    /** One repeated playback-head reading provides no additional evidence. */
    fun omitFrame() {
        mechanicalNoiseGate.reset()
        ring.append(SILENCE, HandsFreeVad.FRAME_SAMPLES)
        received += HandsFreeVad.FRAME_SAMPLES
        addObservation(false)
    }

    private fun addObservation(active: Boolean) {
        val observation = if (active) 1 else 0
        history = ((history shl 1) or observation) and windowMask
    }

    fun reset() {
        mechanicalNoiseGate.reset()
        ring.clear()
        resetClassifier()
        vad=HandsFreeVad(independentClassifier)
        received=0
        history=0
        latched=false
    }

    fun clearVotes() { history = 0 }
    fun clear(){ring.clear();mechanicalNoiseGate.reset()}
    companion object {
        const val PRE_SAMPLES=1920
        const val DUPLEX_PRE_SAMPLES=4800 // 300 ms of screened, processed lead-in.
        const val DEFAULT_WINDOW_FRAMES = 7
        const val DEFAULT_VOTES = HandsFreeVad.ONSET_SAMPLES / HandsFreeVad.FRAME_SAMPLES
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
    val calibrated: Boolean get() = calibratedFrames == calibration.size

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

internal enum class HandsFreeBargeInCaptureOutcome {
    STOPPED, RECORDER_START_FAILED, RECORDER_READ_FAILED, RECORDER_SILENCED,
    INTERRUPTION_REJECTED, CAPTURE_FAILED, EMPTY_COMMAND, COMMAND_READY,
    COMMAND_DISCARDED, RECORDER_RELEASE_FAILED
}

/** Aggregate per response. Audio and individual frame observations are never reported. */
internal data class HandsFreeBargeInCaptureSummary(
    val outcome: HandsFreeBargeInCaptureOutcome,
    val accepted: Boolean,
    val timestampFrames: Int,
    val playbackHeadFrames: Int,
    val timingResets: Int,
    val referenceUnavailableFrames: Int,
    val voiceReadyFrames: Int,
    val independentFrames: Int,
    val levelRejectedFrames: Int,
    val commandFrames: Int,
    val mechanicalVetoFrames: Int
)

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
    private val onProbeResult: (String, Int, Int) -> Unit = { _, _, _ -> },
    private val onAvailabilityChanged: () -> Unit = {},
    private val voicePathReady: () -> Boolean = { false },
    private val onCommandFrame: (ShortArray) -> Unit = {},
    private val playbackClock: HandsFreePlaybackClock? = null,
    private val onCaptureSummary: (HandsFreeBargeInCaptureSummary) -> Unit = {},
    private val learnedEvidence: Boolean = false,
    private val confirmationWindowFrames: Int = HandsFreeBargeInOnset.DEFAULT_WINDOW_FRAMES,
    private val confirmationVotes: Int = HandsFreeBargeInOnset.DEFAULT_VOTES,
    private val keepForOnset: () -> Boolean = { false },
    private val outputMuteChanged: (Boolean) -> Unit = {},
    private val clearOnsetVotes: () -> Boolean = { false },
    private val commandNoiseFloor: () -> Double = { 8.0 },
    private val afterInterruption: (ShortArray) -> Unit = {},
    private val closeProcessor: () -> Unit = {},
    private val onInputFailure: () -> Unit = {},
    // Before the response starts, there is no assistant PCM to align or cancel.
    // The ordinary VAD and calibrated level gate still have to qualify speech.
    private val beforePlayback: Boolean = false,
    private val sustainedSpeechFloor: Double? = null
):AutoCloseable {
    constructor(
        audio:AudioRecord,
        processor:HandsFreeDuplexSpeech,
        playedFrames:HandsFreePlaybackClock,
        mediaVolume: () -> Int,
        stillCurrent:()->Boolean,
        onSpeech:()->Boolean,
        onProbeResult: (String, Int, Int) -> Unit = { _, _, _ -> },
        onAvailabilityChanged: () -> Unit = {},
        onCommandFrame: (ShortArray) -> Unit = {},
        onCaptureSummary: (HandsFreeBargeInCaptureSummary) -> Unit = {},
        inputUsable: () -> Boolean = { true },
        releaseAudio: () -> Unit = { audio.release() },
        onInputFailure: () -> Unit = {},
        onMediaFilterClosed: (Long, Long) -> Unit = { _, _ -> },
        sustainedSpeechFloor: Double? = null
    ):this(AndroidInput(audio, inputUsable, releaseAudio, onMediaFilterClosed),processor::inspect,playedFrames,mediaVolume,processor::reset,stillCurrent,onSpeech,{
            val native=NativeHandsFreeSpeechClassifier()
            object:HandsFreeBargeInClassifier {
                override fun isSpeech(frame:ShortArray)=native.isSpeech(frame)
                override fun reset()=native.reset()
                override fun close()=native.close()
            }
        }, onProbeResult = onProbeResult, onAvailabilityChanged = onAvailabilityChanged,
        voicePathReady = processor::voicePathReady, onCommandFrame = onCommandFrame,
        playbackClock = playedFrames, onCaptureSummary = onCaptureSummary,
        learnedEvidence = true,
        confirmationWindowFrames = processor.confirmationWindowFrames,
        confirmationVotes = processor.confirmationVotes,
        keepForOnset = { processor.keepForOnset },
        outputMuteChanged = processor::setOutputMuted,
        clearOnsetVotes = { processor.clearOnsetVotes },
        commandNoiseFloor = processor::commandNoiseFloor,
        afterInterruption = processor::afterInterruption, closeProcessor = processor::close,
        onInputFailure = onInputFailure, sustainedSpeechFloor = sustainedSpeechFloor)

    constructor(
        audio:AudioRecord,
        stillCurrent:()->Boolean,
        onSpeech:()->Boolean,
        onProbeResult: (String, Int, Int) -> Unit = { _, _, _ -> },
        onAvailabilityChanged: () -> Unit = {},
        onCommandFrame: (ShortArray) -> Unit = {},
        onCaptureSummary: (HandsFreeBargeInCaptureSummary) -> Unit = {},
        inputUsable: () -> Boolean = { true },
        onMediaFilterClosed: (Long, Long) -> Unit = { _, _ -> },
        sustainedSpeechFloor: Double? = null
    ):this(AndroidInput(audio,inputUsable,{audio.release()},onMediaFilterClosed),
        {_,_,_->HandsFreeBargeInEvidence.INDEPENDENT},{null},{0},{},
        stillCurrent,onSpeech,{
            val native=NativeHandsFreeSpeechClassifier()
            object:HandsFreeBargeInClassifier {
                override fun isSpeech(frame:ShortArray)=native.isSpeech(frame)
                override fun reset()=native.reset()
                override fun close()=native.close()
            }
        },onProbeResult=onProbeResult,onAvailabilityChanged=onAvailabilityChanged,voicePathReady={true},onCommandFrame=onCommandFrame,
        onCaptureSummary=onCaptureSummary,beforePlayback=true,sustainedSpeechFloor=sustainedSpeechFloor)
    private val lifecycle=AtomicInteger(0) // new, started, closing, released
    private val stopping=AtomicBoolean(false)
    private val accepted=AtomicBoolean(false)
    private val availability = AtomicReference(HandsFreeBargeInPanelAvailability.PREPARING)
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

    /** No recorder startup wait on the model-request path. Playback still uses start(). */
    fun startAsync():Boolean {
        if(!lifecycle.compareAndSet(0,1))return lifecycle.get()==1 && !stopping.get()
        try{Thread({capture()},"AegisBargeInAudio").apply{isDaemon=true;start()}}
        catch(_:Exception){
            stopping.set(true);lifecycle.set(2)
            Thread({releaseInput();ready.countDown();completed.countDown()},"AegisBargeInRelease").apply{isDaemon=true;start()}
            return false
        }
        return true
    }
    fun start():Boolean {
        if(!startAsync())return false
        val success=try{ready.await(START_WAIT_MS,TimeUnit.MILLISECONDS) && readySucceeded && !stopping.get()}
            catch(_:InterruptedException){Thread.currentThread().interrupt();false}
        if(!success)close()
        return success
    }
    fun panelAvailability(): HandsFreeBargeInPanelAvailability =
        if (stopping.get() || accepted.get()) HandsFreeBargeInPanelAvailability.UNAVAILABLE else availability.get()

    private fun publishAvailability(value: HandsFreeBargeInPanelAvailability) {
        if (availability.getAndSet(value) == value) return
        try { onAvailabilityChanged() } catch (_: Exception) { }
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
        publishAvailability(HandsFreeBargeInPanelAvailability.UNAVAILABLE)
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
        val start = (prefix.size - HandsFreeVad.ONSET_SAMPLES).coerceAtLeast(0)
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
        var headRepeated = false
        val referenceTimeline = HandsFreeBargeInReferenceTimeline()
        var volume: Int? = null
        var framesUntilVolumeRead = 0
        var evidence = HandsFreeBargeInEvidence.UNAVAILABLE
        var ambiguousRun = 0
        var ambiguousRunPeak = 0.0
        var ambiguousDetection: Pair<Double, Double>? = null
        var outcome = HandsFreeBargeInCaptureOutcome.STOPPED
        var timestampFrames = 0
        var playbackHeadFrames = 0
        var timingResets = 0
        var referenceUnavailableFrames = 0
        var voiceReadyFrames = 0
        var independentFrames = 0
        var levelRejectedFrames = 0
        var commandFrames = 0
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
                val calibrated = levels.calibrated
                val loud = levels.observe(rms, voiced)
                if (evidence == HandsFreeBargeInEvidence.INDEPENDENT) {
                    independentFrames++
                    if (calibrated && voiced && !loud) levelRejectedFrames++
                }
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
            onset=HandsFreeBargeInOnset(onsetClassifier, learnedEvidence,
                if (learnedEvidence || beforePlayback) HandsFreeBargeInOnset.DUPLEX_PRE_SAMPLES else HandsFreeBargeInOnset.PRE_SAMPLES,
                confirmationWindowFrames, confirmationVotes) { vad.reset() }
            input.start()
            if(!current())return
            readySucceeded=true;ready.countDown()
            var pending=0
            var consumedFrames = 0L
            val timing = playbackClock?.let { HandsFreeBargeInTiming(it, input::timestamp, input::discontinuity) }
            while(current()) {
                val count = try { input.read(frame,pending,frame.size-pending) } catch (_: Exception) { -1 }
                if(!current())break
                if (count !in 1..(frame.size - pending)) {
                    outcome = HandsFreeBargeInCaptureOutcome.RECORDER_READ_FAILED
                    break
                }
                if (input.silenced()) {
                    outcome = HandsFreeBargeInCaptureOutcome.RECORDER_SILENCED
                    break
                }
                pending+=count
                consumedFrames += count
                if(pending<frame.size)continue
                pending=0
                input.preprocessFrame(frame)
                val now=clock()
                if(!accepted.get()) {
                    if(beforePlayback) {
                        // The processing microphone is retired before any TTS
                        // device can play. Independence is not inferred from a
                        // missing playback timestamp during actual speech.
                        evidence=HandsFreeBargeInEvidence.INDEPENDENT
                    } else {
                    if (framesUntilVolumeRead==0) {
                        val observedVolume=mediaVolume()
                        if (volume!=null && observedVolume!=volume) {
                            resetEvidence()
                            referenceTimeline.reset()
                            onset.reset()
                            clearAmbiguousRun()
                            publishAvailability(HandsFreeBargeInPanelAvailability.PREPARING)
                        }
                        volume=observedVolume
                        outputMuteChanged(observedVolume == 0)
                        framesUntilVolumeRead=VOLUME_READ_INTERVAL
                    }
                    framesUntilVolumeRead--
                    val framePosition = timing?.locate(consumedFrames)
                    when (framePosition?.source) {
                        HandsFreeBargeInTimingSource.TIMESTAMP -> timestampFrames++
                        HandsFreeBargeInTimingSource.PLAYBACK_HEAD -> playbackHeadFrames++
                        else -> Unit
                    }
                    if (framePosition?.discontinuity == true) {
                        timingResets++
                        lastHead = null
                        headRepeated = false
                        referenceTimeline.reset()
                        resetEvidence()
                        onset.reset()
                        clearAmbiguousRun()
                    }
                    val observedPosition = if (timing != null) framePosition?.frame
                        else try { playedFrames()?.takeIf { it > 0 } } catch (_: Exception) { null }
                    val observed = if (learnedEvidence) referenceTimeline.next(observedPosition,
                        try { playbackClock?.enqueuedFrames?.invoke() ?: 0L } catch (_: Exception) { 0L })
                        else observedPosition
                    val advancing=observed!=null && observed>(lastHead?:0L)
                    if (advancing) {
                        lastHead=observed
                        headRepeated = false
                    } else if (observed != null && observed == lastHead && !headRepeated) {
                        headRepeated = true
                        referenceUnavailableFrames++
                        onset.omitFrame()
                        clearAmbiguousRun()
                        continue
                    }
                    if (!advancing && evidence != HandsFreeBargeInEvidence.UNAVAILABLE) resetEvidence()
                    evidence=if(advancing)inspect(frame,frame.size,checkNotNull(observed))
                        else HandsFreeBargeInEvidence.UNAVAILABLE
                    if (evidence==HandsFreeBargeInEvidence.UNAVAILABLE) {
                        referenceUnavailableFrames++
                        clearAmbiguousRun()
                    }
                    }
                    if (clearOnsetVotes()) onset.clearVotes()
                    if (learnedEvidence && evidence == HandsFreeBargeInEvidence.INDEPENDENT) independentFrames++
                    val prefix=onset.process(frame,evidence, evidence == HandsFreeBargeInEvidence.INDEPENDENT ||
                        learnedEvidence && keepForOnset())
                    val voiceAvailable = (learnedEvidence || levels.calibrated) && evidence != HandsFreeBargeInEvidence.UNAVAILABLE && voicePathReady()
                    if (voiceAvailable) voiceReadyFrames++
                    publishAvailability(if (voiceAvailable) HandsFreeBargeInPanelAvailability.VOICE
                        else HandsFreeBargeInPanelAvailability.PREPARING)
                    if (prefix == null) continue
                    try {
                        if(!current()) break
                        // Recheck at handoff so a recent gain change cannot reuse the old profile.
                        val observedVolume=if(beforePlayback)volume else mediaVolume()
                        if (!beforePlayback && observedVolume!=volume) {
                            volume=observedVolume
                            outputMuteChanged(observedVolume == 0)
                            resetEvidence()
                            referenceTimeline.reset()
                            onset.reset()
                            clearAmbiguousRun()
                            framesUntilVolumeRead=VOLUME_READ_INTERVAL
                            publishAvailability(HandsFreeBargeInPanelAvailability.PREPARING)
                            continue
                        }
                        decisionAt=now
                        val approved=try{onSpeech()}catch(_:Exception){false}
                        if(!approved) {
                            outcome = HandsFreeBargeInCaptureOutcome.INTERRUPTION_REJECTED
                            reportDetection("stale",levels.floorRms,prefix)
                            break
                        }
                        accepted.set(true)
                        publishAvailability(HandsFreeBargeInPanelAvailability.UNAVAILABLE)
                        reportDetection("confirmed",levels.floorRms,prefix)
                        val pcm=ShortArray(320000);command=pcm
                        prefix.copyInto(pcm)
                        vad.reset()
                        state=HandsFreeCommandCapture(pcm,prefix.size,vad,initialSpeechConfirmed=true,
                            initialNoiseRms=if(beforePlayback)levels.floorRms else commandNoiseFloor(),
                            sustainedSpeechFloor=sustainedSpeechFloor)
                        trace=HandsFreeTurnLatency(clock=clock).also{it.markAt("barge_in_accepted",now)}
                        state.lastActiveEndSample?.let{lastActiveAt=now-(state.length-it)*1000L/16000}
                    } finally {
                        prefix.fill(0)
                    }
                    continue
                }
                val capture=checkNotNull(state)
                afterInterruption(frame)
                commandFrames++
                try { onCommandFrame(frame) } catch (_: Exception) { }
                val ended=capture.append(frame,frame.size)
                capture.lastActiveEndSample?.let{lastActiveAt=now-(capture.length-it)*1000L/16000}
                if(ended || now-checkNotNull(decisionAt)>=20000L) {
                    if(capture.hasUsefulAudio) {
                        val timing=checkNotNull(trace)
                        lastActiveAt?.let{timing.markAt("last_vad_active_frame_estimated",it)}
                        timing.markAt("endpoint_completed",now);timing.mark("wav_prepare_started")
                        val wav=handsFreeWav(checkNotNull(command),capture.transcriptionLength);timing.mark("wav_ready")
                        candidate=BargeInCommand(wav,timing)
                    } else {
                        outcome = HandsFreeBargeInCaptureOutcome.EMPTY_COMMAND
                    }
                    break
                }
            }
        }catch(_:Exception) {
            outcome = if (readySucceeded) HandsFreeBargeInCaptureOutcome.CAPTURE_FAILED
                else HandsFreeBargeInCaptureOutcome.RECORDER_START_FAILED
        }finally {
            if (outcome in setOf(HandsFreeBargeInCaptureOutcome.RECORDER_START_FAILED,
                    HandsFreeBargeInCaptureOutcome.RECORDER_READ_FAILED,
                    HandsFreeBargeInCaptureOutcome.RECORDER_SILENCED,
                    HandsFreeBargeInCaptureOutcome.CAPTURE_FAILED)) {
                try { onInputFailure() } catch (_: Exception) { }
            }
            stopping.set(true);ready.countDown()
            publishAvailability(HandsFreeBargeInPanelAvailability.UNAVAILABLE)
            try{classifier?.close()}catch(_:Exception){}
            if (!accepted.get()) ambiguousDetection?.let { (floorRms, peakRms) ->
                reportDetection("ambiguous", floorRms, peakRms)
            }
            onset?.clear();frame.fill(0);command?.fill(0)
            releaseInput()
            synchronized(deliveryLock) {
                if (!releaseSucceeded.get()) outcome = HandsFreeBargeInCaptureOutcome.RECORDER_RELEASE_FAILED
                if (!discard.get() && accepted.get() && releaseSucceeded.get()) {
                    result.set(candidate)
                    if (candidate != null) outcome = HandsFreeBargeInCaptureOutcome.COMMAND_READY
                } else if (candidate != null) {
                    candidate.wav.fill(0)
                    if (releaseSucceeded.get()) outcome = HandsFreeBargeInCaptureOutcome.COMMAND_DISCARDED
                }
            }
            completed.countDown()
            try {
                onCaptureSummary(HandsFreeBargeInCaptureSummary(
                    outcome, accepted.get(), timestampFrames, playbackHeadFrames, timingResets,
                    referenceUnavailableFrames, voiceReadyFrames, independentFrames, levelRejectedFrames, commandFrames,
                    onset?.mechanicalVetoFrames ?: 0
                ))
            } catch (_: Exception) { }
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
        try{closeProcessor()}catch(_:Exception){}
        lifecycle.set(3);closed.countDown()
        if(interrupted)Thread.currentThread().interrupt()
    }
    private class AndroidInput(
        private val audio:AudioRecord,
        private val inputUsable: () -> Boolean,
        private val releaseAudio: () -> Unit,
        onMediaFilterClosed: (Long, Long) -> Unit
    ):HandsFreeBargeInInput {
        private val silenced=AtomicBoolean(false)
        private var registered=false
        private var routingRegistered = false
        private var framesUntilInputCheck = 0
        private val audioTimestamp = AudioTimestamp()
        private val mediaFilter = HandsFreeMicrophoneMediaFilter(audio, onMediaFilterClosed)
        private val routeChanges = AtomicLong()
        private val routing = AudioRouting.OnRoutingChangedListener { routeChanges.incrementAndGet() }
        private val callback=if(Build.VERSION.SDK_INT>=29)object:AudioManager.AudioRecordingCallback(){
            override fun onRecordingConfigChanged(configs:MutableList<AudioRecordingConfiguration>){silenced.set(configs.any{it.isClientSilenced})}
        } else null
        override fun start(){
            if(Build.VERSION.SDK_INT>=29 && callback!=null){audio.registerAudioRecordingCallback({it.run()},callback);registered=true}
            audio.addOnRoutingChangedListener(routing, Handler(Looper.getMainLooper()))
            routingRegistered = true
            audio.startRecording();check(audio.recordingState==AudioRecord.RECORDSTATE_RECORDING)
        }
        override fun read(samples:ShortArray,offset:Int,count:Int)=audio.read(samples,offset,count,AudioRecord.READ_BLOCKING).also {
            if (it > 0) mediaFilter.advance(it)
        }
        override fun preprocessFrame(frame: ShortArray) { mediaFilter.filter(frame, frame.size) }
        override fun timestamp() =
            if (audio.getTimestamp(audioTimestamp, AudioTimestamp.TIMEBASE_MONOTONIC) == AudioRecord.SUCCESS)
                HandsFreeAudioTimestamp(audioTimestamp.framePosition, audioTimestamp.nanoTime) else null
        override fun discontinuity() = routeChanges.get()
        override fun silenced(): Boolean {
            if (silenced.get()) return true
            if (framesUntilInputCheck-- > 0) return false
            framesUntilInputCheck = 7
            return !inputUsable()
        }
        override fun stop()=audio.stop()
        override fun close(){
            if(Build.VERSION.SDK_INT>=29 && registered && callback!=null)try{audio.unregisterAudioRecordingCallback(callback)}catch(_:Exception){}
            if (routingRegistered) try { audio.removeOnRoutingChangedListener(routing) } catch (_: Exception) { }
            try { mediaFilter.close() } finally { releaseAudio() }
        }
    }
    companion object {
        const val START_WAIT_MS=750L
        const val COMMAND_WAIT_MS=22000L
        private const val VOLUME_READ_INTERVAL = 4 // Poll at 80 ms and again at the decision boundary.
    }
}
