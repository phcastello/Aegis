package com.aegis.node

import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.lang.management.ManagementFactory
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Host-only replay of the same streaming external-media processor as Android.
 * Requests use the BatchEcho wire format. Responses add the gated output and
 * its thread CPU time in nanoseconds after the nonlinear and linear outputs.
 */
fun main(args: Array<String>) {
    val missingTailFrames = args.firstOrNull()?.toIntOrNull() ?: 0
    require(args.size <= 1 && missingTailFrames in 0..2)
    val input = DataInputStream(BufferedInputStream(System.`in`))
    val output = DataOutputStream(BufferedOutputStream(System.out))
    val threadCpu = ManagementFactory.getThreadMXBean()

    fun readPcm(count: Int): ShortArray {
        val bytes = ByteArray(count * 2)
        input.readFully(bytes)
        return ShortArray(count).also {
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).asShortBuffer().get(it)
        }
    }

    fun writePcm(pcm: ShortArray) {
        val bytes = ByteArray(pcm.size * 2)
        ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).asShortBuffer().put(pcm)
        output.write(bytes)
    }

    val frameSize = 320
    val micReadSize = 3200
    val sampleNanos = 62500L
    val timeBase = 1_000_000_000L
    while (true) {
        val count = try { input.readInt() } catch (_: EOFException) { break }
        if (count == 0) break
        require(count > 0 && count % frameSize == 0 && count <= 16000 * 120)
        val render = readPcm(count)
        val mic = readPcm(count)
        val clean = ShortArray(count)
        val linear = ShortArray(count)
        val gated = ShortArray(count)
        val renderFrame = ShortArray(frameSize)
        val micFrame = ShortArray(frameSize)
        val cleanFrame = ShortArray(frameSize)
        val linearFrame = ShortArray(frameSize)
        checkNotNull(NativeHandsFreeEcho.create()).use { echo ->
            for (at in 0 until count step frameSize) {
                render.copyInto(renderFrame, 0, at, at + frameSize)
                mic.copyInto(micFrame, 0, at, at + frameSize)
                check(echo.process(renderFrame, micFrame, cleanFrame, linear = linearFrame))
                cleanFrame.copyInto(clean, at)
                linearFrame.copyInto(linear, at)
            }
        }

        val block = ShortArray(micReadSize)
        val started = threadCpu.currentThreadCpuTime
        HandsFreeExternalMediaProcessor(copyReference = { end, dest ->
            val frameEnd = (end - timeBase) / sampleNanos
            val readPosition = (frameEnd - 1) % micReadSize + 1
            if (frameEnd !in dest.size.toLong()..count.toLong() || readPosition > micReadSize - missingTailFrames * frameSize) false
            else {
                render.copyInto(dest, 0, frameEnd.toInt() - dest.size, frameEnd.toInt())
                true
            }
        }, referenceGeneration = { 1L }).use { processor ->
            for (at in 0 until count step micReadSize) {
                val size = minOf(micReadSize, count - at)
                mic.copyInto(block, 0, at, at + size)
                processor.process(block, size, timeBase + (at + size) * sampleNanos)
                block.copyInto(gated, at, 0, size)
            }
        }
        val elapsed = threadCpu.currentThreadCpuTime - started
        output.writeInt(count)
        writePcm(clean)
        writePcm(linear)
        writePcm(gated)
        output.writeLong(elapsed)
        output.flush()
    }
}
