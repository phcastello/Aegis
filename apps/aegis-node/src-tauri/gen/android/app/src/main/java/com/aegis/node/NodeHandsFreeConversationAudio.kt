package com.aegis.node

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioDeviceInfo
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.MediaRecorder
import android.media.audiofx.AcousticEchoCanceler
import android.media.audiofx.AudioEffect
import android.os.Build
import android.os.SystemClock
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.pow

internal interface HandsFreeNativeAecEffect : AutoCloseable {
    fun active(): Boolean
}

/** Platform boundary for testing acquisition and cleanup without a real microphone. */
internal interface HandsFreeNativeAecPort<R : Any> {
    fun availabilityIssue(): String?
    fun enterMode(): Boolean
    fun modeActive(): Boolean
    fun selectSpeaker(): Boolean
    fun speakerActive(): Boolean
    fun openRecord(): R
    fun recordReady(record: R): Boolean
    fun registerRecord(record: R)
    fun attachEffect(record: R): HandsFreeNativeAecEffect?
    fun recordingIssue(record: R): String?
    fun releaseRecord(record: R)
    fun releaseSpeaker()
    fun releaseMode()
    fun playbackGain(): Float?
    fun volume(): Int
}

/**
 * A response-scoped request for speakerphone communication audio. An advertised AEC
 * is only a candidate: this lease also verifies routing, effect control/enabled state,
 * and recording ownership. The independent speech detector is still required.
 */
internal class HandsFreeNativeAecLease<R : Any> private constructor(
    private val port: HandsFreeNativeAecPort<R>,
    private val microphoneBlocked: (Boolean) -> Boolean
) : AutoCloseable {
    private val closed = AtomicBoolean(false)
    private val recorderClosed = AtomicBoolean(false)
    private var modeRequested = false
    private var speakerRequested = false
    private var recordValue: R? = null
    private var effect: HandsFreeNativeAecEffect? = null
    private var gain = 1f

    val record: R get() = checkNotNull(recordValue)
    fun mediaVolume(): Int = port.volume()
    fun playbackGain(): Float = gain
    fun isOwnedMode(): Boolean = !closed.get() && modeRequested && try {
        port.modeActive()
    } catch (_: RuntimeException) { false }

    fun unavailableReason(): String? = try {
        when {
            closed.get() -> "native_aec_closed"
            recorderClosed.get() -> "native_aec_recorder_closed"
            !isOwnedMode() -> "native_aec_mode_lost"
            !port.speakerActive() -> "native_aec_route_changed"
            microphoneBlocked(true) -> "native_aec_microphone_blocked"
            effect?.active() != true -> "native_aec_effect_lost"
            else -> port.recordingIssue(record)
        }
    } catch (_: RuntimeException) { "native_aec_probe_failed" }

    fun usable(): Boolean = unavailableReason() == null

    @Synchronized
    fun closeRecorder() {
        if (!recorderClosed.compareAndSet(false, true)) return
        try {
            effect?.close()
        } finally {
            recordValue?.let(port::releaseRecord)
        }
    }

    @Synchronized
    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        // Release all owned resources even if an OEM implementation throws.
        try {
            closeRecorder()
        } finally {
            try {
                if (speakerRequested) port.releaseSpeaker()
            } finally {
                if (modeRequested) port.releaseMode()
            }
        }
    }

    companion object {
        fun <R : Any> tryOpen(
            port: HandsFreeNativeAecPort<R>,
            microphoneBlocked: (Boolean) -> Boolean,
            onUnavailable: (String) -> Unit
        ): HandsFreeNativeAecLease<R>? {
            val lease = HandsFreeNativeAecLease(port, microphoneBlocked)
            var acquired = false
            fun unavailable(reason: String): HandsFreeNativeAecLease<R>? {
                onUnavailable(reason)
                return null
            }
            try {
                // The regular microphone policy is checked before requesting a route.
                if (microphoneBlocked(false)) return unavailable("native_aec_microphone_blocked")
                port.availabilityIssue()?.let { return unavailable(it) }
                if (microphoneBlocked(false)) return unavailable("native_aec_microphone_blocked")
                lease.modeRequested = true
                if (!port.enterMode()) return unavailable("native_aec_mode_unavailable")
                if (microphoneBlocked(true)) return unavailable("native_aec_microphone_blocked")
                lease.speakerRequested = true
                if (!port.selectSpeaker()) return unavailable("native_aec_route_unavailable")
                lease.gain = port.playbackGain() ?: return unavailable("native_aec_gain_unavailable")
                if (microphoneBlocked(true)) return unavailable("native_aec_microphone_blocked")

                val record = port.openRecord()
                lease.recordValue = record
                if (!port.recordReady(record)) return unavailable("native_aec_recorder_unavailable")
                port.registerRecord(record)
                val effect = port.attachEffect(record) ?: return unavailable("native_aec_effect_unavailable")
                lease.effect = effect
                lease.unavailableReason()?.let { return unavailable(it) }
                acquired = true
                return lease
            } catch (_: RuntimeException) {
                return unavailable("native_aec_setup_failed")
            } finally {
                // A restoration failure must not be reported as a clean software fallback.
                if (!acquired) try {
                    lease.close()
                } catch (error: RuntimeException) {
                    onUnavailable("native_aec_release_failed")
                    throw error
                }
            }
        }
    }
}

