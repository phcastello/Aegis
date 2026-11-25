package com.aegis.node

/** One AEC3 instance per active software echo session. The caller feeds paired,
 * contiguous 16 kHz mono PCM16 frames on the shared capture/render timeline. */
internal class NativeHandsFreeEcho private constructor(private var handle: Long) : AutoCloseable {
    /**
     * Process one or two 10 ms blocks. Inputs are unchanged; outputs are distinct.
     * Linear and residual-suppressed outputs have about 4 ms and 8 ms of extra
     * algorithmic delay respectively at 16 kHz. Delay supplied here describes
     * any residual render/capture buffering delay, not those output latencies.
     *
     * A false result means no output is usable; both outputs are zeroed.
     */
    @Synchronized
    fun process(
        render: ShortArray,
        capture: ShortArray,
        output: ShortArray,
        delayMs: Int = 0,
        linear: ShortArray? = null,
    ): Boolean {
        require(capture.size == 160 || capture.size == 320)
        require(render.size == capture.size && output.size == capture.size)
        require(linear == null || linear.size == capture.size)
        require(output !== render && output !== capture)
        require(linear == null || (linear !== output && linear !== capture && linear !== render))
        require(delayMs in 0..500)
        val ok = try {
            handle != 0L && HandsFreeEchoNative.process(
                handle, render, capture, output, delayMs, linear
            )
        } catch (_: LinkageError) {
            false
        }
        if (!ok) {
            output.fill(0)
            linear?.fill(0)
            // Do not continue on a potentially half-advanced native timeline.
            close()
        }
        return ok
    }

    /** Clear the adaptive echo path after a stream/route discontinuity. */
    @Synchronized
    fun reset(): Boolean {
        if (handle == 0L) return false
        val ok = try {
            HandsFreeEchoNative.reset(handle)
        } catch (_: LinkageError) {
            false
        }
        if (ok) return true
        close()
        return false
    }

    @Synchronized
    override fun close() {
        if (handle == 0L) return
        val previous = handle
        handle = 0L
        try {
            HandsFreeEchoNative.destroy(previous)
        } catch (_: LinkageError) {
            // The instance has already been detached and must not be reused.
        }
    }

    companion object {
        const val LINEAR_LATENCY_SAMPLES = 64
        const val SUPPRESSED_LATENCY_SAMPLES = 128

        fun create(): NativeHandsFreeEcho? = try {
            HandsFreeEchoNative.open().takeIf { it != 0L }?.let(::NativeHandsFreeEcho)
        } catch (_: LinkageError) {
            // A missing or unsupported native library must not crash the conversation.
            null
        } catch (_: RuntimeException) {
            null
        }
    }
}

/** Exact closed JNI names are retained in proguard-rules.pro. */
internal object HandsFreeEchoNative {
    init { System.loadLibrary("aegis_aec3") }
    external fun open(): Long
    external fun destroy(handle: Long)
    external fun reset(handle: Long): Boolean
    external fun process(
        handle: Long, render: ShortArray, capture: ShortArray, output: ShortArray,
        delayMs: Int, linear: ShortArray?,
    ): Boolean
}
