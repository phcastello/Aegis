package com.aegis.node

import kotlin.math.PI
import kotlin.math.log10
import kotlin.math.roundToInt
import kotlin.math.sin
import kotlin.math.sqrt
import kotlin.random.Random

/** Synthetic room replay through the same JNI and Kotlin API used in the APK.
 * This verifies acoustic processing/lifecycle, not a speech interruption policy. */
fun main() {
    val processor = checkNotNull(NativeHandsFreeEcho.create()) { "AEC3 native library unavailable" }
    val rng = Random(2405)
    val samples = 16_000 * 4
    val render = ShortArray(samples) { i ->
        // Broadband, time-varying render signal for adaptive filter convergence.
        val envelope = 0.75 + 0.25 * sin(2 * PI * 3.0 * i / 16_000)
        ((rng.nextInt(-8_000, 8_001) + 1500 * sin(i * 2 * PI * 137 / 16_000)) * envelope)
            .roundToInt().toShort()
    }
    val delayedEcho = ShortArray(samples) { i ->
        val direct = if (i >= 240) render[i - 240] * 0.50 else 0.0
        val reflection = if (i >= 425) render[i - 425] * 0.22 else 0.0
        (direct + reflection).roundToInt().toShort()
    }
    val output = ShortArray(320)
    val linear = ShortArray(320)
    fun ShortArray.energy(): Double = sumOf { it.toDouble() * it }
    var echoEnergy = 0.0
    var suppressedEnergy = 0.0
    var linearEnergy = 0.0
    processor.use { echo ->
        for (at in render.indices step 320) {
            val play = render.copyOfRange(at, at + 320)
            val mic = delayedEcho.copyOfRange(at, at + 320)
            val expectedRender = play.copyOf()
            val expectedMic = mic.copyOf()
            check(echo.process(play, mic, output, linear = linear)) { "AEC processing failed at sample $at" }
            check(play.contentEquals(expectedRender) && mic.contentEquals(expectedMic))
            if (at >= 2 * 16_000) {
                echoEnergy += mic.energy()
                suppressedEnergy += output.energy()
                linearEnergy += linear.energy()
            }
        }
        val suppressionDb = 10 * log10(echoEnergy / maxOf(1.0, suppressedEnergy))
        val linearDb = 10 * log10(echoEnergy / maxOf(1.0, linearEnergy))
        check(suppressionDb >= 6.0) { "Insufficient suppression: $suppressionDb dB" }
        println("Synthetic direct/reflected echo: AEC residual suppression $suppressionDb dB; linear $linearDb dB")
        check(echo.reset())
        val silence = ShortArray(160)
        val independent = ShortArray(160) { i -> (4000 * sin(i * 2 * PI * 200.0 / 16_000)).roundToInt().toShort() }
        val nearOutput = ShortArray(160)
        val nearLinear = ShortArray(160)
        var keep = 0.0
        var keepLinear = 0.0
        repeat(30) { frame ->
            check(echo.process(silence, independent, nearOutput, linear = nearLinear))
            if (frame >= 20) {
                keep += nearOutput.energy()
                keepLinear += nearLinear.energy()
            }
        }
        val input = independent.energy() * 10
        check(keep > input * 0.10 && keepLinear > input * 0.10) {
            "Independent voice lost when the playback reference is quiet"
        }
        println("Quiet render: output RMS ratio " + sqrt(keep / input) + ", linear " + sqrt(keepLinear / input))
        check(runCatching { echo.process(silence, independent, independent) }.isFailure)
        check(runCatching { echo.process(silence, independent, ShortArray(80)) }.isFailure)
        check(runCatching { echo.process(silence, independent, nearOutput, delayMs = 501) }.isFailure)
        // Optional linear output must not be required by ordinary callers.
        check(echo.process(silence, independent, nearOutput))
    }
    processor.close()
    output.fill(7)
    linear.fill(-7)
    check(!processor.process(ShortArray(320), ShortArray(320), output, linear = linear))
    check(output.all { it == 0.toShort() } && linear.all { it == 0.toShort() })
    check(!processor.reset())
    println("AEC3 JNI synthetic replay, 10/20-ms boundaries, reset, validation, and close passed")
}
