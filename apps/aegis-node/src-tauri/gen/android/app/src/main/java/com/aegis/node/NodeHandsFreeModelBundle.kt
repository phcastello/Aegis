package com.aegis.node

import java.io.ByteArrayInputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.zip.CRC32
import java.util.zip.ZipInputStream

internal fun handsFreeModelSha256(bytes:ByteArray)=MessageDigest.getInstance("SHA-256")
    .digest(bytes).joinToString(""){"%02x".format(it)}

/** A bounded private bundle. These pins authorize contents, never a manifest-selected model or policy. */
internal object HandsFreeModelBundle {
    const val MAX_BYTES=4*1024*1024
    const val VERSION="aegis-strong-oww-2026-11-16"
    const val NAME="Strong + verificador"
    const val RETRAINED_VERSION="aegis-strong-oww-head-v2-2026-11-24"
    const val RETRAINED_NAME="Strong + verificador treinado"
    const val RETRAINED_THRESHOLD=0.0692f
    const val RETRAINED_PRIMARY_VERSION="aegis-microwakeword-primario-v2-2026-11-24"
    const val RETRAINED_PRIMARY_NAME="microWakeWord retreinado + verificador v2"
    private data class Asset(val bytes:Int,val sha256:String)
    private val assets=linkedMapOf(
        "aegis.int8.tflite" to Asset(39552,HandsFreeModel.STRONG_SHA256),
        "melspectrogram.onnx" to Asset(1087958,"ba2b0e0f8b7b875369a2c89cb13360ff53bac436f2895cced9f479fa65eb176f"),
        "embedding_model.onnx" to Asset(1326578,"70d164290c1d095d1d4ee149bc5e00543250a7316b59f31d056cff7bd3075c1f"),
        "aegis.onnx" to Asset(414743,"ec21cd554897f2f3cbb80962cfc2dd9ab32f4800692dfabf4e50ea2a1659da56"),
        "initial_features.f32le" to Asset(6144,"b4ee1271c05d64fed3032f8ddac4d9731c06e03e142052cf231858657e86df16"),
        "manifest.json" to Asset(1180,"a86e6baad12d12e29a9246693f8d2b0dfb930f95a012fc662b8a614d2efe5acc")
    )
    private val retrainedManifest=Asset(1200,"67b256577d12829c0fea694bf29d6e4eb5e0c7773fda98d4db0a3c0c944e4471")
    private val retrainedKeywordModel=Asset(414743,"f8972e52ef82db7735132ff54a725e69c0462ebda2bff14446a612633aa46c8b")
    private val retrainedPrimaryManifest=Asset(1219,"aaed5c0d19f7544743745277fec7f67712161b3f53014bc2fb265af005bccefb")
    private val retrainedPrimaryModel=Asset(39552,"1efa802e2765a6ba276dbeaf0b171901c421570236c31332cf5d6ecc63dc5291")
    private data class Profile(
        val version:String,val name:String,val threshold:Float,
        val manifest:Asset,val primary:Asset,val classifier:Asset
    )
    private val profiles=listOf(
        Profile(VERSION,NAME,HandsFreeWakeCandidate.THRESHOLD,assets.getValue("manifest.json"),
            assets.getValue("aegis.int8.tflite"),assets.getValue("aegis.onnx")),
        Profile(RETRAINED_VERSION,RETRAINED_NAME,RETRAINED_THRESHOLD,retrainedManifest,
            assets.getValue("aegis.int8.tflite"),retrainedKeywordModel),
        Profile(RETRAINED_PRIMARY_VERSION,RETRAINED_PRIMARY_NAME,RETRAINED_THRESHOLD,retrainedPrimaryManifest,
            retrainedPrimaryModel,retrainedKeywordModel)
    )
    private data class DirectoryEntry(val asset:Asset,val crc:Long)
    private fun directory(bytes:ByteArray):Map<String,DirectoryEntry> {
        require(bytes.size in 22..MAX_BYTES){"model_bundle_size"}
        val view=ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        fun u16(at:Int):Int {require(at>=0 && at<=bytes.size-2){"model_bundle_zip"};return view.getShort(at).toInt() and 65535}
        fun u32(at:Int):Long {require(at>=0 && at<=bytes.size-4){"model_bundle_zip"};return view.getInt(at).toLong() and 0xffffffffL}
        // The exported format has no archive comment, Zip64 or multi-volume records.
        val end=bytes.size-22
        require(u32(0)==0x04034b50L && u32(end)==0x06054b50L){"model_bundle_zip"}
        require(u16(end+4)==0 && u16(end+6)==0 && u16(end+20)==0){"model_bundle_zip"}
        require(u16(end+8)==assets.size && u16(end+10)==assets.size){"model_bundle_entries"}
        val size=u32(end+12);val start=u32(end+16)
        require(start>0 && start+size==end.toLong()){"model_bundle_zip"}
        var cursor=start.toInt()
        val found=linkedMapOf<String,DirectoryEntry>()
        val offsets=mutableSetOf<Long>()
        repeat(assets.size) {
            require(cursor<=end-46 && u32(cursor)==0x02014b50L){"model_bundle_zip"}
            require((u16(cursor+8) and 1)==0 && u16(cursor+10) in setOf(0,8) && u16(cursor+34)==0){"model_bundle_zip"}
            val nameSize=u16(cursor+28);val extra=u16(cursor+30);val comment=u16(cursor+32)
            val next=cursor.toLong()+46+nameSize+extra+comment
            require(next<=end){"model_bundle_zip"}
            val name=String(bytes,cursor+46,nameSize,Charsets.UTF_8)
            val entrySize=u32(cursor+24)
            val expected=if(name=="manifest.json")profiles.singleOrNull{it.manifest.bytes.toLong()==entrySize}?.manifest
                else assets[name]
            require(expected!=null && !found.containsKey(name) && entrySize==expected.bytes.toLong()){"model_bundle_entry"}
            val local=u32(cursor+42)
            require(offsets.add(local) && local<=start-30 && u32(local.toInt())==0x04034b50L){"model_bundle_zip"}
            val localNameSize=u16(local.toInt()+26);val localExtra=u16(local.toInt()+28)
            val data=local+30+localNameSize+localExtra
            require(data<=start && data+u32(cursor+20)<=start){"model_bundle_zip"}
            require(String(bytes,local.toInt()+30,localNameSize,Charsets.UTF_8)==name){"model_bundle_entry"}
            found[name]=DirectoryEntry(expected,u32(cursor+16))
            cursor=next.toInt()
        }
        require(cursor==end && found.keys==assets.keys){"model_bundle_entries"}
        return found
    }
    private fun readEntry(zip:ZipInputStream,size:Int):ByteArray {
        val result=ByteArray(size)
        try {
            var offset=0
            while(offset<size) {
                val n=zip.read(result,offset,size-offset)
                require(n>=0){"model_bundle_truncated"}
                if(n==0)continue
                offset+=n
            }
            require(zip.read()==-1){"model_bundle_entry_size"}
            return result
        }catch(error:Throwable){result.fill(0);throw error}
    }
    internal fun decode(bytes:ByteArray):HandsFreeModelPackage {
        val directory=directory(bytes)
        val profile=profiles.single{it.manifest==directory.getValue("manifest.json").asset}
        val decoded=linkedMapOf<String,ByteArray>()
        try {
            ZipInputStream(ByteArrayInputStream(bytes)).use{zip->
                while(true) {
                    val entry=zip.nextEntry?:break
                    // The verified manifest and both wake models must match one pinned profile.
                    val expected=when(entry.name) {
                        "aegis.int8.tflite" -> profile.primary
                        "aegis.onnx" -> profile.classifier
                        else -> directory[entry.name]?.asset?:throw IllegalArgumentException("model_bundle_entry")
                    }
                    require(!entry.isDirectory && !decoded.containsKey(entry.name)){"model_bundle_entry"}
                    require(entry.size<0 || entry.size==expected.bytes.toLong()){"model_bundle_entry_size"}
                    val content=readEntry(zip,expected.bytes)
                    decoded[entry.name]=content
                    require(handsFreeModelSha256(content)==expected.sha256){"model_bundle_integrity"}
                    val crc=CRC32().apply{update(content)}.value
                    require(directory.getValue(entry.name).crc==crc){"model_bundle_crc"}
                    zip.closeEntry()
                }
            }
            require(decoded.keys==assets.keys){"model_bundle_entries"}
            val primary=decoded.getValue("aegis.int8.tflite")
            val identity=HandsFreeModelIdentity(handsFreeModelSha256(bytes),profile.version,profile.name)
            val verifier=HandsFreeVerifierAssets(decoded.getValue("melspectrogram.onnx"),decoded.getValue("embedding_model.onnx"),
                decoded.getValue("aegis.onnx"),decoded.getValue("initial_features.f32le"),profile.threshold)
            decoded.getValue("manifest.json").fill(0)
            return HandsFreeModelPackage(identity,primary,verifier)
        }catch(error:Throwable){decoded.values.forEach{it.fill(0)};throw error}
    }
}
