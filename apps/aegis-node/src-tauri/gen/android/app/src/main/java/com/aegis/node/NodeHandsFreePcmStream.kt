package com.aegis.node

import java.util.concurrent.CancellationException
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.ScheduledThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/** write consumes the borrowed bytes before returning; it must not retain or queue them. */
interface HandsFreePcmSink : AutoCloseable {
    fun write(bytes: ByteArray, offset: Int, count: Int)
    /** EOF is validated by the producer; this waits for actual playback, not merely queued writes. */
    fun finish()
    /** May run concurrently with write/finish; it must unblock them and discard pending samples. */
    fun abort()
}

/** Only one pending PCM byte and two scratch bytes, regardless of HTTP fragmentation. */
internal class HandsFreePcm16Framer(
    private val sink: HandsFreePcmSink,
    private val onFirstPcmAvailable:()->Unit={},
    private val onFirstUsefulPcmAvailable:(Long)->Unit={},
    private val onFirstUsefulPcmDelivered:()->Unit={}
) {
    private var pending = -1
    private val pair = ByteArray(2)
    var samples: Long = 0; private set
    private var firstAvailable=false
    private var usefulFound=false
    private var usefulDelivered=false
    private fun deliver(bytes:ByteArray,offset:Int,count:Int) {
        if(!firstAvailable){firstAvailable=true;onFirstPcmAvailable()}
        // Inspect only until the first useful sample, including pairs spanning
        // HTTP chunks. Later audio does no profiling scan or per-frame logging.
        if(!usefulFound)for(i in offset until offset+count step 2) {
            val value=((bytes[i].toInt() and 255) or (bytes[i+1].toInt() shl 8)).toShort().toInt()
            if(value>=64 || value<=-64) {
                usefulFound=true;onFirstUsefulPcmAvailable(samples+(i-offset)/2);break
            }
        }
        sink.write(bytes,offset,count);samples+=count/2
        if(usefulFound && !usefulDelivered){usefulDelivered=true;onFirstUsefulPcmDelivered()}
    }
    fun accept(bytes: ByteArray, count: Int) {
        require(count in 0..bytes.size)
        if (count == 0) return
        var offset = 0
        if (pending >= 0) {
            pair[0] = pending.toByte(); pair[1] = bytes[0]
            deliver(pair, 0, 2); pending = -1; offset = 1
        }
        val aligned = (count - offset) and -2
        if (aligned > 0) deliver(bytes,offset,aligned)
        if (count - offset != aligned) pending = bytes[count - 1].toInt() and 255
    }
    fun verifyComplete() { require(samples > 0 && pending < 0) { "incomplete_pcm_stream" } }
    fun finish() { verifyComplete(); sink.finish() }
    fun clear() { pending = -1; pair.fill(0) }
}

/** AudioTrack adapter boundary, injectable in JVM tests without pretending to play physical audio. */
internal interface HandsFreePcmDevice : AutoCloseable {
    fun write(bytes: ByteArray, offset: Int, count: Int): Int
    fun playedFrames(): Long
    fun endMarker(frame: Int, reached: () -> Unit)
    fun play()
    fun pause()
    fun stop()
}

/** Controller for a short listen-only interval on this track. */
internal interface HandsFreePlaybackProbe {
    fun playedFrames(): Long?
    fun pause(): Boolean
    fun resume()
    fun claimPause(): Boolean
}

