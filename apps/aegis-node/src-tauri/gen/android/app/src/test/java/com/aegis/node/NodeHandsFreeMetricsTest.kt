package com.aegis.node

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.time.ZoneId
import java.time.ZonedDateTime

class NodeHandsFreeMetricsTest {
    private val zone=ZoneId.of("America/Sao_Paulo")
    @Test fun restartDoesNotBackfillConfirmedOrRequestedAvailability() {
        var elapsed=0L;var wall=ZonedDateTime.parse("2026-10-31T12:00:00-03:00").toInstant().toEpochMilli();var saved=""
        val first=HandsFreeMetrics(null,{saved=it},{elapsed},{wall},{zone})
        first.enabled(true);first.capture(1000,1000,true,true,false)
        elapsed=60000;wall+=60000;first.tick()
        val restarted=HandsFreeMetrics(saved,{saved=it},{0L},{wall+3600000},{zone})
        restarted.enabled(true)
        val summary=JSONObject(restarted.export());val day=summary.getJSONObject("days").getJSONObject("2026-10-31")
        assertEquals(60000,day.getLong("requestedMs"));assertEquals(1000,day.getLong("captureConfirmedMs"))
        assertTrue(summary.getJSONArray("events").toString().contains("process_restart_unknown_interval"))
    }
    @Test fun monotonicDurationSplitsAtLocalMidnightWithoutCompletingMissingHours() {
        var elapsed=0L;var wall=ZonedDateTime.parse("2026-10-31T23:59:59-03:00").toInstant().toEpochMilli()
        val metrics=HandsFreeMetrics(null,{},{elapsed},{wall},{zone});metrics.enabled(true)
        elapsed=2000;wall+=2000
        val days=JSONObject(metrics.export()).getJSONObject("days")
        assertEquals(1000,days.getJSONObject("2026-10-31").getLong("requestedMs"))
        assertEquals(1000,days.getJSONObject("2026-11-01").getLong("requestedMs"))
    }
    @Test fun clockJumpDoesNotCreateListeningTimeAndEventBufferRemainsLive() {
        var elapsed=0L;var wall=ZonedDateTime.parse("2026-11-01T12:00:00-03:00").toInstant().toEpochMilli()
        val metrics=HandsFreeMetrics(null,{},{elapsed},{wall},{zone});metrics.enabled(true)
        elapsed=1000;wall+=86400000;metrics.tick()
        repeat(300){metrics.event("test_event")};metrics.event("last_event")
        val summary=JSONObject(metrics.export());val events=summary.getJSONArray("events")
        assertEquals(128,events.length());assertEquals("last_event",events.getJSONObject(127).getString("name"))
        val days=summary.getJSONObject("days");var requested=0L
        for(key in days.keys())requested+=days.getJSONObject(key).optLong("requestedMs")
        assertEquals(1000,requested)
    }
    @Test fun receivedAudioWhileProcessingIsNotReportedAsDetection() {
        val wall=ZonedDateTime.parse("2026-11-01T12:00:00-03:00").toInstant().toEpochMilli()
        val metrics=HandsFreeMetrics(null,{},{0L},{wall},{zone});metrics.enabled(true)
        metrics.capture(200,190,false,true,false)
        val day=JSONObject(metrics.export()).getJSONObject("days").getJSONObject("2026-11-01")
        assertEquals(190,day.getLong("captureConfirmedMs"));assertEquals(0,day.optLong("detectionConfirmedMs"))
        assertEquals(190,day.getLong("captureScreenOffConfirmedMs"))
    }
    @Test fun completedDaysKeepObservedBatteryRangesAndChargingSamples() {
        var elapsed=0L;var wall=ZonedDateTime.parse("2026-11-01T12:00:00-03:00").toInstant().toEpochMilli()
        val metrics=HandsFreeMetrics(null,{},{elapsed},{wall},{zone})
        metrics.device(true,false,80,false)
        elapsed+=1000;wall+=1000;metrics.device(true,false,70,true)
        elapsed+=86400000;wall+=86400000;metrics.device(false,true,90,true)
        val days=JSONObject(metrics.export()).getJSONObject("days")
        val first=days.getJSONObject("2026-11-01")
        assertEquals(80,first.getInt("batteryFirstPercent"));assertEquals(70,first.getInt("batteryLastPercent"))
        assertEquals(70,first.getInt("batteryMinPercent"));assertEquals(80,first.getInt("batteryMaxPercent"))
        assertEquals(2,first.getInt("batterySamples"));assertEquals(1,first.getInt("chargingTransitions"))
        assertEquals(1,first.getInt("chargingSamples"));assertEquals(1,first.getInt("notChargingSamples"))
        assertEquals(90,days.getJSONObject("2026-11-02").getInt("batteryFirstPercent"))
    }
    @Test fun unknownBatteryAndChargeStateDoNotInventObservations() {
        val wall=ZonedDateTime.parse("2026-11-01T12:00:00-03:00").toInstant().toEpochMilli()
        val metrics=HandsFreeMetrics(null,{},{0L},{wall},{zone})
        metrics.device(false,true,null,null);metrics.device(false,true,-1,null)
        val d=JSONObject(metrics.export()).getJSONObject("days").getJSONObject("2026-11-01")
        assertFalse(d.has("batteryFirstPercent"));assertFalse(d.has("chargingSamples"));assertFalse(d.has("chargingLast"))
    }
    @Test fun timezoneChangeInvalidatesDayCacheAndRecordsBothOffsets() {
        var elapsed=0L;val wall=ZonedDateTime.parse("2026-11-01T23:30:00-03:00").toInstant().toEpochMilli()
        var currentZone=zone
        val metrics=HandsFreeMetrics(null,{},{elapsed},{wall+elapsed},{currentZone})
        metrics.capture(200,200,true,false,true)
        currentZone=ZoneId.of("Asia/Tokyo");elapsed=1000;metrics.tick()
        metrics.capture(300,300,true,false,true)
        val summary=JSONObject(metrics.export());val days=summary.getJSONObject("days")
        assertEquals(200,days.getJSONObject("2026-11-01").getLong("captureConfirmedMs"))
        assertEquals(300,days.getJSONObject("2026-11-02").getLong("captureConfirmedMs"))
        assertEquals("Asia/Tokyo",summary.getString("timezone"))
        val events=summary.getJSONArray("events")
        val change=(0 until events.length()).map{events.getJSONObject(it)}.single{it.getString("name")=="timezone_changed"}
        assertEquals("America/Sao_Paulo",change.getJSONObject("details").getString("from"))
        assertEquals("Asia/Tokyo",change.getJSONObject("details").getString("to"))
    }
    @Test fun actualConfigurationTimelineSurvivesEventEvictionAndPreferenceRestoration() {
        var elapsed=0L;var wall=ZonedDateTime.parse("2026-11-01T12:00:00-03:00").toInstant().toEpochMilli();var saved=""
        val first=HandsFreeMetrics(null,{saved=it},{elapsed},{wall},{zone})
        first.restoreRequested(true,false)
        first.configure(true,false) // Same preference and mode are not a user change.
        elapsed+=1000;wall+=1000;first.configure(true,true) // Diagnostic only, no fake enable.
        elapsed+=1000;wall+=1000;first.configure(false,true)
        repeat(200){first.event("session_event")}
        val before=JSONObject(first.export());val day=before.getJSONObject("days").getJSONObject("2026-11-01")
        assertEquals(2,day.getInt("configurationChanges"));assertEquals(2,day.getJSONArray("configurationTransitions").length())
        assertTrue(day.getJSONArray("configurationTransitions").getJSONObject(0).getBoolean("diagnostic"))
        assertTrue(day.getJSONArray("configurationTransitions").getJSONObject(0).getBoolean("enabled"))
        assertFalse(day.getJSONArray("configurationTransitions").getJSONObject(1).getBoolean("enabled"))
        assertTrue(before.getBoolean("diagnostic"));assertFalse(before.getBoolean("requestedEnabled"))
        val restarted=HandsFreeMetrics(saved,{saved=it},{0L},{wall+3600000},{zone})
        restarted.restoreRequested(false,true)
        val after=JSONObject(restarted.export())
        assertEquals(2,after.getJSONObject("days").getJSONObject("2026-11-01").getInt("configurationChanges"))
        val events=after.getJSONArray("events")
        assertEquals("configuration_restored",events.getJSONObject(events.length()-1).getString("name"))
        assertEquals(2000,after.getJSONObject("days").getJSONObject("2026-11-01").getLong("requestedMs"))
    }
    @Test fun excessiveConfigurationChangesRemainBoundedAndReportDroppedTimestamps() {
        val wall=ZonedDateTime.parse("2026-11-01T12:00:00-03:00").toInstant().toEpochMilli()
        val metrics=HandsFreeMetrics(null,{},{0L},{wall},{zone});metrics.restoreRequested(false,false)
        repeat(70){metrics.configure(it%2==0,false)}
        val d=JSONObject(metrics.export()).getJSONObject("days").getJSONObject("2026-11-01")
        assertEquals(70,d.getInt("configurationChanges"));assertEquals(64,d.getJSONArray("configurationTransitions").length())
        assertEquals(6,d.getInt("configurationTransitionsDropped"));assertTrue(d.has("configurationFirstChangedAt"));assertTrue(d.has("configurationLastChangedAt"))
    }
}
