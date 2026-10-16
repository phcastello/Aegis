package com.aegis.node

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.content.SharedPreferences
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import android.os.Build
import android.location.Location
import com.google.android.gms.tasks.CancellationTokenSource
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
 private val eventExecutor=Executors.newSingleThreadExecutor()
 fun background(block:()->Unit){executor.execute(block)}
 fun event(block:()->Unit){eventExecutor.execute(block)}
 private val manifestGate=Any()
 private val applyGate=Any()
 private const val PREFS="aegis-geofences-v1"
 private fun prefs(context:Context)=context.getSharedPreferences(PREFS,Context.MODE_PRIVATE)
 class Store(context:Context):SQLiteOpenHelper(context,"aegis-geofence-events-v1.db",null,1), java.io.Closeable {
  override fun onCreate(db:SQLiteDatabase) {
   db.execSQL("CREATE TABLE events(eventId TEXT PRIMARY KEY,occurrenceId TEXT UNIQUE NOT NULL,ruleId TEXT NOT NULL,ruleRevision INTEGER NOT NULL,transition TEXT NOT NULL,occurredAt TEXT NOT NULL,postedAt TEXT,sequence INTEGER NOT NULL,payload TEXT NOT NULL,postResult TEXT NOT NULL DEFAULT 'pending')")
  }
  override fun onUpgrade(db:SQLiteDatabase,oldVersion:Int,newVersion:Int){error("Unsupported geofence schema")}
 }
 private fun pending(context:Context):PendingIntent {
  val flags=PendingIntent.FLAG_UPDATE_CURRENT or if(Build.VERSION.SDK_INT>=31) PendingIntent.FLAG_MUTABLE else 0
  return PendingIntent.getBroadcast(context,0,Intent(context,NodeGeofenceReceiver::class.java).setAction("com.aegis.node.GEOFENCE"),flags)
 }
 fun clear(context:Context) = clearWhen(context){true}
 fun clearIfUnbound(context:Context,origin:String?=null) = clearWhen(context){NodeNativeSession.bindingAllowed(context,null,origin)}
 fun clearForScope(context:Context,id:String,origin:String) = clearWhen(context){NodeNativeSession.scopeIsCurrent(context,id,origin)}
 private fun clearWhen(context:Context,current:()->Boolean) { synchronized(manifestGate) {
  val hadRegions=prefs(context).getBoolean("hasRegions",false)
  synchronized(applyGate){if(!current())return;check(prefs(context).edit().remove("manifest").remove("installedRevision").remove("reportPending").remove("reportError").putBoolean("needsInstall",true).putBoolean("hasRegions",false).commit())}
  WorkManager.getInstance(context).cancelUniqueWork("aegis-geofence-lease")
  WorkManager.getInstance(context).cancelUniqueWork("aegis-geofence-report")
  if(hadRegions)try {Tasks.await(LocationServices.getGeofencingClient(context).removeGeofences(pending(context)),15,TimeUnit.SECONDS)}catch(_:Exception){}
 } }
 fun bindIdentity(context:Context,id:String) { synchronized(applyGate) {
  if(!NodeNativeSession.bindingAllowed(context,id))return
  val previous=prefs(context).getString("scopeNodeId",null) ?: manifest(context)?.optString("observerNodeId")
  if(previous!=id) {
   Store(context).use{it.writableDatabase.delete("events",null,null)}
   NodeNativeNotifications.prefs(context).edit().remove("lastObservation").remove("observationPending").remove("observationScopeId").commit()
   val edit=prefs(context).edit().remove("reportPending").remove("reportError").remove("lastAcquisitionAt").remove("lastAcquisitionStatus")
   for(key in prefs(context).all.keys.filter{it.startsWith("diagnostic:")})edit.remove(key)
   check(edit.commit())
  }
  check(prefs(context).edit().putString("scopeNodeId",id).commit())
 } }
 private fun manifest(context:Context):JSONObject?=prefs(context).getString("manifest",null)?.let { JSONObject(it) }
 private fun stamp(text:String):Long=java.time.OffsetDateTime.parse(text).toInstant().toEpochMilli()
 fun reconcile(context:Context) { synchronized(manifestGate) {
  val session=NodeNativeSession.read(context) ?: return
  val current=NodeNativeSession.call(context,"GET","/api/nodes/geofences/manifest")
  require(current.getString("observerNodeId")==session.nodeId)
  val expiry=stamp(current.getString("leaseUntil")); val now=System.currentTimeMillis()
  require(expiry>now && expiry<=now+TimeUnit.DAYS.toMillis(7)+60000)
  val old=manifest(context)
  if(old!=null && stamp(current.getString("generatedAt"))<stamp(old.getString("generatedAt")))return
  synchronized(applyGate){if(NodeNativeSession.read(context)?.nodeId!=session.nodeId)return;check(prefs(context).edit().putString("manifest",current.toString()).commit())}
  val revision=current.getString("revision")
  val rules=current.getJSONArray("rules")
  NodeReminderActions.reconcileRules(context,rules.objects())
  if(old?.optString("revision")!=revision || prefs(context).getBoolean("needsInstall",true) || prefs(context).getString("installedRevision",null)!=revision || prefs(context).getLong("installedUntil",0)-now<TimeUnit.DAYS.toMillis(1)) install(context,current)
  else if(rules.length()>0 || prefs(context).getBoolean("reportPending",false)) report(context,current)
  if(rules.length()>0) {
   val renewal=PeriodicWorkRequestBuilder<NodeSyncWorker>(1,TimeUnit.DAYS).setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()).build()
   WorkManager.getInstance(context).enqueueUniquePeriodicWork("aegis-geofence-lease",ExistingPeriodicWorkPolicy.KEEP,renewal)
  } else WorkManager.getInstance(context).cancelUniqueWork("aegis-geofence-lease")
 }
 }
 fun sync(context:Context):JSONObject {
  try{flushEvents(context)}catch(_:Exception){}
  reconcile(context)
  return JSONObject().put("status",prefs(context).getString("status","pending_sync")).put("errorCode",prefs(context).getString("error",null)).put("syncPending",prefs(context).getBoolean("reportPending",false))
 }
 /** Local diagnostics contain confidence metadata, never precise coordinates. */
 fun diagnostics(context:Context):JSONObject {
  val permission=NodeLocation.permissions(context)
  val result=JSONObject().put("supported",true).put("powerSaveMode",permission.optBoolean("powerSaveMode",false)).put("rules",JSONArray())
  val value=manifest(context) ?: return result
  if(NodeNativeSession.read(context)?.nodeId!=value.optString("observerNodeId"))return result
  val preferences=prefs(context)
  val now=System.currentTimeMillis()
  val installed=!preferences.getBoolean("needsInstall",true) && preferences.getString("installedRevision",null)==value.getString("revision") && preferences.getBoolean("hasRegions",false) && preferences.getLong("installedUntil",0)>now && stamp(value.getString("leaseUntil"))>now
  val error=preferences.getString("error",null)
  val installation=when {
   installed -> "installed"
   error in setOf("location_permission_required","precise_location_required","background_location_required") -> "permission_required"
   preferences.getString("status",null)=="recoverable_error" -> "failed"
   else -> "pending_sync"
  }
  val acquisitionAt=preferences.getLong("lastAcquisitionAt",0)
  val acquisitionStatus=preferences.getString("lastAcquisitionStatus",null)
  if(acquisitionStatus!=null)result.put("acquisitionStatus",acquisitionStatus)
  val output=result.getJSONArray("rules")
  for(rule in value.getJSONArray("rules").objects()) {
   val id=rule.getString("id")
   val observations=rule.getJSONArray("placeIds").strings().map{preferences.getString("diagnostic:$it",null)?.let{raw->JSONObject(raw)}?:JSONObject().put("presence","unknown").put("reasonCode","not_observed")}
   val saved=rule.getJSONArray("placeIds").strings().map{preferences.getString("state:$it",null)?.let{raw->JSONObject(raw).optString("confirmed","unknown")}?:"unknown"}
   fun combine(states:List<String>)=when {states.any{it=="inside"}->"inside";states.isNotEmpty()&&states.all{it=="outside"}->"outside";else->"unknown"}
   val latest=observations.filter{it.has("lastObservationAt")}.maxByOrNull{it.optLong("lastObservationAt",0)}
   var presence=combine(observations.map{it.optString("presence","unknown")})
   var reason=if(presence=="unknown")observations.firstOrNull{it.optString("presence")=="unknown"}?.optString("reasonCode")?:"not_observed" else "confirmed"
   if(latest!=null && now-latest.getLong("lastObservationAt")>NodeGeoEngine.MAX_AGE_MILLIS){presence="unknown";reason="stale_location"}
   if(acquisitionStatus!=null && acquisitionStatus!="available" && acquisitionAt>(latest?.optLong("receivedAt",0)?:0)) {
    presence="unknown"
    reason=when(acquisitionStatus){"permission_denied"->"permission_required";"location_disabled"->"location_disabled";"provider_unavailable"->"provider_unavailable";"stale"->"stale_location";"invalid_accuracy"->"accuracy_insufficient";else->"location_unavailable"}
   }
   if(!permission.optBoolean("granted") || !permission.optBoolean("precise") || !permission.optBoolean("background")){presence="unknown";reason="permission_required"}
   else if(!permission.optBoolean("servicesEnabled")){presence="unknown";reason="location_disabled"}
   else if(!permission.optBoolean("providerAvailable")){presence="unknown";reason="provider_unavailable"}
   else if(installation=="failed")reason="installation_failed"
   else if(installation=="pending_sync")reason="pending_sync"
   val armed=installed && rule.optString("status")=="active" && (rule.isNull("endsAt")||stamp(rule.getString("endsAt"))>now) && preferences.getLong("ruleRevision:$id",-1)==rule.getLong("revision") && preferences.getBoolean("armed:$id",false) && !preferences.getBoolean("completed:$id",false)
   if(installation=="installed" && !armed && presence=="outside" && rule.optString("transition")=="exit")reason="outside_initial"
   output.put(JSONObject().put("id",id).put("revision",rule.getLong("revision")).put("installationStatus",installation).put("presence",presence).put("confirmedPresence",combine(saved)).put("armed",armed)
    .put("lastObservationAt",latest?.optLong("lastObservationAt")?:JSONObject.NULL).put("accuracyMeters",latest?.optDouble("accuracyMeters")?.takeIf{it.isFinite()&&it>0.0}?:JSONObject.NULL).put("reasonCode",reason).put("powerSaveMode",permission.optBoolean("powerSaveMode",false)))
  }
  return result
 }
 private fun recordAcquisition(context:Context,status:String,scope:String?):Boolean = synchronized(applyGate) {
  if(NodeNativeSession.read(context)?.nodeId!=scope)return@synchronized false
  check(prefs(context).edit().putString("lastAcquisitionStatus",status).putLong("lastAcquisitionAt",System.currentTimeMillis()).commit())
  true
 }
 /** One user-requested acquisition; no registration, continuous listener or upload. */
 fun verifyPresence(context:Context,allowConfirmation:Boolean=true,acquire:((CurrentLocationRequest,CancellationTokenSource,Long)->Location?)?=null):JSONObject {
  val scope=NodeNativeSession.read(context)?.nodeId
  val observation=NodeLocation.current(context,upload=false,fresh=true,durationMillis=12000,expectedScope=scope,highAccuracy=true,acquire=acquire)
  if(!recordAcquisition(context,observation.optString("status","unavailable"),scope))return diagnostics(context).put("acquisitionStatus","superseded")
  if(observation.optString("status")=="available")process(context,NodeLocation.engine(observation),allowConfirmation)
  return diagnostics(context).put("verifiedAt",System.currentTimeMillis())
 }
 private fun activeRules(value:JSONObject,now:Long=System.currentTimeMillis()):List<JSONObject> = value.getJSONArray("rules").objects().filter {
  it.optString("status")=="active" && stamp(it.getString("leaseUntil"))>now && (it.isNull("endsAt")||stamp(it.getString("endsAt"))>now)
 }
 private fun geometry(value:JSONObject,placeId:String):List<NodeGeoEngine.Circle> {
  val place=value.getJSONArray("places").objects().firstOrNull { it.getString("id")==placeId } ?: return emptyList()
  return place.getJSONArray("circles").objects().map { NodeGeoEngine.Circle(it.getDouble("latitude"),it.getDouble("longitude"),it.getDouble("radiusMeters")) }
 }
 private fun install(context:Context,value:JSONObject) {
  synchronized(applyGate){if(!currentDefinition(context,value))return;check(reportPending(context,prefs(context).edit()).putBoolean("needsInstall",true).commit())}
  val state=NodeLocation.permissions(context); val rules=activeRules(value).filter { rule ->
   prefs(context).getLong("ruleRevision:${rule.getString("id")}",-1)!=rule.getLong("revision") || !prefs(context).getBoolean("completed:${rule.getString("id")}",false)
  }
  val client=LocationServices.getGeofencingClient(context)
  if(rules.isEmpty()) {
   try {
    if(prefs(context).getBoolean("hasRegions",false))Tasks.await(client.removeGeofences(pending(context)),15,TimeUnit.SECONDS)
    synchronized(applyGate){if(!currentDefinition(context,value))return;check(reportPending(context,prefs(context).edit()).putString("installedRevision",value.getString("revision")).putLong("installedUntil",stamp(value.getString("leaseUntil"))).putBoolean("needsInstall",false).putString("status","installed").putBoolean("hasRegions",false).remove("error").commit())}
   }catch(_:Exception){registrationFailed(context,value)}
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
   synchronized(applyGate){if(!currentDefinition(context,value))return;check(reportPending(context,prefs(context).edit()).putString("status","recoverable_error").putString("error",error).putBoolean("needsInstall",true).commit())};report(context,value);return
  }
  val placeIds=rules.flatMap { it.getJSONArray("placeIds").strings() }.toSet()
  // A shared region lives until its final consumer expires, bounded by the manifest lease.
  val regionUntil=mutableMapOf<String,Long>()
  val leaseUntil=stamp(value.getString("leaseUntil"))
  for(rule in rules) {
   val until=minOf(leaseUntil,if(rule.isNull("endsAt"))leaseUntil else stamp(rule.getString("endsAt")))
   for(placeId in rule.getJSONArray("placeIds").strings()) {
    val place=value.getJSONArray("places").objects().firstOrNull{it.getString("id")==placeId} ?: error("Missing place")
    for(circle in place.getJSONArray("circles").objects()) {val id=circle.getString("id");regionUntil[id]=maxOf(regionUntil[id]?:0,until)}
   }
  }
  val regions=linkedMapOf<String,Geofence>()
  for(placeId in placeIds) {
   val place=value.getJSONArray("places").objects().firstOrNull{it.getString("id")==placeId} ?: error("Missing place")
   for(circle in place.getJSONArray("circles").objects()) {
    require(circle.getDouble("radiusMeters") in 50.0..100000.0)
    val circleId=circle.getString("id")
    regions[circleId]=Geofence.Builder().setRequestId(circleId)
     .setCircularRegion(circle.getDouble("latitude"),circle.getDouble("longitude"),circle.getDouble("radiusMeters").toFloat())
     .setTransitionTypes(Geofence.GEOFENCE_TRANSITION_ENTER or Geofence.GEOFENCE_TRANSITION_EXIT)
     .setNotificationResponsiveness(120000).setExpirationDuration((regionUntil.getValue(circleId)-System.currentTimeMillis()).coerceAtLeast(1)).build()
   }
  }
  if(regions.isEmpty()||regions.size>100) {
   synchronized(applyGate){if(!currentDefinition(context,value))return;check(reportPending(context,prefs(context).edit()).putString("status","recoverable_error").putString("error",if(regions.isEmpty()) "geometry_required" else "region_limit_exceeded").putBoolean("needsInstall",true).commit())};report(context,value);return
  }
  try {
   // A cached location from before activation cannot establish initial presence.
   // Persist before registering: Google's initial event may itself carry old data.
   val activatedAt=System.currentTimeMillis()
   synchronized(applyGate) {
    val edit=prefs(context).edit()
    for(placeId in placeIds)edit.putLong("notBefore:$placeId",activatedAt)
    check(edit.commit())
   }
   Tasks.await(client.removeGeofences(pending(context)),15,TimeUnit.SECONDS)
   val request=GeofencingRequest.Builder().setInitialTrigger(GeofencingRequest.INITIAL_TRIGGER_ENTER or GeofencingRequest.INITIAL_TRIGGER_EXIT).addGeofences(regions.values.toList()).build()
   Tasks.await(client.addGeofences(request,pending(context)),20,TimeUnit.SECONDS)
   synchronized(applyGate) {
    if(NodeNativeSession.read(context)?.nodeId!=value.getString("observerNodeId") || manifest(context)?.optString("revision")!=value.getString("revision"))return
    val edit=prefs(context).edit()
    // Registration establishes a fresh baseline; missed/initial events are not transitions.
    for(placeId in placeIds)edit.putString("geometry:$placeId",geometry(value,placeId).toString()).remove("state:$placeId").remove("diagnostic:$placeId")
    for(rule in rules) {
     val id=rule.getString("id")
     edit.putBoolean("armed:$id",false)
     if(prefs(context).getLong("ruleRevision:$id",-1)!=rule.getLong("revision"))edit.putLong("ruleRevision:$id",rule.getLong("revision")).remove("completed:$id")
    }
    check(reportPending(context,edit).putString("installedRevision",value.getString("revision")).putLong("installedUntil",stamp(value.getString("leaseUntil"))).putBoolean("needsInstall",false).putString("status","waiting_initial").putBoolean("hasRegions",true).remove("error").commit())
   }
  } catch(error:Exception) {
   registrationFailed(context,value)
   report(context,value);return
  }
  // Neither the first acquisition nor a remote receipt can invalidate a successful
  // Google registration. A failed acquisition keeps the initial presence unknown.
  try {
   val observation=NodeLocation.current(context,upload=false,fresh=true)
   if(!recordAcquisition(context,observation.optString("status","unavailable"),value.getString("observerNodeId")))return
   if(observation.optString("status")=="available") process(context,NodeLocation.engine(observation))
  }catch(error:Exception){notificationLog("geofence_initial_position",error)}
  report(context,value)
 }
 private fun reportPending(context:Context,edit:SharedPreferences.Editor):SharedPreferences.Editor =
  edit.putBoolean("reportPending",true).putLong("reportEpoch",prefs(context).getLong("reportEpoch",0)+1)
 private fun currentDefinition(context:Context,value:JSONObject):Boolean =
  manifest(context)?.optString("revision")==value.getString("revision") && NodeNativeSession.read(context)?.nodeId==value.getString("observerNodeId")
 private fun registrationFailed(context:Context,value:JSONObject) {synchronized(applyGate) {
  if(!currentDefinition(context,value))return
  check(reportPending(context,prefs(context).edit()).putString("status","recoverable_error").putString("error","native_registration_failed").putBoolean("needsInstall",true).commit())
 } }
 private fun reportBody(context:Context,value:JSONObject):JSONObject {
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
  return JSONObject().put("manifestRevision",value.getString("revision")).put("rules",rules)
 }
 private fun report(context:Context,value:JSONObject) {
  synchronized(applyGate) {
   if(!currentDefinition(context,value))return
   if(!prefs(context).getBoolean("reportPending",false))check(reportPending(context,prefs(context).edit()).commit())
  }
  flushReport(context)
 }
 /** Receipts are a durable, coalesced latest-state outbox, independent of GMS. */
 fun flushReport(context:Context,enqueueRetry:Boolean=true):Boolean {
  data class Receipt(val scope:String,val revision:String,val epoch:Long,val body:JSONObject)
  val receipt=synchronized(applyGate) {
   val session=NodeNativeSession.read(context) ?: return false
   val current=manifest(context) ?: return false
   if(!prefs(context).getBoolean("reportPending",false) || current.optString("observerNodeId")!=session.nodeId || stamp(current.getString("leaseUntil"))<=System.currentTimeMillis())return false
   if(prefs(context).getBoolean("needsInstall",true) && prefs(context).getString("status",null)!="recoverable_error")return false
   Receipt(session.nodeId,current.getString("revision"),prefs(context).getLong("reportEpoch",0),reportBody(context,current))
  }
  try {
   val response=NodeNativeSession.call(context,"POST","/api/nodes/geofences/sync",receipt.body,expectedScope=receipt.scope)
   check(response.optBoolean("synchronized",false))
   synchronized(applyGate) {
    if(NodeGeofenceReportPolicy.acknowledges(prefs(context).getLong("reportEpoch",0),receipt.epoch,manifest(context)?.optString("revision"),receipt.revision,NodeNativeSession.read(context)?.nodeId,receipt.scope))check(prefs(context).edit().putBoolean("reportPending",false).remove("reportError").commit())
   }
  }catch(_:Exception) {
   synchronized(applyGate) {
    if(NodeNativeSession.read(context)?.nodeId==receipt.scope && manifest(context)?.optString("revision")==receipt.revision)check(prefs(context).edit().putString("reportError","backend_ack_pending").commit())
   }
  }
  val pending=prefs(context).getBoolean("reportPending",false)
  if(pending && enqueueRetry) {
   val work=OneTimeWorkRequestBuilder<NodeGeofenceReportWorker>().setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()).setBackoffCriteria(BackoffPolicy.EXPONENTIAL,30,TimeUnit.SECONDS).build()
   WorkManager.getInstance(context).enqueueUniqueWork("aegis-geofence-report",ExistingWorkPolicy.APPEND_OR_REPLACE,work)
  }
  return pending
 }
 fun reportRetryAllowed(context:Context,attempt:Int):Boolean {
  val current=manifest(context) ?: return false
  return NodeGeofenceReportPolicy.retry(attempt,System.currentTimeMillis(),stamp(current.getString("leaseUntil")),prefs(context).getBoolean("reportPending",false))
 }
 @Synchronized fun process(context:Context,observation:NodeGeoEngine.Observation,allowConfirmation:Boolean=true) {
  val observer=NodeNativeSession.read(context)?.nodeId ?: return
  val value=manifest(context) ?: return
  if(value.optString("observerNodeId")!=observer)return
   val now=System.currentTimeMillis()
  if(prefs(context).getBoolean("needsInstall",true)||prefs(context).getString("installedRevision",null)!=value.getString("revision"))return
  if(stamp(value.getString("leaseUntil"))<=now || observation.capturedAt>now) return
  val rules=activeRules(value,now).filter{prefs(context).getLong("ruleRevision:${it.getString("id")}",-1)!=it.getLong("revision") || !prefs(context).getBoolean("completed:${it.getString("id")}",false)}
  var confirm=false
  var needsSync=false
  var retireRegions=false
  for(placeId in rules.flatMap{it.getJSONArray("placeIds").strings()}.toSet()) {
   val notBefore=prefs(context).getLong("notBefore:$placeId",0)
   if(observation.capturedAt<notBefore)continue
   val saved=prefs(context).getString("state:$placeId",null)?.let{JSONObject(it)}
   val old=if(saved==null) NodeGeoEngine.State() else NodeGeoEngine.State(saved.getString("confirmed"),saved.getString("candidate"),saved.getLong("candidateAt"),saved.getLong("lastCapturedAt"))
   if(observation.capturedAt<=old.lastCapturedAt)continue
   val assessment=NodeGeoEngine.assess(geometry(value,placeId),observation,now)
   // Future and stale deliveries are visible as diagnostics, but cannot replace
   // newer state or establish temporal persistence.
   val decision=if(assessment.reasonCode in setOf("future_location","stale_location"))NodeGeoEngine.Decision(old) else NodeGeoEngine.observe(old,assessment.classification,observation.capturedAt,notBefore)
   val state=decision.state
   val presence=if(state.candidate!="unknown")"unknown" else assessment.classification
   val reason=if(state.candidate!="unknown")"awaiting_confirmation" else assessment.reasonCode
   val current=synchronized(applyGate) {
    if(prefs(context).getBoolean("needsInstall",true) || prefs(context).getLong("notBefore:$placeId",0)!=notBefore || manifest(context)?.optString("revision")!=value.getString("revision") || NodeNativeSession.read(context)?.nodeId!=observer)false
    else {
     val edit=prefs(context).edit()
     if(state!=old)edit.putString("state:$placeId",JSONObject().put("confirmed",state.confirmed).put("candidate",state.candidate).put("candidateAt",state.candidateAt).put("lastCapturedAt",state.lastCapturedAt).toString())
     val priorDiagnostic=prefs(context).getString("diagnostic:$placeId",null)?.let{JSONObject(it)}
     if(priorDiagnostic==null || observation.capturedAt>priorDiagnostic.optLong("lastObservationAt",0))edit.putString("diagnostic:$placeId",JSONObject().put("lastObservationAt",observation.capturedAt).put("accuracyMeters",observation.accuracyMeters.takeIf{it.isFinite()&&it>0.0}?:JSONObject.NULL).put("presence",presence).put("reasonCode",reason).put("receivedAt",now).toString())
     check(edit.commit())
     true
    }
   }
   if(!current)continue
   confirm=confirm||decision.needsConfirmation
   for(rule in rules.filter{placeId in it.getJSONArray("placeIds").strings()}) synchronized(applyGate) {
    val id=rule.getString("id");val transition=rule.getString("transition")
    if(prefs(context).getBoolean("needsInstall",true) || prefs(context).getLong("notBefore:$placeId",0)!=notBefore || prefs(context).getLong("ruleRevision:$id",-1)!=rule.getLong("revision") || manifest(context)?.optString("revision")!=value.getString("revision") || NodeNativeSession.read(context)?.nodeId!=observer)return@synchronized
    if(prefs(context).getBoolean("completed:$id",false)) return@synchronized
    val initial=if(transition=="exit") "inside" else "outside"
    if(assessment.classification==initial && state.confirmed==initial && !prefs(context).getBoolean("armed:$id",false)) {check(reportPending(context,prefs(context).edit()).putBoolean("armed:$id",true).commit());needsSync=true}
    if(decision.transition==transition && prefs(context).getBoolean("armed:$id",false) && (rule.isNull("startsAt")||observation.capturedAt>=stamp(rule.getString("startsAt")))) {
      needsSync=true
      NodeLocation.queueTransition(context,observation)
      if(createOccurrence(context,rule,observation.capturedAt)) {
       check(reportPending(context,prefs(context).edit()).putBoolean("armed:$id",false).putBoolean("completed:$id",!rule.getBoolean("repeat")).commit())
       retireRegions=retireRegions||!rule.getBoolean("repeat")
      }
    }
   }
  }
  if(confirm && allowConfirmation) {
   val work=OneTimeWorkRequestBuilder<NodeGeoConfirmWorker>().setInitialDelay(30,TimeUnit.SECONDS).setInputData(workDataOf("attempt" to 1,"deadline" to (now+90000))).build()
   WorkManager.getInstance(context).enqueueUniqueWork("aegis-geofence-confirm",ExistingWorkPolicy.KEEP,work)
  }
  if(needsSync) {
   if(retireRegions) background { synchronized(manifestGate) {
    val current=manifest(context)
    if(current?.optString("revision")==value.getString("revision") && prefs(context).getBoolean("hasRegions",false)) {
     val consumers=activeRules(current).filter { !prefs(context).getBoolean("completed:${it.getString("id")}",false) }.flatMap { it.getJSONArray("placeIds").strings() }.toSet()
     val keep=current.getJSONArray("places").objects().filter{it.getString("id") in consumers}.flatMap{it.getJSONArray("circles").objects().map{circle->circle.getString("id")}}.toSet()
     val removed=current.getJSONArray("places").objects().flatMap{it.getJSONArray("circles").objects().map{circle->circle.getString("id")}}.toSet()-keep
     if(removed.isNotEmpty())try{Tasks.await(LocationServices.getGeofencingClient(context).removeGeofences(removed.toList()),15,TimeUnit.SECONDS);if(keep.isEmpty())prefs(context).edit().putBoolean("hasRegions",false).commit()}catch(_:Exception){}
    }
   } }
   NodeBackgroundWork.enqueue(context)
  }
 }
 fun hasConsumers(context:Context):Boolean {
  val current=manifest(context) ?: return false
  if(NodeNativeSession.read(context)?.nodeId!=current.optString("observerNodeId") || stamp(current.getString("leaseUntil"))<=System.currentTimeMillis() || prefs(context).getBoolean("needsInstall",true))return false
  return activeRules(current).any{prefs(context).getLong("ruleRevision:${it.getString("id")}",-1)!=it.getLong("revision") || !prefs(context).getBoolean("completed:${it.getString("id")}",false)}
 }
 fun confirmationPending(context:Context):Boolean {
  val current=manifest(context) ?: return false
  return activeRules(current).flatMap{it.getJSONArray("placeIds").strings()}.distinct().any {id->
   val state=prefs(context).getString("state:$id",null)?.let{JSONObject(it)}
   state!=null && state.optString("candidate","unknown")!="unknown"
  }
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
     put("transition",rule.getString("transition"));put("occurredAt",Instant.ofEpochMilli(capturedAt).toString());put("sequence",sequence);put("payload",JSONObject(rule.toString()).put("observerNodeId",NodeNativeSession.read(context)?.nodeId).toString())
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
    val result=if(now-at<=300000) NodeNativeNotifications.show(context,eventId,Instant.ofEpochMilli(at+300000).toString(),"Aegis",rule.getString("text"),NodeReminderActions.Presentation(id,1,1,null,eventId,rule.getString("id"),rule.getLong("revision"),rule.optString("observerNodeId").takeIf{it.isNotEmpty()})) else NodeNotificationResult("expired")
    db.update("events",ContentValues().apply {put("postResult",result.status);if(result.status in setOf("success","duplicate")) put("postedAt",Instant.now().toString())},"eventId=?",arrayOf(eventId))
   }
  }
 }
 fun pendingOccurrences(context:Context):Set<String> = Store(context).use{store->store.readableDatabase.rawQuery("SELECT occurrenceId FROM events",null).use{cursor->val result=mutableSetOf<String>();while(cursor.moveToNext())result.add(cursor.getString(0));result}}
 fun flushEvents(context:Context) {
  val scope=NodeNativeSession.read(context)?.nodeId ?: return
  finishLocalPosts(context)
  Store(context).use { store ->
   val db=store.writableDatabase;val events=mutableListOf<JSONObject>()
   db.rawQuery("SELECT eventId,occurrenceId,ruleId,ruleRevision,transition,occurredAt,postedAt,sequence FROM events ORDER BY sequence LIMIT 100",null).use {
    while(it.moveToNext()) events.add(JSONObject().put("eventId",it.getString(0)).put("occurrenceId",it.getString(1)).put("ruleId",it.getString(2)).put("ruleRevision",it.getLong(3)).put("transition",it.getString(4)).put("occurredAt",it.getString(5)).put("sequence",it.getLong(7)).apply{if(!it.isNull(6))put("postedAt",it.getString(6))})
   }
   for(event in events) {
    try {
     val response=NodeNativeSession.call(context,"POST","/api/nodes/geofences/events",event,expectedScope=scope)
     if(response.optString("status") in setOf("superseded")) {
      NodeReminderActions.withdrawScoped(context,event.getString("occurrenceId"),1,scope,true)
     }
     db.delete("events","eventId=?",arrayOf(event.getString("eventId")))
    }
    catch(error:NodeNativeSession.HttpFailure){if(error.status in setOf(400,403,404,409,410)) {
      NodeReminderActions.withdrawScoped(context,event.getString("occurrenceId"),1,scope,true);db.delete("events","eventId=?",arrayOf(event.getString("eventId")))
     }else throw error}
   }
  }
 }
 fun scheduleRestore(context:Context) {
  val work=OneTimeWorkRequestBuilder<NodeGeofenceRestoreWorker>().setBackoffCriteria(BackoffPolicy.EXPONENTIAL,30,TimeUnit.SECONDS).build()
  WorkManager.getInstance(context).enqueueUniqueWork("aegis-geofence-restore",ExistingWorkPolicy.KEEP,work)
 }
 fun restore(context:Context) { synchronized(manifestGate) {
  if(NodeNativeSession.read(context)==null){clear(context);return}
  prefs(context).edit().putBoolean("needsInstall",true).commit()
  val current=manifest(context)
  if(current!=null && current.optString("observerNodeId")!=NodeNativeSession.read(context)?.nodeId){clear(context);return}
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
  NodeGeofences.event {
   try {
    val event=GeofencingEvent.fromIntent(intent) ?: return@event
    if(event.hasError()) {NodeGeofences.scheduleRestore(context);return@event}
    val location=event.triggeringLocation
    if(location!=null) {val observation=NodeLocation.observation(location);NodeGeofenceRuntime.observe(context,observation.getDouble("latitude"),observation.getDouble("longitude"),observation.getDouble("accuracyMeters"),NodeLocation.engine(observation).capturedAt,true)}
   }catch(error:Exception){notificationLog("geofence_event",error)}finally{pending.finish()}
  }
 }
}
class NodeGeoConfirmWorker(context:Context,params:WorkerParameters):Worker(context,params) {
 override fun doWork():Result=try {
  val deadline=inputData.getLong("deadline",0);val attempt=inputData.getInt("attempt",1)
  val remaining=deadline-System.currentTimeMillis()
  if(remaining<=0 || attempt !in 1..2 || !NodeGeofences.hasConsumers(applicationContext))Result.success() else {
   val observation=NodeLocation.current(applicationContext,upload=false,fresh=true,durationMillis=minOf(15000,remaining))
   if(observation.optString("status")=="available")NodeGeofenceRuntime.observe(applicationContext,observation.getDouble("latitude"),observation.getDouble("longitude"),observation.getDouble("accuracyMeters"),NodeLocation.engine(observation).capturedAt,false)
   if(NodeGeoEngine.canConfirmAgain(attempt,System.currentTimeMillis(),deadline,NodeGeofences.confirmationPending(applicationContext))) {
    val next=OneTimeWorkRequestBuilder<NodeGeoConfirmWorker>().setInitialDelay(30,TimeUnit.SECONDS).setInputData(workDataOf("attempt" to (attempt+1),"deadline" to deadline)).build()
    WorkManager.getInstance(applicationContext).enqueueUniqueWork("aegis-geofence-confirm",ExistingWorkPolicy.APPEND_OR_REPLACE,next)
   }
   Result.success()
  }
 }catch(_:Exception){Result.success()}
}
class NodeGeofenceRestoreReceiver:BroadcastReceiver() {
 override fun onReceive(context:Context,intent:Intent) {
  if(intent.action !in setOf(Intent.ACTION_BOOT_COMPLETED,Intent.ACTION_MY_PACKAGE_REPLACED))return
  NodeGeofences.scheduleRestore(context)
 }
}

/** Google registration/acquisition can outlive a BroadcastReceiver's execution budget. */
class NodeGeofenceRestoreWorker(context:Context,params:WorkerParameters):Worker(context,params) {
 override fun doWork():Result {
  NodeGeofenceRuntime.restore(applicationContext)
  val prefs=applicationContext.getSharedPreferences("aegis-geofences-v1",Context.MODE_PRIVATE)
  val definition=prefs.getString("manifest",null)?.let{JSONObject(it)} ?: return Result.success()
  val valid=java.time.OffsetDateTime.parse(definition.getString("leaseUntil")).toInstant().toEpochMilli()>System.currentTimeMillis()
  return if(valid && prefs.getString("error",null)=="native_registration_failed")Result.retry()else Result.success()
 }
}

/** Bounded remote receipt retries never remove/re-register installed Google regions. */
class NodeGeofenceReportWorker(context:Context,params:WorkerParameters):Worker(context,params) {
 override fun doWork():Result {
  val pending=NodeGeofences.flushReport(applicationContext,enqueueRetry=false)
  return if(pending && NodeGeofences.reportRetryAllowed(applicationContext,runAttemptCount))Result.retry()else Result.success()
 }
}
