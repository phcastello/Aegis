package com.aegis.node

import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference
import java.util.concurrent.atomic.AtomicReferenceArray
import kotlin.math.PI
import kotlin.math.ceil
import kotlin.math.cos
import kotlin.math.log10
import kotlin.math.pow
import kotlin.math.roundToInt
import kotlin.math.sin
import kotlin.math.sqrt

/** The spectrum is an opt-in presentation of the Aegis's own PCM. Each FFT
 * frame is keyed by the playback position; queued sound is not displayed early.
 * A hidden panel does no spectral analysis and no audio is sent to the UI.
 */
internal object HandsFreePlaybackSpectrum {
    const val BAR_COUNT = 32
    const val WINDOW_FRAMES = 1024
    const val STALE_CLEAR_MS = 500L
    private const val SAMPLE_RATE = 24000
    private const val CAPACITY = 64

    private class Observer(val owner: Any, val session: String) {
        @Volatile var listener: (() -> Unit)? = null
    }

    private data class Frame(val observer: Observer, val end: Long, val levels: FloatArray)

    private val observer = AtomicReference<Observer?>(null)
    private val active = AtomicReference<Stream?>(null)
    private val streamSequence = AtomicLong()
    private val lifecycle = Any()
    private var latestStarted = 0L // Protected by lifecycle, even after its stream closes.

    fun attach(owner: Any, session: String?) {
        observer.set(session?.let { Observer(owner, it) })
    }

    fun detach(owner: Any) {
        val current = observer.get()?.takeIf { it.owner === owner } ?: return
        observer.compareAndSet(current, null)
    }

    fun setListener(owner: Any, listener: (() -> Unit)?) {
        observer.get()?.takeIf { it.owner === owner }?.listener = listener
    }

    fun reset(owner: Any) {
        val current = observer.get()?.takeIf { it.owner === owner } ?: return
        observer.compareAndSet(current, Observer(owner, current.session).also { it.listener = current.listener })
    }

    /** First PCM can already be submitted while the UI still says processing.
     * Preserve that observer when entering SPEAKING; clear it when leaving. */
    fun phaseChanged(owner: Any, previous: HandsFreePanelState, next: HandsFreePanelState) {
        if (previous == HandsFreePanelState.SPEAKING && next != previous) reset(owner)
    }

    class Stream(private val session: String) {
        private val sequence = streamSequence.incrementAndGet()
        private var closed = false // Protected by lifecycle.
        @Volatile private var clock: HandsFreePlaybackClock? = null
        private var generation: Observer? = null
        private var analyzer: Analyzer? = null
        private var nextFrame = -1L
        private val frames = AtomicReferenceArray<Frame?>(CAPACITY)
        private val written = AtomicLong()
        // Consumer-owned timing for a stalled playback head.
        private var lastHead = -1L
        private var advancedAt = 0L
        @Volatile private var headInvalid = false

        fun start(playbackClock: HandsFreePlaybackClock) {
            // A canceled device-creation callback can resume after the next
            // reply starts. Never let it replace the new stream's spectrum.
            synchronized(lifecycle) {
                if (closed || sequence <= latestStarted) return
                clock = playbackClock
                latestStarted = sequence
                active.set(this)
            }
        }

        /** Called before the blocking device write, with the same PCM/position
         * as the echo reference. A repeated partial write skips its overlap.
         * Only a frame actually passed by the device head can be displayed.
         */
        fun offer(bytes: ByteArray, offset: Int, count: Int, framePosition: Long) {
            if (headInvalid) return
            val current = observer.get()?.takeIf { it.session == session && active.get() === this } ?: return
            if (generation !== current || framePosition > nextFrame) {
                analyzer = Analyzer()
                generation = current
                nextFrame = framePosition
            }
            val skip = (nextFrame - framePosition).coerceIn(0L, (count / 2).toLong()).toInt()
            val source = analyzer ?: return
            val first = offset + skip * 2
            val end = offset + count
            var notify = false
            for (index in first until end step 2) {
                val raw = ((bytes[index].toInt() and 255) or (bytes[index + 1].toInt() shl 8)).toShort()
                nextFrame++
                val spectrum = source.add(raw)
                if (spectrum != null) {
                    val position = written.get()
                    frames.set((position % CAPACITY).toInt(), Frame(current, nextFrame, spectrum))
                    written.set(position + 1)
                    notify = true
                }
            }
            if (notify && observer.get() === current && active.get() === this) current.listener?.invoke()
        }

