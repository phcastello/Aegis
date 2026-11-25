package com.aegis.node

/** Monotonic, bounded sampling; it never changes the microphone or detector cadence. */
internal class HandsFreeDetectorProfileSampler(private val intervalMs:Long) {
    private var lastAt:Long?=null
    init { require(intervalMs>0) }
    fun sampleDue(nowMs:Long):Boolean {
        require(nowMs>=0)
        val previous=lastAt
        if(previous!=null && nowMs>=previous && nowMs-previous<intervalMs)return false
        lastAt=nowMs
        return true
    }
}
