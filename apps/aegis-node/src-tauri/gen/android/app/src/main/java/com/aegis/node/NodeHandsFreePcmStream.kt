package com.aegis.node

import java.util.concurrent.CancellationException
import java.util.concurrent.Semaphore
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/** write consumes the borrowed bytes before returning; it must not retain or queue them. */
interface HandsFreePcmSink : AutoCloseable {
    fun write(bytes: ByteArray, offset: Int, count: Int)
    /** EOF is validated by the producer; this waits for actual playback, not merely queued writes. */
    fun finish()
    /** Temporarily suspend this turn, preserving the device and its queued audio. */
    fun pause(): Boolean = false
    /** Resume the same turn and cursor; a cancelled or superseded sink cannot resume. */
    fun resume(): Boolean = false
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
    /** Suspend without a flush. False means this device does not support reversible pause. */
    fun pause(): Boolean = false
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
    private val nanoTime: () -> Long = System::nanoTime
) : HandsFreePcmSink {
    private val cancelled = AtomicBoolean(false)
    private val closed = AtomicBoolean(false)
    private val finished = AtomicBoolean(false)
    private val creationLock = Any()
    // Independent of creationLock: a writer may be blocked in device creation,
    // AudioTrack.write or drain when the capture owner requests a pause.
    private val playbackLock = java.lang.Object()
    private val drainSignal = Semaphore(0)
    private var pauseStartedAt: Long? = null
    private var pausedNanos = 0L
    private var pauseRevision = 0L
    private var deviceStarted = false
    private var drained = false
    private val spectrum = spectrumSession?.let { HandsFreePlaybackSpectrum.Stream(it) }
    private val boostedPcm = if (boostSpeechVolume) ByteArray(WRITE_CHUNK_BYTES) else null
    @Volatile private var device: HandsFreePcmDevice? = null
    @Volatile private var writtenFrames = 0L
    private var firstWriteObserved=false
    private fun checkOpen() { if (cancelled.get() || closed.get()) throw CancellationException() }
    private fun checkCurrent() { checkOpen(); if (!stillCurrent()) throw CancellationException() }

    // Caller holds playbackLock. The drain marker callback releases the semaphore
    // directly, so it can also run from inside the device without a lock cycle.
    private fun signalStateChanged() {
        playbackLock.notifyAll()
        drainSignal.release()
    }

    private fun activeTimeLocked(): Long {
        val now = nanoTime()
        return now - pausedNanos - (pauseStartedAt?.let { now - it } ?: 0L)
    }

    private fun awaitWritable(): Long {
        while (true) {
            checkCurrent()
            synchronized(playbackLock) {
                checkOpen()
                if (pauseStartedAt == null) return pauseRevision
                // The owner resolves the candidate or cancels its turn. Recheck
                // ownership even if the owner disappeared without a callback.
                playbackLock.wait(1000L)
            }
        }
    }

    override fun pause(): Boolean {
        if (!stillCurrent()) return false
        return synchronized(playbackLock) {
            if (cancelled.get() || closed.get() || drained) return@synchronized false
            if (pauseStartedAt != null) return@synchronized true
            if (deviceStarted && device?.pause() != true) return@synchronized false
            pauseStartedAt = nanoTime()
            pauseRevision++
            signalStateChanged()
            true
        }
    }

    override fun resume(): Boolean {
        if (!stillCurrent()) return false
        return synchronized(playbackLock) {
            if (cancelled.get() || closed.get() || drained) return@synchronized false
            val start = pauseStartedAt ?: return@synchronized true
            // A lazily opened device is started by its producer, only after
            // onPlaybackReady has returned.
            if (deviceStarted) checkNotNull(device).play()
            pausedNanos += (nanoTime() - start).coerceAtLeast(0L)
            pauseStartedAt = null
            signalStateChanged()
            true
        }
    }

    private fun output(): HandsFreePcmDevice = device ?: synchronized(creationLock) {
        awaitWritable()
        device ?: createDevice().also { created ->
            try {
                synchronized(playbackLock) {
                    checkOpen()
                    device = created
                }
                checkCurrent()
                fun currentDevice() = created.takeUnless { target ->
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
                // Pause may arrive while the player is being initialized. Its
                // producer remains responsible for the first play and event.
                while (true) {
                    awaitWritable()
                    val started = synchronized(playbackLock) {
                        checkOpen()
                        if (pauseStartedAt != null) false
                        else {
                            created.play()
                            deviceStarted = true
                            true
                        }
                    }
                    if (started) break
                }
                onPlaybackEvent("player_play_called")
            } catch (error: Exception) {
                synchronized(playbackLock) {
                    device = null
                    deviceStarted = false
                }
                spectrum?.close()
                created.close()
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
                    val beforeWrite = awaitWritable()
                    val remaining = chunk - chunkConsumed
                    // A blocking write can become audible before it returns. The
                    // echo reference must receive exactly the PCM given to the
                    // device, including our speech gain and peak compression.
                    val start = outputOffset + chunkConsumed
                    echo?.offer(outputBytes, start, remaining, writtenFrames)
                    spectrum?.offer(outputBytes, start, remaining, writtenFrames)
                    val written = try { target.write(outputBytes, start, remaining) }
                    catch (error: Exception) { checkCurrent(); throw error }
                    checkCurrent()
                    check(written >= 0 && written % 2 == 0 && written <= remaining) { "audio_playback_failed" }
                    if (written == 0) {
                        // AudioTrack can return a short transfer, including zero,
                        // when paused concurrently or while entering write.
                        // Zero without an intervening pause is a playback failure.
                        check(synchronized(playbackLock) { pauseRevision != beforeWrite }) { "audio_playback_failed" }
                        continue
                    }
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
        check(writtenFrames in 1..(24000L * 180))
        val deadline = synchronized(playbackLock) {
            checkOpen()
            val remaining = (writtenFrames - target.playedFrames()).coerceAtLeast(0L)
            if (remaining > 0) target.endMarker(writtenFrames.toInt()) { drainSignal.release() }
            val timeout = (remaining * 1000L / 24000L + 5000L).coerceAtMost(185000L)
            activeTimeLocked() + TimeUnit.MILLISECONDS.toNanos(timeout)
        }
        while (true) {
            checkCurrent()
            val waitNanos = synchronized(playbackLock) {
                checkOpen()
                if (pauseStartedAt != null) TimeUnit.SECONDS.toNanos(1L)
                else if (target.playedFrames() >= writtenFrames) {
                    drained = true
                    0L
                } else {
                    val remaining = deadline - activeTimeLocked()
                    check(remaining > 0L) { "audio_playback_timeout" }
                    remaining
                }
            }
            if (waitNanos == 0L) break
            // Wakes on marker, pause, resume or cancellation. Time advances
            // against the playback budget only while the device may play.
            drainSignal.tryAcquire(waitNanos, TimeUnit.NANOSECONDS)
        }
        checkCurrent()
        spectrum?.close()
        onPlaybackEvent("player_drained")
    }

    override fun abort() {
        synchronized(playbackLock) {
            if (!cancelled.compareAndSet(false, true)) return
            signalStateChanged()
            try { device?.stop() } catch (_: Exception) { }
        }
        spectrum?.close()
    }

    override fun close() {
        synchronized(playbackLock) {
            if (!closed.compareAndSet(false, true)) return
            signalStateChanged()
            // Unblock a paused producer before waiting on lazy creation to end.
            if (!drained && !cancelled.get()) try { device?.stop() } catch (_: Exception) { }
        }
        spectrum?.close()
        synchronized(creationLock) {
            val target = synchronized(playbackLock) {
                device.also {
                    device = null
                    deviceStarted = false
                }
            }
            try { target?.close() } finally { onClosed() }
        }
    }

    companion object {
        private const val WRITE_CHUNK_BYTES = 8192
    }
}

