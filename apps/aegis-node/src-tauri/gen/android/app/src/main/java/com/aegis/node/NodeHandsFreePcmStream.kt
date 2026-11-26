package com.aegis.node

import java.util.concurrent.CancellationException
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/** write consumes the borrowed bytes before returning; it must not retain or queue them. */
interface HandsFreePcmSink : AutoCloseable {
    fun write(bytes: ByteArray, offset: Int, count: Int)
    /** Freeze queued device audio without discarding frames or releasing ownership. */
    fun pausePlayback(): Boolean = false
    fun resumePlayback() = Unit
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
    fun timestamp(): HandsFreeAudioTimestamp? = null
    fun discontinuity(): Long = 0
    fun endMarker(frame: Int, reached: () -> Unit)
    fun play()
    fun pause() = Unit
    fun stop()
}

/** Moderate speech lift with a bounded, continuous soft knee. The mapping has no
 * envelope state, so packet boundaries and short device writes cannot change it.
 */
internal object HandsFreePlaybackLevel {
    fun amplify(source: ByteArray, offset: Int, count: Int, output: ByteArray) {
        require(offset >= 0 && count >= 0 && count % 2 == 0 && offset <= source.size - count && count <= output.size)
        for (index in 0 until count step 2) {
            val sample = ((source[offset + index].toInt() and 255) or
                (source[offset + index + 1].toInt() shl 8)).toShort().toInt()
            // 1.6x (+4.08 dB) except for the relatively rare high peaks. Leave
            // headroom for the device's resampler and avoid hard PCM clipping.
            val magnitude = (kotlin.math.abs(sample) * 8 + 2) / 5
            val limited = if (magnitude <= KNEE) magnitude else {
                val excess = magnitude - KNEE
                KNEE + (CEILING - KNEE) * excess / (CEILING - KNEE + excess)
            }
            val adjusted = if (sample < 0) -limited else limited
            output[index] = adjusted.toByte()
            output[index + 1] = (adjusted shr 8).toByte()
        }
    }

    private const val KNEE = 26000
    private const val CEILING = 30000
}

