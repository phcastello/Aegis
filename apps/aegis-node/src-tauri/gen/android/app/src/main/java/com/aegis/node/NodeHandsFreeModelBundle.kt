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
    private data class Asset(val bytes:Int,val sha256:String)
    private val assets=linkedMapOf(
        "aegis.int8.tflite" to Asset(39552,HandsFreeModel.STRONG_SHA256),
        "melspectrogram.onnx" to Asset(1087958,"ba2b0e0f8b7b875369a2c89cb13360ff53bac436f2895cced9f479fa65eb176f"),
        "embedding_model.onnx" to Asset(1326578,"70d164290c1d095d1d4ee149bc5e00543250a7316b59f31d056cff7bd3075c1f"),
        "aegis.onnx" to Asset(414743,"ec21cd554897f2f3cbb80962cfc2dd9ab32f4800692dfabf4e50ea2a1659da56"),
        "initial_features.f32le" to Asset(6144,"b4ee1271c05d64fed3032f8ddac4d9731c06e03e142052cf231858657e86df16"),
        "manifest.json" to Asset(1180,"a86e6baad12d12e29a9246693f8d2b0dfb930f95a012fc662b8a614d2efe5acc")
    )
    private data class DirectoryEntry(val bytes:Int,val crc:Long)
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
            val expected=assets[name]?:throw IllegalArgumentException("model_bundle_entry")
            require(!found.containsKey(name) && u32(cursor+24)==expected.bytes.toLong()){"model_bundle_entry"}
            val local=u32(cursor+42)
            require(offsets.add(local) && local<=start-30 && u32(local.toInt())==0x04034b50L){"model_bundle_zip"}
            val localNameSize=u16(local.toInt()+26);val localExtra=u16(local.toInt()+28)
            val data=local+30+localNameSize+localExtra
            require(data<=start && data+u32(cursor+20)<=start){"model_bundle_zip"}
            require(String(bytes,local.toInt()+30,localNameSize,Charsets.UTF_8)==name){"model_bundle_entry"}
            found[name]=DirectoryEntry(expected.bytes,u32(cursor+16))
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
        val decoded=linkedMapOf<String,ByteArray>()
        try {
            ZipInputStream(ByteArrayInputStream(bytes)).use{zip->
                while(true) {
                    val entry=zip.nextEntry?:break
                    val expected=assets[entry.name]?:throw IllegalArgumentException("model_bundle_entry")
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
            require(HandsFreeModel.authorizedIdentity(handsFreeModelSha256(primary),primary.size)==HandsFreeModel.STRONG){"model_bundle_primary"}
            val identity=HandsFreeModelIdentity(handsFreeModelSha256(bytes),VERSION,NAME)
            val verifier=HandsFreeVerifierAssets(decoded.getValue("melspectrogram.onnx"),decoded.getValue("embedding_model.onnx"),
                decoded.getValue("aegis.onnx"),decoded.getValue("initial_features.f32le"))
            decoded.getValue("manifest.json").fill(0)
            return HandsFreeModelPackage(identity,primary,verifier)
        }catch(error:Throwable){decoded.values.forEach{it.fill(0)};throw error}
    }
}
