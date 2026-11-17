package com.aegis.node

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.time.Instant

class NodeHandsFreeBatteryDiagnosticsTest {
    private class Fixture {
        var now=1000L
        var uptime=1000L
        var cpu=100L
        var charge:Long?=3_200_000L
        var energy:Long?=12_000_000_000L
        var percent:Double?=80.0
        var plugged:Int?=0
        var status:Int?=3
        var screen:Boolean?=false
        var process="process-a"
        var healthCpu=100L
        var probes=0
        var writes=0
        var saved:String?=null
        var failWrites=false
        var failProbe=false
        var device=JSONObject().put("sdkInt",33).put("manufacturer","test-maker").put("model","test-model").put("androidVersion","13").put("androidBuildFingerprint","test-firmware")
        var id=0
        fun snapshot():JSONObject {
            probes++
            if(failProbe)throw IllegalStateException("unavailable")
            return JSONObject().put("at",Instant.ofEpochMilli(now.coerceAtLeast(0)).toString())
                .put("elapsedMs",if(now>=0)now else JSONObject.NULL).put("uptimeMs",if(uptime>=0)uptime else JSONObject.NULL)
                .put("processId",process).put("processCpuMs",if(cpu>=0)cpu else JSONObject.NULL)
                .put("chargeMicroAh",charge?:JSONObject.NULL).put("energyNanoWh",energy?:JSONObject.NULL)
                .put("batteryPercent",percent?:JSONObject.NULL).put("currentMicroA",-123000)
                .put("plugged",plugged?:JSONObject.NULL).put("batteryStatus",status?:JSONObject.NULL)
                .put("screenInteractive",screen?:JSONObject.NULL)
                .put("uidHealth",JSONObject().put("status","ok").put("userCpuMs",healthCpu).put("systemCpuMs",healthCpu).put("audioTimerMs",healthCpu))
                .also{snapshot->for(key in device.keys())snapshot.put(key,device.opt(key))}
        }
        fun store()=HandsFreeBatteryDiagnostics(saved,{if(failWrites)throw IllegalStateException("disk");saved=it;writes++},::snapshot,{now},{"run-"+(++id)})
        fun advance(ms:Long) {now+=ms;uptime+=ms/10;cpu+=10;healthCpu+=10}
    }
    private fun last(store:HandsFreeBatteryDiagnostics):JSONObject=store.state().getJSONArray("runs").let{it.getJSONObject(it.length()-1)}
    private fun issues(row:JSONObject)=row.getJSONArray("issues").let{a->(0 until a.length()).map{a.getString(it)}}
    private fun identity()=JSONObject().put("modelSha256","private-bundle-hash").put("appVersion","test")

