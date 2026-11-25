package com.aegis.node

import android.os.SystemClock
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

/** A bounded preparation handoff. The caller validates PCM, silence and generation.
 * No microphone, timer, executor, JSON, IO or wake-lock ownership lives here.
 */
internal class HandsFreeCaptureStartup(
    val mode:String,
    val controlGeneration:Int,
    val audioEpoch:Int,
    val deadlineElapsedMs:Long,
    private val elapsedClock:()->Long={SystemClock.elapsedRealtime()}
) {
    private enum class Phase { PENDING,READY,MEASURING,CANCELLED,FAILED }
    private data class Snapshot(val phase:Phase,val readyMs:Long?=null,val readyCpu:Long?=null,val runId:String?=null,val reason:String?=null)
    private val completion=CountDownLatch(1)
    private val requestedMs=readClock()
    private val current=AtomicReference(Snapshot(Phase.PENDING))
    init {
        require(mode in setOf("capture_only","detection") && deadlineElapsedMs>0){"capture_startup_config"}
        if(requestedMs==null)fail("clock_invalid") else if(requestedMs>=deadlineElapsedMs)fail("startup_timeout")
    }
    /** Called once on the audio thread after a positive, current, non-silenced read. */
    fun signalReady(threadCpuNanos:Long):Boolean {
        val before=current.get()
        if(before.phase!=Phase.PENDING)return false
        val now=validNow()?:return false
        val ready=Snapshot(Phase.READY,now,threadCpuNanos.takeIf{it>=0})
        if(!current.compareAndSet(before,ready))return false
        completion.countDown();return true
    }
    /** Blocks only the coordinator, bounded by the original lease's absolute deadline. */
    fun awaitReady():Boolean {
        while(true) {
            when(current.get().phase) {
                Phase.MEASURING->return true
                Phase.READY->return validNow()!=null && current.get().phase in setOf(Phase.READY,Phase.MEASURING)
                Phase.CANCELLED,Phase.FAILED->return false
                Phase.PENDING->{
                    val now=validNow()?:return false
                    try {
                        if(!completion.await(deadlineElapsedMs-now,TimeUnit.MILLISECONDS))expire("startup_timeout")
                    }catch(_:InterruptedException){cancel("startup_interrupted");Thread.currentThread().interrupt();return false}
                }
            }
        }
    }
    fun markMeasuring(runId:String):Boolean {
        require(runId.matches(Regex("[A-Za-z0-9_-]{1,128}"))){"capture_startup_run"}
        while(true) {
            val before=current.get()
            if(before.phase==Phase.MEASURING)return before.runId==runId
            if(before.phase!=Phase.READY || validNow()==null)return false
            if(current.compareAndSet(before,before.copy(phase=Phase.MEASURING,runId=runId)))return true
        }
    }
    /** Once measuring, the startup lease deadline no longer bounds the phase. */
    fun shouldMeasure():Boolean {
        val phase=current.get().phase
        if(phase==Phase.MEASURING)return true
        if(phase==Phase.PENDING || phase==Phase.READY)validNow()
        return false
    }
    fun measurementRunId():String?=current.get().let{if(it.phase==Phase.MEASURING)it.runId else null}
    fun readyWallMs():Long?=current.get().readyMs
    fun readyThreadCpuNanos():Long?=current.get().readyCpu
    fun preparationMs():Long?=current.get().readyMs?.let{ready->requestedMs?.let{ready-it}}
    fun state():String=current.get().phase.name.lowercase()
    fun failure():String?=current.get().reason
    fun cancel(reason:String){finish(Phase.CANCELLED,reason,true)}
    fun fail(reason:String){finish(Phase.FAILED,reason,true)}
    private fun expire(reason:String){finish(Phase.FAILED,reason,false)}
    private fun finish(phase:Phase,reason:String,includeMeasuring:Boolean) {
        while(true) {
            val before=current.get()
            if(before.phase in setOf(Phase.CANCELLED,Phase.FAILED) || !includeMeasuring && before.phase==Phase.MEASURING)return
            if(current.compareAndSet(before,before.copy(phase=phase,runId=null,reason=reason.take(80)))){completion.countDown();return}
        }
    }
    private fun readClock():Long?=try{elapsedClock().takeIf{it>=0}}catch(_:Exception){null}
    private fun validNow():Long? {
        val now=readClock()
        if(now==null || requestedMs==null || now<requestedMs){expire("clock_invalid");return null}
        if(now>=deadlineElapsedMs){expire("startup_timeout");return null}
        return now
    }
}
