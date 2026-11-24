package com.aegis.node

import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.nio.file.Files
import java.util.zip.CRC32
import java.util.zip.ZipInputStream

class NodeHandsFreeModelBundleTest {
    private fun bundle():ByteArray {
        val path=File(System.getenv("AEGIS_WAKEWORD_CASCADE_BUNDLE_PATH")
            ?: "/var/tmp/aegis-wakeword-verifiers-20261116/package-v1/aegis-strong-verifier-20261116.zip")
        assumeTrue("Private cascade fixture absent; supply AEGIS_WAKEWORD_CASCADE_BUNDLE_PATH",path.isFile)
        return path.readBytes()
    }
    private fun retrainedBundle():ByteArray {
        val path=System.getenv("AEGIS_WAKEWORD_RETRAINED_BUNDLE_PATH")?.let(::File)
        assumeTrue("Private retrained fixture absent; supply AEGIS_WAKEWORD_RETRAINED_BUNDLE_PATH",path?.isFile==true)
        return path!!.readBytes()
    }
    private fun retrainedPrimaryBundle():ByteArray {
        val path=System.getenv("AEGIS_WAKEWORD_RETRAINED_PRIMARY_BUNDLE_PATH")?.let(::File)
        assumeTrue("Private retrained primary fixture absent; supply AEGIS_WAKEWORD_RETRAINED_PRIMARY_BUNDLE_PATH",path?.isFile==true)
        return path!!.readBytes()
    }
    private fun baseline():ByteArray {
        val path=File(System.getenv("AEGIS_WAKEWORD_BASELINE_MODEL_PATH")
            ?: File(System.getProperty("user.home"),".cache/aegis-wakeword-tradeoff/frozen-v1/baseline/aegis.int8.tflite").path)
        assumeTrue("Private baseline fixture absent; supply AEGIS_WAKEWORD_BASELINE_MODEL_PATH",path.isFile)
        return path.readBytes()
    }
    private fun target(test:(File)->Unit) {
        val dir=Files.createTempDirectory("aegis-bundle-import-test").toFile()
        try{test(File(dir,"aegis.int8.tflite"))}finally{dir.deleteRecursively()}
    }
    private fun entries(bytes:ByteArray):List<Pair<String,ByteArray>> {
        val result=mutableListOf<Pair<String,ByteArray>>()
        ZipInputStream(ByteArrayInputStream(bytes)).use{zip->
            while(true){val entry=zip.nextEntry?:break;result.add(entry.name to zip.readBytes());zip.closeEntry()}
        }
        return result
    }
    /** Test archives include a real central directory, including deliberately duplicated names. */
    private fun storedZip(entries:List<Pair<String,ByteArray>>):ByteArray {
        val out=ByteArrayOutputStream()
        fun u16(n:Int){out.write(n and 255);out.write((n ushr 8) and 255)}
        fun u32(n:Int){u16(n and 65535);u16(n ushr 16)}
        data class Item(val name:ByteArray,val body:ByteArray,val offset:Int,val crc:Int)
        val items=entries.map{(name,body)->
            val item=Item(name.toByteArray(Charsets.UTF_8),body,out.size(),CRC32().apply{update(body)}.value.toInt())
            u32(0x04034b50);u16(20);u16(0x800);u16(0);u16(0);u16(0);u32(item.crc)
            u32(body.size);u32(body.size);u16(item.name.size);u16(0);out.write(item.name);out.write(body)
            item
        }
        val start=out.size()
        for(item in items) {
            u32(0x02014b50);u16(20);u16(20);u16(0x800);u16(0);u16(0);u16(0);u32(item.crc)
            u32(item.body.size);u32(item.body.size);u16(item.name.size);u16(0);u16(0);u16(0);u16(0);u32(0);u32(item.offset)
            out.write(item.name)
        }
        val size=out.size()-start
        u32(0x06054b50);u16(0);u16(0);u16(items.size);u16(items.size);u32(size);u32(start);u16(0)
        return out.toByteArray()
    }
    @Test fun realPrivateBundleHasSeparateIdentityAndOwnedZeroisableArrays()=target{path->
        val encoded=bundle();val encodedHash=handsFreeModelSha256(encoded)
        assertEquals(HandsFreeModelBundle.VERSION,HandsFreeModel.identify(encoded).version)
        val model=HandsFreeModel.decode(encoded)
        val verifier=checkNotNull(model.verifier)
        assertEquals(encodedHash,model.identity.sha256)
        assertEquals("Strong + verificador",model.identity.name)
        assertArrayEquals(model.wakeModel,entries(encoded).single{it.first=="aegis.int8.tflite"}.second)
        assertEquals(HandsFreeModel.STRONG_SHA256,handsFreeModelSha256(model.wakeModel))
        assertEquals(1087958,verifier.melModel.size);assertEquals(1326578,verifier.embeddingModel.size)
        assertEquals(414743,verifier.keywordModel.size);assertEquals(6144,verifier.seedEmbedding.size)
        path.writeBytes(encoded)
        val legacy=checkNotNull(HandsFreeModel.read(path))
        model.close()
        assertTrue(model.wakeModel.all{it==0.toByte()})
        for(bytes in listOf(verifier.melModel,verifier.embeddingModel,verifier.keywordModel,verifier.seedEmbedding))
            assertTrue(bytes.all{it==0.toByte()})
        assertEquals(encodedHash,handsFreeModelSha256(encoded))
        assertEquals(HandsFreeModel.STRONG_SHA256,handsFreeModelSha256(legacy))
        HandsFreeModel.readPackage(path)!!.use{assertEquals(encodedHash,it.identity.sha256)}
    }
    @Test fun completeValidatedRepackingKeepsContentsButRecordsItsActualArchiveHash() {
        val encoded=bundle();val repacked=storedZip(entries(encoded))
        HandsFreeModel.decode(repacked).use {
            assertEquals(HandsFreeModelBundle.VERSION,it.identity.version)
            assertEquals(handsFreeModelSha256(repacked),it.identity.sha256)
            assertNotEquals(handsFreeModelSha256(encoded),it.identity.sha256)
        }
    }
    @Test fun bothEnginesValidateBeforePauseAndRawRollbackReplacesTheWholeBundle()=target{path->
        val before=baseline();val encoded=bundle();path.writeBytes(before)
        val order=mutableListOf<String>()
        val result=HandsFreeModel.installPackage(path,ByteArrayInputStream(encoded),
            wakeRuntimeValidator={assertEquals(HandsFreeModel.STRONG,HandsFreeModel.identify(it));assertArrayEquals(before,path.readBytes());order.add("wake")},
            verifierRuntimeValidator={assertEquals(6144,it.seedEmbedding.size);assertArrayEquals(before,path.readBytes());order.add("verifier")},
            beforeInstall={assertEquals(listOf("wake","verifier"),order);assertArrayEquals(before,path.readBytes());order.add("pause")})
        assertEquals(HandsFreeModelBundle.VERSION,result.version)
        assertEquals(listOf("wake","verifier","pause"),order)
        assertArrayEquals(encoded,path.readBytes())
        assertEquals(HandsFreeModel.BASELINE,HandsFreeModel.install(path,ByteArrayInputStream(before),{}))
        HandsFreeModel.readPackage(path)!!.use{assertEquals(HandsFreeModel.BASELINE,it.identity);assertNull(it.verifier)}
        assertArrayEquals(before,path.readBytes())
        assertEquals(listOf(path.name),path.parentFile!!.list()!!.toList())
    }
    @Test fun retrainedPackagePinsClassifierAndManifestTogetherAndPreservesLegacyImport()=target{path->
        val oldBytes=bundle()
        val newBytes=retrainedBundle()
        val oldEntries=entries(oldBytes).toMap()
        val newEntries=entries(newBytes).toMap()
        assertEquals(oldEntries.keys,newEntries.keys)
        for(name in oldEntries.keys - setOf("manifest.json","aegis.onnx"))
            assertArrayEquals(oldEntries.getValue(name),newEntries.getValue(name))
        assertFalse(oldEntries.getValue("aegis.onnx").contentEquals(newEntries.getValue("aegis.onnx")))
        HandsFreeModel.decode(oldBytes).use{assertEquals(.5f,it.verifier!!.threshold,0f)}
        val identity=HandsFreeModel.installPackage(path,ByteArrayInputStream(newBytes),
            wakeRuntimeValidator={assertEquals(HandsFreeModel.STRONG,HandsFreeModel.identify(it))},
            verifierRuntimeValidator={assets->
                assertEquals(.0692f,assets.threshold,0f)
                HandsFreeWakeVerifier(assets.melModel,assets.embeddingModel,assets.keywordModel,
                    assets.seedEmbedding,assets.threshold).use{it.validate()}
            })
        assertEquals(HandsFreeModelBundle.RETRAINED_VERSION,identity.version)
        assertEquals(handsFreeModelSha256(newBytes),identity.sha256)
        HandsFreeModel.readPackage(path)!!.use{assertEquals(.0692f,it.verifier!!.threshold,0f)}
        for((base,other) in listOf(newEntries to oldEntries,oldEntries to newEntries)) {
            val crossed=base.map{(name,body)->name to if(name=="aegis.onnx")other.getValue(name) else body}
            assertThrows(IllegalArgumentException::class.java){HandsFreeModel.identify(storedZip(crossed))}
        }
        val altered=entries(newBytes).map{(name,body)->
            name to if(name=="manifest.json")body.copyOf().also{it[0]='!'.code.toByte()} else body
        }
        assertThrows(IllegalArgumentException::class.java){HandsFreeModel.identify(storedZip(altered))}
        assertArrayEquals(newBytes,path.readBytes())
    }

