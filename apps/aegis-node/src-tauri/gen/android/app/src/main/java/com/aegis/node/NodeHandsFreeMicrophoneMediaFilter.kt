package com.aegis.node

import android.media.AudioRecord
import android.media.AudioTimestamp

/** Independent microphone timeline for the optional, user-authorized media
 * reference. No per-read inference or timestamp polling occurs without a reference.
 */
internal class HandsFreeMicrophoneMediaFilter(
    private val audio: AudioRecord,
    private val onClosed: (referenceSamples: Long, filteredReads: Long) -> Unit = { _, _ -> }
) : AutoCloseable {
    private val clock = HandsFreeCaptureClock()
    private val timestamp = AudioTimestamp()
    private var processor: HandsFreeExternalMediaProcessor? = null
    private var generation: Long? = null
    private var referenceSamples = 0L
    private var filteredReads = 0L

    fun advance(count: Int) = clock.advance(count)

    fun filter(samples: ShortArray, count: Int): Boolean {
        if (!NodeExternalPlaybackReference.active()) {
            if (generation != null) releaseFilter()
            return false
        }
        val current = NodeExternalPlaybackReference.generation()
        if (generation != current) {
            releaseFilter()
            generation = current
        }
        referenceSamples += count
        val now = System.nanoTime()
        val anchor = try {
            if (audio.getTimestamp(timestamp, AudioTimestamp.TIMEBASE_MONOTONIC) == AudioRecord.SUCCESS)
                HandsFreeAudioTimestamp(timestamp.framePosition, timestamp.nanoTime) else null
        } catch (_: RuntimeException) { null }
        val end = clock.endNanos(now, anchor)
        if (end == null) {
            processor?.reset()
            return false
        }
        if (count >= 320) {
            val newestCompleteFrame = end - (count % 320) * HandsFreeCaptureClock.NANOS_PER_SAMPLE
            NodeExternalPlaybackReference.awaitNewestFrame(newestCompleteFrame, current)
        }
        val active = processor ?: HandsFreeExternalMediaProcessor(
            NodeExternalPlaybackReference::copyFrame, NodeExternalPlaybackReference::generation
        ).also { processor = it }
        val filtered = try { active.process(samples, count, end) } catch (_: RuntimeException) {
            releaseFilter()
            false
        }
        if (filtered) filteredReads++
        return filtered
    }

    private fun releaseFilter() {
        processor?.close()
        processor = null
        generation = null
        clock.invalidate()
    }

    override fun close() {
        releaseFilter()
        if (referenceSamples > 0) try { onClosed(referenceSamples, filteredReads) } catch (_: RuntimeException) { }
    }
}
