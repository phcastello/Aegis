package com.aegis.node

import java.util.concurrent.Callable
import java.util.concurrent.ExecutorService
import java.util.concurrent.Future
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.atomic.AtomicBoolean

/** One bounded asynchronous candidate, while the capture thread keeps reading.
 * Only the verifier copy crosses to the worker. The original decision and all
 * command PCM received since it stay owned by the capture thread until accepted.
 */
internal class HandsFreePendingWakeVerification(
    val candidate:HandsFreeWakeCandidate,
    ring:HandsFreeRing,
    streamSamples:Long,
    private val executor:ExecutorService,
    private val startedAtMs:Long,
    private val clock:()->Long,
    verify:(HandsFreeVerificationWindow,()->Boolean)->Float,
    private val timeoutMs:Long=2000L
):AutoCloseable {
    enum class Status { WAITING,ACCEPTED,REJECTED,TIMED_OUT,CANCELLED,FAILED,LIMIT_EXCEEDED }
    data class Outcome(val status:Status,val score:Float?=null)
    private data class Completed(val score:Float,val finishedAtMs:Long)
    private val stopped=AtomicBoolean(false)
    // Claims ownership of the worker's PCM: close-before-start or worker-finally,
    // never both concurrently. Future.cancel alone does not prove the worker stopped.
    private val windowClaimed=AtomicBoolean(false)
    private val window=candidate.verificationWindow(ring,streamSamples)
    private val prefix=run {
        val context=candidate.context(ring,streamSamples)
        try { context.copyOf(minOf(candidate.decisionSample,32000L).toInt()) }
        finally { context.fill(0) }
    }
    private val command=ShortArray(320000)
    private var length=candidate.copyCommandTo(ring,streamSamples,command)
    private var terminal:Outcome?=null
    private val future:Future<Completed>

    init {
        require(timeoutMs in 1..5000 && startedAtMs>=0){"wake_verifier_invalid_deadline"}
        try {
            future=executor.submit(Callable {
                if(!windowClaimed.compareAndSet(false,true))throw InterruptedException("wake_verifier_cancelled")
                try {
                    if(stopped.get())throw InterruptedException("wake_verifier_cancelled")
                    val score=verify(window){stopped.get() || Thread.currentThread().isInterrupted}
                    require(score.isFinite() && score in 0f..1f){"wake_verifier_invalid_score"}
                    Completed(score,clock())
                } finally { window.samples.fill(0) }
            })
        } catch(error:Throwable) {
            stopped.set(true);window.samples.fill(0);prefix.fill(0);command.fill(0)
            throw error
        }
    }

    /** Append every next microphone read before polling the result. */
    fun append(samples:ShortArray,count:Int):Boolean {
        require(count in 0..samples.size)
        if(stopped.get() || terminal!=null)return false
        if(count>command.size-length) {
            terminal=Outcome(Status.LIMIT_EXCEEDED)
            cancelWorker();prefix.fill(0);command.fill(0);length=0
            return false
        }
        samples.copyInto(command,length,0,count);length+=count
        return true
    }

    fun poll(nowMs:Long):Outcome {
        terminal?.let { return it }
        if(stopped.get())return Outcome(Status.CANCELLED).also { terminal=it }
        if(future.isDone) {
            val completed=try { future.get() } catch(_:Exception) {
                return Outcome(Status.FAILED).also { terminal=it }
            }
            val status=when {
                completed.finishedAtMs<startedAtMs -> Status.FAILED
                completed.finishedAtMs-startedAtMs>=timeoutMs -> Status.TIMED_OUT
                completed.score>=HandsFreeWakeCandidate.THRESHOLD -> Status.ACCEPTED
                else -> Status.REJECTED
            }
            return Outcome(status,completed.score).also { terminal=it }
        }
        if(nowMs<startedAtMs || nowMs-startedAtMs>=timeoutMs) {
            val outcome=Outcome(if(nowMs<startedAtMs)Status.FAILED else Status.TIMED_OUT)
            terminal=outcome;cancelWorker()
            return outcome
        }
        return Outcome(Status.WAITING)
    }

    fun copyCommandTo(destination:ShortArray):Int {
        check(!stopped.get() && terminal?.status==Status.ACCEPTED){"wake_verifier_not_accepted"}
        require(destination.size>=length)
        command.copyInto(destination,0,0,length)
        return length
    }

    /** Existing collection contract: up to2s before and200ms after decision. */
    fun context():ShortArray {
        check(!stopped.get() && terminal?.status==Status.ACCEPTED){"wake_verifier_not_accepted"}
        val suffix=minOf(length,3200)
        return ShortArray(prefix.size+suffix).also {
            prefix.copyInto(it);command.copyInto(it,prefix.size,0,suffix)
        }
    }

    private fun cancelWorker() {
        stopped.set(true)
        future.cancel(true)
        // A cancelled FutureTask remains queued by default. Remove it so a slow
        // native call cannot leave expired candidates occupying the one queue slot.
        (executor as? ThreadPoolExecutor)?.purge()
        if(windowClaimed.compareAndSet(false,true))window.samples.fill(0)
    }

    override fun close() {
        if(terminal==null)terminal=Outcome(Status.CANCELLED)
        cancelWorker();prefix.fill(0);command.fill(0);length=0
    }
}
