package com.aegis.node

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.AbstractExecutorService
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import java.util.concurrent.atomic.AtomicInteger

class NodeHandsFreePendingWakeVerificationTest {
    private class ManualExecutor:AbstractExecutorService() {
        var task:Runnable?=null;private var stopped=false
        override fun execute(command:Runnable){check(!stopped);check(task==null);task=command}
        fun run(){val next=checkNotNull(task);task=null;next.run()}
        override fun shutdown(){stopped=true}
        override fun shutdownNow():MutableList<Runnable>{stopped=true;return mutableListOf<Runnable>().also{task?.let(it::add);task=null}}
        override fun isShutdown()=stopped
        override fun isTerminated()=stopped && task==null
        override fun awaitTermination(timeout:Long,unit:TimeUnit)=isTerminated
    }
    private fun pcm(size:Int,offset:Int=0)=ShortArray(size){((it+offset)%30000+1).toShort()}
    private fun ring():HandsFreeRing=HandsFreeRing(HandsFreeWakeCandidate.RING_CAPACITY).apply{append(pcm(44800),44800)}
    private fun candidate()=HandsFreeWakeCandidate(40320,160,floatArrayOf(.7f,.5f))

    @Test fun microphoneReadsRemainLosslessWhileTheVerifierIsPending() {
        val executor=ManualExecutor();var now=1000L
        val source=pcm(48000);var seen:ShortArray?=null
        HandsFreePendingWakeVerification(candidate(),ring(),44800,executor,1000,{now},{window,_->
            seen=window.samples.copyOf();assertEquals(40000,window.candidateSample);.8f
        }).use { pending->
            assertEquals(HandsFreePendingWakeVerification.Status.WAITING,pending.poll(1200).status)
            assertTrue(pending.append(source.copyOfRange(44800,48000),3200))
            now=1500;executor.run()
            assertEquals(HandsFreePendingWakeVerification.Status.ACCEPTED,pending.poll(1700).status)
            assertArrayEquals(source.copyOfRange(320,42880),seen)
            val destination=ShortArray(320000);val size=pending.copyCommandTo(destination)
            assertArrayEquals(source.copyOfRange(40320,48000),destination.copyOf(size))
            assertArrayEquals(source.copyOfRange(8320,43520),pending.context())
            assertEquals(40320L,pending.candidate.decisionSample)
        }
    }

    @Test fun rejectedFailedAndTimedOutCandidatesNeverExposeACommand() {
        for(value in listOf(.49f,Float.NaN)) {
            val executor=ManualExecutor()
            HandsFreePendingWakeVerification(candidate(),ring(),44800,executor,1000,{1200},{_,_->value}).use { pending->
                executor.run()
                assertEquals(if(value.isFinite())HandsFreePendingWakeVerification.Status.REJECTED else HandsFreePendingWakeVerification.Status.FAILED,pending.poll(1200).status)
                assertThrows(IllegalStateException::class.java){pending.copyCommandTo(ShortArray(320000))}
                assertThrows(IllegalStateException::class.java){pending.context()}
            }
        }
        val executor=ManualExecutor();var calls=0
        HandsFreePendingWakeVerification(candidate(),ring(),44800,executor,1000,{4000},{_,_->calls++;1f}).use { pending->
            assertEquals(HandsFreePendingWakeVerification.Status.TIMED_OUT,pending.poll(3000).status)
            executor.run();assertEquals(0,calls)
            assertThrows(IllegalStateException::class.java){pending.copyCommandTo(ShortArray(320000))}
        }
    }

    @Test fun packageThresholdAppliesToTheSameVerifierScore() {
        fun outcome(score:Float,threshold:Float):HandsFreePendingWakeVerification.Status {
            val executor=ManualExecutor()
            return HandsFreePendingWakeVerification(candidate(),ring(),44800,executor,1000,{1200},
                {_,_->score},threshold=threshold).use { pending->
                executor.run()
                pending.poll(1200).status
            }
        }
        assertEquals(HandsFreePendingWakeVerification.Status.REJECTED,outcome(.0692f,.5f))
        assertEquals(HandsFreePendingWakeVerification.Status.REJECTED,outcome(.0691f,.0692f))
        assertEquals(HandsFreePendingWakeVerification.Status.ACCEPTED,outcome(.0692f,.0692f))
    }

    @Test fun completionTimeDecidesTheDeadlineRatherThanTheLaterPoll() {
        for(finishedAt in listOf(2999L,3000L,900L)) {
            val executor=ManualExecutor()
            HandsFreePendingWakeVerification(candidate(),ring(),44800,executor,1000,{finishedAt},{_,_->1f}).use { pending->
                executor.run()
                val expected=when(finishedAt){2999L->HandsFreePendingWakeVerification.Status.ACCEPTED;3000L->HandsFreePendingWakeVerification.Status.TIMED_OUT;else->HandsFreePendingWakeVerification.Status.FAILED}
                assertEquals(expected,pending.poll(9000).status)
            }
        }
    }

