package com.aegis.node

import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.Properties
import kotlin.math.abs

class NodeHandsFreeVerifierTest {
    private fun floats(bytes:ByteArray):FloatArray {
        require(bytes.size%4==0)
        val buffer=ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).asFloatBuffer()
        return FloatArray(buffer.remaining()).also { buffer.get(it) }
    }
    private fun pcm(file:File):ShortArray {
        val buffer=ByteBuffer.wrap(file.readBytes()).order(ByteOrder.LITTLE_ENDIAN).asShortBuffer()
        return ShortArray(buffer.remaining()).also { buffer.get(it) }
    }
    private fun fixtureRoot():File {
        val root=System.getenv("AEGIS_WAKE_VERIFIER_FIXTURES")?.let(::File)
        assumeTrue("Private verifier fixtures absent; supply AEGIS_WAKE_VERIFIER_FIXTURES",root?.isDirectory==true)
        return root!!
    }
    private fun verifier(root:File):HandsFreeWakeVerifier {
        val models=System.getenv("AEGIS_WAKE_VERIFIER_MODELS")?.let(::File)?:File(root.parentFile,"package-v1/model")
        assertTrue("Private verifier models missing",models.isDirectory)
        return HandsFreeWakeVerifier(File(models,"melspectrogram.onnx").readBytes(),
            File(models,"embedding_model.onnx").readBytes(),File(models,"aegis.onnx").readBytes(),
            File(models,"initial_features.f32le").readBytes())
    }
    private fun properties(root:File,name:String)=Properties().apply {
        File(root,"$name.properties").inputStream().use { load(it) }
    }

    @Test fun malformedSeedAndWindowAreRejectedBeforeInference() {
        for(size in listOf(0,HandsFreeWakeVerifier.SEED_BYTES-1,HandsFreeWakeVerifier.SEED_BYTES+1))
            assertThrows(IllegalArgumentException::class.java){HandsFreeWakeVerifier.decodeInitialState(ByteArray(size))}
        val nonfinite=ByteBuffer.allocate(HandsFreeWakeVerifier.SEED_BYTES).order(ByteOrder.LITTLE_ENDIAN)
            .putFloat(Float.NaN).array()
        assertThrows(IllegalArgumentException::class.java){HandsFreeWakeVerifier.decodeInitialState(nonfinite)}
        assertEquals(1536,HandsFreeWakeVerifier.decodeInitialState(ByteArray(HandsFreeWakeVerifier.SEED_BYTES)).size)
        for(count in listOf(-1,42561))
            assertThrows(IllegalArgumentException::class.java){HandsFreeWakeVerifier.validateWindow(50000,count,0)}
        assertThrows(IllegalArgumentException::class.java){HandsFreeWakeVerifier.validateWindow(10,11,0)}
        assertThrows(IllegalArgumentException::class.java){HandsFreeWakeVerifier.validateWindow(10,10,11)}
        HandsFreeWakeVerifier.validateWindow(42560,42560,40000)
    }

    @Test fun scoreUsesOnlyTheBoundedCandidateIntervalAndRejectsNonfiniteOutput() {
        val values=FloatArray(34)
        values[24]=1f //32000samples, before candidate-lookback
        values[25]=.3f
        values[32]=.4f
        values[33]=.9f //43520samples, after candidate+lookahead
        assertEquals(.4f,HandsFreeWakeVerifier.maximumScore(values,40000),0f)
        values[0]=Float.NaN
        assertThrows(IllegalArgumentException::class.java){HandsFreeWakeVerifier.maximumScore(values,40000)}
        assertEquals(0f,HandsFreeWakeVerifier.maximumScore(floatArrayOf(),0),0f)
    }

    @Test fun privateRecordedWindowsMatchUpstreamFramesOnTheProductionOrtRuntime() {
        val root=fixtureRoot()
        val names=File(root,"cases.txt").readLines().filter(String::isNotBlank)
        assertTrue("Full verifier corpus must include recorded candidates and controls",names.size>100)
        verifier(root).use { detector->
            for(name in names) {
                val samples=pcm(File(root,"$name.pcm"));val original=samples.copyOf()
                val details=properties(root,name)
                assertEquals(samples.size,details.getProperty("count").toInt())
                val expected=floats(File(root,"$name.scores.f32le").readBytes())
                val actual=detector.frameScores(samples)
                assertEquals("$name: incomplete tail must not be zero padded",expected.size,actual.size)
                for(i in expected.indices)
                    assertTrue("$name: frame$i drift ${abs(expected[i]-actual[i])}",abs(expected[i]-actual[i])<=0.0001f)
                val candidate=details.getProperty("candidateSample").toInt()
                val expectedMaximum=HandsFreeWakeVerifier.maximumScore(expected,candidate)
                val actualMaximum=detector.score(samples,samples.size,candidate)
                assertEquals("$name: verification decision",expectedMaximum>=HandsFreeWakeVerifier.THRESHOLD,
                    actualMaximum>=HandsFreeWakeVerifier.THRESHOLD)
                assertArrayEquals("$name: input PCM must remain unchanged",original,samples)
            }
        }
    }

    @Test fun cancellationAndPreviousCandidatesCannotContaminateTheNextVerification() {
        val root=fixtureRoot();val names=File(root,"cases.txt").readLines().filter(String::isNotBlank)
        val samples=pcm(File(root,"${names.first()}.pcm"));val candidate=properties(root,names.first()).getProperty("candidateSample").toInt()
        val detector=verifier(root)
        try {
            val expected=detector.score(samples,samples.size,candidate)
            val noise=pcm(File(root,"control-noise.pcm"))
            detector.score(noise,noise.size,40000)
            assertEquals(expected,detector.score(samples,samples.size,candidate),0f)
            var checks=0
            assertThrows(InterruptedException::class.java){detector.score(samples,samples.size,candidate){++checks>=3}}
            assertEquals(expected,detector.score(samples,samples.size,candidate),0f)
        } finally { detector.close() }
        detector.close()
        assertThrows(IllegalStateException::class.java){detector.score(samples,samples.size,candidate)}
    }
}