        internal fun render(owner: Any?, output: FloatArray, nowNanos: Long): Boolean {
            val current = observer.get()?.takeIf { it.owner === owner && it.session == session }
                ?: run { output.fill(0f); return false }
            val head = try { clock?.invoke() } catch (_: RuntimeException) { null }
            if (headInvalid || head == null || head < 0) {
                output.fill(0f)
                return false
            }
            // A reset head no longer indexes the PCM submitted to this
            // stream. Leave the display neutral until a fresh AudioTrack.
            if (head < lastHead) {
                headInvalid = true
                output.fill(0f)
                return false
            }
            if (head != lastHead) {
                lastHead = head
                advancedAt = nowNanos
            }
            val count = written.get()
            val first = (count - CAPACITY).coerceAtLeast(0L)
            var selected: Frame? = null
            var hasPending = false
            for (index in count - 1 downTo first) {
                val frame = frames.get((index % CAPACITY).toInt()) ?: continue
                if (frame.observer !== current) continue
                if (frame.end > head) {
                    hasPending = true
                    continue
                }
                selected = frame
                break
            }
            val stalled = nowNanos - advancedAt >= STALE_CLEAR_MS * 1_000_000L
            if (stalled || selected == null || head - selected.end > 2L * WINDOW_FRAMES) {
                output.fill(0f)
            } else {
                selected.levels.copyInto(output, endIndex = BAR_COUNT)
            }
            if (observer.get() !== current || active.get() !== this) {
                output.fill(0f)
                return false
            }
            return !stalled && (hasPending || (selected != null && head - selected.end <= 2L * WINDOW_FRAMES))
        }

        fun close() {
            val removed = synchronized(lifecycle) {
                closed = true
                clock = null
                active.compareAndSet(this, null)
            }
            if (removed) observer.get()?.takeIf { it.session == session }?.listener?.invoke()
        }
    }

    fun render(owner: Any?, output: FloatArray, nowNanos: Long = System.nanoTime()): Boolean {
        require(output.size >= BAR_COUNT)
        return active.get()?.render(owner, output, nowNanos) ?: run { output.fill(0f); false }
    }

    /** In-place radix-2 FFT, Hann window, fixed dBFS calibration. Bands are
     * ordered from 80 Hz to just below 12 kHz with logarithmic bin edges.
     */
    internal class Analyzer {
        private val real = DoubleArray(WINDOW_FRAMES)
        private val imaginary = DoubleArray(WINDOW_FRAMES)
        private var count = 0

        fun add(sample: Short): FloatArray? {
            real[count] = (sample.toDouble() / 32768.0) * window[count]
            count++
            if (count != WINDOW_FRAMES) return null
            count = 0
            imaginary.fill(0.0)
            var j = 0
            for (i in 1 until WINDOW_FRAMES) {
                var bit = WINDOW_FRAMES shr 1
                while (j and bit != 0) {
                    j = j xor bit
                    bit = bit shr 1
                }
                j = j xor bit
                if (i < j) {
                    val swap = real[i]
                    real[i] = real[j]
                    real[j] = swap
                }
            }
            var size = 2
            while (size <= WINDOW_FRAMES) {
                val half = size / 2
                val stride = WINDOW_FRAMES / size
                for (start in 0 until WINDOW_FRAMES step size) {
                    for (k in 0 until half) {
                        val w = k * stride
                        val a = start + k
                        val b = a + half
                        val re = real[b] * cosTable[w] - imaginary[b] * sinTable[w]
                        val im = real[b] * sinTable[w] + imaginary[b] * cosTable[w]
                        real[b] = real[a] - re
                        imaginary[b] = imaginary[a] - im
                        real[a] += re
                        imaginary[a] += im
                    }
                }
                size *= 2
            }
            return FloatArray(BAR_COUNT) { band ->
                var power = 0.0
                for (bin in edges[band] until edges[band + 1]) {
                    power = maxOf(power, real[bin] * real[bin] + imaginary[bin] * imaginary[bin])
                }
                val magnitude = 4.0 / WINDOW_FRAMES * sqrt(power)
                if (magnitude <= 0.0012589254) 0f
                else ((20.0 * log10(magnitude) + 58.0) / 46.0).coerceIn(0.0, 1.0).toFloat()
            }
        }
    }

    private val window = DoubleArray(WINDOW_FRAMES) { 0.5 - 0.5 * cos(2 * PI * it / WINDOW_FRAMES) }
    private val cosTable = DoubleArray(WINDOW_FRAMES / 2) { cos(-2 * PI * it / WINDOW_FRAMES) }
    private val sinTable = DoubleArray(WINDOW_FRAMES / 2) { sin(-2 * PI * it / WINDOW_FRAMES) }
    private val edges = IntArray(BAR_COUNT + 1).apply {
        val step = SAMPLE_RATE.toDouble() / WINDOW_FRAMES
        this[0] = ceil(80.0 / step).toInt()
        for (i in 1 until BAR_COUNT) {
            val bin = (80.0 * (12000.0 / 80).pow(i.toDouble() / BAR_COUNT) / step).roundToInt()
            this[i] = bin.coerceIn(this[i - 1] + 1, WINDOW_FRAMES / 2 - (BAR_COUNT - i))
        }
        this[BAR_COUNT] = WINDOW_FRAMES / 2
    }
}