/** No application audio queue: blocking device writes provide bounded backpressure. */
internal class HandsFreeStreamingPcmSink(
    private val createDevice: () -> HandsFreePcmDevice,
    private val stillCurrent: () -> Boolean = { true },
    private val onClosed: () -> Unit = {},
    private val onPlaybackEvent:(String)->Unit={},
    private val echo: HandsFreeBargeInEcho? = null,
    private val onPlaybackReady: (HandsFreePlaybackClock) -> Unit = {},
    boostSpeechVolume: Boolean = false,
    spectrumSession: String? = null,
    private val maximumFrames: Long = 24000L * 180
) : HandsFreePcmSink {
    private val cancelled = AtomicBoolean(false)
    private val closed = AtomicBoolean(false)
    private val finished = AtomicBoolean(false)
    private val creationLock = Any()
    private val playbackLock = java.lang.Object()
    private var paused = false
    private var pauseCount = 0L
    private var drained = false
    private val completion = CountDownLatch(1)
    private val spectrum = spectrumSession?.let { HandsFreePlaybackSpectrum.Stream(it) }
    private val boostedPcm = if (boostSpeechVolume) ByteArray(WRITE_CHUNK_BYTES) else null
    @Volatile private var device: HandsFreePcmDevice? = null
    @Volatile private var writtenFrames = 0L
    private var firstWriteObserved=false
    private fun checkCurrent() { if (cancelled.get() || closed.get() || !stillCurrent()) throw CancellationException() }
    private fun output(): HandsFreePcmDevice = device ?: synchronized(creationLock) {
        checkCurrent()
        device ?: createDevice().also {
            device = it
            try {
                checkCurrent()
                fun currentDevice() = it.takeUnless { target ->
                    cancelled.get() || closed.get() || device !== target
                }
                val clock = HandsFreePlaybackClock(
                    playedFrames = { currentDevice()?.playedFrames() },
                    timestamp = { currentDevice()?.timestamp() },
                    discontinuity = { currentDevice()?.discontinuity() ?: 0 },
                    enqueuedFrames = { if (currentDevice() != null) writtenFrames else 0L }
                )
                spectrum?.start(clock)
                onPlaybackReady(clock)
                synchronized(playbackLock) {
                    checkCurrent()
                    if (!paused) {
                        it.play()
                        onPlaybackEvent("player_play_called")
                    }
                }
            } catch (error: Exception) {
                device = null
                spectrum?.close()
                it.close()
                throw error
            }
        }
    }
    override fun write(bytes: ByteArray, offset: Int, count: Int) {
        require(offset >= 0 && count > 0 && count % 2 == 0 && offset + count <= bytes.size)
        check(!finished.get()) { "pcm_stream_already_finished" }; checkCurrent()
        val target = output()
        try {
            var consumed = 0
            while (consumed < count) {
                checkCurrent()
                val chunk = minOf(WRITE_CHUNK_BYTES, count - consumed)
                val outputBytes = boostedPcm ?: bytes
                val outputOffset = if (boostedPcm == null) offset + consumed else 0
                if (boostedPcm != null) {
                    HandsFreePlaybackLevel.amplify(bytes, offset + consumed, chunk, boostedPcm)
                }
                var chunkConsumed = 0
                while (chunkConsumed < chunk) {
                    val beforeWrite = awaitPlayback()
                    val remaining = chunk - chunkConsumed
                    // A blocking write can become audible before it returns. The
                    // echo reference must receive exactly the PCM given to the
                    // device, including our speech gain and peak compression.
                    val start = outputOffset + chunkConsumed
                    echo?.offer(outputBytes, start, remaining, writtenFrames)
                    spectrum?.offer(outputBytes, start, remaining, writtenFrames)
                    val written = target.write(outputBytes, start, remaining)
                    if (written == 0 && synchronized(playbackLock) { pauseCount != beforeWrite }) continue
                    check(written > 0 && written % 2 == 0 && written <= remaining) { "audio_playback_failed" }
                    chunkConsumed += written
                    writtenFrames += written / 2
                    if (!firstWriteObserved) {
                        firstWriteObserved = true
                        // A single observation, never a timer or playback polling loop.
                        if (target.playedFrames() > 0) onPlaybackEvent("player_first_head_observed")
                    }
                }
                consumed += chunk
            }
            checkCurrent()
        } finally {
            boostedPcm?.fill(0)
        }
    }
    override fun finish() {
        check(finished.compareAndSet(false, true)); checkCurrent()
        val target = device ?: error("empty_audio_playback")
        check(writtenFrames in 1..maximumFrames)
        if (target.playedFrames() < writtenFrames) {
            target.endMarker(writtenFrames.toInt()) { completion.countDown() }
            val remaining = (writtenFrames - target.playedFrames()).coerceAtLeast(0)
            var budget = (remaining * 1000 / 24000 + 5000).coerceAtMost(185000)
            while (target.playedFrames() < writtenFrames) {
                val beforeWait = awaitPlayback()
                val interval = minOf(100L, budget)
                check(interval > 0) { "audio_playback_timeout" }
                if (completion.await(interval, TimeUnit.MILLISECONDS)) break
                if (synchronized(playbackLock) { pauseCount == beforeWait }) budget -= interval
            }
        }
        synchronized(playbackLock) {
            awaitPlayback()
            check(target.playedFrames() >= writtenFrames) { "audio_playback_incomplete" }
            drained = true
        }
        spectrum?.close()
        onPlaybackEvent("player_drained")
    }
    private fun awaitPlayback(): Long = synchronized(playbackLock) {
        while (paused) {
            checkCurrent()
            playbackLock.wait(100)
        }
        checkCurrent()
        pauseCount
    }
    override fun pausePlayback(): Boolean = synchronized(playbackLock) {
        if (closed.get() || cancelled.get() || drained || !stillCurrent()) return@synchronized false
        if (!paused) {
            device?.pause()
            paused = true
            pauseCount++
        }
        true
    }
    override fun resumePlayback() {
        synchronized(playbackLock) {
            if (!paused || closed.get() || cancelled.get() || !stillCurrent()) return
            val target = device
            if (target != null) {
                target.play()
                onPlaybackEvent("player_play_called")
            }
            paused = false
            playbackLock.notifyAll()
        }
    }
    override fun abort() {
        if (!cancelled.compareAndSet(false, true)) return
        spectrum?.close()
        completion.countDown()
        synchronized(playbackLock) {
            playbackLock.notifyAll()
            try { device?.stop() } catch (_: Exception) { }
        }
    }
    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        spectrum?.close()
        completion.countDown()
        synchronized(creationLock) {
            try {
                synchronized(playbackLock) {
                    playbackLock.notifyAll()
                    try { device?.close() } finally { device = null }
                }
            } finally {
                onClosed()
            }
        }
    }

    companion object {
        private const val WRITE_CHUNK_BYTES = 8192
    }
}