    @Test fun commandCapacityIsBoundedAndCloseBeforeExecutionPreventsInference() {
        val executor=ManualExecutor();var calls=0
        HandsFreePendingWakeVerification(candidate(),ring(),44800,executor,1000,{1000},{_,_->calls++;1f}).use { pending->
            assertFalse(pending.append(pcm(320000),320000))
            assertEquals(HandsFreePendingWakeVerification.Status.LIMIT_EXCEEDED,pending.poll(1000).status)
            executor.run();assertEquals(0,calls)
        }
        val other=ManualExecutor()
        val pending=HandsFreePendingWakeVerification(candidate(),ring(),44800,other,1000,{1000},{_,_->calls++;1f})
        pending.close();pending.close();other.run()
        assertEquals(HandsFreePendingWakeVerification.Status.CANCELLED,pending.poll(1000).status)
        assertEquals(0,calls)
    }

    @Test fun cancellingARunningNativeCallDoesNotZeroItsInputConcurrently() {
        val executor=Executors.newSingleThreadExecutor()
        val started=CountDownLatch(1);val release=CountDownLatch(1)
        val reference=AtomicReference<ShortArray>()
        val pending=HandsFreePendingWakeVerification(candidate(),ring(),44800,executor,1000,{1200},{window,cancelled->
            reference.set(window.samples);started.countDown()
            // Simulate a native operation which cannot observe interruption until it returns.
            while(true) {
                try { release.await();break } catch(_:InterruptedException) {}
            }
            assertTrue(cancelled());.8f
        })
        try {
            assertTrue(started.await(2,TimeUnit.SECONDS))
            pending.close()
            assertTrue(reference.get().any{it.toInt()!=0})
            release.countDown();executor.shutdown()
            assertTrue(executor.awaitTermination(2,TimeUnit.SECONDS))
            assertTrue(reference.get().all{it.toInt()==0})
            assertEquals(HandsFreePendingWakeVerification.Status.CANCELLED,pending.poll(1200).status)
        } finally { release.countDown();pending.close();executor.shutdownNow() }
    }

    @Test fun timedOutQueuedCandidateCannotBlockTheNextBoundedSubmission() {
        val executor=ThreadPoolExecutor(0,1,30,TimeUnit.SECONDS,LinkedBlockingQueue<Runnable>(1))
        val firstStarted=CountDownLatch(1);val releaseFirst=CountDownLatch(1)
        val laterCalls=AtomicInteger(0)
        val first=HandsFreePendingWakeVerification(candidate(),ring(),44800,executor,1000,{1200},{_,_->
            firstStarted.countDown()
            // Simulate ORT still inside native code after its Future was cancelled.
            while(true) {
                try { releaseFirst.await();break } catch(_:InterruptedException) {}
            }
            .8f
        })
        var second:HandsFreePendingWakeVerification?=null
        var third:HandsFreePendingWakeVerification?=null
        try {
            assertTrue(firstStarted.await(2,TimeUnit.SECONDS))
            assertEquals(HandsFreePendingWakeVerification.Status.TIMED_OUT,first.poll(3000).status)
            first.close()
            second=HandsFreePendingWakeVerification(candidate(),ring(),44800,executor,1000,{1200},{_,_->laterCalls.incrementAndGet();.8f})
            assertEquals(1,executor.queue.size)
            assertEquals(HandsFreePendingWakeVerification.Status.TIMED_OUT,second.poll(3000).status)
            assertEquals(0,executor.queue.size)
            second.close()
            // The first worker is still occupied. Without purge this submit rejects,
            // because the cancelled second FutureTask retains the only queue slot.
            third=HandsFreePendingWakeVerification(candidate(),ring(),44800,executor,1000,{1200},{_,_->laterCalls.incrementAndGet();.8f})
            assertEquals(1,executor.queue.size)
            assertEquals(HandsFreePendingWakeVerification.Status.WAITING,third.poll(1200).status)
            releaseFirst.countDown();executor.shutdown()
            assertTrue(executor.awaitTermination(2,TimeUnit.SECONDS))
            assertEquals(HandsFreePendingWakeVerification.Status.ACCEPTED,third.poll(1200).status)
            assertEquals(1,laterCalls.get())
        } finally { releaseFirst.countDown();first.close();second?.close();third?.close();executor.shutdownNow() }
    }

}
