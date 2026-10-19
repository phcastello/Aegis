package com.aegis.node

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.Looper
import com.google.android.gms.tasks.CancellationTokenSource
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID
import java.util.concurrent.Executors

/** All limits survive process death. No periodic work restarts an approach. */
object NodeMonitoringBudget {
 const val WINDOW_MILLIS=180000L
 const val MAX_ACQUISITIONS=6
 const val MIN_INTERVAL_MILLIS=30000L
 const val ACQUISITION_MILLIS=10000L
 const val COOLDOWN_MILLIS=900000L
 const val START_WINDOW_MILLIS=3600000L
 const val MAX_STARTS=3
 fun mayStart(now:Long,lastEnded:Long,starts:List<Long>):Boolean =
  (lastEnded==0L || now-lastEnded>=COOLDOWN_MILLIS) && starts.count{now-it in 0 until START_WINDOW_MILLIS}<MAX_STARTS
 fun mayAcquire(now:Long,deadline:Long,attempts:Int,lastAttempt:Long):Boolean =
  now<deadline && attempts<MAX_ACQUISITIONS && (lastAttempt==0L || now-lastAttempt>=MIN_INTERVAL_MILLIS)
}

/** Private durable session metadata contains no coordinates. A single FGS serves all places. */
object NodeMonitoringSessions {
 private val lock=Any()
 private val processInstance=UUID.randomUUID().toString()
 private fun prefs(context:Context)=context.getSharedPreferences("aegis-hybrid-sessions-v1",Context.MODE_PRIVATE)
 private fun rows(context:Context)=prefs(context).all.keys.filter{it.startsWith("session:")}.mapNotNull{prefs(context).getString(it,null)?.let{raw->JSONObject(raw)}}
 fun recoverInterrupted(context:Context) {
  val interrupted=synchronized(lock){rows(context).filter{it.optString("status")=="active" && (it.optString("processInstance")!=processInstance || it.getLong("deadline")<=System.currentTimeMillis())}}
  for(row in interrupted)finish(context,row.getString("placeId"),if(row.optString("processInstance")!=processInstance)"monitoring_interrupted" else "monitoring_session_expired")
 }
 fun active(context:Context,place:String):Boolean {recoverInterrupted(context);return synchronized(lock){rows(context).any{it.optString("placeId")==place && it.optString("status")=="active" && it.getLong("deadline")>System.currentTimeMillis()}}}
 fun status(context:Context,place:String):JSONObject? {recoverInterrupted(context);return synchronized(lock){prefs(context).getString("session:$place",null)?.let{JSONObject(it).apply{remove("scope");remove("configurationId");remove("id");remove("placeId");remove("processInstance")}}}}
 fun snapshot(context:Context):JSONObject {recoverInterrupted(context);return synchronized(lock) {
  JSONObject().put("running",NodeMonitoringService.running).put("serviceStarts",prefs(context).getInt("serviceStarts",0)).put("serviceStops",prefs(context).getInt("serviceStops",0)).put("fixtureAcquisitionInFlight",prefs(context).getInt("fixtureAcquisitionInFlight",0)).put("fixtureAcquisitionsStarted",prefs(context).getInt("fixtureAcquisitionsStarted",0)).put("fixtureAcquisitionsReleased",prefs(context).getInt("fixtureAcquisitionsReleased",0)).put("fixtureCancellationObserved",prefs(context).getInt("fixtureCancellationObserved",0)).put("fixtureInterruptionsObserved",prefs(context).getInt("fixtureInterruptionsObserved",0)).put("sessions",JSONArray(rows(context).map{it.apply{remove("scope");remove("configurationId");remove("placeId");remove("id");remove("processInstance")}}))
 }}
 fun serviceLifecycle(context:Context,start:Boolean) = synchronized(lock) {
  val key=if(start)"serviceStarts" else "serviceStops"
  prefs(context).edit().putInt(key,prefs(context).getInt(key,0)+1).commit()
 }
 fun fixture(context:Context,operation:String,placeId:String,scenario:String):JSONObject {
  check(BuildConfig.DEBUG || BuildConfig.AEGIS_FRAMEWORK_FIXTURE)
  when(operation) {
   "configure" -> {require(scenario in setOf("real","unavailable","denied","start_denied","inside","outside","blocking","provider_error"));prefs(context).edit().putString("fixtureScenario",scenario).putString("fixturePlace",placeId).commit()}
   "stop" -> cancelAll(context,"monitoring_cancelled")
   "expire" -> synchronized(lock){rows(context).filter{it.optString("status")=="active"}.forEach{record(context,it.put("deadline",System.currentTimeMillis()-1))}}
   "reset" -> {cancelAll(context,"monitoring_cancelled");prefs(context).edit().clear().commit()}
   "synchronize" -> NodeGeofences.sync(context,flushOccurrences=false)
   "drain" -> try{NodeGeofences.flushEvents(context)}catch(_:NodeNativeSession.HttpFailure){}
   "snapshot" -> Unit
   else -> throw IllegalArgumentException("Unknown hybrid fixture operation")
  }
  return snapshot(context)
 }
 fun fixtureStartAllowed(context:Context):Boolean =
  !(BuildConfig.DEBUG || BuildConfig.AEGIS_FRAMEWORK_FIXTURE) || prefs(context).getString("fixtureScenario","real")!="start_denied"
 private fun fixtureCounter(context:Context,key:String,delta:Int=1) = synchronized(lock) {
  prefs(context).edit().putInt(key,prefs(context).getInt(key,0)+delta).apply()
 }
 fun fixtureLocation(context:Context,request:com.google.android.gms.location.CurrentLocationRequest,cancel:CancellationTokenSource,timeout:Long):android.location.Location? {
  val scenario=if(BuildConfig.DEBUG || BuildConfig.AEGIS_FRAMEWORK_FIXTURE)prefs(context).getString("fixtureScenario","real") else "real"
  return when(scenario) {
   "unavailable" -> null
   "denied" -> throw SecurityException("fixture_background_denied")
   "provider_error" -> throw IllegalStateException("fixture_provider_error")
   "blocking" -> {
    // The actual SDK await/cancellation mechanism stays pending until the real
    // Android service is stopped. No callback/listener or executor is retained.
    val pending=com.google.android.gms.tasks.TaskCompletionSource<android.location.Location>(cancel.token)
    fixtureCounter(context,"fixtureAcquisitionsStarted");fixtureCounter(context,"fixtureAcquisitionInFlight")
    try {com.google.android.gms.tasks.Tasks.await(pending.task,timeout,java.util.concurrent.TimeUnit.MILLISECONDS)}
    catch(interrupted:InterruptedException){fixtureCounter(context,"fixtureInterruptionsObserved");throw interrupted}
    finally {
     if(cancel.token.isCancellationRequested)fixtureCounter(context,"fixtureCancellationObserved")
     fixtureCounter(context,"fixtureAcquisitionsReleased");fixtureCounter(context,"fixtureAcquisitionInFlight",-1)
    }
   }
   "inside","outside" -> NodeGeofences.fixtureLocation(context,prefs(context).getString("fixturePlace",null),scenario=="outside")
   else -> com.google.android.gms.tasks.Tasks.await(com.google.android.gms.location.LocationServices.getFusedLocationProviderClient(context).getCurrentLocation(request,cancel.token),timeout,java.util.concurrent.TimeUnit.MILLISECONDS)
  }
 }
 fun hasActive(context:Context):Boolean=synchronized(lock){rows(context).any{it.optString("status")=="active"}}
 private fun record(context:Context,row:JSONObject){check(prefs(context).edit().putString("session:${row.getString("placeId")}",row.toString()).commit())}
 /** Called exclusively after a fresh native callback has passed its installed allowlist. */
 fun begin(context:Context,place:String,configurationId:String,scope:String,trigger:String):Boolean {
  recoverInterrupted(context)
  var rejection:String?=null
  val prepared=synchronized(lock) {
   val now=System.currentTimeMillis()
   val cleanup=prefs(context).edit()
   for(old in rows(context).filter{it.optString("status")!="active" && now-it.optLong("endedAt",now)>7*86400000L})cleanup.remove("session:${old.getString("placeId")}").remove("starts:${old.getString("placeId")}")
   cleanup.commit()
   val previous=prefs(context).getString("session:$place",null)?.let{JSONObject(it)}
   // startForegroundService returns before onCreate. A second legitimate callback
   // in that short window joins the same reservation instead of cancelling it.
   if(previous?.optString("status")=="active" && previous.getLong("deadline")>now)return true
   val starts=prefs(context).getString("starts:$place",null)?.let{raw->JSONArray(raw).let{a->(0 until a.length()).map{a.getLong(it)}}}?:emptyList()
   val lastEnded=previous?.optLong("endedAt",0)?:0
   if(!NodeMonitoringBudget.mayStart(now,lastEnded,starts)) {
    rejection=if(lastEnded>0 && now-lastEnded<NodeMonitoringBudget.COOLDOWN_MILLIS)"monitoring_cooldown" else "monitoring_budget_exhausted"
    return@synchronized false
   }
   val row=JSONObject().put("id",UUID.randomUUID().toString()).put("placeId",place).put("scope",scope).put("processInstance",processInstance).put("configurationId",configurationId).put("trigger",trigger).put("status","active").put("startedAt",now).put("deadline",now+NodeMonitoringBudget.WINDOW_MILLIS).put("attempts",0).put("lastAttemptAt",0).put("acquired",0).put("unavailable",0)
   record(context,row)
   check(prefs(context).edit().putString("starts:$place",JSONArray(starts.filter{now-it in 0 until NodeMonitoringBudget.START_WINDOW_MILLIS}+now).toString()).commit())
   true
  }
  if(!prepared){NodeGeofences.monitoringHistory(context,place,rejection?:"monitoring_budget_exhausted");return false}
  NodeGeofences.monitoringHistory(context,place,"monitoring_session_started")
  return try {
   val intent=Intent(context,NodeMonitoringService::class.java)
   if(Build.VERSION.SDK_INT>=26)context.startForegroundService(intent)else context.startService(intent)
   true
  }catch(_:RuntimeException){finish(context,place,"monitoring_start_denied");false}
 }
 fun finish(context:Context,place:String,reason:String) {
  val changed=synchronized(lock) {
   val row=prefs(context).getString("session:$place",null)?.let{JSONObject(it)}?:return@synchronized false
   if(row.optString("status")!="active")return@synchronized false
   record(context,row.put("status",reason).put("endedAt",System.currentTimeMillis()));true
  }
  if(changed){NodeGeofences.monitoringFinished(context,place);NodeGeofences.monitoringHistory(context,place,reason)}
  if(!hasActive(context))context.stopService(Intent(context,NodeMonitoringService::class.java))
 }
 fun cancelAll(context:Context,reason:String="monitoring_interrupted") {
  val places=synchronized(lock){rows(context).filter{it.optString("status")=="active"}.map{it.getString("placeId")}}
  for(place in places)finish(context,place,reason)
 }
 /** Atomically reserve attempts before acquiring. A killed process cannot spend them again. */
 fun reserve(context:Context):List<JSONObject> {
  recoverInterrupted(context)
  val now=System.currentTimeMillis()
  val all=synchronized(lock){rows(context).filter{it.optString("status")=="active"}}
  for(row in all) {
   val place=row.getString("placeId")
   if(now>=row.getLong("deadline") || row.getInt("attempts")>=NodeMonitoringBudget.MAX_ACQUISITIONS)finish(context,place,"monitoring_session_expired")
   else if(!NodeGeofences.monitoringCurrent(context,place,row.getString("configurationId"),row.getString("scope")))finish(context,place,"monitoring_superseded")
  }
  return synchronized(lock){rows(context).filter{it.optString("status")=="active" && NodeMonitoringBudget.mayAcquire(now,it.getLong("deadline"),it.getInt("attempts"),it.getLong("lastAttemptAt"))}.map{row->record(context,row.put("attempts",row.getInt("attempts")+1).put("lastAttemptAt",now));row}}
 }
 fun result(context:Context,reservation:JSONObject,observation:JSONObject) {
  val place=reservation.getString("placeId")
  val valid=synchronized(lock) {
   val current=prefs(context).getString("session:$place",null)?.let{JSONObject(it)}?:return@synchronized false
   if(current.optString("status")!="active" || current.getString("id")!=reservation.getString("id") || System.currentTimeMillis()>=current.getLong("deadline"))return@synchronized false
   val available=observation.optString("status")=="available"
   record(context,current.put(if(available)"acquired" else "unavailable",current.optInt(if(available)"acquired" else "unavailable",0)+1).put("lastResult",observation.optString("status","unavailable")));true
  }
  if(!valid)return
  NodeGeofences.monitoringHistory(context,place,if(observation.optString("status")=="available")"location_acquired" else "location_unavailable")
  if(observation.optString("status")=="available")NodeGeofences.monitoringObservation(context,place,NodeLocation.engine(observation),reservation.getString("configurationId"),reservation.getString("scope"))
  else if(observation.optString("status") in setOf("permission_denied","location_disabled","provider_unavailable"))finish(context,place,"monitoring_location_blocked")
 }
}