/** Native AEC is optional and does not alter the idle MIC audio source. */
internal class HandsFreeConversationAudio private constructor(
    private val lease: HandsFreeNativeAecLease<AudioRecord>
) : AutoCloseable {
    val record: AudioRecord get() = lease.record
    fun mediaVolume(): Int = lease.mediaVolume()
    fun playbackGain(): Float = lease.playbackGain()
    fun isOwnedMode(): Boolean = lease.isOwnedMode()
    fun usable(): Boolean = lease.usable()
    fun unavailableReason(): String? = lease.unavailableReason()
    fun closeRecorder() = lease.closeRecorder()
    override fun close() = lease.close()

    companion object {
        fun tryOpen(
            context: Context,
            microphoneBlocked: (allowCommunication: Boolean) -> Boolean,
            ownAudioSession: (Int) -> Unit,
            onUnavailable: (String) -> Unit = {}
        ): HandsFreeConversationAudio? {
            // VOICE_COMMUNICATION defaults to privacy-sensitive capture. Before API 30
            // it cannot opt out; keep the portable MIC route and its no-preemption policy.
            if (Build.VERSION.SDK_INT < 30) {
                onUnavailable("native_aec_privacy_unsupported")
                return null
            }
            val manager = context.getSystemService(AudioManager::class.java)
            val port = AndroidNativeAecPort(context, manager, ownAudioSession)
            return HandsFreeNativeAecLease.tryOpen(port, microphoneBlocked, onUnavailable)
                ?.let(::HandsFreeConversationAudio)
        }
    }
}