/** No application audio queue: blocking device writes provide bounded backpressure. */
internal class HandsFreeStreamingPcmSink(
    private val createDevice: () -> HandsFreePcmDevice,
    private val stillCurrent: () -> Boolean = { true },
    private val onClosed: () -> Unit = {},
    private val onPlaybackEvent:(String)->Unit={},
    private val echo: HandsFreeBargeInEcho? = null,
    private val onPlaybackReady: (HandsFreePlaybackProbe) -> Unit = {}
) : HandsFreePcmSink {
    private val cancelled = AtomicBoolean(false)
    private val closed = AtomicBoolean(false)
    private val finished = AtomicBoolean(false)
    private val creationLock = Any()
    private val trackControl = java.lang.Object()
    private var playing = false
    private var paused = false
    private var pauseVersion = 0L
    private var probeTimeout: ScheduledFuture<*>? = null
    private val completion = CountDownLatch(1)
    @Volatile private var device: HandsFreePcmDevice? = null
    private var writtenFrames = 0L
    private var firstWriteObserved=false
    private fun isCurrent() = !cancelled.get() && !closed.get() && stillCurrent()
    private fun checkCurrent() { if (!isCurrent()) throw CancellationException() }

    private fun clearPause() {
        probeTimeout?.cancel(false)
        probeTimeout = null
        paused = false
        trackControl.notifyAll()
    }

    private fun playbackProbe(target: HandsFreePcmDevice) = object : HandsFreePlaybackProbe {
        override fun playedFrames(): Long? = synchronized(trackControl) {
            if (playing && isCurrent() && device === target) target.playedFrames() else null
        }

        override fun pause(): Boolean = synchronized(trackControl) {
            if (!playing || paused || !isCurrent() || device !== target) return false
            try {
                target.pause()
            } catch (_: IllegalStateException) {
                return false
            }
            paused = true
            val version = ++pauseVersion
            probeTimeout = probeWatchdog.schedule({
                synchronized(trackControl) {
                    if (pauseVersion == version) resume()
                }
            }, MAX_PROBE_MS, TimeUnit.MILLISECONDS)
            true
        }

        override fun resume() = synchronized(trackControl) {
            if (!paused || probeTimeout == null || device !== target) return
            try {
                if (playing && isCurrent()) target.play()
            } catch (_: IllegalStateException) {
                abort()
            } finally {
                clearPause()
            }
        }

        override fun claimPause(): Boolean = synchronized(trackControl) {
            if (!paused || probeTimeout == null || !playing || !isCurrent() || device !== target) return false
            probeTimeout?.cancel(false)
            // Only the controller that claimed the pause can end this response.
            // Its next action aborts the sink. A queued watchdog can no longer resume it.
            probeTimeout = null
            true
        }
    }
    private fun output(): HandsFreePcmDevice = device ?: synchronized(creationLock) {
        checkCurrent()
        device ?: createDevice().also {
            synchronized(trackControl) { device = it }
            try {
                checkCurrent()
                onPlaybackReady(playbackProbe(it))
                synchronized(trackControl) {
                    checkCurrent()
                    it.play()
                    playing = true
                }
                onPlaybackEvent("player_play_called")
            } catch (error: Exception) {
                synchronized(trackControl) {
                    device = null
                    playing = false
                    clearPause()
                    it.close()
                }
                throw error
            }
        }
    }
    override fun write(bytes: ByteArray, offset: Int, count: Int) {
        require(offset >= 0 && count > 0 && count % 2 == 0 && offset + count <= bytes.size)
        check(!finished.get()) { "pcm_stream_already_finished" }; checkCurrent()
        val target = output()
        var consumed = 0
        while (consumed < count) {
            val before = synchronized(trackControl) {
                while (paused && isCurrent()) trackControl.wait()
                checkCurrent()
                pauseVersion
            }
            val chunk = minOf(8192, count - consumed)
            // A blocking write may already be audible before it returns. The playback
            // head bounds which samples the echo filter can use, including short writes.
            echo?.offer(bytes, offset + consumed, chunk, writtenFrames)
            val written = target.write(bytes, offset + consumed, chunk)
            checkCurrent()
            // Android may return zero (or part of the bytes) when paused during a
            // blocking write. After resuming, write only the untouched suffix.
            if (written == 0 && synchronized(trackControl) { pauseVersion != before }) continue
            check(written > 0 && written % 2 == 0 && written <= chunk) { "audio_playback_failed" }
            consumed += written; writtenFrames += written / 2
            if(!firstWriteObserved) {
                firstWriteObserved=true
                // A single observation, never a timer or playback polling loop.
                if(target.playedFrames()>0)onPlaybackEvent("player_first_head_observed")
            }
        }
        checkCurrent()
    }
    override fun finish() {
        check(finished.compareAndSet(false, true)); checkCurrent()
        val target = device ?: error("empty_audio_playback")
        check(writtenFrames in 1..(24000L * 180))
        if (target.playedFrames() < writtenFrames) {
            target.endMarker(writtenFrames.toInt()) { completion.countDown() }
            // The playback head may pass the marker between the previous read and arming it.
            if (target.playedFrames() < writtenFrames) {
                val remaining = (writtenFrames - target.playedFrames()).coerceAtLeast(0)
                val timeout = (remaining * 1000 / 24000 + 5000).coerceAtMost(185000)
                check(completion.await(timeout, TimeUnit.MILLISECONDS)) { "audio_playback_timeout" }
            }
        }
        synchronized(trackControl) {
            checkCurrent()
            check(target.playedFrames() >= writtenFrames) { "audio_playback_incomplete" }
            playing = false
            clearPause()
        }
        onPlaybackEvent("player_drained")
    }
    override fun abort() {
        if (!cancelled.compareAndSet(false, true)) return
        completion.countDown()
        synchronized(trackControl) {
            playing = false
            clearPause()
            try { device?.stop() } catch (_: Exception) { }
        }
    }
    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        completion.countDown()
        synchronized(creationLock) {
            try {
                synchronized(trackControl) {
                    playing = false
                    clearPause()
                    try { device?.close() } finally { device = null }
                }
            } finally {
                onClosed()
            }
        }
    }

    companion object {
        private const val MAX_PROBE_MS = 700L
        private val probeWatchdog = ScheduledThreadPoolExecutor(1) { task ->
            Thread(task, "AegisPlaybackProbe").apply { isDaemon = true }
        }.apply {
            removeOnCancelPolicy = true
            setKeepAliveTime(30, TimeUnit.SECONDS)
            allowCoreThreadTimeOut(true)
        }
    }
}