/** No sticky recreation, repeating alarms, wakelocks or background polling worker. */
class NodeMonitoringService:Service() {
 companion object {@Volatile var running=false; private set}
 private val handler=Handler(Looper.getMainLooper())
 private val executor=Executors.newSingleThreadExecutor()
 @Volatile private var destroyed=false
 @Volatile private var cancellation:CancellationTokenSource?=null
 private var busy=false
 private var startedElapsed=0L
 private val tick=object:Runnable {
  override fun run() {
   if(destroyed)return
   if(android.os.SystemClock.elapsedRealtime()-startedElapsed>=NodeMonitoringBudget.WINDOW_MILLIS){NodeMonitoringSessions.cancelAll(this@NodeMonitoringService,"monitoring_session_expired");stopSelf();return}
   val reservations=if(!busy)NodeMonitoringSessions.reserve(this@NodeMonitoringService)else emptyList()
   if(!NodeMonitoringSessions.hasActive(this@NodeMonitoringService)){stopSelf();return}
   if(!busy && reservations.isNotEmpty()) {
    busy=true
    executor.execute {
     try {
      // One acquisition is shared by all due place sessions, never one GPS per rule.
      val first=reservations.first()
      val remaining=reservations.minOf{it.getLong("deadline")}-System.currentTimeMillis()
      if(remaining>0 && !destroyed) {
       val saver=getSystemService(android.os.PowerManager::class.java).isPowerSaveMode
       val observation=NodeLocation.current(this@NodeMonitoringService,upload=false,fresh=true,durationMillis=minOf(NodeMonitoringBudget.ACQUISITION_MILLIS,remaining),expectedScope=first.getString("scope"),highAccuracy=!saver || first.getInt("attempts")>2,acquire={request,cancel,timeout->
        cancellation=cancel
        if(destroyed)cancel.cancel()
        try{NodeMonitoringSessions.fixtureLocation(this@NodeMonitoringService,request,cancel,timeout)}finally{cancellation=null}
       })
       if(!destroyed) {
        if(observation.optString("status")=="cancelled")acquisitionFailed(reservations,"cancelled","monitoring_interrupted")
        else for(row in reservations)NodeMonitoringSessions.result(this@NodeMonitoringService,row,observation)
       }
      }
     }catch(_:InterruptedException){
      Thread.currentThread().interrupt()
      acquisitionFailed(reservations,"cancelled","monitoring_interrupted")
     }catch(_:java.util.concurrent.CancellationException){
      acquisitionFailed(reservations,"cancelled","monitoring_interrupted")
     }catch(_:Exception){
      // Provider failures expose only closed status codes, never exception text.
      acquisitionFailed(reservations,"provider_error","monitoring_provider_failed")
     }finally{if(!destroyed)handler.post{busy=false}}
    }
   }
   handler.postDelayed(this,1000)
  }
 }
 private fun acquisitionFailed(reservations:List<JSONObject>,status:String,reason:String) {
  if(destroyed)return
  for(row in reservations) {
   NodeMonitoringSessions.result(this,row,JSONObject().put("status",status))
   NodeMonitoringSessions.finish(this,row.getString("placeId"),reason)
  }
 }
 override fun onCreate() {
  super.onCreate()
  running=true;startedElapsed=android.os.SystemClock.elapsedRealtime();NodeMonitoringSessions.serviceLifecycle(this,true)
  NodeMonitoringSessions.recoverInterrupted(this)
  if(!NodeMonitoringSessions.hasActive(this)){stopSelf();return}
  // Handler deadlines use uptime: changing the device clock cannot extend GPS.
  handler.postDelayed({NodeMonitoringSessions.cancelAll(this,"monitoring_session_expired");stopSelf()},NodeMonitoringBudget.WINDOW_MILLIS)
  try {
   val permissions=NodeLocation.permissions(this)
   check(NodeMonitoringSessions.fixtureStartAllowed(this))
   check(permissions.optBoolean("precise") && permissions.optBoolean("background") && permissions.optBoolean("servicesEnabled"))
   val manager=getSystemService(NotificationManager::class.java)
   if(Build.VERSION.SDK_INT>=26)manager.createNotificationChannel(NotificationChannel("aegis-proximity","Verificação de proximidade",NotificationManager.IMPORTANCE_LOW))
   val notification=androidx.core.app.NotificationCompat.Builder(this,"aegis-proximity").setSmallIcon(android.R.drawable.ic_menu_mylocation).setContentTitle("Aegis · Verificando proximidade").setContentText("Verificando entrada ou saída por até 3 minutos.").setOngoing(true).setTimeoutAfter(NodeMonitoringBudget.WINDOW_MILLIS).build()
   if(Build.VERSION.SDK_INT>=29)startForeground(7351,notification,ServiceInfo.FOREGROUND_SERVICE_TYPE_LOCATION)else startForeground(7351,notification)
  }catch(_:RuntimeException){NodeMonitoringSessions.cancelAll(this,"monitoring_start_denied");stopSelf()}
 }
 override fun onStartCommand(intent:Intent?,flags:Int,startId:Int):Int {
  if(intent==null){NodeMonitoringSessions.cancelAll(this,"monitoring_interrupted");stopSelf();return START_NOT_STICKY}
  if(!destroyed){handler.removeCallbacks(tick);handler.post(tick)}
  return START_NOT_STICKY
 }
 override fun onDestroy() {
  destroyed=true;running=false;NodeMonitoringSessions.serviceLifecycle(this,false);handler.removeCallbacksAndMessages(null);cancellation?.cancel();executor.shutdownNow()
  NodeMonitoringSessions.cancelAll(this,"monitoring_interrupted")
  stopForeground(STOP_FOREGROUND_REMOVE)
  super.onDestroy()
 }
 override fun onBind(intent:Intent?)=null
}
