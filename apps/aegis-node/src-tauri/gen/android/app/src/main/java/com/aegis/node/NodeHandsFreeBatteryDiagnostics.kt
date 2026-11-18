package com.aegis.node

import android.content.Context
import android.os.BatteryManager
import android.os.SystemClock
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID
import java.security.MessageDigest

/** Bounded local study. Platform samples are observations of the whole phone.
 * capture()/component() only update counters; observe() borrows existing callbacks.
 * A restored/interrupted study blocks normal capture until explicit finish().
 */
internal class HandsFreeBatteryDiagnostics internal constructor(
    saved:String?,
    private val save:(String)->Unit,
    private val probe:()->JSONObject,
    private val elapsedClock:()->Long,
    private val newId:()->String={UUID.randomUUID().toString()}
) {
    constructor(context:Context):this(
        context.getSharedPreferences("aegis-hands-free-battery-v1",Context.MODE_PRIVATE).getString("study",null),
        {value->check(context.getSharedPreferences("aegis-hands-free-battery-v1",Context.MODE_PRIVATE).edit().putString("study",value).commit()){"battery_storage"}},
        HandsFreeBatteryProbe(context)::snapshot,{SystemClock.elapsedRealtime()}
    )
    companion object {
        const val OBSERVATION_INTERVAL_MS=60000L
        const val MIN_WINDOW_MS=20*60000L
        const val MAX_RUNS=32
        const val MAX_SAMPLES=256
        const val MIN_CAPTURE_FRACTION=0.95
        const val MAX_SCREEN_ON_FRACTION=0.05
        private val modes=setOf("off","capture_only","detection")
        private val componentKinds=setOf("detector","verifier","capture_thread")
        private fun comparisonKey(identity:JSONObject,snapshot:JSONObject):String {
            val stableIdentity=JSONObject(identity.toString())
            // Study controls and observations never define the app/model/device group.
            for(key in listOf("mode","id","runId","at","startedAt","endedAt","elapsedMs","uptimeMs","processId","processCpuMs",
                "batteryPercent","chargeMicroAh","energyNanoWh","currentMicroA","currentAverageMicroA","plugged","batteryStatus","charging",
                "temperatureDeciC","voltageMv","screenInteractive","locked"))stableIdentity.remove(key)
            val device=JSONObject()
            for(key in listOf("sdkInt","manufacturer","model","androidVersion","androidBuildFingerprint"))device.put(key,snapshot.opt(key)?:JSONObject.NULL)
            val value=JSONObject().put("identity",stableIdentity).put("device",device)
            fun canonical(entry:Any?):String=when(entry) {
                is JSONObject->entry.keys().asSequence().sorted().joinToString(",","{","}"){JSONObject.quote(it)+":"+canonical(entry.opt(it))}
                is JSONArray->(0 until entry.length()).joinToString(",","[","]"){canonical(entry.opt(it))}
                is String->JSONObject.quote(entry)
                is Number->JSONObject.numberToString(entry)
                is Boolean->entry.toString()
                else->"null"
            }
            return MessageDigest.getInstance("SHA-256").digest(canonical(value).toByteArray(Charsets.UTF_8)).joinToString(""){"%02x".format(it)}
        }
        private fun restore(saved:String?):JSONObject {
            if(saved==null)return JSONObject()
            return try {
                val value=JSONObject(saved)
                require(!value.has("schema") || value.optInt("schema")==1)
                require(!value.has("requiresResume") || value.opt("requiresResume") is Boolean)
                val rows=value.optJSONArray("runs")
                require(!value.has("runs") || rows!=null)
                if(rows!=null)for(i in 0 until rows.length()) {
                    val row=checkNotNull(rows.optJSONObject(i))
                    require(row.opt("id") is String && row.getString("id").isNotEmpty())
                    require(row.optString("mode") in modes && row.optString("status") in setOf("active","completed","interrupted"))
                    require(row.optJSONObject("components")!=null && row.optJSONObject("identity")!=null)
                    for(key in listOf("captureInputSamples","detectionInputSamples"))require((row.opt(key) as? Number)?.toLong()?.let{it>=0}==true)
                    val issues=checkNotNull(row.optJSONArray("issues"))
                    for(j in 0 until issues.length())require(issues.opt(j) is String)
                    val samples=checkNotNull(row.optJSONArray("samples"));require(samples.length()>0)
                    for(j in 0 until samples.length()) {
                        val sample=checkNotNull(samples.optJSONObject(j))
                        for(key in listOf("screenInteractive","charging","locked"))if(sample.has(key) && !sample.isNull(key))require(sample.opt(key) is Boolean)
                    }
                    val components=row.getJSONObject("components")
                    for(key in components.keys()) {
                        require(key in componentKinds)
                        val bucket=checkNotNull(components.optJSONObject(key))
                        for(field in listOf("calls","inputSamples","wallNanos","cpuNanos"))require((bucket.opt(field) as? Number)?.toLong()?.let{it>=0}==true)
                        require(!bucket.has("valid") || bucket.opt("valid") is Boolean)
                    }
                }
                value
            }catch(_:Exception){JSONObject().put("requiresResume",true).put("storageIssue","saved_state_invalid")}
        }
    }
    private var root=restore(saved)
    private val runs=root.optJSONArray("runs")?:JSONArray().also{root.put("runs",it)}
    private var active:JSONObject?=null
    @Volatile private var publishedMode:String?=null
    @Volatile private var publishedStartElapsedMs:Long?=null
    // The audio/verifier threads never acquire the JSON/IO monitor of this class.
    // This separate monitor protects only primitive counters and bounded issue codes.
    private class RunCounters(val id:String,val mode:String) {
        var capture=0L;var detection=0L
        val values=Array(3){LongArray(4)} // calls, input samples, wall ns, CPU ns
        val seen=BooleanArray(3);val valid=BooleanArray(3){true}
        val issues=linkedSetOf<String>()
        fun issue(code:String){if(issues.size<32)issues.add(code)}
        fun add(before:Long,value:Long):Long {
            if(value>Long.MAX_VALUE-before){issue("counter_overflow");return before}
            return before+value
        }
    }
    private data class CounterSnapshot(val capture:Long,val detection:Long,val values:Array<LongArray>,val seen:BooleanArray,val valid:BooleanArray,val issues:List<String>)
    @Volatile private var activeCounters:RunCounters?=null
    private var latest:JSONObject?=root.optJSONObject("latest")
    private var lastProbeAt:Long?=null
    @Volatile private var requiresResume=root.optBoolean("requiresResume",false)
    init {
        root.put("schema",1)
        while(runs.length()>MAX_RUNS)runs.remove(0)
        active=(0 until runs.length()).map{runs.getJSONObject(it)}.lastOrNull{it.optString("status")=="active"}
        if(active!=null) {
            // Never resume a microphone or infer an unobserved CPU/battery interval.
            requiresResume=true
            active?.let{issue(it,"process_restart")}
            closeRun("interrupted","observation_incomplete",takeSnapshot())
            persist()
        } else if(saved!=null && requiresResume)persist()
    }
    fun activeMode():String?=publishedMode
    fun activeRunId():String?=activeCounters?.id
    fun activeStartElapsedMs():Long?=publishedStartElapsedMs
    fun blocksNormal():Boolean=publishedMode!=null || requiresResume

    @Synchronized fun start(mode:String,identity:JSONObject,automationId:String?=null,validation:Boolean=false,
        preparationMs:Long?=null,measurementStartBasis:String?=null):JSONObject {
        active?.let{return state()}
        require(mode in modes){"battery_mode"}
        require(preparationMs==null || preparationMs>=0){"battery_preparation"}
        require(measurementStartBasis==null || measurementStartBasis.length in 1..80){"battery_start_basis"}
        require(automationId==null || automationId.matches(Regex("[A-Za-z0-9_-]{1,128}"))){"battery_automation_id"}
        require(!validation || automationId!=null){"battery_validation_requires_automation"}
        if(requiresResume) {
            val previous=if(runs.length()>0)runs.optJSONObject(runs.length()-1) else null
            check(automationId!=null && previous!=null && !previous.isNull("automationId") &&
                previous.optString("automationId")==automationId && previous.optString("status")=="completed" &&
                previous.optBoolean("validation",false)==validation){"battery_resume_required"}
        }
        val snapshot=takeSnapshot()
        val row=JSONObject().put("id",newId()).put("mode",mode).put("status","active")
            .put("comparisonKey",comparisonKey(identity,snapshot))
            .put("automationId",automationId?:JSONObject.NULL).put("validation",validation)
            .put("preparationMs",preparationMs?:JSONObject.NULL).put("measurementStartBasis",measurementStartBasis?:JSONObject.NULL)
            .put("identity",JSONObject(identity.toString())).put("startedAt",snapshot.opt("at")?:JSONObject.NULL)
            .put("endedAt",JSONObject.NULL).put("durationMs",JSONObject.NULL)
            .put("eligible",false).put("issues",JSONArray()).put("samples",JSONArray())
            .put("captureInputSamples",0L).put("detectionInputSamples",0L)
            .put("components",JSONObject())
        runs.put(row);while(runs.length()>MAX_RUNS){runs.remove(0);root.put("runsDropped",root.optLong("runsDropped")+1)}
        active=row;activeCounters=RunCounters(row.getString("id"),mode);publishedStartElapsedMs=snapshot.number("elapsedMs");publishedMode=mode;requiresResume=false
        if(validation)issue(row,"validation_only")
        appendSnapshot(row,snapshot);updateSummary(row)
        try{persist(strict=true)}catch(error:Exception){
            requiresResume=true;closeRun("interrupted","storage_failure",snapshot);throw error
        }
        return state()
    }
    /** Explicit action: caller stops/joins the diagnostic worker before releasing this gate. */
    @Synchronized fun finish():JSONObject {
        // Keep the published gate closed until the explicit finish is durable.
        requiresResume=true
        if(active!=null)closeRun("completed",null,takeSnapshot())
        persist(strict=true,resumeValue=false)
        requiresResume=false
        return state()
    }
    /** Automation-only boundary: complete this row but keep normal listening blocked. */
    @Synchronized fun finishPhase():JSONObject {
        if(active==null)return state()
        requiresResume=true
        closeRun("completed",null,takeSnapshot())
        persist(strict=true)
        return state()
    }
    @Synchronized fun interrupt(reason:String) {
        if(active==null && !requiresResume)return
        requiresResume=true
        if(active!=null)closeRun("interrupted",reason.take(80),takeSnapshot())
        persist()
    }
    @Synchronized fun observe(force:Boolean=false) {
        val now=clock()
        val previous=lastProbeAt
        if(!force && previous!=null && now!=null && now>=previous && now-previous<OBSERVATION_INTERVAL_MS)return
        // No idle/background polling: only a running study needs periodic platform IPC.
        if(active==null)return
        val snapshot=takeSnapshot()
        active?.let{appendSnapshot(it,snapshot);updateSummary(it)}
        persist()
    }
    fun capture(inputSamples:Int,detecting:Boolean) {
        val counters=activeCounters?:return
        synchronized(counters) {
            if(inputSamples<0){counters.issue("invalid_capture");return}
            counters.capture=counters.add(counters.capture,inputSamples.toLong())
            if(detecting)counters.detection=counters.add(counters.detection,inputSamples.toLong())
            if(counters.mode=="off" && inputSamples>0 || counters.mode=="capture_only" && detecting)counters.issue("scenario_mismatch")
        }
    }
    fun component(kind:String,inputSamples:Int,wallNanos:Long,cpuNanos:Long) {
        val counters=activeCounters?:return
        synchronized(counters) {
            val index=when(kind){"detector"->0;"verifier"->1;"capture_thread"->2;else->{counters.issue("invalid_component");return}}
            counters.seen[index]=true
            if(inputSamples<0 || wallNanos<0 || cpuNanos<0){counters.valid[index]=false;counters.issue("invalid_component");return}
            val bucket=counters.values[index]
            bucket[0]=counters.add(bucket[0],1);bucket[1]=counters.add(bucket[1],inputSamples.toLong())
            bucket[2]=counters.add(bucket[2],wallNanos);bucket[3]=counters.add(bucket[3],cpuNanos)
            if(counters.mode!="detection" && index!=2 || counters.mode=="off" && index==2)counters.issue("scenario_mismatch")
        }
    }
    private fun flushCounters(row:JSONObject) {
        val counters=activeCounters?:return
        if(counters.id!=row.getString("id"))return
        // Copy primitive state quickly. All JSONObject work happens after releasing it.
        val copied=synchronized(counters){CounterSnapshot(counters.capture,counters.detection,
            Array(3){counters.values[it].copyOf()},counters.seen.copyOf(),counters.valid.copyOf(),counters.issues.toList())}
        row.put("captureInputSamples",copied.capture).put("detectionInputSamples",copied.detection)
        val values=copied.values;val seen=copied.seen;val valid=copied.valid
        val components=JSONObject()
        for(i in 0..2)if(seen[i])components.put(listOf("detector","verifier","capture_thread")[i],JSONObject()
            .put("valid",valid[i]).put("calls",values[i][0]).put("inputSamples",values[i][1])
            .put("wallNanos",values[i][2]).put("cpuNanos",values[i][3]))
        row.put("components",components)
        copied.issues.forEach{issue(row,it)}
    }
    @Synchronized fun state():JSONObject {
        active?.let{updateSummary(it)}
        val compact=JSONArray()
        for(i in maxOf(0,runs.length()-8) until runs.length()) {
            val row=JSONObject(runs.getJSONObject(i).toString())
            row.remove("samples");row.remove("identity");row.remove("components")
            row.remove("captureInputSamples");row.remove("detectionInputSamples")
            compact.put(row)
        }
        val running=active?.let { row->
            val start=first(row)?.number("elapsedMs");val now=clock()
            JSONObject().put("id",row.getString("id")).put("mode",row.getString("mode"))
                .put("startedAt",row.opt("startedAt")?:JSONObject.NULL)
                .put("elapsedMs",if(start!=null && now!=null && now>=start)now-start else JSONObject.NULL)
        }
        return JSONObject().put("schema",1).put("active",running?:JSONObject.NULL).put("requiresResume",requiresResume)
            .put("latest",latest?.let{JSONObject(it.toString())}?:JSONObject.NULL).put("runs",compact)
    }
    @Synchronized fun export():JSONObject {
        observe(force=true)
        root.put("schema",1).put("requiresResume",requiresResume).put("latest",latest?:JSONObject.NULL)
        return JSONObject(root.toString()).put("scope","whole_phone_battery; process_and_thread_cpu_are_separate_observations")
            .put("observationIntervalMs",OBSERVATION_INTERVAL_MS).put("minimumWindowMs",MIN_WINDOW_MS)
            .put("minimumCaptureFraction",MIN_CAPTURE_FRACTION).put("maximumScreenOnFraction",MAX_SCREEN_ON_FRACTION)
            .put("screenOnBasis","previous observed screen state between samples; unobserved transitions are unknown")
            .put("uidHealthScope","own UID Android accounting; audio timer is not measured microphone energy")
    }
    private fun clock():Long?=try{elapsedClock().takeIf{it>=0}}catch(_:Exception){null}
    private fun takeSnapshot():JSONObject {
        val value=try{JSONObject(probe().toString())}catch(_:Exception){JSONObject().put("probeFailed",true)}
        latest=value;lastProbeAt=clock()
        return value
    }
    private fun appendSnapshot(row:JSONObject,snapshot:JSONObject) {
        val samples=row.getJSONArray("samples")
        val previous=if(samples.length()>0)samples.getJSONObject(samples.length()-1) else null
        if(snapshot.number("elapsedMs")==null || snapshot.number("uptimeMs")==null)issue(row,"observation_incomplete")
        if(snapshot.optBoolean("probeFailed"))issue(row,"observation_incomplete")
        if(snapshot.number("plugged")==null || snapshot.number("batteryStatus")==null)issue(row,"observation_incomplete")
        if(snapshot.optBoolean("charging",false) || (snapshot.number("plugged")?:0)>0 || snapshot.number("batteryStatus") in listOf(BatteryManager.BATTERY_STATUS_CHARGING.toLong(),BatteryManager.BATTERY_STATUS_FULL.toLong()))issue(row,"charging")
        if(previous!=null) {
            if(previous.number("plugged")!=null && snapshot.number("plugged")!=null && previous.number("plugged")!=snapshot.number("plugged") ||
                !previous.isNull("charging") && !snapshot.isNull("charging") && previous.has("charging") && snapshot.has("charging") && previous.getBoolean("charging")!=snapshot.getBoolean("charging"))issue(row,"charging_transition")
            val elapsed=delta(previous,snapshot,"elapsedMs")
            if(elapsed==null)issue(row,"clock_reset")
            if(previous.optString("processId")!=snapshot.optString("processId"))issue(row,"process_restart")
            val before=previous.number("processCpuMs");val after=snapshot.number("processCpuMs")
            if(before!=null && after!=null && after<before)issue(row,"process_cpu_reset")
        }
        samples.put(JSONObject(snapshot.toString()))
        if(samples.length()>MAX_SAMPLES) {
            // Keep the true start plus the newest bounded observations.
            samples.remove(1);row.put("samplesDropped",row.optLong("samplesDropped")+1);issue(row,"storage_limit")
        }
    }
    private fun closeRun(status:String,reason:String?,snapshot:JSONObject) {
        val row=active?:return
        appendSnapshot(row,snapshot)
        row.put("status",status).put("endedAt",snapshot.opt("at")?:JSONObject.NULL)
        if(reason!=null)issue(row,reason)
        updateSummary(row)
        val duration=row.number("durationMs")
        if(duration==null || duration<MIN_WINDOW_MS)issue(row,"short_window")
        val start=first(row);val end=last(row)
        if(start==null || end==null || start.number("processCpuMs")==null || end.number("processCpuMs")==null ||
            (start.number("chargeMicroAh")==null || end.number("chargeMicroAh")==null) &&
            (start.number("energyNanoWh")==null || end.number("energyNanoWh")==null) &&
            (start.number("batteryPercent")==null || end.number("batteryPercent")==null))issue(row,"observation_incomplete")
        val summary=row.getJSONObject("summary")
        val audioMs=row.optLong("captureInputSamples")/16.0
        val mode=row.getString("mode")
        if(mode!="off" && (duration==null || duration<=0 || audioMs/duration<MIN_CAPTURE_FRACTION))issue(row,"capture_gap")
        if(mode=="detection" && row.optLong("captureInputSamples")>0 &&
            row.optLong("detectionInputSamples").toDouble()/row.optLong("captureInputSamples")<MIN_CAPTURE_FRACTION)issue(row,"detection_gap")
        if(summary.isNull("screenOnMs") || duration==null || duration<=0)issue(row,"observation_incomplete")
        else if(summary.getLong("screenOnMs").toDouble()/duration>MAX_SCREEN_ON_FRACTION)issue(row,"screen_on")
        val components=row.getJSONObject("components")
        if(row.optLong("captureInputSamples")>0 && !components.has("capture_thread") ||
            row.optLong("detectionInputSamples")>0 && !components.has("detector"))issue(row,"observation_incomplete")
        if(status!="completed")issue(row,"observation_incomplete")
        row.put("eligible",status=="completed" && row.getJSONArray("issues").length()==0)
        active=null;activeCounters=null;publishedMode=null;publishedStartElapsedMs=null
    }
    private fun updateSummary(row:JSONObject) {
        flushCounters(row)
        val start=first(row);val end=last(row)
        val duration=if(start!=null && end!=null && !hasIssue(row,"clock_reset"))delta(start,end,"elapsedMs") else null
        row.put("durationMs",duration?:JSONObject.NULL)
        val summary=JSONObject()
        fun difference(field:String):Double? {
            val a=start?.decimal(field);val b=end?.decimal(field)
            return if(a!=null && b!=null)a-b else null
        }
        val charge=difference("chargeMicroAh")?.div(1000.0)
        val energy=difference("energyNanoWh")?.div(1_000_000.0)
        if(charge!=null && charge<0)issue(row,"charge_counter_increased")
        if(energy!=null && energy<0)issue(row,"energy_counter_increased")
        fun monotonic(field:String,source:String=field):Long? {
            if(start==null || end==null)return null
            return delta(start,end,source)
        }
        val process=if(start!=null && end!=null && start.optString("processId")==end.optString("processId") &&
            !hasIssue(row,"process_cpu_reset") && !hasIssue(row,"process_restart") && !hasIssue(row,"clock_reset"))
            monotonic("processCpuMs") else null
        val awake=if(!hasIssue(row,"clock_reset"))monotonic("uptimeMs") else null
        if(duration!=null && awake!=null && awake>duration+100)issue(row,"clock_reset")
        var screen=0L;var screenKnown=true
        val samples=row.getJSONArray("samples")
        for(i in 1 until samples.length()) {
            val previous=samples.getJSONObject(i-1);val current=samples.getJSONObject(i)
            val span=delta(previous,current,"elapsedMs")
            if(span==null || !previous.has("screenInteractive") || previous.isNull("screenInteractive"))screenKnown=false
            else if(previous.getBoolean("screenInteractive"))screen+=span
        }
        val components=row.getJSONObject("components")
        fun cpu(kind:String):Any {
            val bucket=components.optJSONObject(kind)?:return if(
                kind=="capture_thread" && row.optLong("captureInputSamples")>0 ||
                kind=="detector" && row.optLong("detectionInputSamples")>0)JSONObject.NULL else 0.0
            return if(bucket.optBoolean("valid",true))bucket.number("cpuNanos")?.div(1_000_000.0)?:JSONObject.NULL else JSONObject.NULL
        }
        summary.put("phoneChargeMah",charge?:JSONObject.NULL).put("phoneEnergyMwh",energy?:JSONObject.NULL)
            .put("phoneDischargeMahPerHour",if(charge!=null && duration!=null && duration>0)charge*3_600_000.0/duration else JSONObject.NULL)
            .put("batteryPercentPoints",difference("batteryPercent")?:JSONObject.NULL)
            .put("processCpuMs",process?:JSONObject.NULL)
            .put("captureThreadCpuMs",cpu("capture_thread")).put("detectorCpuMs",cpu("detector")).put("verifierCpuMs",cpu("verifier"))
            .put("awakeMs",if(duration!=null && awake!=null && awake<=duration+100)awake else JSONObject.NULL)
            .put("screenOnMs",if(screenKnown)screen else JSONObject.NULL)
            .put("captureAudioMs",row.optLong("captureInputSamples")/16.0)
            .put("verifierCandidates",components.optJSONObject("verifier")?.optLong("calls")?:0L)
        val healthStart=start?.optJSONObject("uidHealth");val healthEnd=end?.optJSONObject("uidHealth")
        for(field in listOf("userCpuMs","systemCpuMs","audioTimerMs")) {
            val before=healthStart?.number(field);val after=healthEnd?.number(field)
            if(before!=null && after!=null && after<before)issue(row,"uid_cpu_reset")
            summary.put("uid"+field.replaceFirstChar{it.uppercase()},if(before!=null && after!=null && after>=before && !hasIssue(row,"uid_cpu_reset"))after-before else JSONObject.NULL)
        }
        row.put("summary",summary)
    }
    private fun first(row:JSONObject)=row.getJSONArray("samples").let{if(it.length()>0)it.getJSONObject(0) else null}
    private fun last(row:JSONObject)=row.getJSONArray("samples").let{if(it.length()>0)it.getJSONObject(it.length()-1) else null}
    private fun JSONObject.number(key:String):Long?=(opt(key) as? Number)?.toLong()?.takeIf{it>=0}
    private fun JSONObject.decimal(key:String):Double?=(opt(key) as? Number)?.toDouble()?.takeIf{it.isFinite()}
    private fun delta(start:JSONObject,end:JSONObject,key:String):Long? {
        val before=start.number(key);val after=end.number(key)
        return if(before!=null && after!=null && after>=before)after-before else null
    }
    private fun hasIssue(row:JSONObject,code:String):Boolean=row.optJSONArray("issues")?.let{issues->(0 until issues.length()).any{issues.optString(it)==code}}?:false
    private fun issue(row:JSONObject,code:String) {
        val issues=row.optJSONArray("issues")?:JSONArray().also{row.put("issues",it)}
        if(issues.length()<32 && (0 until issues.length()).none{issues.optString(it)==code})issues.put(code)
    }
    private fun persist(strict:Boolean=false,resumeValue:Boolean=requiresResume) {
        root.put("schema",1).put("requiresResume",resumeValue).put("latest",latest?:JSONObject.NULL)
        try{save(root.toString());root.remove("storageIssue")}catch(error:Exception){
            root.put("storageIssue","write_failed").put("requiresResume",requiresResume);active?.let{issue(it,"storage_failure")}
            if(strict)throw error
        }
    }
}
