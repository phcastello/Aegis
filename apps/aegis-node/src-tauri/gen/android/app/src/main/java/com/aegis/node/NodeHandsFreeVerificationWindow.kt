package com.aegis.node

internal data class HandsFreeVerificationWindow(val samples:ShortArray,val candidateSample:Int)

/** Retains the original wake decision while a bounded lookahead is captured on the same stream. */
internal class HandsFreeWakeCandidate(val decisionSample:Long,val blockOffset:Int,val scores:FloatArray) {
    init { require(decisionSample>=0 && blockOffset>=0) }
    var verifierScore:Float?=null
    fun ready(streamSamples:Long)=streamSamples>=decisionSample && streamSamples-decisionSample>=LOOKAHEAD_SAMPLES
    private fun copy(ring:HandsFreeRing,streamSamples:Long,start:Long,end:Long):ShortArray {
        val retainedStart=streamSamples-ring.size
        require(retainedStart>=0 && start>=retainedStart && end>=start && end<=streamSamples)
        return ShortArray((end-start).toInt()).also { ring.copyRangeTo(it,(start-retainedStart).toInt(),it.size) }
    }
    fun verificationWindow(ring:HandsFreeRing,streamSamples:Long,withLookahead:Boolean=true):HandsFreeVerificationWindow {
        require(if(withLookahead)ready(streamSamples) else streamSamples>=decisionSample)
        val begin=maxOf(0L,decisionSample-LOOKBACK_SAMPLES)
        val end=if(withLookahead)decisionSample+LOOKAHEAD_SAMPLES else decisionSample
        return HandsFreeVerificationWindow(copy(ring,streamSamples,begin,end),(decisionSample-begin).toInt())
    }
    fun context(ring:HandsFreeRing,streamSamples:Long):ShortArray =
        copy(ring,streamSamples,maxOf(0L,decisionSample-32000),minOf(streamSamples,decisionSample+3200))
    fun copyCommandTo(ring:HandsFreeRing,streamSamples:Long,destination:ShortArray):Int {
        val retainedStart=streamSamples-ring.size
        val length=streamSamples-decisionSample
        require(retainedStart>=0 && decisionSample>=retainedStart && length in 0..destination.size.toLong())
        return ring.copyRangeTo(destination,(decisionSample-retainedStart).toInt(),length.toInt())
    }
    companion object {
        const val LOOKBACK_SAMPLES=40000L
        const val LOOKAHEAD_SAMPLES=2560L
        // One extra blocking read can overshoot lookahead; do not evict its oldest reference PCM.
        const val RING_CAPACITY=40000+2560+3200
        const val THRESHOLD=0.5f
    }
}
