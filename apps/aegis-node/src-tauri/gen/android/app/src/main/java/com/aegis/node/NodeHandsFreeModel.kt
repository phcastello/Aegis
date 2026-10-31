package com.aegis.node

import android.content.Context
import java.io.File
import java.io.InputStream
import java.security.MessageDigest

internal object HandsFreeModel {
    const val SHA256="f9be8104b6a82dc91ed38febd1601e07eca0ef51f2ebbec4899e06f6e3aea514"
    const val BYTES=39552
    private fun file(context:Context)=File(context.filesDir,"hands-free/aegis.int8.tflite")
    private fun validate(bytes:ByteArray) {
        require(bytes.size==BYTES){"model_size"}
        val hash=MessageDigest.getInstance("SHA-256").digest(bytes).joinToString(""){"%02x".format(it)}
        require(hash==SHA256){"model_integrity"}
    }
    fun read(context:Context):ByteArray? {
        val path=file(context);if(!path.exists())return null
        return path.inputStream().use{it.readBytesBounded(BYTES)}.also{validate(it)}
    }
    fun import(context:Context,input:InputStream) {
        val bytes=input.use{it.readBytesBounded(BYTES)};validate(bytes)
        val test=MicroWakeWordNative.create(bytes);require(test!=0L){"model_runtime_incompatible"}
        MicroWakeWordNative.destroy(test)
        val target=file(context);target.parentFile!!.mkdirs()
        val staging=File(target.parentFile,"model-import.tmp")
        try {
            staging.outputStream().use{it.write(bytes);it.flush();it.fd.sync()}
            check(staging.renameTo(target)){"model_storage"}
        }finally{staging.delete();bytes.fill(0)}
    }
}
