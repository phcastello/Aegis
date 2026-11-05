package com.aegis.node

import android.content.Context
import java.io.File
import java.io.InputStream
import java.security.MessageDigest
import org.json.JSONObject

internal data class HandsFreeModelIdentity(val sha256:String,val version:String,val name:String)

internal object HandsFreeModel {
    const val SHA256="f9be8104b6a82dc91ed38febd1601e07eca0ef51f2ebbec4899e06f6e3aea514"
    const val STRONG_SHA256="1e7c686420f65d8da892f02c71d945f03a8dd528a1005de8c9c8482dce52ad2e"
    const val BYTES=39552
    val BASELINE=HandsFreeModelIdentity(SHA256,"aegis-mww-fleurs-dev-2026-10-31","Baseline")
    val STRONG=HandsFreeModelIdentity(STRONG_SHA256,"aegis-mww-strong-epoch-02-2026-11-05","Strong — época 2")
    private fun file(context:Context)=File(context.filesDir,"hands-free/aegis.int8.tflite")
    internal fun authorizedIdentity(hash:String,size:Int):HandsFreeModelIdentity {
        require(size==BYTES){"model_size"}
        return when(hash){SHA256->BASELINE;STRONG_SHA256->STRONG;else->throw IllegalArgumentException("model_integrity")}
    }
    internal fun identify(bytes:ByteArray):HandsFreeModelIdentity {
        val hash=MessageDigest.getInstance("SHA-256").digest(bytes).joinToString(""){"%02x".format(it)}
        return authorizedIdentity(hash,bytes.size)
    }
    internal fun metadata(identity:HandsFreeModelIdentity?):JSONObject=JSONObject()
        .put("modelName",identity?.name?:JSONObject.NULL)
        .put("modelVersion",identity?.version?:JSONObject.NULL)
        .put("modelSha256",identity?.sha256?:JSONObject.NULL)
    private fun validateRuntime(bytes:ByteArray) {
        val test=MicroWakeWordNative.create(bytes);require(test!=0L){"model_runtime_incompatible"}
        MicroWakeWordNative.destroy(test)
    }
    fun read(context:Context):ByteArray? {
        return read(file(context))
    }
    @Synchronized internal fun read(path:File):ByteArray? {
        if(!path.exists())return null
        val bytes=path.inputStream().use{it.readBytesBounded(BYTES)}
        try{identify(bytes);return bytes}catch(error:Throwable){bytes.fill(0);throw error}
    }
    fun import(context:Context,input:InputStream,beforeInstall:()->Unit={}):HandsFreeModelIdentity =
        install(file(context),input,beforeInstall=beforeInstall)
    /** Validation precedes both pausing capture and replacing the installed file. */
    internal fun install(target:File,input:InputStream,runtimeValidator:(ByteArray)->Unit=::validateRuntime,
        beforeInstall:()->Unit={}):HandsFreeModelIdentity {
        val bytes=input.use{it.readBytesBounded(BYTES)}
        try {
            val identity=identify(bytes);runtimeValidator(bytes);beforeInstall()
            synchronized(this) {
                val parent=checkNotNull(target.parentFile)
                check(parent.isDirectory || parent.mkdirs()){"model_storage"}
                val staging=File.createTempFile("model-import-",".tmp",parent)
                try {
                    staging.outputStream().use{it.write(bytes);it.flush();it.fd.sync()}
                    // Same-directory rename replaces only after the complete file is synced.
                    check(staging.renameTo(target)){"model_storage"}
                }finally{staging.delete()}
            }
            return identity
        }finally{bytes.fill(0)}
    }
}
