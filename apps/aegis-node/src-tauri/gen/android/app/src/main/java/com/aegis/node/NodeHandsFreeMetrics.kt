package com.aegis.node

import android.content.Context
import android.os.SystemClock
import org.json.JSONArray
import org.json.JSONObject
import java.time.ZonedDateTime
import java.time.Instant
import java.time.ZoneId
import java.util.UUID

/** No command text, audio, per-frame events, app usage or device screenshots. */
internal class HandsFreeMetrics internal constructor(
    saved:String?,
    private val save:(String)->Unit,
    private val elapsedClock:()->Long={SystemClock.elapsedRealtime()},
    private val wallClock:()->Long={System.currentTimeMillis()},
    private val zone:()->ZoneId={ZoneId.systemDefault()}
) {
    constructor(context:Context):this(
        context.getSharedPreferences("aegis-hands-free-metrics-v1",Context.MODE_PRIVATE).getString("aggregate",null),
        {value->context.getSharedPreferences("aegis-hands-free-metrics-v1",Context.MODE_PRIVATE).edit().putString("aggregate",value).apply()}
    )
    private val root=try{JSONObject(saved?:"{}")}catch(_:Exception){JSONObject()}
    private val days=root.optJSONObject("days")?:JSONObject().also{root.put("days",it)}
    private val events=root.optJSONArray("events")?:JSONArray().also{root.put("events",it)}
    private var lastElapsed=elapsedClock()
    private var lastWall=wallClock()
    private var persistedAt=lastElapsed
    private var requested=false
    private var diagnostic=false
    private var dayKey=""
    private var dayStart=0L
    private var dayEnd=0L
    private var activeZone=zone()
    private var lastTick=lastElapsed
    private var currentState="unavailable"
    init {
        root.put("schema",1).put("processId",UUID.randomUUID().toString())
        root.put("timezone",activeZone.id)
        if(saved!=null)event("process_restart_unknown_interval",JSONObject().put("since",root.optString("checkpointAt")).put("availability","unknown"))
        event("process_started")
    }
    private fun now()=Instant.ofEpochMilli(wallClock()).atZone(zone())
    private fun day(wall:Long=wallClock()):JSONObject {
        if(dayKey.isEmpty() || wall<dayStart || wall>=dayEnd) {
            val date=Instant.ofEpochMilli(wall).atZone(activeZone).toLocalDate()
            dayKey=date.toString();dayStart=date.atStartOfDay(activeZone).toInstant().toEpochMilli()
            dayEnd=date.plusDays(1).atStartOfDay(activeZone).toInstant().toEpochMilli()
        }
        if(!days.has(dayKey))days.put(dayKey,JSONObject())
        while(days.length()>14){days.keys().asSequence().filter{it!=dayKey}.sorted().firstOrNull()?.let{days.remove(it)}}
        return days.getJSONObject(dayKey)
    }
    private fun addAt(key:String,value:Long,wall:Long) {val d=day(wall);d.put(key,d.optLong(key)+value.coerceAtLeast(0))}
    @Synchronized fun capture(audioMs:Long,confirmedMs:Long,detecting:Boolean,locked:Boolean,interactive:Boolean) {
        add("captureAudioMs",audioMs);add("captureConfirmedMs",confirmedMs)
        if(detecting)add("detectionConfirmedMs",confirmedMs)
        if(locked)add("captureLockedConfirmedMs",confirmedMs)
        if(!interactive)add("captureScreenOffConfirmedMs",confirmedMs)
    }
    @Synchronized fun maybeTick(){if(elapsedClock()-lastTick>=10000){tick();lastTick=elapsedClock()}}
    @Synchronized fun add(key:String,value:Long=1) { val d=day();d.put(key,d.optLong(key)+value.coerceAtLeast(0)) }
    @Synchronized fun tick() {
        val elapsed=elapsedClock();val wall=wallClock();val delta=(elapsed-lastElapsed).coerceAtLeast(0)
        val observedZone=zone()
        if(observedZone!=activeZone) {
            event("timezone_changed",JSONObject().put("from",activeZone.id).put("to",observedZone.id)
                .put("previousObservationAt",Instant.ofEpochMilli(lastWall).atZone(activeZone).toString()))
            activeZone=observedZone;dayKey="";root.put("timezone",activeZone.id)
        }
        if(kotlin.math.abs((wall-lastWall)-delta)>60000)event("wall_clock_changed")
        // Attribute monotonic durations across local midnight; clock jumps are explicit events.
        var remaining=delta;var cursor=lastWall
        while(remaining>0) {
            day(cursor)
            val span=minOf(remaining,(dayEnd-cursor).coerceAtLeast(1))
            if(requested)addAt("requestedMs",span,cursor)
            addAt("state_${currentState}Ms",span,cursor)
            remaining-=span;cursor+=span
        }
        lastElapsed=elapsed;lastWall=wall
        if(elapsed-persistedAt>=60000)persist()
    }
    /** Process restoration is an observation, never a claim that the owner toggled the setting. */
    @Synchronized fun restoreRequested(value:Boolean,diagnosticValue:Boolean=false) {
        tick();requested=value;diagnostic=diagnosticValue
        root.put("requestedEnabled",value).put("diagnostic",diagnosticValue)
        event("configuration_restored",JSONObject().put("enabled",value).put("diagnostic",diagnosticValue));persist()
    }
    @Synchronized fun enabled(value:Boolean){configure(value,diagnostic)}
    @Synchronized fun configure(value:Boolean,diagnosticValue:Boolean) {
        if(requested==value && diagnostic==diagnosticValue)return
        tick();val enabledChanged=requested!=value;requested=value;diagnostic=diagnosticValue
        root.put("requestedEnabled",value).put("diagnostic",diagnosticValue)
        val at=now().toString();val d=day()
        val changes=d.optJSONArray("configurationTransitions")?:JSONArray().also{d.put("configurationTransitions",it)}
        changes.put(JSONObject().put("at",at).put("enabled",value).put("diagnostic",diagnosticValue))
        if(changes.length()>64){changes.remove(0);d.put("configurationTransitionsDropped",d.optLong("configurationTransitionsDropped")+1)}
        if(!d.has("configurationFirstChangedAt"))d.put("configurationFirstChangedAt",at)
        d.put("configurationLastChangedAt",at).put("configurationChanges",d.optLong("configurationChanges")+1)
            .put("configurationLastEnabled",value).put("configurationLastDiagnostic",diagnosticValue)
        event(if(enabledChanged){if(value)"enabled" else "disabled"}else "diagnostic_changed",
            JSONObject().put("enabled",value).put("diagnostic",diagnosticValue));persist()
    }
    @Synchronized fun state(value:String){tick();if(currentState!=value){currentState=value;event("state",JSONObject().put("value",value));persist()}}
    @Synchronized fun event(name:String,details:JSONObject=JSONObject()) {
        val event=JSONObject().put("at",now().toString()).put("elapsedMs",elapsedClock()).put("name",name).put("details",details)
        events.put(event)
        while(events.length()>128)events.remove(0)
    }
    @Synchronized fun device(locked:Boolean,interactive:Boolean,battery:Int?,charging:Boolean?) {
        tick()
        val at=now().toString();val percent=battery?.takeIf{it in 0..100}
        root.put("device",JSONObject().put("locked",locked).put("interactive",interactive).put("batteryPercent",percent?:JSONObject.NULL).put("charging",charging?:JSONObject.NULL).put("at",at))
        val d=day()
        if(percent!=null) {
            if(!d.has("batteryFirstPercent"))d.put("batteryFirstPercent",percent).put("batteryFirstAt",at)
            d.put("batteryLastPercent",percent).put("batteryLastAt",at)
                .put("batteryMinPercent",minOf(d.optInt("batteryMinPercent",percent),percent))
                .put("batteryMaxPercent",maxOf(d.optInt("batteryMaxPercent",percent),percent))
                .put("batterySamples",d.optLong("batterySamples")+1)
        }
        if(charging!=null) {
            if(d.has("chargingLast") && d.getBoolean("chargingLast")!=charging)
                d.put("chargingTransitions",d.optLong("chargingTransitions")+1)
            d.put("chargingLast",charging)
            val key=if(charging)"chargingSamples" else "notChargingSamples"
            d.put(key,d.optLong(key)+1)
        }
    }
    @Synchronized fun identity(version:String,build:String,modelReady:Boolean,role:Boolean,permission:Boolean) {
        if(root.has("assistantSelected") && (root.optBoolean("assistantSelected")!=role || root.optBoolean("permissionGranted")!=permission))event("prerequisites_changed",JSONObject().put("assistantSelected",role).put("permissionGranted",permission))
        root.put("appVersion",version).put("build",build).put("modelVersion","aegis-mww-fleurs-dev-2026-10-31").put("modelSha256",HandsFreeModel.SHA256)
          .put("config",JSONObject().put("sampleRate",16000).put("featureStepMs",10).put("stride",3).put("threshold",.5).put("scoreWindow",5).put("hysteresisFraction",.6).put("refractoryMs",1000))
          .put("modelReady",modelReady).put("assistantSelected",role).put("permissionGranted",permission)
    }
    @Synchronized fun persist() {
        root.put("checkpointAt",now().toString()).put("checkpointElapsedMs",elapsedClock())
        save(root.toString());persistedAt=elapsedClock()
    }
    @Synchronized fun export():String{tick();persist();return root.toString(2)}
}
