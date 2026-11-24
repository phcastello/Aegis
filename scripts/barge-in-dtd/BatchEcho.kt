package com.aegis.node

import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.nio.ByteBuffer
import java.nio.ByteOrder

fun main() {
    val input = DataInputStream(BufferedInputStream(System.`in`))
    val output = DataOutputStream(BufferedOutputStream(System.out))
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
    while (true) {
        val count = try { input.readInt() } catch (_: EOFException) { break }
        if (count == 0) break
        require(count > 0 && count % 320 == 0 && count <= 16000 * 120)
        val render = readPcm(count)
        val mic = readPcm(count)
        val clean = ShortArray(count)
        val linear = ShortArray(count)
        val renderFrame = ShortArray(320)
        val micFrame = ShortArray(320)
        val cleanFrame = ShortArray(320)
        val linearFrame = ShortArray(320)
        checkNotNull(NativeHandsFreeEcho.create()).use { echo ->
            for (at in 0 until count step 320) {
                render.copyInto(renderFrame, 0, at, at + 320)
                mic.copyInto(micFrame, 0, at, at + 320)
                check(echo.process(renderFrame, micFrame, cleanFrame, linear = linearFrame))
                cleanFrame.copyInto(clean, at)
                linearFrame.copyInto(linear, at)
            }
        }
        output.writeInt(count)
        writePcm(clean)
        writePcm(linear)
        output.flush()
    }
}
