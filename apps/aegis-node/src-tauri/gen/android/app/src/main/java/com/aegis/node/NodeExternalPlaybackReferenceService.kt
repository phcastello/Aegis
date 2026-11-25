package com.aegis.node

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioPlaybackConfiguration
import android.media.AudioRecord
import android.media.AudioTimestamp
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import androidx.core.app.NotificationCompat
import java.util.concurrent.atomic.AtomicBoolean

/** Audio-only playback capture. No virtual display, screen surface or file is created. */
class NodeExternalPlaybackReferenceService : Service() {
    private val main = Handler(Looper.getMainLooper())
    private val manager by lazy { getSystemService(AudioManager::class.java) }
    private var session = 0L
    private var sessionStartId = 0
    private var projection: MediaProjection? = null
    private var projectionCallback: MediaProjection.Callback? = null
    private var playbackRegistered = false
    private var playing = false
    private var tailPending = false
    private var failedEpisode = false
    private var worker: CaptureWorker? = null
    private val tail = Runnable {
        tailPending = false
        if (!playing) stopCollector("waiting")
    }
    private val watchdog = Runnable { checkPlayback() }
    private val playback = object : AudioManager.AudioPlaybackCallback() {
        override fun onPlaybackConfigChanged(configs: MutableList<AudioPlaybackConfiguration>) {
            checkPlayback(configs)
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            terminate("off", startId)
            return START_NOT_STICKY
        }
        if (Build.VERSION.SDK_INT < 29 || intent?.action != ACTION_START) {
            stopSelf(startId)
            return START_NOT_STICKY
        }
        val next = intent.getLongExtra("sessionId", 0L)
        // Take the transient result exactly once. Never restore a token after a
        // process restart, screen lock or revocation.
        val consent = NodeExternalPlaybackReference.takeConsent(next)
        // Ignore stale or duplicate start intents without touching the live owner.
        if (consent == null && session != 0L) {
            sessionStartId = startId
            return START_NOT_STICKY
        }
        if (session != 0L) endSession("stopped")
        session = next
        sessionStartId = startId
        try {
            // On modern Android this must precede getMediaProjection().
            startForeground(NOTIFICATION, notification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
            if (consent == null || !NodeExternalPlaybackReference.current(next) ||
                !NodeHandsFree.externalReferenceAllowed(this)) {
                terminate("blocked", startId)
                return START_NOT_STICKY
            }
            val acquired = getSystemService(MediaProjectionManager::class.java)
                .getMediaProjection(consent.resultCode, consent.data) ?: error("no projection")
            projection = acquired
            val callback = object : MediaProjection.Callback() {
                override fun onStop() {
                    if (session == next) terminate("stopped")
                }
            }
            acquired.registerCallback(callback, main)
            projectionCallback = callback
            if (!NodeExternalPlaybackReference.authorized(next) { stopped ->
                    if (session == stopped) terminate("off")
                }) {
                terminate("stopped", startId)
                return START_NOT_STICKY
            }
            manager.registerAudioPlaybackCallback(playback, main)
            playbackRegistered = true
            checkPlayback()
        } catch (_: RuntimeException) {
            terminate("unavailable", startId)
        }
        return START_NOT_STICKY
    }

    private fun eligible(): Boolean = session != 0L &&
        NodeExternalPlaybackReference.current(session) && NodeHandsFree.externalReferenceAllowed(this)

    private fun checkPlayback(configs: List<AudioPlaybackConfiguration>? = null) {
        main.removeCallbacks(watchdog)
        if (!eligible()) {
            if (session != 0L) terminate("stopped")
            return
        }
        val mediaActive = try {
            manager.isMusicActive || (configs ?: manager.activePlaybackConfigurations).any { config ->
                config.audioAttributes.usage in MEDIA_USAGES
            }
        } catch (_: RuntimeException) {
            false
        }
        if (mediaActive) {
            if (!playing) failedEpisode = false
            playing = true
            tailPending = false
            main.removeCallbacks(tail)
            if (!failedEpisode) startCollector()
        } else if (playing) {
            playing = false
            tailPending = true
            main.removeCallbacks(tail)
            main.postDelayed(tail, 600L)
        } else if (!tailPending && worker == null) {
            NodeExternalPlaybackReference.markStatus(session, "waiting")
        }
        // No periodic work while the projection is waiting for media. The
        // playback callback and assistant lifecycle handle the idle period.
        if (playing || worker != null) main.postDelayed(watchdog, 4000L)
    }

    private fun startCollector() {
        if (worker != null || !eligible()) return
        val source = projection ?: return
        val id = session
        val captureId = NodeExternalPlaybackReference.beginCapture(id) ?: return
        val next = CaptureWorker(id, captureId, source)
        worker = next
        Thread(next::run, "AegisExternalPlayback").apply { isDaemon = true; start() }
    }

    private fun stopCollector(outcome: String) {
        val currentWorker = worker ?: return
        NodeExternalPlaybackReference.endCapture(currentWorker.sessionId, currentWorker.captureId, outcome)
        currentWorker.stop()
    }

    private fun workerEnded(currentWorker: CaptureWorker, failed: Boolean) {
        if (worker !== currentWorker) return
        worker = null
        if (session != currentWorker.sessionId || !NodeExternalPlaybackReference.current(session)) return
        if (failed && playing) {
            failedEpisode = true
            NodeExternalPlaybackReference.markStatus(session, "unavailable")
        } else if (playing && !failedEpisode) {
            startCollector()
        } else {
            NodeExternalPlaybackReference.markStatus(session, "waiting")
        }
    }

    private fun endSession(outcome: String) {
        val old = session
        session = 0L
        sessionStartId = 0
        if (old != 0L) NodeExternalPlaybackReference.finish(old, outcome)
        main.removeCallbacks(tail)
        main.removeCallbacks(watchdog)
        playing = false
        tailPending = false
        failedEpisode = false
        if (playbackRegistered) {
            playbackRegistered = false
            try { manager.unregisterAudioPlaybackCallback(playback) } catch (_: RuntimeException) {}
        }
        val previousWorker = worker
        worker = null
        previousWorker?.stop()
        val previousProjection = projection
        projection = null
        val callback = projectionCallback
        projectionCallback = null
        try {
            if (callback != null) previousProjection?.unregisterCallback(callback)
        } catch (_: RuntimeException) { } finally {
            try { previousProjection?.stop() } catch (_: RuntimeException) {}
        }
    }

    private fun terminate(outcome: String, startId: Int = sessionStartId) {
        endSession(outcome)
        stopForeground(STOP_FOREGROUND_REMOVE)
        if (startId > 0) stopSelf(startId) else stopSelf()
    }

    override fun onDestroy() {
        endSession("stopped")
        stopForeground(STOP_FOREGROUND_REMOVE)
        super.onDestroy()
    }

    private fun notification(): android.app.Notification {
        val notifications = getSystemService(NotificationManager::class.java)
        notifications.createNotificationChannel(NotificationChannel(CHANNEL, "Aegis · áudio de mídia",
            NotificationManager.IMPORTANCE_LOW).apply { setShowBadge(false) })
        val settings = PendingIntent.getActivity(this, NOTIFICATION, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val stop = PendingIntent.getService(this, NOTIFICATION + 1,
            Intent(this, NodeExternalPlaybackReferenceService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        return NotificationCompat.Builder(this, CHANNEL)
            .setContentTitle("Aegis · áudio de mídia")
            .setContentText("Cancelamento local de mídia autorizado nesta sessão.")
            .setContentIntent(settings).setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .setOngoing(true).setSilent(true).addAction(0, "Desativar", stop).build()
    }

    private inner class CaptureWorker(val sessionId: Long, val captureId: Long, val source: MediaProjection) {
        private val running = AtomicBoolean(true)
        private val audioLock = Any()
        private var audio: AudioRecord? = null

        fun stop() {
            running.set(false)
            synchronized(audioLock) {
                try { audio?.stop() } catch (_: RuntimeException) {}
            }
        }

        private fun current() = running.get() && NodeExternalPlaybackReference.current(sessionId)

        @Suppress("MissingPermission")
        fun run() {
            var record: AudioRecord? = null
            var ownedSession = 0
            var failed = false
            val pcm = ShortArray(320)
            val clock = HandsFreeCaptureClock()
            val timestamp = AudioTimestamp()
            try {
                if (!current()) return
                try { android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_AUDIO) }
                catch (_: RuntimeException) { }
                val configuration = AudioPlaybackCaptureConfiguration.Builder(source)
                    .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                    .addMatchingUsage(AudioAttributes.USAGE_GAME)
                    .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                    .excludeUid(android.os.Process.myUid())
                    .build()
                val minimum = AudioRecord.getMinBufferSize(16000, AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT)
                check(minimum > 0)
                record = AudioRecord.Builder().setAudioPlaybackCaptureConfig(configuration)
                    .setAudioFormat(AudioFormat.Builder().setSampleRate(16000)
                        .setChannelMask(AudioFormat.CHANNEL_IN_MONO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
                    .setBufferSizeInBytes(maxOf(minimum, 12800)).build()
                check(record.state == AudioRecord.STATE_INITIALIZED && record.sampleRate == 16000 &&
                    record.channelCount == 1 && record.audioFormat == AudioFormat.ENCODING_PCM_16BIT)
                ownedSession = record.audioSessionId
                check(ownedSession > 0)
                NodeExternalPlaybackReference.ownRecord(ownedSession)
                synchronized(audioLock) {
                    if (!current()) return
                    audio = record
                    record.startRecording()
                    check(record.recordingState == AudioRecord.RECORDSTATE_RECORDING)
                }
                var nextSilenceCheck = 0L
                while (current()) {
                    val count = record.read(pcm, 0, pcm.size, AudioRecord.READ_BLOCKING)
                    if (!current()) break
                    check(count in 1..pcm.size)
                    val readAt = System.nanoTime()
                    if (readAt >= nextSilenceCheck) {
                        check(record.activeRecordingConfiguration?.isClientSilenced != true)
                        nextSilenceCheck = readAt + 500_000_000L
                    }
                    clock.advance(count)
                    val observed = if (record.getTimestamp(timestamp, AudioTimestamp.TIMEBASE_MONOTONIC) == AudioRecord.SUCCESS)
                        HandsFreeAudioTimestamp(timestamp.framePosition, timestamp.nanoTime) else null
                    val now = System.nanoTime()
                    NodeExternalPlaybackReference.offer(sessionId, captureId, pcm, count,
                        clock.endNanos(now, observed), now)
                    pcm.fill(0)
                }
            } catch (_: RuntimeException) {
                failed = current()
            } finally {
                NodeExternalPlaybackReference.endCapture(sessionId, captureId, if (failed) "unavailable" else "waiting")
                try {
                    synchronized(audioLock) {
                        audio = null
                        try { record?.stop() } catch (_: RuntimeException) {}
                        try { record?.release() } catch (_: RuntimeException) {}
                    }
                } finally {
                    if (ownedSession > 0) NodeExternalPlaybackReference.releaseRecord(ownedSession)
                    pcm.fill(0)
                    main.post { workerEnded(this, failed) }
                }
            }
        }
    }

    companion object {
        internal const val ACTION_START = "com.aegis.node.EXTERNAL_MEDIA_START"
        private const val ACTION_STOP = "com.aegis.node.EXTERNAL_MEDIA_STOP"
        private const val CHANNEL = "aegis-external-media-reference"
        private const val NOTIFICATION = 583
        private val MEDIA_USAGES = setOf(AudioAttributes.USAGE_MEDIA, AudioAttributes.USAGE_GAME, AudioAttributes.USAGE_UNKNOWN)
    }
}