    @Test fun startsAreIdempotentAndStateNeverProbesOrWrites() {
        val f=Fixture();val d=f.store()
        assertFalse(d.blocksNormal());assertNull(d.activeMode());assertEquals(0,f.probes)
        d.start("off",identity());val before=d.state();val writes=f.writes
        d.start("detection",identity())
        repeat(100){d.state()}
        assertEquals("off",d.activeMode());assertEquals(1,f.probes);assertEquals(writes,f.writes)
        assertEquals(before.getJSONObject("active").getString("id"),d.state().getJSONObject("active").getString("id"))
        assertTrue(d.blocksNormal())
        assertThrows(IllegalArgumentException::class.java){f.store().start("unknown",identity())}
    }
    @Test fun normalObservationsAreThrottledAndExportCanTakeExplicitSnapshot() {
        val f=Fixture();val d=f.store();d.start("off",identity())
        repeat(100){f.advance(100);d.observe()}
        assertEquals(1,f.probes)
        f.advance(50000);d.observe();assertEquals(2,f.probes)
        d.observe();assertEquals(2,f.probes)
        val exported=d.export();assertEquals(3,f.probes)
        assertEquals(3,exported.getJSONArray("runs").getJSONObject(0).getJSONArray("samples").length())
        assertEquals("whole_phone_battery; process_and_thread_cpu_are_separate_observations",exported.getString("scope"))
    }
    @Test fun twentyMinuteDischargingRunKeepsPhoneAndComponentCpuSeparate() {
        val f=Fixture();val d=f.store();d.start("detection",identity())
        d.capture(19_200_000,true)
        d.component("detector",3200,1_800_000,1_000_000)
        d.component("detector",3200,2_800_000,2_000_000)
        d.component("verifier",42560,8_000_000,4_000_000)
        d.component("capture_thread",0,1_000_000_000,10_000_000)
        f.advance(20*60000);f.charge=3_180_000;f.energy=11_950_000_000;f.percent=79.0;f.cpu=250
        d.finish()
        val row=last(d);val s=row.getJSONObject("summary")
        assertEquals("completed",row.getString("status"));assertTrue(row.getBoolean("eligible"));assertTrue(issues(row).isEmpty())
        assertEquals(20.0,s.getDouble("phoneChargeMah"),0.0)
        assertEquals(50.0,s.getDouble("phoneEnergyMwh"),0.0)
        assertEquals(60.0,s.getDouble("phoneDischargeMahPerHour"),0.0)
        assertEquals(1.0,s.getDouble("batteryPercentPoints"),0.0)
        assertEquals(150L,s.getLong("processCpuMs"))
        assertEquals(3.0,s.getDouble("detectorCpuMs"),0.0);assertEquals(4.0,s.getDouble("verifierCpuMs"),0.0)
        assertEquals(10.0,s.getDouble("captureThreadCpuMs"),0.0)
        assertEquals(120000L,s.getLong("awakeMs"));assertEquals(0L,s.getLong("screenOnMs"))
        assertEquals(1200000.0,s.getDouble("captureAudioMs"),0.0);assertEquals(1L,s.getLong("verifierCandidates"))
        assertFalse(d.blocksNormal());assertNull(d.activeMode())
    }
    @Test fun negativePhoneDeltasAreVisibleAndChargingCannotBeEligible() {
        val f=Fixture();val d=f.store();d.start("off",identity());f.advance(20*60000)
        f.charge=3_220_000;f.energy=12_050_000_000;f.percent=81.0;f.plugged=1;f.status=2;d.finish()
        val row=last(d);val s=row.getJSONObject("summary")
        assertEquals(-20.0,s.getDouble("phoneChargeMah"),0.0);assertEquals(-50.0,s.getDouble("phoneEnergyMwh"),0.0)
        assertEquals(-60.0,s.getDouble("phoneDischargeMahPerHour"),0.0);assertEquals(-1.0,s.getDouble("batteryPercentPoints"),0.0)
        assertTrue(issues(row).containsAll(listOf("charging","charge_counter_increased","energy_counter_increased")))
        assertFalse(row.getBoolean("eligible"))
    }
    @Test fun restartInterruptsStudyAndRetainsGateUntilExplicitFinish() {
        val f=Fixture();val d=f.store();d.start("capture_only",identity());d.capture(3200,false);f.advance(60000);d.observe()
        f.process="process-b";f.cpu=10;f.advance(10000)
        val restored=f.store()
        assertTrue(restored.blocksNormal());assertNull(restored.activeMode());assertTrue(restored.state().getBoolean("requiresResume"))
        val row=last(restored);assertEquals("interrupted",row.getString("status"));assertFalse(row.getBoolean("eligible"))
        assertTrue(issues(row).contains("process_restart"));assertTrue(row.getJSONObject("summary").isNull("processCpuMs"))
        assertThrows(IllegalStateException::class.java){restored.start("detection",identity())}
        restored.finish();assertFalse(restored.blocksNormal())
        assertFalse(f.store().blocksNormal())
    }
    @Test fun explicitInterruptAlsoRequiresResumeAndFinishIsIdempotent() {
        val f=Fixture();val d=f.store();d.start("off",identity());f.advance(20000)
        d.interrupt("manual_audio");assertTrue(d.blocksNormal());assertNull(d.activeMode())
        assertTrue(issues(last(d)).contains("manual_audio"))
        d.finish();d.finish();assertFalse(d.blocksNormal());assertEquals(1,d.state().getJSONArray("runs").length())
    }
    @Test fun unavailableBatteryDoesNotBecomeZeroAndProbeFailureIsIncomplete() {
        val f=Fixture();f.charge=null;f.energy=null;f.percent=null;val d=f.store()
        d.start("off",identity());f.advance(20*60000);d.finish()
        val row=last(d);val s=row.getJSONObject("summary")
        for(key in listOf("phoneChargeMah","phoneEnergyMwh","phoneDischargeMahPerHour","batteryPercentPoints"))assertTrue(key,s.isNull(key))
        assertTrue(issues(row).contains("observation_incomplete"));assertFalse(row.getBoolean("eligible"))
        val g=Fixture();val broken=g.store();g.failProbe=true
        broken.start("off",identity());g.advance(1200000);broken.finish()
        assertTrue(issues(last(broken)).contains("observation_incomplete"))
    }
    @Test fun invalidMonotonicAndComponentClocksAreNotReportedAsZeroCost() {
        val f=Fixture();val d=f.store();d.start("detection",identity());d.capture(3200,true)
        d.component("detector",3200,-1,100);d.component("verifier",42560,100,-1)
        f.now=500;f.uptime=-1;f.cpu=50;f.healthCpu=1
        d.finish();val row=last(d);val s=row.getJSONObject("summary")
        for(key in listOf("detectorCpuMs","verifierCpuMs","processCpuMs","awakeMs"))assertTrue(key,s.isNull(key))
        assertTrue(row.isNull("durationMs"))
        assertTrue(issues(row).containsAll(listOf("invalid_component","clock_reset","process_cpu_reset","uid_cpu_reset")))
        assertFalse(row.getBoolean("eligible"))
    }
    @Test fun shortWindowAndCaptureMismatchRemainExplicit() {
        val f=Fixture();val d=f.store();d.start("capture_only",identity());d.capture(3200,true);f.advance(30000);d.finish()
        assertTrue(issues(last(d)).containsAll(listOf("short_window","scenario_mismatch")))
        d.start("off",identity());d.capture(3200,false);f.advance(1200000);d.finish()
        assertTrue(issues(last(d)).contains("scenario_mismatch"))
        d.start("detection",identity());f.advance(1200000);d.finish()
        assertTrue(issues(last(d)).contains("capture_gap"))
    }
    @Test fun summariesAndSamplesAreBoundedWithoutMutatingEarlierIdentity() {
        val f=Fixture();val d=f.store();val original=identity()
        d.start("off",original);original.put("modelSha256","changed")
        repeat(260){f.advance(60000);d.observe()}
        var row=d.export().getJSONArray("runs").getJSONObject(0)
        assertEquals(256,row.getJSONArray("samples").length())
        assertEquals(1000L,row.getJSONArray("samples").getJSONObject(0).getLong("elapsedMs"))
        assertEquals("private-bundle-hash",row.getJSONObject("identity").getString("modelSha256"))
        assertTrue(issues(row).contains("storage_limit"));d.finish()
        repeat(35){d.start("off",identity());f.advance(1200000);d.finish()}
        assertEquals(8,d.state().getJSONArray("runs").length());assertEquals(32,d.export().getJSONArray("runs").length())
        val response=d.state();response.getJSONArray("runs").getJSONObject(0).put("status","forged")
        assertNotEquals("forged",d.state().getJSONArray("runs").getJSONObject(0).getString("status"))
    }
    @Test fun failedBoundaryPersistenceCannotReleaseGate() {
        val f=Fixture();val d=f.store();f.failWrites=true
        assertThrows(IllegalStateException::class.java){d.start("off",identity())}
        assertTrue(d.blocksNormal());assertTrue(d.state().getBoolean("requiresResume"))
        f.failWrites=false;d.finish();assertFalse(d.blocksNormal())
        d.start("off",identity());f.failWrites=true
        assertThrows(IllegalStateException::class.java){d.finish()}
        assertTrue(d.blocksNormal())
        f.failWrites=false;d.finish();assertFalse(d.blocksNormal())
    }
    @Test fun corruptSavedStateFailsClosedAndBatteryApiSentinelsStayNull() {
        val f=Fixture();f.saved="{invalid";val d=f.store()
        assertTrue(d.blocksNormal());d.finish();assertFalse(d.blocksNormal())
        assertNull(HandsFreeBatteryProbe.intProperty(Int.MIN_VALUE));assertNull(HandsFreeBatteryProbe.longProperty(Long.MIN_VALUE))
        assertEquals(-123L,HandsFreeBatteryProbe.intProperty(-123))
        assertEquals(0L,HandsFreeBatteryProbe.intProperty(0))
    }

