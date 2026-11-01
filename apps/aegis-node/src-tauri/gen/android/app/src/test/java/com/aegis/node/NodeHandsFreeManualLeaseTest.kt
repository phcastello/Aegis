package com.aegis.node

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.CancellationException
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference

/** Native completion has no dependency on a paused/missing WebView event loop. */
class NodeHandsFreeManualLeaseTest {
    @Test fun leaseStaysOwnedUntilNativePlaybackDrainsThenReleasesWithoutAnyJsCallback() {
        val owner=AtomicReference("playback-A");val released=AtomicInteger()
        val drained=CountDownLatch(1);val started=CountDownLatch(1)
        val lease=ManualPlaybackLease("playback-A",{owner.get()==it},{id->if(owner.compareAndSet(id,null))released.incrementAndGet()})
        val executor=Executors.newSingleThreadExecutor()
        try {
            val native=executor.submit{lease.use{started.countDown();check(drained.await(5,TimeUnit.SECONDS))}}
            assertTrue(started.await(5,TimeUnit.SECONDS));assertTrue(lease.isCurrent());assertEquals(0,released.get())
            drained.countDown();native.get(5,TimeUnit.SECONDS)
            assertNull(owner.get());assertEquals(1,released.get());assertFalse(lease.isCurrent())
        }finally{drained.countDown();executor.shutdownNow()}
    }
    @Test fun cancellationOrPlayerFailureReleasesNativeOwnershipExactlyOnce() {
        for(error in listOf(CancellationException(),IllegalStateException("player_failed"))) {
            val released=AtomicInteger()
            val lease=ManualPlaybackLease("playback-A",{true},{released.incrementAndGet()})
            assertThrows(error.javaClass){lease.use{throw error}}
            lease.close();assertEquals(1,released.get());assertFalse(lease.isCurrent())
        }
    }
    @Test fun anOldNativeCompletionCannotReleaseANewerPlaybackOrCaptureLease() {
        for(next in listOf("playback-B","capture-B")) {
            val owner=AtomicReference("playback-A");val released=AtomicInteger()
            val old=ManualPlaybackLease("playback-A",{owner.get()==it},{id->if(owner.compareAndSet(id,null))released.incrementAndGet()})
            owner.set(next);assertFalse(old.isCurrent());old.close()
            assertEquals(next,owner.get());assertEquals(0,released.get())
        }
    }
    @Test fun lateJsReleaseAfterNativeCompletionIsIdempotentAndCannotReleaseTheNextLease() {
        val owner=AtomicReference("playback-A");val released=AtomicInteger()
        fun release(id:String){if(owner.compareAndSet(id,null))released.incrementAndGet()}
        val native=ManualPlaybackLease("playback-A",{owner.get()==it},{release(it)})
        native.close();assertEquals(1,released.get());owner.set("playback-B")
        release("playback-A");native.close()
        assertEquals("playback-B",owner.get());assertEquals(1,released.get())
    }
}
