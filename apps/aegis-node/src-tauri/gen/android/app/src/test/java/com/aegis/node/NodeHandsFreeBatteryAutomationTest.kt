package com.aegis.node

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class NodeHandsFreeBatteryAutomationTest {
    private class Fixture {
        var now=1000L;var saved:String?=null;var writes=0;var nextId=0;var fail=false
        fun model()=HandsFreeBatteryAutomation(saved,{if(fail)throw IllegalStateException("storage");saved=it;writes++},{now},{"seq-"+(++nextId)})
    }
    @Test fun armingIsIdempotentAndWaitsForScreenOff() {
        val f=Fixture();val m=f.model()
        assertFalse(m.isLive());assertFalse(m.blocksNormal());assertNull(m.token())
        m.arm(false);val id=m.state().getString("id");val writes=f.writes
        m.arm(true)
        assertEquals(id,m.state().getString("id"));assertEquals(writes,f.writes)
        assertFalse(m.state().getBoolean("validation"));assertTrue(m.isWaiting());assertTrue(m.blocksNormal())
        assertEquals(5400000L,m.state().getLong("remainingEstimateMs"))
        val step=m.claimScreenOff()!!
        assertEquals("off",step.mode);assertEquals(f.now,step.claimedAtElapsedMs)
        assertFalse(m.isWaiting());assertNull(m.claimScreenOff());assertNull(m.token())
    }
    @Test fun entireSequenceAssociatesExactlyThreeRunsAndActualTimings() {
        val f=Fixture();val m=f.model();m.arm(false)
        var step=m.claimScreenOff()!!
        for(index in 0..2) {
            assertEquals(index,step.stageIndex);assertEquals(listOf("off","capture_only","detection")[index],step.mode)
            f.now+=123
            val start=f.now;val token=m.running(step,"run-"+index,start)
            assertEquals(start+1800000,token.deadlineElapsedMs)
            assertEquals(token,m.running(step,"run-"+index,start))
            m.setAlarmPrecision(token,if(index==0)"inexact" else "exact")
            assertEquals(if(index==0)"inexact" else "exact",m.state().getString("alarmPrecision"))
            f.now=token.deadlineElapsedMs-1;assertNull(m.claimDue(token))
            f.now=token.deadlineElapsedMs+456
            step=m.claimDue(token)!!
            assertNull(m.claimDue(token));assertNull(m.token())
            assertEquals(456L,m.state().getLong("delayMs"))
        }
        assertEquals(3,step.stageIndex);assertNull(step.mode);m.complete(step);m.complete(step)
        assertFalse(m.isLive());assertFalse(m.blocksNormal());assertEquals("completed",m.state().getString("status"))
        assertEquals(0L,m.state().getLong("remainingEstimateMs"))
        val ids=m.export().getJSONArray("runIds");assertEquals(3,ids.length())
        for(i in 0..2)assertEquals("run-"+i,ids.getString(i))
        val transitions=m.export().getJSONArray("transitions");assertEquals(4,transitions.length())
        assertTrue(transitions.getJSONObject(0).isNull("plannedDeadlineElapsedMs"))
        assertEquals(1123L,transitions.getJSONObject(0).getLong("nextStartElapsedMs"))
        assertTrue(transitions.getJSONObject(3).isNull("nextStartElapsedMs"))
    }
    @Test fun staleDuplicateAndForeignTokensNeverAdvance() {
        val f=Fixture();val m=f.model();m.arm(true);val step=m.claimScreenOff()!!
        val token=m.running(step,"run-0",f.now,"exact")
        assertEquals(f.now+60000,token.deadlineElapsedMs);f.now=token.deadlineElapsedMs
        for(stale in listOf(token.copy(sequenceId="other"),token.copy(runId="other"),token.copy(stageIndex=1),token.copy(deadlineElapsedMs=token.deadlineElapsedMs+1)))assertNull(m.claimDue(stale))
        val next=m.claimDue(token)!!
        assertNull(m.claimDue(token));assertThrows(IllegalStateException::class.java){m.running(step,"run-x",f.now)}
        assertThrows(IllegalStateException::class.java){m.complete(next)}
        m.running(next,"run-1",f.now,"exact")
        val writes=f.writes;m.setAlarmPrecision(token,"inexact");assertEquals(writes,f.writes)
    }
    @Test fun everyLiveRecoveryIncludingRebootIsInterruptedAndBlocked() {
        for(status in listOf("waiting","transitioning","running")) {
            val f=Fixture();val m=f.model();m.arm(false)
            if(status!="waiting") {
                val step=m.claimScreenOff()!!
                if(status=="running")m.running(step,"run-0",f.now,"exact")
            }
            f.now=1 // New boot or process; old elapsed deadlines are not replayed.
            val restored=f.model()
            assertFalse(restored.isLive());assertFalse(restored.isWaiting());assertNull(restored.token())
            assertTrue(restored.blocksNormal());assertEquals("process_restart",restored.state().getString("reason"))
            assertThrows(IllegalStateException::class.java){restored.arm(false)}
            restored.cancel();assertFalse(restored.blocksNormal());assertFalse(f.model().blocksNormal())
        }
    }
    @Test fun persistenceFailureAbortsMemoryAndCannotReplaySavedRunningPlan() {
        val f=Fixture();val m=f.model();m.arm(false);val step=m.claimScreenOff()!!
        val token=m.running(step,"run-0",f.now,"exact");f.now=token.deadlineElapsedMs
        f.fail=true;assertThrows(IllegalStateException::class.java){m.claimDue(token)}
        assertFalse(m.isLive());assertTrue(m.blocksNormal());assertNull(m.token())
        assertEquals("storage_failure",m.state().getString("reason"))
        f.fail=false;val restored=f.model()
        assertFalse(restored.isLive());assertTrue(restored.blocksNormal());assertNull(restored.claimDue(token))
        restored.cancel();restored.arm(true);assertTrue(restored.isWaiting())
    }
    @Test fun failedFirstArmAndCorruptSavedStateAreExplicitlyRecoverable() {
        val f=Fixture();val m=f.model();f.fail=true
        assertThrows(IllegalStateException::class.java){m.arm(false)}
        assertTrue(m.blocksNormal());assertFalse(m.isLive())
        f.fail=false;m.cancel();assertFalse(f.model().blocksNormal())
        for(saved in listOf("{broken","{}","{\"schema\":1,\"status\":\"running\",\"runIds\":[null,null,null,null],\"transitions\":[]}")) {
            f.saved=saved;val corrupt=f.model()
            assertTrue(corrupt.blocksNormal());assertFalse(corrupt.isLive());corrupt.cancel()
            assertFalse(f.model().blocksNormal())
        }
    }
    @Test fun cancellingAndInterruptingInvalidateAllPendingWork() {
        val f=Fixture();val m=f.model();m.arm(true);val step=m.claimScreenOff()!!
        val token=m.running(step,"run-0",f.now,"exact")
        m.interrupt("charging");assertTrue(m.blocksNormal());assertNull(m.claimDue(token))
        assertThrows(IllegalStateException::class.java){m.running(step,"run-0",f.now)}
        m.cancel();assertFalse(m.blocksNormal());m.arm(false)
        assertNotEquals(token.sequenceId,m.state().getString("id"));assertNull(m.claimDue(token))
        m.cancel();assertNull(m.claimScreenOff())
    }
    @Test fun publicationHappensOnlyAfterPersistenceAndHotQueriesNeverWaitForIt() {
        var now=1000L;val entered=CountDownLatch(1);val release=CountDownLatch(1)
        val m=HandsFreeBatteryAutomation(null,{entered.countDown();release.await()},{now},{"seq-1"})
        val executor=Executors.newSingleThreadExecutor()
        try {
            val pending=executor.submit{m.arm(false)}
            assertTrue(entered.await(2,TimeUnit.SECONDS))
            assertFalse(m.isLive());assertFalse(m.isWaiting());assertFalse(m.blocksNormal());assertNull(m.token())
            release.countDown();pending.get(2,TimeUnit.SECONDS)
            assertTrue(m.isLive());assertTrue(m.isWaiting());assertTrue(m.blocksNormal())
        }finally{release.countDown();executor.shutdownNow()}
    }
    @Test fun returnedJsonAndTransitionClaimsCannotMutateOrForgePlan() {
        val f=Fixture();val m=f.model();m.arm(false);val step=m.claimScreenOff()!!
        val value=m.state();value.put("status","completed")
        assertEquals("transitioning",m.state().getString("status"))
        assertThrows(IllegalStateException::class.java){m.running(step.copy(claimedAtElapsedMs=step.claimedAtElapsedMs+1),"run-0",f.now)}
        assertThrows(IllegalArgumentException::class.java){m.running(step,"run-0",f.now-1)}
        assertTrue(m.isLive())
        assertThrows(IllegalArgumentException::class.java){HandsFreeBatteryAutomation.Token("../bad",0,"run",1)}
        assertThrows(IllegalArgumentException::class.java){HandsFreeBatteryAutomation.Token("seq",3,"run",1)}
    }
    @Test fun invalidRecoveryAndFirstWriteFailureExposeValidRecoverablePublicState() {
        val f=Fixture();f.saved="{broken";val invalid=f.model()
        val state=invalid.state()
        assertTrue(state.getString("id").matches(Regex("[A-Za-z0-9_-]{1,128}")))
        assertEquals(0,state.getInt("stageIndex"));assertEquals(1800000L,state.getLong("stageDurationMs"))
        assertEquals("interrupted",state.getString("status"));assertEquals("saved_state_invalid",state.getString("issue"))
        assertTrue(state.isNull("mode"));assertTrue(state.isNull("remainingEstimateMs"))
        invalid.cancel();assertFalse(f.model().blocksNormal())
        f.saved=null;val first=f.model();f.fail=true
        assertThrows(IllegalStateException::class.java){first.arm(false)}
        assertTrue(first.state().getString("id").isNotEmpty());assertEquals("storage_failure",first.state().getString("issue"))
    }

    @Test fun captureReadyMetadataIsPersistentAndDeadlineStartsAtMeasuredSnapshot() {
        val f=Fixture();val m=f.model();m.arm(false);var step=m.claimScreenOff()!!
        val off=m.running(step,"run-off",f.now,"exact");f.now=off.deadlineElapsedMs;step=m.claimDue(off)!!
        f.now+=200;m.recordCaptureReady(step,f.now,123456)
        assertEquals(200L,m.state().getLong("preparationElapsedMs"))
        val ready=f.now;f.now+=50
        val measured=m.running(step,"run-capture",f.now,"exact",250,"first_positive_pcm")
        assertEquals(f.now+1800000,measured.deadlineElapsedMs)
        val entry=m.export().getJSONArray("transitions").getJSONObject(1)
        assertEquals(ready,entry.getLong("captureReadyElapsedMs"));assertEquals(123456L,entry.getLong("startupThreadCpuNanos"))
        assertEquals(250L,entry.getLong("preparationMs"));assertEquals("first_positive_pcm",entry.getString("measurementStartBasis"))
        assertFalse(m.state().getBoolean("validation"))
    }
    @Test fun captureReadyMetadataCannotBeForgedForOffOrAStaleTransition() {
        val f=Fixture();val m=f.model();m.arm(true);val offStep=m.claimScreenOff()!!
        assertThrows(IllegalArgumentException::class.java){m.recordCaptureReady(offStep,f.now,100)}
        val off=m.running(offStep,"run-off",f.now,"exact");f.now=off.deadlineElapsedMs;val step=m.claimDue(off)!!
        f.now+=100;m.recordCaptureReady(step,f.now,-1)
        val entry=m.state().getJSONArray("transitions").getJSONObject(1);assertTrue(entry.isNull("startupThreadCpuNanos"))
        assertThrows(IllegalStateException::class.java){m.recordCaptureReady(offStep,f.now,100)}
        f.now+=1
        assertThrows(IllegalStateException::class.java){m.recordCaptureReady(step,f.now,100)}
    }
}
