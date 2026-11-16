package com.aegis.node

import android.content.Context
import java.io.File
import java.io.InputStream
import org.json.JSONObject

internal data class HandsFreeModelIdentity(val sha256:String,val version:String,val name:String)

internal class HandsFreeVerifierAssets(
    val melModel:ByteArray,
    val embeddingModel:ByteArray,
    val keywordModel:ByteArray,
    val seedEmbedding:ByteArray
):AutoCloseable {
    override fun close(){melModel.fill(0);embeddingModel.fill(0);keywordModel.fill(0);seedEmbedding.fill(0)}
}

internal class HandsFreeModelPackage(
    val identity:HandsFreeModelIdentity,
    val wakeModel:ByteArray,
    val verifier:HandsFreeVerifierAssets?=null
):AutoCloseable {
    override fun close(){wakeModel.fill(0);verifier?.close()}
}

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
    internal fun decode(bytes:ByteArray):HandsFreeModelPackage {
        if(bytes.size==BYTES) {
            val identity=authorizedIdentity(handsFreeModelSha256(bytes),bytes.size)
            return HandsFreeModelPackage(identity,bytes.copyOf())
        }
        return HandsFreeModelBundle.decode(bytes)
    }
    internal fun identify(bytes:ByteArray):HandsFreeModelIdentity=decode(bytes).use{it.identity}
    internal fun metadata(identity:HandsFreeModelIdentity?):JSONObject=JSONObject()
        .put("modelName",identity?.name?:JSONObject.NULL)
        .put("modelVersion",identity?.version?:JSONObject.NULL)
        .put("modelSha256",identity?.sha256?:JSONObject.NULL)
    private fun validateRuntime(bytes:ByteArray) {
        val test=MicroWakeWordNative.create(bytes);require(test!=0L){"model_runtime_incompatible"}
        MicroWakeWordNative.destroy(test)
    }
    private fun validateVerifierRuntime(assets:HandsFreeVerifierAssets) {
        HandsFreeWakeVerifier(assets.melModel,assets.embeddingModel,assets.keywordModel,assets.seedEmbedding).use{it.validate()}
    }
    fun readPackage(context:Context):HandsFreeModelPackage?=readPackage(file(context))
    @Synchronized internal fun readPackage(path:File):HandsFreeModelPackage? {
        if(!path.exists())return null
        val bytes=path.inputStream().use{it.readBytesBounded(HandsFreeModelBundle.MAX_BYTES)}
        return try{decode(bytes)}finally{bytes.fill(0)}
    }
    /** Legacy callers receive only primary wake weights; package identity uses readPackage. */
    fun read(context:Context):ByteArray?=read(file(context))
    @Synchronized internal fun read(path:File):ByteArray?=readPackage(path)?.use{it.wakeModel.copyOf()}
    fun import(context:Context,input:InputStream,beforeInstall:()->Unit={}):HandsFreeModelIdentity =
        installPackage(file(context),input,beforeInstall=beforeInstall)
    /** Preserve the raw import hook and trailing-lambda contract used by existing callers. */
    internal fun install(target:File,input:InputStream,runtimeValidator:(ByteArray)->Unit=::validateRuntime,
        beforeInstall:()->Unit={}):HandsFreeModelIdentity =
        installPackage(target,input,wakeRuntimeValidator=runtimeValidator,beforeInstall=beforeInstall)
    /** Validate both engines before pausing; one synced rename installs or rolls back the whole package. */
    internal fun installPackage(target:File,input:InputStream,
        wakeRuntimeValidator:(ByteArray)->Unit=::validateRuntime,
        verifierRuntimeValidator:(HandsFreeVerifierAssets)->Unit=::validateVerifierRuntime,
        beforeInstall:()->Unit={}):HandsFreeModelIdentity {
        val bytes=input.use{it.readBytesBounded(HandsFreeModelBundle.MAX_BYTES)}
        try {
            return decode(bytes).use { model->
                wakeRuntimeValidator(model.wakeModel)
                model.verifier?.let{verifierRuntimeValidator(it)}
                beforeInstall()
                synchronized(this) {
                    val parent=checkNotNull(target.parentFile)
                    check(parent.isDirectory || parent.mkdirs()){"model_storage"}
                    val staging=File.createTempFile("model-import-",".tmp",parent)
                    try {
                        staging.outputStream().use{it.write(bytes);it.flush();it.fd.sync()}
                        check(staging.renameTo(target)){"model_storage"}
                    }finally{staging.delete()}
                }
                model.identity
            }
        }finally{bytes.fill(0)}
    }
}