@Suppress("DEPRECATION")
private class AndroidNativeAecPort(
    private val context: Context,
    private val manager: AudioManager,
    private val ownAudioSession: (Int) -> Unit
) : HandsFreeNativeAecPort<AudioRecord> {
    private var previousSpeaker = false
    private var speakerId: Int? = null
    private var initialMediaDb = Float.NaN

    override fun availabilityIssue(): String? = when {
        !AcousticEchoCanceler.isAvailable() -> "native_aec_effect_unsupported"
        AudioEffect.queryEffects()?.any {
            it.type == AudioEffect.EFFECT_TYPE_AEC && it.uuid != AOSP_AEC
        } != true -> "native_aec_effect_unsupported"
        !speakerRouteEligible() || !normalMediaSpeaker() -> "native_aec_non_speaker_route"
        manager.isStreamMute(AudioManager.STREAM_MUSIC) ||
            manager.getStreamVolume(AudioManager.STREAM_MUSIC) == 0 -> "native_aec_media_muted"
        else -> {
            // Sample the normal media route before putting the device into call mode.
            initialMediaDb = manager.getStreamVolumeDb(AudioManager.STREAM_MUSIC,
                manager.getStreamVolume(AudioManager.STREAM_MUSIC), AudioDeviceInfo.TYPE_BUILTIN_SPEAKER)
            if (initialMediaDb.isFinite()) null else "native_aec_gain_unavailable"
        }
    }

    private fun normalMediaSpeaker(): Boolean {
        if (Build.VERSION.SDK_INT < 33) return true
        val attributes = AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA)
            .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build()
        val anticipated = manager.getAudioDevicesForAttributes(attributes)
        return anticipated.isNotEmpty() && anticipated.all { isSpeaker(it.type) }
    }

    private fun speakerRouteEligible(): Boolean {
        val outputs = manager.getDevices(AudioManager.GET_DEVICES_OUTPUTS)
        // Do not force a media headset, USB, Bluetooth, cast, or unfamiliar device
        // onto a different communication route. The software path follows normal media.
        if (outputs.none { isSpeaker(it.type) } ||
            outputs.any {
                !isSpeaker(it.type) && it.type != AudioDeviceInfo.TYPE_BUILTIN_EARPIECE &&
                    it.type != AudioDeviceInfo.TYPE_TELEPHONY
            }) return false
        return true
    }

    override fun enterMode(): Boolean {
        manager.mode = AudioManager.MODE_IN_COMMUNICATION
        return awaitPlatform { modeActive() }
    }

    override fun modeActive() = manager.mode == AudioManager.MODE_IN_COMMUNICATION

    override fun selectSpeaker(): Boolean {
        if (!modeActive() || !speakerRouteEligible()) return false
        if (Build.VERSION.SDK_INT >= 31) {
            val speaker = manager.availableCommunicationDevices.firstOrNull {
                it.type == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER
            } ?: return false
            // Record our request before calling the platform so failed setup is unwound.
            speakerId = speaker.id
            if (!manager.setCommunicationDevice(speaker)) return false
        } else {
            previousSpeaker = manager.isSpeakerphoneOn
            legacySpeakerRequested = true
            manager.isSpeakerphoneOn = true
        }
        return awaitPlatform { speakerActive() }
    }

    private var legacySpeakerRequested = false

    override fun speakerActive(): Boolean {
        if (!modeActive() || !speakerRouteEligible()) return false
        return if (Build.VERSION.SDK_INT >= 31) {
            speakerId != null && manager.communicationDevice?.id == speakerId
        } else legacySpeakerRequested && manager.isSpeakerphoneOn
    }

    @Suppress("MissingPermission")
    override fun openRecord(): AudioRecord {
        val minimum = AudioRecord.getMinBufferSize(16000, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        check(minimum > 0) { "native_aec_sample_rate" }
        val builder = AudioRecord.Builder()
            .setAudioSource(MediaRecorder.AudioSource.VOICE_COMMUNICATION)
            .setAudioFormat(AudioFormat.Builder().setSampleRate(16000)
                .setChannelMask(AudioFormat.CHANNEL_IN_MONO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
            .setBufferSizeInBytes(maxOf(minimum, 12800))
            .setPrivacySensitive(false) // The caller checked API 30 or newer.
        if (Build.VERSION.SDK_INT >= 31) builder.setContext(context)
        return builder.build()
    }

    override fun recordReady(record: AudioRecord) = record.state == AudioRecord.STATE_INITIALIZED

    override fun registerRecord(record: AudioRecord) = ownAudioSession(record.audioSessionId)

    override fun attachEffect(record: AudioRecord): HandsFreeNativeAecEffect? {
        val effect = AcousticEchoCanceler.create(record.audioSessionId) ?: return null
        var attached = false
        try {
            // Match WebRTC's choice to prefer manufacturer implementations over the
            // old generic AOSP AEC; the portable fallback is independently benchmarked.
            if (effect.descriptor.uuid == AOSP_AEC || !effect.hasControl()) return null
            if (effect.setEnabled(true) != AudioEffect.SUCCESS || !effect.enabled) return null
            attached = true
            return object : HandsFreeNativeAecEffect {
                override fun active() = effect.hasControl() && effect.enabled
                override fun close() = effect.release()
            }
        } finally {
            if (!attached) effect.release()
        }
    }

    override fun recordingIssue(record: AudioRecord): String? {
        if (!recordReady(record)) return "native_aec_recorder_unavailable"
        if (record.activeRecordingConfiguration?.isClientSilenced == true)
            return "native_aec_recorder_silenced"
        val device = record.routedDevice
        if (record.recordingState == AudioRecord.RECORDSTATE_RECORDING &&
            device?.type != AudioDeviceInfo.TYPE_BUILTIN_MIC)
            return "native_aec_route_changed"
        return null
    }

    override fun releaseRecord(record: AudioRecord) = record.release()

    override fun releaseSpeaker() {
        if (Build.VERSION.SDK_INT >= 31) {
            // clearCommunicationDevice releases our application's request.
            if (speakerId != null) manager.clearCommunicationDevice()
            speakerId = null
        } else if (legacySpeakerRequested) {
            manager.isSpeakerphoneOn = previousSpeaker
            legacySpeakerRequested = false
        }
    }

    override fun releaseMode() {
        // Entered only from MODE_NORMAL. Remove our communication mode request even
        // if another owner (e.g. telephony) took priority during the response.
        manager.mode = AudioManager.MODE_NORMAL
        // Mode updates are asynchronous on some implementations. Give our request
        // time to disappear before the normal MIC fallback starts. If another app
        // now owns communication mode the caller's strict MIC policy still blocks it.
        awaitPlatform { !modeActive() }
    }

    override fun volume() = manager.getStreamVolume(AudioManager.STREAM_VOICE_CALL)

    override fun playbackGain(): Float? {
        if (manager.isStreamMute(AudioManager.STREAM_VOICE_CALL)) return null
        val voiceDb = manager.getStreamVolumeDb(AudioManager.STREAM_VOICE_CALL, volume(),
            AudioDeviceInfo.TYPE_BUILTIN_SPEAKER)
        return nativeAecAttenuation(initialMediaDb, voiceDb)
    }

    private fun awaitPlatform(condition: () -> Boolean): Boolean {
        val deadline = SystemClock.elapsedRealtime() + 150
        while (!condition()) {
            if (SystemClock.elapsedRealtime() >= deadline || Thread.currentThread().isInterrupted) return false
            SystemClock.sleep(10)
        }
        return true
    }

    private fun isSpeaker(type: Int) = type == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER ||
        type == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER_SAFE

    companion object {
        private val AOSP_AEC = UUID.fromString("bb392ec0-8d4d-11e0-a896-0002a5d5c51b")
    }
}

/** Nominal Android stream dB, not a measurement of acoustic sound pressure. */
internal fun nativeAecAttenuation(mediaDb: Float, voiceDb: Float): Float? {
    if (!mediaDb.isFinite() || !voiceDb.isFinite()) return null
    if (mediaDb > voiceDb) return null // A track cannot safely boost past its maximum gain.
    return 10.0.pow((mediaDb.toDouble() - voiceDb.toDouble()) / 20.0)
        .toFloat().takeIf { it.isFinite() && it > 0f && it <= 1f }
}