    @Test fun retrainedPrimaryPackagePinsBothModelsAndKeepsOriginalVerifierV2()=target{path->
        val previous=retrainedBundle()
        val candidate=retrainedPrimaryBundle()
        val previousEntries=entries(previous).toMap()
        val candidateEntries=entries(candidate).toMap()
        assertEquals(previousEntries.keys,candidateEntries.keys)
        for(name in previousEntries.keys - setOf("manifest.json","aegis.int8.tflite"))
            assertArrayEquals(previousEntries.getValue(name),candidateEntries.getValue(name))
        val primary=candidateEntries.getValue("aegis.int8.tflite")
        assertFalse(previousEntries.getValue("aegis.int8.tflite").contentEquals(primary))
        assertThrows(IllegalArgumentException::class.java){HandsFreeModel.identify(primary)}
        val identity=HandsFreeModel.installPackage(path,ByteArrayInputStream(candidate),
            wakeRuntimeValidator={assertArrayEquals(primary,it)},
            verifierRuntimeValidator={assets->
                assertEquals(.0692f,assets.threshold,0f)
                assertArrayEquals(previousEntries.getValue("aegis.onnx"),assets.keywordModel)
            })
        assertEquals(HandsFreeModelBundle.RETRAINED_PRIMARY_VERSION,identity.version)
        assertEquals(handsFreeModelSha256(candidate),identity.sha256)
        for((base,other) in listOf(candidateEntries to previousEntries,previousEntries to candidateEntries)) {
            val crossed=base.map{(name,body)->name to if(name=="aegis.int8.tflite")other.getValue(name) else body}
            assertThrows(IllegalArgumentException::class.java){HandsFreeModel.identify(storedZip(crossed))}
        }
        HandsFreeModel.readPackage(path)!!.use{assertEquals(identity,it.identity);assertArrayEquals(primary,it.wakeModel)}
        assertEquals(HandsFreeModelBundle.RETRAINED_VERSION,HandsFreeModel.installPackage(path,
            ByteArrayInputStream(previous),wakeRuntimeValidator={},verifierRuntimeValidator={}).version)
        assertArrayEquals(previous,path.readBytes())
    }

