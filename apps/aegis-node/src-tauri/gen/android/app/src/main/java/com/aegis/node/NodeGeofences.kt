package com.aegis.node

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import android.os.Build
import androidx.work.*
import com.google.android.gms.location.*
import com.google.android.gms.tasks.Tasks
import org.json.JSONArray
import org.json.JSONObject
import java.time.Instant
import java.util.UUID
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/** Native persisted execution of server definitions; no separate recurrence scheduler. */
object NodeGeofences {
 private val executor=Executors.newSingleThreadExecutor()
 fun background(block:()->Unit){executor.execute(block)}
 private val manifestGate=Any()
 private const val PREFS="aegis-geofences-v1"
 private fun prefs(context:Context)=context.getSharedPreferences(PREFS,Context.MODE_PRIVATE)
 class Store(context:Context):SQLiteOpenHelper(context,"aegis-geofence-events-v1.db",null,1) {
  override fun onCreate(db:SQLiteDatabase) {
   db.execSQL("CREATE TABLE events(eventId TEXT PRIMARY KEY,occurrenceId TEXT UNIQUE NOT NULL,ruleId TEXT NOT NULL,ruleRevision INTEGER NOT NULL,transition TEXT NOT NULL,occurredAt TEXT NOT NULL,postedAt TEXT,sequence INTEGER NOT NULL,payload TEXT NOT NULL,postResult TEXT NOT NULL DEFAULT 'pending')")
  }
  override fun onUpgrade(db:SQLiteDatabase,oldVersion:Int,newVersion:Int){error("Unsupported geofence schema")}
 }
 private fun pending(context:Context):PendingIntent {
  val flags=PendingIntent.FLAG_UPDATE_CURRENT or if(Build.VERSION.SDK_INT>=31) PendingIntent.FLAG_MUTABLE else 0
  return PendingIntent.getBroadcast(context,0,Intent(context,NodeGeofenceReceiver::class.java).setAction("com.aegis.node.GEOFENCE"),flags)
 }
 fun clear(context:Context) {
  if(prefs(context).getBoolean("hasRegions",false))try { LocationServices.getGeofencingClient(context).removeGeofences(pending(context)) }catch(_:Exception){}
  prefs(context).edit().remove("manifest").remove("installedRevision").putBoolean("needsInstall",true).putBoolean("hasRegions",false).commit()
  WorkManager.getInstance(context).cancelUniqueWork("aegis-geofence-lease")
 }
 private fun manifest(context:Context):JSONObject?=prefs(context).getString("manifest",null)?.let { JSONObject(it) }
 private fun stamp(text:String):Long=NodeNotificationPayload.expiryEpochSecond(text)*1000
 fun reconcile(context:Context) { synchronized(manifestGate) {
  val session=NodeNativeSession.read(context) ?: return
  val current=NodeNativeSession.call(context,"GET","/api/nodes/geofences/manifest")
  require(current.getString("observerNodeId")==session.nodeId)
  val expiry=stamp(current.getString("leaseUntil")); val now=System.currentTimeMillis()
  require(expiry>now && expiry<=now+TimeUnit.DAYS.toMillis(7)+60000)
  val old=manifest(context)
  if(old!=null && stamp(current.getString("generatedAt"))<stamp(old.getString("generatedAt")))return
  check(prefs(context).edit().putString("manifest",current.toString()).commit())
  val revision=current.getString("revision")
  val rules=current.getJSONArray("rules")
  NodeReminderActions.reconcileRules(context,rules.objects())
  if(old?.optString("revision")!=revision || prefs(context).getBoolean("needsInstall",true) || prefs(context).getString("installedRevision",null)!=revision || prefs(context).getLong("installedUntil",0)-now<TimeUnit.DAYS.toMillis(1)) install(context,current)
  else if(rules.length()>0) report(context,current)
  if(rules.length()>0) {
   val renewal=PeriodicWorkRequestBuilder<NodeSyncWorker>(1,TimeUnit.DAYS).setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()).build()
   WorkManager.getInstance(context).enqueueUniquePeriodicWork("aegis-geofence-lease",ExistingPeriodicWorkPolicy.KEEP,renewal)
  } else WorkManager.getInstance(context).cancelUniqueWork("aegis-geofence-lease")
 }
 }
 fun sync(context:Context):JSONObject {
  try{flushEvents(context)}catch(_:Exception){}
  reconcile(context)
  return JSONObject().put("status",prefs(context).getString("status","pending_sync")).put("errorCode",prefs(context).getString("error",null))
 }
 private fun activeRules(value:JSONObject,now:Long=System.currentTimeMillis()):List<JSONObject> = value.getJSONArray("rules").objects().filter {
  it.optString("status")=="active" && stamp(it.getString("leaseUntil"))>now && (it.isNull("endsAt")||stamp(it.getString("endsAt"))>now)
 }
 private fun geometry(value:JSONObject,placeId:String):List<NodeGeoEngine.Circle> {
  val place=value.getJSONArray("places").objects().firstOrNull { it.getString("id")==placeId } ?: return emptyList()
  return place.getJSONArray("circles").objects().map { NodeGeoEngine.Circle(it.getDouble("latitude"),it.getDouble("longitude"),it.getDouble("radiusMeters")) }
 }
 private fun install(context:Context,value:JSONObject) {
  val state=NodeLocation.permissions(context); val rules=activeRules(value)
  val client=LocationServices.getGeofencingClient(context)
  if(rules.isEmpty()) {
   if(prefs(context).getBoolean("hasRegions",false))Tasks.await(client.removeGeofences(pending(context)),15,TimeUnit.SECONDS)
   prefs(context).edit().putString("installedRevision",value.getString("revision")).putLong("installedUntil",stamp(value.getString("leaseUntil"))).putBoolean("needsInstall",false).putString("status","installed").putBoolean("hasRegions",false).remove("error").commit()
   report(context,value);return
  }
  val error=when {
   !state.getBoolean("providerAvailable") -> "location_provider_unavailable"
   !state.getBoolean("granted") -> "location_permission_required"
   !state.getBoolean("precise") -> "precise_location_required"
   !state.getBoolean("background") -> "background_location_required"
   !state.getBoolean("servicesEnabled") -> "location_services_disabled"
   else -> null
  }
  if(error!=null) {
   prefs(context).edit().putString("status","recoverable_error").putString("error",error).putBoolean("needsInstall",true).commit();report(context,value);return
  }
  val placeIds=rules.flatMap { it.getJSONArray("placeIds").strings() }.toSet()
  val regions=linkedMapOf<String,Geofence>()
  for(placeId in placeIds) {
   val place=value.getJSONArray("places").objects().firstOrNull{it.getString("id")==placeId} ?: error("Missing place")
   for(circle in place.getJSONArray("circles").objects()) {
    require(circle.getDouble("radiusMeters") in 50.0..100000.0)
    val circleId=circle.getString("id")
    regions[circleId]=Geofence.Builder().setRequestId(circleId)
     .setCircularRegion(circle.getDouble("latitude"),circle.getDouble("longitude"),circle.getDouble("radiusMeters").toFloat())
     .setTransitionTypes(Geofence.GEOFENCE_TRANSITION_ENTER or Geofence.GEOFENCE_TRANSITION_EXIT)
     .setNotificationResponsiveness(120000).setExpirationDuration((stamp(value.getString("leaseUntil"))-System.currentTimeMillis()).coerceAtLeast(1)).build()
   }
  }
  if(regions.isEmpty()||regions.size>100) {
   prefs(context).edit().putString("status","recoverable_error").putString("error",if(regions.isEmpty()) "geometry_required" else "region_limit_exceeded").putBoolean("needsInstall",true).commit();report(context,value);return
  }
  try {
   Tasks.await(client.removeGeofences(pending(context)),15,TimeUnit.SECONDS)
   val request=GeofencingRequest.Builder().setInitialTrigger(GeofencingRequest.INITIAL_TRIGGER_ENTER or GeofencingRequest.INITIAL_TRIGGER_EXIT).addGeofences(regions.values.toList()).build()
   Tasks.await(client.addGeofences(request,pending(context)),20,TimeUnit.SECONDS)
   prefs(context).edit().putString("installedRevision",value.getString("revision")).putLong("installedUntil",stamp(value.getString("leaseUntil"))).putBoolean("needsInstall",false).putString("status","waiting_initial").putBoolean("hasRegions",true).remove("error").commit()
   // Revision changes reset only changed rules. Place geometry revisions reset their presence baseline.
   for(placeId in placeIds) {
    val hash=geometry(value,placeId).toString()
    // Registration establishes a fresh baseline; missed/initial native events are not transitions.
    prefs(context).edit().putString("geometry:$placeId",hash).remove("state:$placeId").commit()
   }
   for(rule in rules) if(prefs(context).getLong("ruleRevision:${rule.getString("id")}",-1)!=rule.getLong("revision")) {
    prefs(context).edit().putLong("ruleRevision:${rule.getString("id")}",rule.getLong("revision")).putBoolean("armed:${rule.getString("id")}",false).remove("completed:${rule.getString("id")}").commit()
   }
   val observation=NodeLocation.current(context,upload=true)
   if(observation.optString("status")=="available") process(context,NodeLocation.engine(observation))
   report(context,value)
  } catch(error:Exception) {
   prefs(context).edit().putString("status","recoverable_error").putString("error","native_registration_failed").putBoolean("needsInstall",true).commit()
   report(context,value)
  }
 }
 private fun report(context:Context,value:JSONObject) {
  val global=prefs(context).getString("status","pending_sync")
  val rules=JSONArray()
  for(rule in value.getJSONArray("rules").objects()) {
   val id=rule.getString("id")
   val status=when {
    rule.optString("status")=="paused" -> "paused"
    prefs(context).getBoolean("completed:$id",false) -> "completed"
    global=="recoverable_error" -> "recoverable_error"
    prefs(context).getBoolean("armed:$id",false) -> "active"
    else -> "waiting_initial"
   }
   rules.put(JSONObject().put("id",id).put("revision",rule.getLong("revision")).put("status",status).apply { if(status=="recoverable_error") put("errorCode",prefs(context).getString("error","native_registration_failed")) })
  }
  NodeNativeSession.call(context,"POST","/api/nodes/geofences/sync",JSONObject().put("manifestRevision",value.getString("revision")).put("rules",rules))
 }
 @Synchronized fun process(context:Context,observation:NodeGeoEngine.Observation,allowConfirmation:Boolean=true) {
  val value=manifest(context) ?: return; val now=System.currentTimeMillis()
  if(stamp(value.getString("leaseUntil"))<=now || now-observation.capturedAt !in 0..300000) return
  val rules=activeRules(value,now)
  var confirm=false
  for(placeId in rules.flatMap{it.getJSONArray("placeIds").strings()}.toSet()) {
   val saved=prefs(context).getString("state:$placeId",null)?.let{JSONObject(it)}
   val old=if(saved==null) NodeGeoEngine.State() else NodeGeoEngine.State(saved.getString("confirmed"),saved.getString("candidate"),saved.getLong("candidateAt"),saved.getLong("lastCapturedAt"))
   val decision=NodeGeoEngine.observe(old,NodeGeoEngine.classify(geometry(value,placeId),observation,now),observation.capturedAt)
   val state=decision.state
   check(prefs(context).edit().putString("state:$placeId",JSONObject().put("confirmed",state.confirmed).put("candidate",state.candidate).put("candidateAt",state.candidateAt).put("lastCapturedAt",state.lastCapturedAt).toString()).commit())
   confirm=confirm||decision.needsConfirmation
   for(rule in rules.filter{placeId in it.getJSONArray("placeIds").strings()}) {
    val id=rule.getString("id");val transition=rule.getString("transition")
    if(prefs(context).getBoolean("completed:$id",false)) continue
    val initial=if(transition=="exit") "inside" else "outside"
    if(state.confirmed==initial) prefs(context).edit().putBoolean("armed:$id",true).commit()
    if(decision.transition==transition && prefs(context).getBoolean("armed:$id",false) && (rule.isNull("startsAt")||observation.capturedAt>=stamp(rule.getString("startsAt")))) {
     if(manifest(context)?.optString("revision")!=value.getString("revision"))return
     NodeLocation.queueTransition(context,observation)
     if(createOccurrence(context,rule,observation.capturedAt)) prefs(context).edit().putBoolean("armed:$id",false).putBoolean("completed:$id",!rule.getBoolean("repeat")).commit()
    }
   }
  }
  if(confirm && allowConfirmation) {
   val work=OneTimeWorkRequestBuilder<NodeGeoConfirmWorker>().setInitialDelay(30,TimeUnit.SECONDS).build()
   WorkManager.getInstance(context).enqueueUniqueWork("aegis-geofence-confirm",ExistingWorkPolicy.KEEP,work)
  }
  NodeBackgroundWork.enqueue(context)
 }
 private fun createOccurrence(context:Context,rule:JSONObject,capturedAt:Long):Boolean {
  Store(context).use { store ->
   val db=store.writableDatabase;db.beginTransaction()
   try {
    val count=db.rawQuery("SELECT COUNT(*) FROM events",null).use{it.moveToFirst();it.getInt(0)}
    if(count>=100) {prefs(context).edit().putString("status","recoverable_error").putString("error","event_outbox_full").commit();return false}
    val sequence=prefs(context).getLong("sequence",0)+1
    check(prefs(context).edit().putLong("sequence",sequence).commit())
    val eventId=UUID.randomUUID().toString();val occurrenceId=UUID.randomUUID().toString()
    db.insertOrThrow("events",null,ContentValues().apply {
     put("eventId",eventId);put("occurrenceId",occurrenceId);put("ruleId",rule.getString("id"));put("ruleRevision",rule.getLong("revision"))
     put("transition",rule.getString("transition"));put("occurredAt",Instant.ofEpochMilli(capturedAt).toString());put("sequence",sequence);put("payload",rule.toString())
    })
    db.setTransactionSuccessful()
   }finally{db.endTransaction()}
  }
  finishLocalPosts(context)
  return true
 }
 private fun finishLocalPosts(context:Context) {
  Store(context).use { store ->
   val db=store.writableDatabase
   val rows=mutableListOf<Triple<String,String,JSONObject>>()
   db.rawQuery("SELECT eventId,occurrenceId,payload,occurredAt FROM events WHERE postResult='pending'",null).use {
    while(it.moveToNext()) rows.add(Triple(it.getString(0),it.getString(1),JSONObject(it.getString(2)).put("occurredAt",it.getString(3))))
   }
   for((eventId,id,rule) in rows) {
    val at=stamp(rule.getString("occurredAt"));val now=System.currentTimeMillis()
    val result=if(now-at<=300000) NodeNativeNotifications.show(context,eventId,Instant.ofEpochMilli(at+300000).toString(),"Aegis",rule.getString("text"),NodeReminderActions.Presentation(id,1,1,null,eventId,rule.getString("id"),rule.getLong("revision"))) else NodeNotificationResult("expired")
    db.update("events",ContentValues().apply {put("postResult",result.status);if(result.status in setOf("success","duplicate")) put("postedAt",Instant.now().toString())},"eventId=?",arrayOf(eventId))
   }
  }
 }
 fun pendingOccurrences(context:Context):Set<String> = Store(context).use{store->store.readableDatabase.rawQuery("SELECT occurrenceId FROM events",null).use{cursor->val result=mutableSetOf<String>();while(cursor.moveToNext())result.add(cursor.getString(0));result}}
 fun flushEvents(context:Context) {
  finishLocalPosts(context)
  Store(context).use { store ->
   val db=store.writableDatabase;val events=mutableListOf<JSONObject>()
   db.rawQuery("SELECT eventId,occurrenceId,ruleId,ruleRevision,transition,occurredAt,postedAt,sequence FROM events ORDER BY sequence LIMIT 100",null).use {
    while(it.moveToNext()) events.add(JSONObject().put("eventId",it.getString(0)).put("occurrenceId",it.getString(1)).put("ruleId",it.getString(2)).put("ruleRevision",it.getLong(3)).put("transition",it.getString(4)).put("occurredAt",it.getString(5)).put("sequence",it.getLong(7)).apply{if(!it.isNull(6))put("postedAt",it.getString(6))})
   }
   for(event in events) {
    try {
     val response=NodeNativeSession.call(context,"POST","/api/nodes/geofences/events",event)
     if(response.optString("status") in setOf("superseded")) {
      NodeReminderActions.withdraw(context,event.getString("occurrenceId"),1)
      NodeReminderActions.discardAction(context,event.getString("occurrenceId"))
     }
     db.delete("events","eventId=?",arrayOf(event.getString("eventId")))
    }
    catch(error:NodeNativeSession.HttpFailure){if(error.status in setOf(400,403,404,409,410)) {
      NodeReminderActions.withdraw(context,event.getString("occurrenceId"),1);NodeReminderActions.discardAction(context,event.getString("occurrenceId"));db.delete("events","eventId=?",arrayOf(event.getString("eventId")))
     }else throw error}
   }
  }
 }
 fun restore(context:Context) { synchronized(manifestGate) {
  prefs(context).edit().putBoolean("needsInstall",true).commit()
  val current=manifest(context)
  if(current!=null && stamp(current.getString("leaseUntil"))>System.currentTimeMillis()) try{install(context,current)}catch(_:Exception){}
  NodeBackgroundWork.enqueue(context)
 }
 }
}
internal fun JSONArray.objects():List<JSONObject> =(0 until length()).map{getJSONObject(it)}
internal fun JSONArray.strings():List<String> =(0 until length()).map{getString(it)}
class NodeGeofenceReceiver:BroadcastReceiver() {
 override fun onReceive(context:Context,intent:Intent) {
  val pending=goAsync()
  NodeGeofences.background {
   try {
    val event=GeofencingEvent.fromIntent(intent) ?: return@background
    if(event.hasError()) {NodeGeofences.restore(context);return@background}
    val location=event.triggeringLocation
    if(location!=null) NodeGeofences.process(context,NodeLocation.engine(NodeLocation.observation(location)))
   }catch(error:Exception){notificationLog("geofence_event",error)}finally{pending.finish()}
  }
 }
}
class NodeGeoConfirmWorker(context:Context,params:WorkerParameters):Worker(context,params) {
 override fun doWork():Result=try {
  val observation=NodeLocation.current(applicationContext,upload=false,fresh=true)
  if(observation.optString("status")=="available")NodeGeofences.process(applicationContext,NodeLocation.engine(observation),allowConfirmation=false)
  Result.success()
 }catch(_:Exception){Result.success()}
}
class NodeGeofenceRestoreReceiver:BroadcastReceiver() {
 override fun onReceive(context:Context,intent:Intent) {
  if(intent.action !in setOf(Intent.ACTION_BOOT_COMPLETED,Intent.ACTION_MY_PACKAGE_REPLACED))return
  val pending=goAsync()
  NodeGeofences.background{try{NodeGeofences.restore(context)}finally{pending.finish()}}
 }
}
