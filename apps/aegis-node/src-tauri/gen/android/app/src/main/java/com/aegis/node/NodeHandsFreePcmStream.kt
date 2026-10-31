package com.aegis.node

import java.util.concurrent.CancellationException
import java.util.concurrent.CountDownLatch
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
    fun stop()
}

/** No application audio queue: blocking device writes provide bounded backpressure. */
internal class HandsFreeStreamingPcmSink(
    private val createDevice: () -> HandsFreePcmDevice,
    private val stillCurrent: () -> Boolean = { true },
    private val onClosed: () -> Unit = {},
    private val onPlaybackEvent:(String)->Unit={}
) : HandsFreePcmSink {
    private val cancelled = AtomicBoolean(false)
    private val closed = AtomicBoolean(false)
    private val finished = AtomicBoolean(false)
    private val creationLock = Any()
    private val completion = CountDownLatch(1)
    @Volatile private var device: HandsFreePcmDevice? = null
    private var writtenFrames = 0L
    private var firstWriteObserved=false
    private fun checkCurrent() { if (cancelled.get() || closed.get() || !stillCurrent()) throw CancellationException() }
    private fun output(): HandsFreePcmDevice = device ?: synchronized(creationLock) {
        checkCurrent()
        device ?: createDevice().also {
            device = it
            try { checkCurrent(); it.play();onPlaybackEvent("player_play_called") } catch (error: Exception) { it.close(); throw error }
        }
    }
    override fun write(bytes: ByteArray, offset: Int, count: Int) {
        require(offset >= 0 && count > 0 && count % 2 == 0 && offset + count <= bytes.size)
        check(!finished.get()) { "pcm_stream_already_finished" }; checkCurrent()
        val target = output()
        var consumed = 0
        while (consumed < count) {
            checkCurrent()
            val written = target.write(bytes, offset + consumed, minOf(8192, count - consumed))
            check(written > 0 && written % 2 == 0 && written <= count - consumed) { "audio_playback_failed" }
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
        checkCurrent()
        check(target.playedFrames() >= writtenFrames) { "audio_playback_incomplete" }
        onPlaybackEvent("player_drained")
    }
    override fun abort() {
        cancelled.set(true); completion.countDown()
        try { device?.stop() } catch (_: Exception) { }
    }
    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        completion.countDown()
        synchronized(creationLock) { try { device?.close() } finally { device = null; onClosed() } }
    }
}