    @Test fun verifierRejectionKeepsInstalledBytesAndClearsRejectedAssetsBeforePause()=target{path->
        val before=baseline();path.writeBytes(before)
        var rejected:HandsFreeVerifierAssets?=null
        var rejectedWake:ByteArray?=null
        assertThrows(IllegalArgumentException::class.java) {
            HandsFreeModel.installPackage(path,ByteArrayInputStream(bundle()),
                wakeRuntimeValidator={rejectedWake=it},
                verifierRuntimeValidator={rejected=it;throw IllegalArgumentException("verifier_runtime_incompatible")},
                beforeInstall={fail("Rejected verifier paused capture")})
        }
        assertArrayEquals(before,path.readBytes())
        assertTrue(checkNotNull(rejectedWake).all{it==0.toByte()})
        val assets=checkNotNull(rejected)
        for(bytes in listOf(assets.melModel,assets.embeddingModel,assets.keywordModel,assets.seedEmbedding))
            assertTrue(bytes.all{it==0.toByte()})
        assertEquals(listOf(path.name),path.parentFile!!.list()!!.toList())
    }
    @Test fun malformedBundlesNeverReachEitherEngineOrPauseAndKeepInstalledModel()=target{path->
        val before=baseline();val encoded=bundle();val original=entries(encoded);path.writeBytes(before)
        val damaged=original.map{(name,bytes)->name to bytes.copyOf().also{if(name=="aegis.onnx")it[0]=(it[0].toInt() xor 1).toByte()}}
        val manifests=original.map{(name,bytes)->name to if(name=="manifest.json")bytes.copyOf().also{it[0]='!'.code.toByte()} else bytes}
        val cases=listOf(
            encoded.copyOf(encoded.size-1),
            encoded.copyOf(encoded.size-22),
            storedZip(original.dropLast(1)),
            storedZip(original+("unexpected.bin" to byteArrayOf(1))),
            storedZip(original.map{(name,bytes)->(if(name=="manifest.json")"../manifest.json" else name) to bytes}),
            storedZip(original.map{(name,bytes)->(if(name=="manifest.json")"aegis.onnx" else name) to bytes}),
            storedZip(original.map{(name,bytes)->name to if(name=="initial_features.f32le")bytes+0.toByte() else bytes}),
            storedZip(damaged),
            storedZip(manifests),
            ByteArray(HandsFreeModelBundle.MAX_BYTES+1)
        )
        for(bytes in cases) {
            assertThrows(Exception::class.java) {
                HandsFreeModel.installPackage(path,ByteArrayInputStream(bytes),
                    wakeRuntimeValidator={fail("Invalid bundle reached wake runtime")},
                    verifierRuntimeValidator={fail("Invalid bundle reached verifier runtime")},
                    beforeInstall={fail("Invalid bundle paused capture")})
            }
            assertArrayEquals(before,path.readBytes())
            assertEquals(listOf(path.name),path.parentFile!!.list()!!.toList())
        }
    }
}