    @Test fun captureAndDetectionCoverageAreRequiredForEligibleComparisons() {
        val f=Fixture();val d=f.store()
        d.start("detection",identity());d.capture(18_000_000,true)
        d.component("detector",3200,100,100);d.component("capture_thread",0,100,100)
        f.advance(1200000);d.finish()
        assertTrue(issues(last(d)).contains("capture_gap"))
        d.start("detection",identity());d.capture(17_000_000,true);d.capture(2_200_000,false)
        d.component("detector",3200,100,100);d.component("capture_thread",0,100,100)
        f.advance(1200000);d.finish()
        assertTrue(issues(last(d)).contains("detection_gap"))
        d.start("capture_only",identity());d.capture(18_240_000,false)
        d.component("capture_thread",0,100,100);f.advance(1200000);d.finish()
        assertTrue(issues(last(d)).toString(),last(d).getBoolean("eligible"))
    }
    @Test fun missingComponentSamplesCannotBePresentedAsZeroCpu() {
        val f=Fixture();val d=f.store();d.start("detection",identity());d.capture(19_200_000,true);f.advance(1200000);d.finish()
        val row=last(d);val s=row.getJSONObject("summary")
        assertTrue(s.isNull("detectorCpuMs"));assertTrue(s.isNull("captureThreadCpuMs"))
        assertEquals(0.0,s.getDouble("verifierCpuMs"),0.0)
        assertTrue(issues(row).contains("observation_incomplete"));assertFalse(row.getBoolean("eligible"))
    }
    @Test fun screenOnIntervalsAndChargingTransitionsInvalidateComparison() {
        val f=Fixture();val d=f.store();d.start("off",identity())
        f.advance(60000);f.screen=true;d.observe(force=true)
        f.advance(120000);f.screen=false;d.observe(force=true)
        f.advance(1020000);d.finish()
        assertEquals(120000L,last(d).getJSONObject("summary").getLong("screenOnMs"))
        assertTrue(issues(last(d)).contains("screen_on"));assertFalse(last(d).getBoolean("eligible"))
        d.start("off",identity());f.advance(60000);f.plugged=1;d.observe(force=true)
        f.advance(60000);f.plugged=0;d.observe(force=true)
        f.advance(1080000);d.finish()
        assertTrue(issues(last(d)).contains("charging_transition"));assertFalse(last(d).getBoolean("eligible"))
    }

