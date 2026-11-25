package com.aegis.node

import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.File
import java.nio.file.Files

class NodeHandsFreeModelTest {
    private fun privateModel(strong:Boolean):ByteArray {
        // Private fixtures are supplied locally; weights are never copied into source/APK.
        val env=if(strong)"AEGIS_WAKEWORD_STRONG_MODEL_PATH" else "AEGIS_WAKEWORD_BASELINE_MODEL_PATH"
        val relative=if(strong)".cache/aegis-wakeword-tradeoff/frozen-v1/strong/aegis.int8.tflite"
            else ".cache/aegis-wakeword-tradeoff/frozen-v1/baseline/aegis.int8.tflite"
        val path=File(System.getenv(env)?:File(System.getProperty("user.home"),relative).path)
        assumeTrue("Private model fixture absent; supply $env for authorized-model integration",path.isFile)
        return path.readBytes()
    }
    private fun target(test:(File)->Unit) {
        val dir=Files.createTempDirectory("aegis-model-import-test").toFile()
        try{test(File(dir,"aegis.int8.tflite"))}finally{dir.deleteRecursively()}
    }
    @Test fun exactlyTwoAuthorizedHashesAndSizeAreAccepted() {
        assertEquals(HandsFreeModel.BASELINE,HandsFreeModel.authorizedIdentity(HandsFreeModel.SHA256,39552))
        assertEquals(HandsFreeModel.STRONG,HandsFreeModel.authorizedIdentity(HandsFreeModel.STRONG_SHA256,39552))
        assertThrows(IllegalArgumentException::class.java){HandsFreeModel.authorizedIdentity("0".repeat(64),39552)}
        assertThrows(IllegalArgumentException::class.java){HandsFreeModel.authorizedIdentity(HandsFreeModel.STRONG_SHA256,39551)}
    }
    @Test fun realPrivateWeightsIdentifyTheirActualModelAndRejectChangedBytes() {
        for(strong in listOf(false,true)) {
            val bytes=privateModel(strong)
            assertEquals(if(strong)HandsFreeModel.STRONG else HandsFreeModel.BASELINE,HandsFreeModel.identify(bytes))
            bytes[bytes.lastIndex]=(bytes.last().toInt() xor 1).toByte()
            assertThrows(IllegalArgumentException::class.java){HandsFreeModel.identify(bytes)}
        }
    }
    @Test fun realBaselineToStrongAndBackUsesAtomicReplacementAndCorrectRestartIdentity()=target{path->
        val baseline=privateModel(false);val strong=privateModel(true)
        var runtimeChecks=0;var pauseCalls=0
        val validator:(ByteArray)->Unit={bytes->HandsFreeModel.identify(bytes);runtimeChecks++}
        assertEquals(HandsFreeModel.BASELINE,HandsFreeModel.install(path,ByteArrayInputStream(baseline),validator))
        assertEquals(HandsFreeModel.STRONG,HandsFreeModel.install(path,ByteArrayInputStream(strong),validator){
            assertArrayEquals(baseline,path.readBytes());pauseCalls++
        })
        assertEquals(HandsFreeModel.STRONG,HandsFreeModel.identify(HandsFreeModel.read(path)!!))
        assertEquals(HandsFreeModel.BASELINE,HandsFreeModel.install(path,ByteArrayInputStream(baseline),validator){
            assertArrayEquals(strong,path.readBytes());pauseCalls++
        })
        assertEquals(HandsFreeModel.BASELINE,HandsFreeModel.identify(HandsFreeModel.read(path)!!))
        assertEquals(3,runtimeChecks);assertEquals(2,pauseCalls)
        assertEquals(listOf(path.name),path.parentFile!!.list()!!.toList())
    }
    @Test fun invalidHashNeverPausesOrDestroysInstalledModel()=target{path->
        val baseline=privateModel(false);path.writeBytes(baseline)
        assertThrows(IllegalArgumentException::class.java){
            HandsFreeModel.install(path,ByteArrayInputStream(ByteArray(HandsFreeModel.BYTES)),
                {fail("Invalid hash reached runtime")},{fail("Invalid hash paused capture")})
        }
        assertArrayEquals(baseline,path.readBytes())
    }
    @Test fun runtimeRejectionPreservesPreviousModelBeforePausing()=target{path->
        val baseline=privateModel(false);path.writeBytes(baseline)
        assertThrows(IllegalArgumentException::class.java){
            HandsFreeModel.install(path,ByteArrayInputStream(privateModel(true)),
                {throw IllegalArgumentException("model_runtime_incompatible")},{fail("Runtime rejection paused capture")})
        }
        assertArrayEquals(baseline,path.readBytes())
    }
    @Test fun activeCaptureRejectionPreservesPreviousModel()=target{path->
        val baseline=privateModel(false);path.writeBytes(baseline)
        assertThrows(IllegalStateException::class.java){
            HandsFreeModel.install(path,ByteArrayInputStream(privateModel(true)),{}){error("model_import_busy")}
        }
        assertArrayEquals(baseline,path.readBytes())
    }
    @Test fun oversizedOrTruncatedInputCannotReplacePreviousModel()=target{path->
        val baseline=privateModel(false);path.writeBytes(baseline)
        for(size in listOf(39551,39553)) {
            assertThrows(Exception::class.java){HandsFreeModel.install(path,ByteArrayInputStream(ByteArray(size)),{})}
            assertArrayEquals(baseline,path.readBytes())
        }
    }
    @Test fun modelMetadataContainsRealIdentityAndAbsentModelStaysUnknown() {
        val baseline=HandsFreeModel.metadata(HandsFreeModel.BASELINE);val strong=HandsFreeModel.metadata(HandsFreeModel.STRONG)
        assertEquals("Baseline",baseline.getString("modelName"));assertEquals(HandsFreeModel.SHA256,baseline.getString("modelSha256"))
        assertEquals("Strong — época 2",strong.getString("modelName"));assertEquals(HandsFreeModel.STRONG_SHA256,strong.getString("modelSha256"))
        assertNotEquals(baseline.getString("modelVersion"),strong.getString("modelVersion"))
        for(key in listOf("modelName","modelVersion","modelSha256"))assertTrue(HandsFreeModel.metadata(null).isNull(key))
    }
}
