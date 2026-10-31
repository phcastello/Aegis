package com.aegis.node

import android.content.Context
import android.os.SystemClock
import org.json.JSONArray
import org.json.JSONObject
import java.time.ZonedDateTime
import java.time.Instant
import java.time.ZoneId
import java.util.UUID
import java.security.MessageDigest

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
    private val identities=root.optJSONObject("identities")?:JSONObject().also{root.put("identities",it)}
    private val boundaries=root.optJSONArray("identityBoundaries")?:JSONArray().also{root.put("identityBoundaries",it)}
    private var observedIdentity:JSONObject?=null
    private var activeIdentity:String?=null
    private var activeFingerprint:String?=null
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
        if(root.optInt("schema",1)<2) {
            // Old totals have no period-level identity. A last-observed version is not
            // evidence that all earlier days were produced by it. Keep the bytes' data.
            for(key in days.keys()) {
                val day=days.getJSONObject(key)
                if(!day.has("unattributedBeforeSchema2"))day.put("unattributedBeforeSchema2",JSONObject(day.toString()))
            }
        }
        root.put("schema",2).put("processId",UUID.randomUUID().toString())
            .put("identityScope","current observation only; historical periods use byIdentity; legacy attribution is unknown")
        root.remove("currentIdentityId")
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
    private fun attributed(d:JSONObject,identity:String?=activeIdentity):JSONObject {
        val entries=d.optJSONObject("byIdentity")?:JSONObject().also{d.put("byIdentity",it)}
        val key=identity?.takeIf{identities.has(it) || it=="unknown_overflow"}?:"unknown"
        return entries.optJSONObject(key)?:JSONObject().also{entries.put(key,it)}
    }
    private fun increment(d:JSONObject,key:String,value:Long){d.put(key,d.optLong(key)+value.coerceAtLeast(0))}
    private fun addAt(key:String,value:Long,wall:Long) {
        val d=day(wall);increment(d,key,value);increment(attributed(d),key,value)
    }
    @Synchronized fun capture(audioMs:Long,confirmedMs:Long,detecting:Boolean,locked:Boolean,interactive:Boolean) {
        add("captureAudioMs",audioMs);add("captureConfirmedMs",confirmedMs)
        if(detecting)add("detectionConfirmedMs",confirmedMs)
        if(locked)add("captureLockedConfirmedMs",confirmedMs)
        if(!interactive)add("captureScreenOffConfirmedMs",confirmedMs)
    }
    @Synchronized fun maybeTick(){if(elapsedClock()-lastTick>=10000){tick();lastTick=elapsedClock()}}
    @Synchronized fun add(key:String,value:Long=1) {addAt(key,value,wallClock())}
    @Synchronized fun identityToken():String?=activeIdentity
    /** A remote operation retains the identity/configuration observed when it began. */
    @Synchronized fun addForIdentity(key:String,value:Long,identity:String?) {
        val d=day();increment(d,key,value);increment(attributed(d,identity),key,value)
    }
    @Synchronized fun timing(stage:String,milliseconds:Long,identity:String?) {
        val total=day()
        for(d in listOf(total,attributed(total,identity))) {
            val duration=milliseconds.coerceAtLeast(0)
            increment(d,"${stage}Ms",duration);increment(d,"${stage}Count",1)
            d.put("${stage}MinMs",minOf(d.optLong("${stage}MinMs",duration),duration))
                .put("${stage}MaxMs",maxOf(d.optLong("${stage}MaxMs",duration),duration))
        }
    }
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
        selectIdentity()
        event("configuration_restored",JSONObject().put("enabled",value).put("diagnostic",diagnosticValue));persist()
    }
    @Synchronized fun enabled(value:Boolean){configure(value,diagnostic)}
    @Synchronized fun configure(value:Boolean,diagnosticValue:Boolean) {
        if(requested==value && diagnostic==diagnosticValue)return
        tick();val enabledChanged=requested!=value;requested=value;diagnostic=diagnosticValue
        root.put("requestedEnabled",value).put("diagnostic",diagnosticValue)
        selectIdentity()
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
        val total=day()
        for(d in listOf(total,attributed(total))) {
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
    }
    @Synchronized fun identity(version:String,build:String,modelReady:Boolean,role:Boolean,permission:Boolean,
        modelVersion:String="aegis-mww-fleurs-dev-2026-10-31",modelSha256:String=HandsFreeModel.SHA256,
        detectorConfig:JSONObject=detectorConfiguration()) {
        tick()
        if(root.has("assistantSelected") && (root.optBoolean("assistantSelected")!=role || root.optBoolean("permissionGranted")!=permission))event("prerequisites_changed",JSONObject().put("assistantSelected",role).put("permissionGranted",permission))
        observedIdentity=JSONObject().put("appVersion",version).put("build",build).put("modelVersion",modelVersion)
            .put("modelSha256",modelSha256).put("config",JSONObject(detectorConfig.toString()))
        root.put("appVersion",version).put("build",build).put("modelVersion",modelVersion).put("modelSha256",modelSha256)
          .put("config",JSONObject(detectorConfig.toString()))
          .put("modelReady",modelReady).put("assistantSelected",role).put("permissionGranted",permission)
        selectIdentity()
    }
    private fun selectIdentity() {
        val identity=observedIdentity?.let{JSONObject(it.toString())}?:return
        identity.put("enabled",requested).put("diagnostic",diagnostic)
        // Canonical ordering is independent of JSONObject iteration order on Android/JVM.
        fun canonical(value:Any?):String=when(value) {
            is JSONObject->value.keys().asSequence().sorted().joinToString(",","{","}"){JSONObject.quote(it)+":"+canonical(value.get(it))}
            is JSONArray->(0 until value.length()).joinToString(",","[","]"){canonical(value.get(it))}
            is String->JSONObject.quote(value)
            is Number,is Boolean->value.toString()
            else->"null"
        }
        val key=MessageDigest.getInstance("SHA-256").digest(canonical(identity).toByteArray(Charsets.UTF_8))
            .take(12).joinToString(""){"%02x".format(it)}
        if(activeFingerprint==key)return
        activeFingerprint=key
        val selected=if(identities.has(key) || identities.length()<64)key else "unknown_overflow"
        if(selected==key && !identities.has(key))identities.put(key,identity)
        if(selected=="unknown_overflow")root.put("identityObservationsUnattributed",root.optLong("identityObservationsUnattributed")+1)
        activeIdentity=selected;root.put("currentIdentityId",selected)
        boundaries.put(JSONObject().put("at",now().toString()).put("elapsedMs",elapsedClock())
            .put("processId",root.getString("processId")).put("identityId",selected))
        while(boundaries.length()>128){boundaries.remove(0);root.put("identityBoundariesDropped",root.optLong("identityBoundariesDropped")+1)}
        event("identity_boundary",JSONObject().put("identityId",selected));persist()
    }
    companion object {
        private fun detectorConfiguration()=JSONObject().put("sampleRate",16000).put("featureStepMs",10).put("stride",3)
            .put("threshold",.5).put("scoreWindow",5).put("hysteresisFraction",.6).put("refractoryMs",1000)
            .put("endpointing",JSONObject().put("version",HandsFreeVad.VERSION).put("frameSamples",HandsFreeVad.FRAME_SAMPLES)
                .put("onsetSamples",HandsFreeVad.ONSET_SAMPLES).put("noSpeechSamples",HandsFreeVad.NO_SPEECH_SAMPLES))
            .put("tts","pcm24k_streaming")
            .put("activationCue",HandsFreeCueSignal.VERSION)
    }
    @Synchronized fun persist() {
        root.put("checkpointAt",now().toString()).put("checkpointElapsedMs",elapsedClock())
        save(root.toString());persistedAt=elapsedClock()
    }
    @Synchronized fun export():String{tick();persist();return root.toString(2)}
}
