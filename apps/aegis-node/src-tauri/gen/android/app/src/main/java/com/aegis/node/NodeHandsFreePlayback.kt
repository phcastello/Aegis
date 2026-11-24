package com.aegis.node

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioDeviceInfo
import android.media.AudioFocusRequest
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRouting
import android.media.AudioTimestamp
import android.media.AudioTrack
import android.os.Build
import android.os.Handler
import android.os.Looper
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

/** TTS stays on the media playback chain even when native AEC owns the capture route. */
internal fun handsFreeSpeechMediaAttributes(): AudioAttributes = AudioAttributes.Builder()
    .setUsage(AudioAttributes.USAGE_MEDIA)
    .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
    .build()

internal fun handsFreeMediaSpeakerAnticipated(manager: AudioManager, attributes: AudioAttributes): Boolean {
    if (Build.VERSION.SDK_INT < 33) return true
    val outputs = manager.getAudioDevicesForAttributes(attributes)
    return outputs.isNotEmpty() && outputs.all {
        it.type == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER ||
            it.type == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER_SAFE
    }
}

/** A fresh, lazily opened stream per turn. AudioTrack's fixed buffer supplies backpressure. */
internal class HandsFreePlayback(
    private val createDevice: (Boolean, () -> Unit) -> HandsFreePcmDevice
) {
    constructor(createDevice: (() -> Unit) -> HandsFreePcmDevice) : this({ _, onFocusLoss -> createDevice(onFocusLoss) })
    constructor(context: Context) : this({ nativeCapture, onFocusLoss -> openDevice(context, nativeCapture, onFocusLoss) })
    private val generation = AtomicInteger()
    private val active = AtomicReference<HandsFreePcmSink?>()
    fun cancel() { generation.incrementAndGet(); active.getAndSet(null)?.abort() }

    fun openStream(stillCurrent: () -> Boolean): HandsFreePcmSink = openStream(stillCurrent,{})
    fun openStream(
        stillCurrent: () -> Boolean,
        onPlaybackEvent: (String) -> Unit,
        echo: HandsFreeBargeInEcho? = null,
        prepareCommunication: () -> Boolean = { false },
        onCommunicationFailed: () -> Unit = {},
        onPlaybackReady: (HandsFreePlaybackClock) -> Unit = {}
    ): HandsFreePcmSink {
        val token = generation.incrementAndGet()
        lateinit var stream: HandsFreeStreamingPcmSink
        stream = HandsFreeStreamingPcmSink(
            createDevice = {
                val focusLost = {
                    // Audio focus events may arrive after an old track has been replaced.
                    // This callback only owns this stream.
                    if (active.get() === stream && generation.compareAndSet(token, token + 1)) {
                        active.compareAndSet(stream, null)
                        stream.abort()
                    }
                    Unit
                }
                val nativeCapture = prepareCommunication()
                if (generation.get() != token || !stillCurrent()) {
                    if (nativeCapture) onCommunicationFailed()
                    throw java.util.concurrent.CancellationException()
                }
                if (!nativeCapture) createDevice(false, focusLost)
                else try {
                    createDevice(true, focusLost)
                } catch (_: Exception) {
                    onCommunicationFailed()
                    if (generation.get() != token || !stillCurrent()) throw java.util.concurrent.CancellationException()
                    createDevice(false, focusLost)
                }
            },
            stillCurrent = { generation.get() == token && stillCurrent() },
            onClosed = { active.compareAndSet(stream, null) },
            onPlaybackEvent = onPlaybackEvent,
            echo = echo,
            onPlaybackReady = onPlaybackReady
        )
        active.getAndSet(stream)?.abort()
        if (generation.get() != token) { stream.abort(); active.compareAndSet(stream, null) }
        return stream
    }

    companion object {
    private fun openDevice(context: Context, nativeCapture: Boolean,
                           onFocusLoss: () -> Unit): HandsFreePcmDevice {
        val manager = context.getSystemService(AudioManager::class.java)
        val attributes = handsFreeSpeechMediaAttributes()
        if (nativeCapture) {
            // The media route must still be compatible with the microphone's native AEC.
            check(manager.mode == AudioManager.MODE_IN_COMMUNICATION &&
                handsFreeMediaSpeakerAnticipated(manager, attributes)) { "native_aec_media_route_changed" }
        }
        val handler = Handler(Looper.getMainLooper())
        val listener = AudioManager.OnAudioFocusChangeListener { if (it < 0) onFocusLoss() }
        val request = if (Build.VERSION.SDK_INT >= 26) AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN_TRANSIENT)
            .setAudioAttributes(attributes).setOnAudioFocusChangeListener(listener, handler).build() else null
        var granted = false
        var output: AudioTrack? = null
        fun abandon() {
            if (!granted) return
            granted = false
            if (request != null) manager.abandonAudioFocusRequest(request)
            else @Suppress("DEPRECATION") manager.abandonAudioFocus(listener)
        }
        try {
            val focus = if (request != null) manager.requestAudioFocus(request)
                else @Suppress("DEPRECATION") manager.requestAudioFocus(listener, AudioManager.STREAM_MUSIC, AudioManager.AUDIOFOCUS_GAIN_TRANSIENT)
            check(focus == AudioManager.AUDIOFOCUS_REQUEST_GRANTED) { "audio_focus_unavailable" }; granted = true
            val minimum = AudioTrack.getMinBufferSize(24000, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT)
            check(minimum > 0)
            val track = AudioTrack.Builder().setAudioAttributes(attributes)
                .setAudioFormat(AudioFormat.Builder().setSampleRate(24000).setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
                .setBufferSizeInBytes(maxOf(minimum, 24000)).setTransferMode(AudioTrack.MODE_STREAM).build()
            output = track; check(track.state == AudioTrack.STATE_INITIALIZED)
            // Default streaming threshold is the entire capacity. Modern Android can start even
            // a short reply immediately; older releases use the smallest accepted native buffer.
            if (Build.VERSION.SDK_INT >= 31) track.setStartThresholdInFrames(1)
            else track.setBufferSizeInFrames(1)
            val routeChanges = AtomicLong()
            val routing = AudioRouting.OnRoutingChangedListener { routeChanges.incrementAndGet() }
            track.addOnRoutingChangedListener(routing, handler)
            return object : HandsFreePcmDevice {
                private val audioTimestamp = AudioTimestamp()
                private val closed = java.util.concurrent.atomic.AtomicBoolean(false)
                override fun write(bytes: ByteArray, offset: Int, count: Int) = track.write(bytes, offset, count, AudioTrack.WRITE_BLOCKING)
                override fun playedFrames() = track.playbackHeadPosition.toLong() and 0xffffffffL
                override fun timestamp() = if (track.getTimestamp(audioTimestamp))
                    HandsFreeAudioTimestamp(audioTimestamp.framePosition, audioTimestamp.nanoTime) else null
                override fun discontinuity() =
                    (routeChanges.get() shl 32) or (track.underrunCount.toLong() and 0xffffffffL)
                override fun endMarker(frame: Int, reached: () -> Unit) {
                    track.setPlaybackPositionUpdateListener(object : AudioTrack.OnPlaybackPositionUpdateListener {
                        override fun onMarkerReached(track: AudioTrack) { reached() }
                        override fun onPeriodicNotification(track: AudioTrack) { }
                    }, handler)
                    check(track.setNotificationMarkerPosition(frame) == AudioTrack.SUCCESS) { "audio_marker_failed" }
                }
                override fun play() { track.play() }
                override fun stop() { try { track.pause(); track.flush(); track.stop() } catch (_: Exception) { } }
                override fun close() {
                    if (!closed.compareAndSet(false, true)) return
                    try {
                        track.removeOnRoutingChangedListener(routing)
                    } finally {
                        try { stop(); track.release() } finally { abandon() }
                    }
                }
            }
        } catch (error: Exception) {
            try { output?.release() } finally { abandon() }
            throw error
        }
    }
    }
}