    @Test fun structurallyCorruptSavedRunsRemainRecoverableAndFailClosed() {
        for(saved in listOf("{\"runs\":[null]}","{\"runs\":[{}]}","{\"runs\":{}}","{\"requiresResume\":\"false\"}","{\"schema\":2}")) {
            val f=Fixture();f.saved=saved;val d=f.store()
            assertTrue(saved,d.blocksNormal());assertNull(d.activeMode());assertEquals(0,d.state().getJSONArray("runs").length())
            d.finish();assertFalse(d.blocksNormal())
        }
    }
    @Test fun slowPlatformProbeOrPersistenceCannotBlockCaptureCounters() {
        for(phase in listOf("probe","save")) {
            val f=Fixture()
            val entered=java.util.concurrent.CountDownLatch(1)
            val release=java.util.concurrent.CountDownLatch(1)
            var block=false
            val d=HandsFreeBatteryDiagnostics(null,{value->
                if(block && phase=="save"){entered.countDown();release.await()}
                f.saved=value
            },{
                if(block && phase=="probe"){entered.countDown();release.await()}
                f.snapshot()
            },{f.now},{"run-1"})
            d.start("detection",identity());f.advance(60000);block=true
            val executor=java.util.concurrent.Executors.newFixedThreadPool(2)
            try {
                val observation=executor.submit{d.observe(force=true)}
                assertTrue(entered.await(2,java.util.concurrent.TimeUnit.SECONDS))
                val counters=executor.submit{
                    d.capture(3200,true);d.component("detector",3200,100,100)
                    assertEquals("detection",d.activeMode());assertTrue(d.blocksNormal())
                }
                counters.get(2,java.util.concurrent.TimeUnit.SECONDS)
                release.countDown();observation.get(2,java.util.concurrent.TimeUnit.SECONDS)
                assertEquals(200.0,last(d).getJSONObject("summary").getDouble("captureAudioMs"),0.0)
            } finally {release.countDown();executor.shutdownNow()}
        }
    }
    @Test fun explicitFinishPublishesOpenGateOnlyAfterSuccessfulPersistence() {
        val f=Fixture();var block=false
        val entered=java.util.concurrent.CountDownLatch(1);val release=java.util.concurrent.CountDownLatch(1)
        val d=HandsFreeBatteryDiagnostics(null,{value->
            if(block){entered.countDown();release.await()}
            f.saved=value
        },f::snapshot,{f.now},{"run-1"})
        d.start("off",identity());f.advance(1200000);block=true
        val executor=java.util.concurrent.Executors.newSingleThreadExecutor()
        try {
            val finishing=executor.submit{d.finish()}
            assertTrue(entered.await(2,java.util.concurrent.TimeUnit.SECONDS))
            assertNull(d.activeMode());assertTrue(d.blocksNormal())
            release.countDown();finishing.get(2,java.util.concurrent.TimeUnit.SECONDS)
            assertFalse(d.blocksNormal());assertFalse(f.store().blocksNormal())
        } finally {release.countDown();executor.shutdownNow()}
    }

