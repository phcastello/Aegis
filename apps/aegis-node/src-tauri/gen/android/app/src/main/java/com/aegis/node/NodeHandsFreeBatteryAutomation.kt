package com.aegis.node

import android.content.Context
import android.os.SystemClock
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

/** Pure persistent sequence owner. It schedules nothing and never opens audio.
 * A restored live plan is interrupted; only a fresh, explicit arm may start it.
 */
internal class HandsFreeBatteryAutomation internal constructor(
    saved:String?,
    private val save:(String)->Unit,
    private val elapsedClock:()->Long,
    private val newId:()->String={UUID.randomUUID().toString()}
) {
    constructor(context:Context):this(
        context.getSharedPreferences("aegis-hands-free-battery-auto-v1",Context.MODE_PRIVATE).getString("plan",null),
        {value->check(context.getSharedPreferences("aegis-hands-free-battery-auto-v1",Context.MODE_PRIVATE).edit().putString("plan",value).commit()){"battery_auto_storage"}},
        {SystemClock.elapsedRealtime()}
    )
    companion object {
        const val STAGE_DURATION_MS=30*60000L
        const val VALIDATION_DURATION_MS=60000L
        private val modes=listOf("off","capture_only","detection")
        private val liveStatuses=setOf("waiting_screen_off","transitioning","running")
        private fun validId(value:String)=value.matches(Regex("[A-Za-z0-9_-]{1,128}"))
        private fun empty()=JSONObject().put("schema",1).put("status","idle").put("id",JSONObject.NULL)
            .put("mode",JSONObject.NULL).put("stageIndex",JSONObject.NULL).put("validation",false)
            .put("requiresResume",false).put("runIds",JSONArray()).put("transitions",JSONArray()).put("token",JSONObject.NULL)
    }
    data class Token(val sequenceId:String,val stageIndex:Int,val runId:String,val deadlineElapsedMs:Long) {
        init {require(validId(sequenceId) && validId(runId) && stageIndex in 0..2 && deadlineElapsedMs>0){"battery_auto_token"}}
        internal fun json()=JSONObject().put("sequenceId",sequenceId).put("stageIndex",stageIndex)
            .put("runId",runId).put("deadlineElapsedMs",deadlineElapsedMs)
    }
    data class Step(val sequenceId:String,val stageIndex:Int,val validation:Boolean,val claimedAtElapsedMs:Long=0L) {
        init {require(validId(sequenceId) && stageIndex in 0..3 && claimedAtElapsedMs>=0){"battery_auto_step"}}
        val mode:String? get()=modes.getOrNull(stageIndex)
        val durationMs:Long get()=if(validation)VALIDATION_DURATION_MS else STAGE_DURATION_MS
    }
    private var root=try {
        val value=if(saved==null)empty() else JSONObject(saved)
        require(value.optInt("schema")==1)
        require(value.optString("status") in liveStatuses+setOf("idle","completed","cancelled","interrupted"))
        require(value.optJSONArray("runIds")?.length()?.let{it<=3}==true && value.optJSONArray("transitions")?.length()?.let{it<=4}==true)
        if(value.optString("status")!="idle") {
            require(validId(value.getString("id")))
            require(value.opt("validation") is Boolean && value.optInt("stageIndex") in 0..3)
        }
        value
    }catch(_:Exception){empty().put("id",UUID.randomUUID().toString()).put("stageIndex",0).put("stageDurationMs",STAGE_DURATION_MS)
        .put("status","interrupted").put("requiresResume",true).put("reason","saved_state_invalid")}
    @Volatile private var publishedBlocked=false
    @Volatile private var publishedLive=false
    @Volatile private var publishedWaiting=false
    @Volatile private var publishedToken:Token?=null
    init {
        if(root.optString("status") in liveStatuses) {
            root.put("status","interrupted").put("requiresResume",true).put("reason","process_restart")
                .put("mode",JSONObject.NULL).put("lastToken",root.opt("token")?:JSONObject.NULL).put("token",JSONObject.NULL)
            root.put("interruptedElapsedMs",readClock()?:JSONObject.NULL)
            try{save(root.toString())}catch(_:Exception){root.put("storageIssue","write_failed")}
        }
        publish()
    }
    fun blocksNormal():Boolean=publishedBlocked
    fun isLive():Boolean=publishedLive
    fun isWaiting():Boolean=publishedWaiting
    fun token():Token?=publishedToken

    @Synchronized fun arm(validation:Boolean):JSONObject {
        if(isLive())return state()
        check(!root.optBoolean("requiresResume")){"battery_auto_resume_required"}
        val now=now()
        val candidate=empty().put("id",newId()).put("status","waiting_screen_off").put("stageIndex",0)
            .put("validation",validation).put("stageDurationMs",if(validation)VALIDATION_DURATION_MS else STAGE_DURATION_MS)
            .put("armedElapsedMs",now).put("requiresResume",true)
        require(validId(candidate.getString("id"))){"battery_auto_id"}
        commit(candidate);return state()
    }
    @Synchronized fun claimScreenOff():Step? {
        if(root.optString("status")!="waiting_screen_off")return null
        val now=now()
        val step=Step(root.getString("id"),0,root.getBoolean("validation"),now)
        val candidate=copy().put("status","transitioning").put("stageIndex",0).put("mode","off")
            .put("transitionStartedElapsedMs",now)
        transition(candidate,0,null,now)
        commit(candidate);return step
    }
    @Synchronized fun claimDue(token:Token):Step? {
        if(root.optString("status")!="running" || token!=publishedToken)return null
        val now=now()
        if(now<token.deadlineElapsedMs)return null
        val next=token.stageIndex+1
        val step=Step(token.sequenceId,next,root.getBoolean("validation"),now)
        val candidate=copy().put("status","transitioning").put("stageIndex",next)
            .put("mode",step.mode?:JSONObject.NULL).put("lastToken",token.json()).put("token",JSONObject.NULL)
            .put("transitionStartedElapsedMs",now)
        transition(candidate,next,token.deadlineElapsedMs,now)
        commit(candidate);return step
    }
    @Synchronized fun running(step:Step,runId:String,startElapsedMs:Long,alarmPrecision:String="pending",
        preparationMs:Long?=null,measurementStartBasis:String?=null):Token {
        val existing=publishedToken
        if(root.optString("status")=="running" && existing!=null && existing.sequenceId==step.sequenceId &&
            existing.stageIndex==step.stageIndex && existing.runId==runId)return existing
        checkStep(step);require(step.stageIndex in 0..2 && validId(runId)){"battery_auto_run"}
        require(preparationMs==null || preparationMs>=0){"battery_auto_preparation"}
        require(measurementStartBasis==null || measurementStartBasis.length in 1..80){"battery_auto_basis"}
        require(alarmPrecision in setOf("pending","exact","inexact")){"battery_auto_precision"}
        val now=now()
        require(startElapsedMs>=step.claimedAtElapsedMs && startElapsedMs<=now && startElapsedMs<=Long.MAX_VALUE-step.durationMs){"battery_auto_start_clock"}
        val token=Token(step.sequenceId,step.stageIndex,runId,startElapsedMs+step.durationMs)
        val candidate=copy()
        val ids=candidate.getJSONArray("runIds")
        check(ids.length()==step.stageIndex && (0 until ids.length()).none{ids.getString(it)==runId}){"battery_auto_run_order"}
        ids.put(runId)
        candidate.put("status","running").put("mode",step.mode).put("token",token.json())
            .put("actualStartElapsedMs",startElapsedMs).put("deadlineElapsedMs",token.deadlineElapsedMs).put("alarmPrecision",alarmPrecision)
        candidate.getJSONArray("transitions").getJSONObject(candidate.getJSONArray("transitions").length()-1)
            .put("nextStartElapsedMs",startElapsedMs).put("transitionCompletedElapsedMs",now)
            .put("preparationMs",preparationMs?:JSONObject.NULL).put("measurementStartBasis",measurementStartBasis?:JSONObject.NULL)
        candidate.put("preparationMs",preparationMs?:JSONObject.NULL).put("measurementStartBasis",measurementStartBasis?:JSONObject.NULL)
        commit(candidate);return token
    }
    @Synchronized fun recordCaptureReady(step:Step,readyElapsedMs:Long,startupCpuNanos:Long?) {
        checkStep(step)
        require(step.mode=="capture_only" || step.mode=="detection"){"battery_auto_ready_mode"}
        require(readyElapsedMs>=step.claimedAtElapsedMs && readyElapsedMs<=now()){"battery_auto_ready_clock"}
        val candidate=copy()
        val transitions=candidate.getJSONArray("transitions")
        val entry=transitions.getJSONObject(transitions.length()-1)
        if(entry.has("captureReadyElapsedMs")) {
            check(entry.getLong("captureReadyElapsedMs")==readyElapsedMs){"battery_auto_ready_duplicate"}
            return
        }
        entry.put("captureReadyElapsedMs",readyElapsedMs).put("startupThreadCpuNanos",startupCpuNanos?.takeIf{it>=0}?:JSONObject.NULL)
        commit(candidate)
    }
    @Synchronized fun setAlarmPrecision(token:Token,precision:String) {
        require(precision in setOf("exact","inexact")){"battery_auto_precision"}
        if(root.optString("status")!="running" || token!=publishedToken)return
        commit(copy().put("alarmPrecision",precision))
    }
    @Synchronized fun complete(step:Step):JSONObject {
        if(root.optString("status")=="completed" && root.optString("id")==step.sequenceId)return state()
        checkStep(step);check(step.stageIndex==3 && root.getJSONArray("runIds").length()==3){"battery_auto_incomplete"}
        val now=now()
        val candidate=copy().put("status","completed").put("mode",JSONObject.NULL).put("token",JSONObject.NULL)
            .put("completedElapsedMs",now).put("requiresResume",false)
        candidate.getJSONArray("transitions").getJSONObject(candidate.getJSONArray("transitions").length()-1).put("transitionCompletedElapsedMs",now)
        commit(candidate);return state()
    }
    @Synchronized fun cancel():JSONObject {
        if(root.optString("status") in setOf("idle","completed","cancelled") && !root.optBoolean("requiresResume"))return state()
        val candidate=copy().put("status","cancelled").put("mode",JSONObject.NULL)
            .put("lastToken",root.opt("token")?:root.opt("lastToken")?:JSONObject.NULL).put("token",JSONObject.NULL)
            .put("cancelledElapsedMs",readClock()?:JSONObject.NULL).put("requiresResume",false)
        commit(candidate);return state()
    }
    @Synchronized fun interrupt(reason:String):JSONObject {
        if(!isLive() && root.optString("status")!="interrupted")return state()
        val candidate=copy().put("status","interrupted").put("mode",JSONObject.NULL)
            .put("reason",reason.take(80)).put("lastToken",root.opt("token")?:root.opt("lastToken")?:JSONObject.NULL)
            .put("token",JSONObject.NULL).put("interruptedElapsedMs",readClock()?:JSONObject.NULL).put("requiresResume",true)
        commit(candidate);return state()
    }
    @Synchronized fun state():JSONObject {
        val value=copy()
        val duration=if(root.optBoolean("validation"))VALIDATION_DURATION_MS else STAGE_DURATION_MS
        val remaining:Long?=when(root.optString("status")) {
            "waiting_screen_off"->3*duration
            "running"->publishedToken?.let{token->readClock()?.let{now->(token.deadlineElapsedMs-now).coerceAtLeast(0)+(2-token.stageIndex)*duration}}
            "completed"->0L
            else->null
        }
        val transitions=root.optJSONArray("transitions")
        val latest=transitions?.let{if(it.length()>0)it.optJSONObject(it.length()-1) else null}
        val preparationElapsed=if(root.optString("status")=="transitioning")readClock()?.let{now->
            (root.opt("transitionStartedElapsedMs") as? Number)?.toLong()?.takeIf{it<=now}?.let{now-it}
        } else null
        value.put("preparationElapsedMs",preparationElapsed?:JSONObject.NULL)
        value.put("stageDurationMs",duration).put("remainingEstimateMs",remaining?:JSONObject.NULL)
            .put("issue",root.opt("reason")?:JSONObject.NULL)
        if(value.optString("alarmPrecision")=="pending")value.remove("alarmPrecision")
        val delay=latest?.opt("delayMs") as? Number
        if(delay!=null)value.put("delayMs",delay) else value.remove("delayMs")
        return value
    }
    @Synchronized fun export():JSONObject=state()
    private fun checkStep(step:Step) {
        check(root.optString("status")=="transitioning" && root.optString("id")==step.sequenceId &&
            root.optInt("stageIndex")==step.stageIndex && root.optBoolean("validation")==step.validation &&
            root.optLong("transitionStartedElapsedMs",-1)==step.claimedAtElapsedMs){"battery_auto_stale_step"}
    }
    private fun transition(candidate:JSONObject,index:Int,planned:Long?,now:Long) {
        val transitions=candidate.getJSONArray("transitions");check(transitions.length()<4){"battery_auto_transition_limit"}
        transitions.put(JSONObject().put("stageIndex",index).put("plannedDeadlineElapsedMs",planned?:JSONObject.NULL)
            .put("callbackReceivedElapsedMs",now).put("transitionStartedElapsedMs",now)
            .put("delayMs",planned?.let{now-it}?:JSONObject.NULL).put("nextStartElapsedMs",JSONObject.NULL))
    }
    private fun readClock():Long?=try{elapsedClock().takeIf{it>=0}}catch(_:Exception){null}
    private fun now():Long=readClock()?:throw IllegalStateException("battery_auto_clock")
    private fun copy()=JSONObject(root.toString())
    private fun commit(candidate:JSONObject) {
        try {save(candidate.toString())}catch(error:Exception) {
            // Publish a terminal, safe in-memory gate even if the disk still has the old plan.
            root=copy().put("status","interrupted").put("mode",JSONObject.NULL).put("requiresResume",true)
                .put("reason","storage_failure").put("storageIssue","write_failed")
                .put("lastToken",root.opt("token")?:JSONObject.NULL).put("token",JSONObject.NULL)
            if(root.isNull("id"))root.put("id",UUID.randomUUID().toString()).put("stageIndex",0).put("stageDurationMs",STAGE_DURATION_MS)
            publish();throw error
        }
        root=candidate;publish()
    }
    private fun publish() {
        publishedBlocked=root.optString("status") in liveStatuses || root.optBoolean("requiresResume")
        publishedToken=if(root.optString("status")=="running")root.optJSONObject("token")?.let {
            try{Token(it.getString("sequenceId"),it.getInt("stageIndex"),it.getString("runId"),it.getLong("deadlineElapsedMs"))}catch(_:Exception){null}
        } else null
        publishedWaiting=root.optString("status")=="waiting_screen_off"
        publishedLive=root.optString("status") in liveStatuses
    }
}