    @Test fun comparisonGroupsIgnoreModeReadingsAndJsonPropertyOrder() {
        val f=Fixture();val d=f.store()
        val first=JSONObject().put("appVersion","v1").put("build",42).put("modelSha256","model-a")
            .put("config",JSONObject().put("stride",3).put("threshold",0.5))
        val reordered=JSONObject().put("config",JSONObject().put("threshold",0.5).put("stride",3))
            .put("modelSha256","model-a").put("build",42).put("appVersion","v1")
            .put("mode","ignored").put("elapsedMs",999).put("batteryPercent",1).put("processId","ignored")
        d.start("off",first);val key=last(d).getString("comparisonKey")
        assertTrue(key.matches(Regex("[a-f0-9]{64}")));d.finish()
        f.advance(60000);f.cpu=999;f.charge=2_900_000;f.energy=10_000_000_000;f.percent=70.0;f.process="process-b"
        d.start("capture_only",reordered);assertEquals(key,last(d).getString("comparisonKey"));d.finish()
        f.advance(60000);d.start("detection",first);assertEquals(key,last(d).getString("comparisonKey"))
        val exported=d.export().getJSONArray("runs")
        assertEquals(key,exported.getJSONObject(exported.length()-1).getString("comparisonKey"))
    }
    @Test fun comparisonGroupsSeparateAppModelConfigurationFirmwareAndPhones() {
        fun key(identity:JSONObject,device:JSONObject):String {
            val f=Fixture();f.device=device;val d=f.store();d.start("off",identity)
            return last(d).getString("comparisonKey")
        }
        val identity=JSONObject().put("appVersion","v1").put("build",42).put("modelSha256","model-a")
            .put("config",JSONObject().put("stride",3).put("threshold",0.5))
        val device=Fixture().device
        val baseline=key(identity,device)
        for(changed in listOf(
            JSONObject(identity.toString()).put("appVersion","v2"),
            JSONObject(identity.toString()).put("build",43),
            JSONObject(identity.toString()).put("modelSha256","model-b"),
            JSONObject(identity.toString()).put("config",JSONObject().put("stride",3).put("threshold",0.6))
        ))assertNotEquals(baseline,key(changed,device))
        for(changed in listOf(
            JSONObject(device.toString()).put("model","another-model"),
            JSONObject(device.toString()).put("manufacturer","another-maker"),
            JSONObject(device.toString()).put("sdkInt",34),
            JSONObject(device.toString()).put("androidVersion","14"),
            JSONObject(device.toString()).put("androidBuildFingerprint","firmware-update-same-android13")
        ))assertNotEquals(baseline,key(identity,changed))
    }

    @Test fun automationPhasesKeepGateAndRequireTheSameCompletedSequence() {
        val f=Fixture();val d=f.store()
        d.start("off",identity(),"seq-a");assertNotNull(d.activeRunId());assertEquals(1000L,d.activeStartElapsedMs())
        f.advance(1200000);d.finishPhase()
        assertTrue(d.blocksNormal());assertNull(d.activeMode());assertNull(d.activeRunId());assertNull(d.activeStartElapsedMs())
        assertThrows(IllegalStateException::class.java){d.start("capture_only",identity())}
        assertThrows(IllegalStateException::class.java){d.start("capture_only",identity(),"seq-b")}
        d.start("capture_only",identity(),"seq-a");d.capture(19_200_000,false);d.component("capture_thread",0,1,1)
        f.advance(1200000);d.finishPhase();assertTrue(d.blocksNormal())
        d.start("detection",identity(),"seq-a");d.capture(19_200_000,true);d.component("capture_thread",0,1,1);d.component("detector",3200,1,1)
        f.advance(1200000);d.finishPhase();assertTrue(d.blocksNormal());d.finish();assertFalse(d.blocksNormal())
        val rows=d.export().getJSONArray("runs");assertEquals(3,rows.length())
        val key=rows.getJSONObject(0).getString("comparisonKey")
        for(i in 0..2){assertEquals("seq-a",rows.getJSONObject(i).getString("automationId"));assertEquals(key,rows.getJSONObject(i).getString("comparisonKey"))}
    }
    @Test fun validationMetadataNeverMakesAnEnergyComparisonEligible() {
        val f=Fixture();val d=f.store();d.start("off",identity(),"seq-validation",true)
        f.advance(1800000);d.finishPhase()
        val row=last(d);assertEquals("completed",row.getString("status"));assertFalse(row.getBoolean("eligible"))
        assertTrue(row.getBoolean("validation"));assertTrue(issues(row).contains("validation_only"))
        assertThrows(IllegalStateException::class.java){d.start("capture_only",identity(),"seq-validation",false)}
        d.start("capture_only",identity(),"seq-validation",true)
        d.interrupt("charging")
        assertThrows(IllegalStateException::class.java){d.start("detection",identity(),"seq-validation",true)}
        d.finish()
    }
    @Test fun restartedAutomationPhaseCannotUseTheInternalGateContinuation() {
        val f=Fixture();val d=f.store();d.start("off",identity(),"seq-a");f.advance(60000)
        val restored=f.store();assertTrue(restored.blocksNormal())
        assertThrows(IllegalStateException::class.java){restored.start("capture_only",identity(),"seq-a")}
        restored.finish();assertFalse(restored.blocksNormal())
    }
}
