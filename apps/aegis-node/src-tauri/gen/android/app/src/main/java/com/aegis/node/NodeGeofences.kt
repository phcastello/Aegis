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
 private val eventFlushing=java.util.concurrent.atomic.AtomicBoolean(false)
 fun background(block:()->Unit){executor.execute(block)}
 fun event(block:()->Unit){eventExecutor.execute(block)}
 private val manifestGate=Any()
 private val applyGate=Any()
 private const val PREFS="aegis-geofences-v1"
 private fun prefs(context:Context)=context.getSharedPreferences(PREFS,Context.MODE_PRIVATE)
 class Store(context:Context):SQLiteOpenHelper(context,"aegis-geofence-events-v1.db",null,2), java.io.Closeable {
  override fun onCreate(db:SQLiteDatabase) {
   db.execSQL("CREATE TABLE events(eventId TEXT PRIMARY KEY,occurrenceId TEXT UNIQUE NOT NULL,ruleId TEXT NOT NULL,ruleRevision INTEGER NOT NULL,transition TEXT NOT NULL,occurredAt TEXT NOT NULL,postedAt TEXT,sequence INTEGER NOT NULL,payload TEXT NOT NULL,postResult TEXT NOT NULL DEFAULT 'pending')")
   db.execSQL("CREATE TABLE transition_keys(identity TEXT PRIMARY KEY,recordedAt INTEGER NOT NULL)")
  }
  override fun onUpgrade(db:SQLiteDatabase,oldVersion:Int,newVersion:Int){if(oldVersion<2)db.execSQL("CREATE TABLE IF NOT EXISTS transition_keys(identity TEXT PRIMARY KEY,recordedAt INTEGER NOT NULL)")}
 }
 private fun pending(context:Context):PendingIntent {
  val flags=PendingIntent.FLAG_UPDATE_CURRENT or if(Build.VERSION.SDK_INT>=31) PendingIntent.FLAG_MUTABLE else 0
  return PendingIntent.getBroadcast(context,0,Intent(context,NodeGeofenceReceiver::class.java).setAction("com.aegis.node.GEOFENCE"),flags)
 }
 fun clear(context:Context) = clearWhen(context){true}
 fun clearIfUnbound(context:Context,origin:String?=null) = clearWhen(context){NodeNativeSession.bindingAllowed(context,null,origin)}
 fun clearForScope(context:Context,id:String,origin:String) = clearWhen(context){NodeNativeSession.scopeIsCurrent(context,id,origin)}
 private fun clearWhen(context:Context,current:()->Boolean) { synchronized(manifestGate) {
  synchronized(applyGate){if(!current())return}
  NodeMonitoringSessions.cancelAll(context,"monitoring_cancelled")
  val hadRegions=prefs(context).getBoolean("hasRegions",false)
  synchronized(applyGate){if(!current())return;check(prefs(context).edit().remove("manifest").remove("installedRevision").remove("installedConfiguration").remove("reportPending").remove("reportError").putBoolean("needsInstall",true).putBoolean("hasRegions",false).commit())}
  WorkManager.getInstance(context).cancelUniqueWork("aegis-geofence-lease")
  WorkManager.getInstance(context).cancelUniqueWork("aegis-geofence-report")
  if(hadRegions)try {Tasks.await(LocationServices.getGeofencingClient(context).removeGeofences(pending(context)),15,TimeUnit.SECONDS)}catch(_:Exception){}
 } }
 fun bindIdentity(context:Context,id:String) { synchronized(applyGate) {
  if(!NodeNativeSession.bindingAllowed(context,id))return
  val previous=prefs(context).getString("scopeNodeId",null) ?: manifest(context)?.optString("observerNodeId")
  if(previous!=id) {
   NodeMonitoringSessions.cancelAll(context,"monitoring_superseded")
   Store(context).use{it.writableDatabase.delete("events",null,null);it.writableDatabase.delete("transition_keys",null,null)}
   NodeNativeNotifications.prefs(context).edit().remove("lastObservation").remove("observationPending").remove("observationScopeId").commit()
   val edit=prefs(context).edit().remove("reportPending").remove("reportError").remove("lastAcquisitionAt").remove("lastAcquisitionStatus")
   for(key in prefs(context).all.keys.filter{it.startsWith("state:") || it.startsWith("geometry:") || it.startsWith("notBefore:") || it.startsWith("confirmedAt:") || it.startsWith("hybridFix:") || it.startsWith("armed:") || it.startsWith("ruleRevision:") || it.startsWith("completed:") || it.startsWith("operationalInside:") || it.startsWith("operationalRegion:") || it.startsWith("diagnostic:") || it.startsWith("reliable:") || it.startsWith("history:") || it.startsWith("nativePending:") || it.startsWith("accepted:") || it=="nativeSeen" || it.startsWith("lastNativeEventAt:") || it.startsWith("lastNativeTransition:")})edit.remove(key)
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
  if((rules.length()>0 && prefs(context).getString("installedConfiguration",null)==null) || old?.optString("revision")!=revision || prefs(context).getBoolean("needsInstall",true) || prefs(context).getString("installedRevision",null)!=revision || prefs(context).getLong("installedUntil",0)-now<TimeUnit.DAYS.toMillis(1)) install(context,current)
  else if(rules.length()>0 || prefs(context).getBoolean("reportPending",false)) report(context,current)
  if(rules.length()>0) {
   val renewal=PeriodicWorkRequestBuilder<NodeSyncWorker>(1,TimeUnit.DAYS).setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()).build()
   WorkManager.getInstance(context).enqueueUniquePeriodicWork("aegis-geofence-lease",ExistingPeriodicWorkPolicy.KEEP,renewal)
  } else WorkManager.getInstance(context).cancelUniqueWork("aegis-geofence-lease")
 }
 }
 fun sync(context:Context,flushOccurrences:Boolean=true):JSONObject {
  if(flushOccurrences)try{flushEvents(context)}catch(_:Exception){}
  reconcile(context)
  return JSONObject().put("status",prefs(context).getString("status","pending_sync")).put("errorCode",prefs(context).getString("error",null)).put("syncPending",prefs(context).getBoolean("reportPending",false))
 }
 /** Local diagnostics contain confidence metadata, never precise coordinates. */
 fun diagnostics(context:Context):JSONObject {
  NodeMonitoringSessions.recoverInterrupted(context)
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
   // A newer native event without coordinates must not borrow an older fix's
   // precision/time. Compare capture clocks when known, receipt only otherwise.
   val latestEvidence=observations.maxByOrNull{it.optLong("lastObservationAt",it.optLong("receivedAt",0))}
   val latest=latestEvidence?.takeIf{it.has("lastObservationAt")}
   var presence=combine(observations.map{it.optString("presence","unknown")})
   var reason=if(presence=="unknown")observations.firstOrNull{it.optString("presence")=="unknown"}?.optString("reasonCode")?:"not_observed" else "confirmed"
   if(latest!=null && now-latest.getLong("lastObservationAt")>NodeGeoEngine.MAX_AGE_MILLIS){presence="unknown";reason="stale_location"}
   if(latestEvidence!=null && latest==null){presence="unknown";reason=latestEvidence.optString("reasonCode","native_location_absent")}
   if(acquisitionStatus!=null && acquisitionStatus!="available" && acquisitionAt>(latestEvidence?.optLong("receivedAt",0)?:0)) {
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
   if(installation=="installed" && !armed && combine(saved)=="inside" && rule.optString("transition")=="enter")reason="inside_initial"
   val approaching=rule.getJSONArray("placeIds").strings().any{NodeMonitoringSessions.active(context,it)}
   val verifying=rule.getJSONArray("placeIds").strings().any{place->preferences.getString("nativePending:$place",null)?.let{JSONObject(it).optLong("deadline",0)>now}==true}
   val monitoring=when{preferences.getBoolean("completed:$id",false)->"completed";installation!="installed"->"waiting_initial";approaching->"approaching";verifying->"verifying";armed->"armed";combine(saved)=="inside" && rule.optString("transition")=="enter"->"waiting_departure";combine(saved)=="outside" && rule.optString("transition")=="exit"->"waiting_arrival";presence=="unknown"->"uncertain";else->"waiting_initial"}
   output.put(JSONObject().put("id",id).put("revision",rule.getLong("revision")).put("installationStatus",installation).put("presence",presence).put("confirmedPresence",combine(saved)).put("armed",armed)
    .put("lastObservationAt",latest?.optLong("lastObservationAt")?:JSONObject.NULL).put("accuracyMeters",latest?.optDouble("accuracyMeters")?.takeIf{it.isFinite()&&it>0.0}?:JSONObject.NULL).put("reasonCode",reason).put("powerSaveMode",permission.optBoolean("powerSaveMode",false)).put("transition",rule.getString("transition")).put("monitoringState",monitoring).put("confirmedAt",rule.getJSONArray("placeIds").strings().maxOfOrNull{preferences.getLong("confirmedAt:$it",0)}?:JSONObject.NULL).put("lastNativeEventAt",preferences.getLong("lastNativeEventAt:$id",0).takeIf{it>0}?:JSONObject.NULL).put("lastNativeTransition",preferences.getString("lastNativeTransition:$id",null)).put("monitoringSessions",JSONArray(rule.getJSONArray("placeIds").strings().mapNotNull{NodeMonitoringSessions.status(context,it)})).put("installedRegionKinds",prefs(context).getString("installedConfiguration",null)?.let{raw->JSONArray(JSONObject(raw).getJSONArray("regions").objects().filter{it.getString("placeId") in rule.getJSONArray("placeIds").strings()}.map{it.optString("kind","real")}.distinct())}?:JSONArray()).put("history",preferences.getString("history:$id",null)?.let{JSONArray(it)}?:JSONArray()))
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
 private fun physicalRegions(place:JSONObject):List<JSONObject> {
  val real=place.getJSONArray("circles").objects().map{JSONObject(it.toString()).put("kind","real")}
  val envelope=place.optJSONObject("monitoringEnvelope")?:return real
  if(!envelope.optBoolean("enabled") || envelope.optBoolean("approvalRequired",true) || envelope.isNull("approvedAt"))return real
  return real+(envelope.optJSONArray("circles")?.objects()?:emptyList()).map{JSONObject(it.toString()).put("kind","operational")}
 }
 internal fun fixtureLocation(context:Context,placeId:String?,outside:Boolean):Location? {
  check(BuildConfig.DEBUG || BuildConfig.AEGIS_FRAMEWORK_FIXTURE)
  val value=manifest(context)?:return null
  val circle=geometry(value,placeId?:return null).firstOrNull()?:return null
  val distance=if(outside)geometry(value,placeId).maxOf{NodeGeoEngine.distance(circle.latitude,circle.longitude,it.latitude,it.longitude)+it.radiusMeters}+300 else 0.0
  return Location("fused").apply{latitude=circle.latitude;longitude=circle.longitude+Math.toDegrees(distance/(6371000.0*kotlin.math.cos(Math.toRadians(circle.latitude))));accuracy=13f;time=System.currentTimeMillis();elapsedRealtimeNanos=android.os.SystemClock.elapsedRealtimeNanos()}
 }
 internal fun monitoringCurrent(context:Context,placeId:String,configurationId:String,scope:String):Boolean = synchronized(applyGate) {
  val value=manifest(context)?:return@synchronized false
  val config=prefs(context).getString("installedConfiguration",null)?.let{JSONObject(it)}?:return@synchronized false
  currentDefinition(context,value) && !prefs(context).getBoolean("needsInstall",true) && config.getString("id")==configurationId && NodeNativeSession.read(context)?.nodeId==scope && stamp(value.getString("leaseUntil"))>System.currentTimeMillis() && activeRules(value).any{placeId in it.getJSONArray("placeIds").strings() && prefs(context).getLong("ruleRevision:${it.getString("id")}",-1)==it.getLong("revision") && !prefs(context).getBoolean("completed:${it.getString("id")}",false)}
 }
 internal fun monitoringFinished(context:Context,placeId:String) = synchronized(applyGate) {
  val old=state(context,placeId)
  val edit=prefs(context).edit().remove("nativePending:$placeId")
  if(old.candidate!="unknown")saveState(edit,placeId,old.copy(candidate="unknown",candidateAt=0))
  check(edit.commit())
 }
 internal fun monitoringHistory(context:Context,placeId:String,code:String) = synchronized(applyGate) {
  val value=manifest(context)?:return@synchronized
  history(context,value.getJSONArray("rules").objects().filter{placeId in it.getJSONArray("placeIds").strings()},code,System.currentTimeMillis(),source="monitoring_session")
 }
 internal fun monitoringObservation(context:Context,placeId:String,observation:NodeGeoEngine.Observation,configurationId:String,scope:String) = synchronized(applyGate) {
  if(!monitoringCurrent(context,placeId,configurationId,scope))return@synchronized
  val value=manifest(context)?:return@synchronized
  val now=System.currentTimeMillis()
  val config=JSONObject(prefs(context).getString("installedConfiguration",null)!!)
  val old=state(context,placeId)
  val prior=prefs(context).getString("hybridFix:$placeId",null)?.let{NodeLocation.engine(JSONObject(it))}
  if(!NodeGeoEngine.plausible(prior,observation)){monitoringHistory(context,placeId,"location_implausible");return@synchronized}
  val area=geometry(value,placeId)
  val assessment=NodeGeoEngine.assess(area,observation,now,if(old.confirmed=="unknown")1.0 else 0.4)
  if(assessment.reasonCode in setOf("future_location","stale_location","accuracy_insufficient"))return@synchronized
  if(observation.capturedAt<=old.lastCapturedAt)return@synchronized
  val rules=activeRules(value).filter{placeId in it.getJSONArray("placeIds").strings() && !prefs(context).getBoolean("completed:${it.getString("id")}",false)}
  val decision=NodeGeoEngine.hybrid(old,area,observation,now,prefs(context).getLong("notBefore:$placeId",config.getLong("installedAt")))
  recordNativeDiagnostic(context,value,placeId,observation,now,"monitoring_session",now)
  if(assessment.classification=="inside")monitoringHistory(context,placeId,"real_area_enter_candidate")
  val baselineAt=prefs(context).getLong("confirmedAt:$placeId",old.lastCapturedAt)
  val evidence=NodeGeoEngine.NativeEvidence(decision.transition?:"enter",observation.capturedAt,now,observation=observation,configurationId=configurationId,configurationRevision=config.getString("revision"),observerNodeId=scope,source="monitoring_session")
  if(decision.transition!=null) {
   applyNativeRules(context,value,rules,placeId,old,decision.state,decision.transition,evidence,baselineAt,observation)
   monitoringHistory(context,placeId,"real_area_${decision.transition}_accepted")
  }
  val edit=prefs(context).edit()
  saveState(edit,placeId,decision.state)
  if(decision.state.confirmed!=old.confirmed)edit.putLong("confirmedAt:$placeId",observation.capturedAt)
  if(assessment.classification!="unknown")edit.putString("hybridFix:$placeId",JSONObject().put("latitude",observation.latitude).put("longitude",observation.longitude).put("accuracyMeters",observation.accuracyMeters).put("capturedAt",Instant.ofEpochMilli(observation.capturedAt).toString()).toString())
  check(edit.commit())
  recordReliable(context,placeId,assessment.classification,observation.capturedAt)
  if(armNativeRules(context,rules,decision.state.confirmed))NodeBackgroundWork.enqueue(context)
  if(decision.transition!=null) {NodeMonitoringSessions.finish(context,placeId,"monitoring_transition_accepted");NodeBackgroundWork.enqueue(context)}
 }
 private fun geometry(value:JSONObject,placeId:String):List<NodeGeoEngine.Circle> {
  val place=value.getJSONArray("places").objects().firstOrNull { it.getString("id")==placeId } ?: return emptyList()
  return place.getJSONArray("circles").objects().map { NodeGeoEngine.Circle(it.getDouble("latitude"),it.getDouble("longitude"),it.getDouble("radiusMeters")) }
 }
 private fun install(context:Context,value:JSONObject) {
  synchronized(applyGate){if(!currentDefinition(context,value))return;check(reportPending(context,prefs(context).edit()).putBoolean("needsInstall",true).commit())}
  NodeMonitoringSessions.cancelAll(context,"monitoring_superseded")
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
    for(circle in physicalRegions(place)) {val id=circle.getString("id");regionUntil[id]=maxOf(regionUntil[id]?:0,until)}
   }
  }
  val generation=UUID.randomUUID().toString()
  val installedRegions=JSONArray()
  val regions=linkedMapOf<String,Geofence>()
  for(placeId in placeIds) {
   val place=value.getJSONArray("places").objects().firstOrNull{it.getString("id")==placeId} ?: error("Missing place")
   for(circle in physicalRegions(place)) {
    require(circle.getDouble("radiusMeters") in 50.0..100000.0)
    val circleId=circle.getString("id")
    // Inherited geometry can belong to several logical places; register the
    // physical circle once and retain each place binding in the allowlist.
    val requestId=installedRegions.objects().firstOrNull{it.getString("circleId")==circleId}?.getString("requestId")?:"$generation:${regions.size}"
    installedRegions.put(JSONObject().put("requestId",requestId).put("placeId",placeId).put("circleId",circleId).put("kind",circle.optString("kind","real")).put("geometryRevision",place.optLong("revision",0)).put("envelopeRevision",place.optJSONObject("monitoringEnvelope")?.optLong("revision",0)?:0))
    regions[requestId]=Geofence.Builder().setRequestId(requestId)
     .setCircularRegion(circle.getDouble("latitude"),circle.getDouble("longitude"),circle.getDouble("radiusMeters").toFloat())
     .setTransitionTypes(Geofence.GEOFENCE_TRANSITION_ENTER or Geofence.GEOFENCE_TRANSITION_EXIT)
     .setNotificationResponsiveness(30000).setExpirationDuration((regionUntil.getValue(circleId)-System.currentTimeMillis()).coerceAtLeast(1)).build()
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
    for(placeId in placeIds)if(prefs(context).getString("geometry:$placeId",null)!=geometry(value,placeId).toString())edit.putLong("notBefore:$placeId",activatedAt)
    check(edit.commit())
   }
   Tasks.await(client.removeGeofences(pending(context)),15,TimeUnit.SECONDS)
   val request=GeofencingRequest.Builder().setInitialTrigger(0).addGeofences(regions.values.toList()).build()
   Tasks.await(client.addGeofences(request,pending(context)),20,TimeUnit.SECONDS)
   synchronized(applyGate) {
    if(NodeNativeSession.read(context)?.nodeId!=value.getString("observerNodeId") || manifest(context)?.optString("revision")!=value.getString("revision"))return
    val edit=prefs(context).edit()
    // GMS does not label initial callbacks; disable them. Retain prior physical
    // presence only for unchanged real geometry; registration itself is never movement.
    edit.putString("installedConfiguration",JSONObject().put("id",generation).put("nodeId",value.getString("observerNodeId")).put("revision",value.getString("revision")).put("installedAt",activatedAt).put("regions",installedRegions).toString())
    for(key in prefs(context).all.keys.filter{it.startsWith("nativePending:") || it=="nativeSeen" || it.startsWith("lastNativeEventAt:") || it.startsWith("lastNativeTransition:") || it.startsWith("regionState:") || it.startsWith("regionAt:") || it.startsWith("operationalInside:") || it.startsWith("operationalRegion:") || it.startsWith("operationalAt:")})edit.remove(key)
    for(placeId in placeIds) {
     val encoded=geometry(value,placeId).toString()
     if(prefs(context).getString("geometry:$placeId",null)!=encoded)edit.remove("state:$placeId").remove("diagnostic:$placeId").remove("reliable:$placeId").remove("confirmedAt:$placeId").remove("hybridFix:$placeId")
     edit.putString("geometry:$placeId",encoded)
    }
    for(rule in rules) {
     val id=rule.getString("id")
     val baselineLost=rule.getJSONArray("placeIds").strings().any{placeId->prefs(context).getString("geometry:$placeId",null)!=geometry(value,placeId).toString() || state(context,placeId).confirmed=="unknown"}
     if(baselineLost)edit.putBoolean("armed:$id",false)
     if(prefs(context).getLong("ruleRevision:$id",-1)!=rule.getLong("revision"))edit.putBoolean("armed:$id",false).putLong("ruleRevision:$id",rule.getLong("revision")).remove("completed:$id").remove("accepted:$id")
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
   if(observation.optString("status")=="available") process(context,NodeLocation.engine(observation),allowConfirmation=false,baselineOnly=true)
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
  return JSONObject().put("manifestRevision",value.getString("revision")).put("rules",rules).put("installationComplete",!prefs(context).getBoolean("needsInstall",true) && prefs(context).getString("installedRevision",null)==value.getString("revision"))
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
 /** Internal evidence keeps capture, receipt and confirmation clocks separate. */
 private fun state(context:Context,placeId:String):NodeGeoEngine.State {
  val raw=prefs(context).getString("state:$placeId",null)?.let{JSONObject(it)} ?: return NodeGeoEngine.State()
  return NodeGeoEngine.State(raw.optString("confirmed","unknown"),raw.optString("candidate","unknown"),raw.optLong("candidateAt",0),raw.optLong("lastCapturedAt",0))
 }
 private fun saveState(edit:SharedPreferences.Editor,placeId:String,state:NodeGeoEngine.State) {
  edit.putString("state:$placeId",JSONObject().put("confirmed",state.confirmed).put("candidate",state.candidate).put("candidateAt",state.candidateAt).put("lastCapturedAt",state.lastCapturedAt).toString())
 }
 private fun history(context:Context,rules:List<JSONObject>,code:String,receivedAt:Long,transition:String?=null,evidenceAt:Long?=null,classification:String="unknown",regionCurrent:Boolean=true,source:String="android_event",occurrenceCreated:Boolean=false) {
  val edit=prefs(context).edit()
  for(rule in rules) {
   val id=rule.getString("id")
   val previous=prefs(context).getString("history:$id",null)?.let{JSONArray(it).objects()}?:emptyList()
   val item=JSONObject().put("code",code).put("receivedAt",receivedAt).put("source",source).put("armed",prefs(context).getBoolean("armed:$id",false)).put("classification",classification).put("regionCurrent",regionCurrent).put("occurrenceCreated",occurrenceCreated)
   if(transition!=null)item.put("transition",transition)
   if(evidenceAt!=null)item.put("evidenceAt",evidenceAt).put("ageMillis",(receivedAt-evidenceAt).coerceAtLeast(0))
   edit.putString("history:$id",JSONArray((previous.filter{receivedAt-it.optLong("receivedAt",0) in 0..TimeUnit.DAYS.toMillis(7)}+item).takeLast(32)).toString())
   if(source=="android_event")edit.putLong("lastNativeEventAt:$id",receivedAt).putString("lastNativeTransition:$id",transition)
  }
  check(edit.commit())
 }
 fun nativeError(context:Context,code:Int) = synchronized(applyGate) {
  val value=manifest(context) ?: return@synchronized
  if(NodeNativeSession.read(context)?.nodeId!=value.optString("observerNodeId"))return@synchronized
  val reason=when(code){GeofenceStatusCodes.GEOFENCE_NOT_AVAILABLE->"native_service_unavailable";GeofenceStatusCodes.GEOFENCE_TOO_MANY_GEOFENCES->"native_region_limit";GeofenceStatusCodes.GEOFENCE_TOO_MANY_PENDING_INTENTS->"native_registration_limit";else->"native_event_error"}
  history(context,value.getJSONArray("rules").objects(),reason,System.currentTimeMillis(),regionCurrent=false)
  // Only loss of the geofencing service requires registration recovery.
  if(code==GeofenceStatusCodes.GEOFENCE_NOT_AVAILABLE)scheduleRestore(context)
 }
 private fun nativeContext(context:Context,placeId:String,config:JSONObject,unionPresence:String):NodeGeoEngine.NativeContext {
  val old=state(context,placeId)
  val diagnostic=prefs(context).getString("reliable:$placeId",null)?.let{JSONObject(it)}?:prefs(context).getString("diagnostic:$placeId",null)?.let{JSONObject(it)}
  return NodeGeoEngine.NativeContext(installedAt=config.getLong("installedAt"),baselineReliable=old.confirmed!="unknown" && prefs(context).getLong("confirmedAt:$placeId",old.lastCapturedAt)>=prefs(context).getLong("notBefore:$placeId",config.getLong("installedAt")),baselineAt=prefs(context).getLong("confirmedAt:$placeId",old.lastCapturedAt),unionPresence=unionPresence,recentReliablePresence=diagnostic?.optString("presence","unknown")?:"unknown",recentReliableAt=diagnostic?.optLong("lastObservationAt",0)?:0)
 }
 private fun recordReliable(context:Context,placeId:String,classification:String,capturedAt:Long) {
  if(classification !in setOf("inside","outside"))return
  val prior=prefs(context).getString("reliable:$placeId",null)?.let{JSONObject(it).optLong("lastObservationAt",0)}?:0
  if(capturedAt>prior)check(prefs(context).edit().putString("reliable:$placeId",JSONObject().put("presence",classification).put("lastObservationAt",capturedAt).toString()).commit())
 }
 /** Coordinate diagnostics retain their acquisition clock; native receipt is not a fix. */
 private fun recordNativeDiagnostic(context:Context,value:JSONObject,placeId:String,observation:NodeGeoEngine.Observation?,receivedAt:Long,source:String,now:Long) {
  if(observation!=null && observation.capturedAt>now)return
  val prior=prefs(context).getString("diagnostic:$placeId",null)?.let{JSONObject(it)}
  val priorAt=prior?.let{it.optLong("lastObservationAt",it.optLong("receivedAt",0))}?:0
  val evidenceAt=observation?.capturedAt?:receivedAt
  if(evidenceAt<priorAt)return
  val diagnostic=JSONObject().put("receivedAt",receivedAt).put("source",source)
  if(observation==null) {
   diagnostic.put("presence","unknown").put("accuracyMeters",JSONObject.NULL).put("reasonCode","native_location_absent")
  } else {
   val assessment=NodeGeoEngine.assess(geometry(value,placeId),observation,if(source=="android_event")observation.capturedAt else now,if(state(context,placeId).confirmed!="unknown")0.4 else 1.0)
   val stale=now-observation.capturedAt>NodeGeoEngine.MAX_AGE_MILLIS
   diagnostic.put("lastObservationAt",observation.capturedAt).put("accuracyMeters",observation.accuracyMeters.takeIf{it.isFinite()&&it>0.0}?:JSONObject.NULL)
    .put("presence",if(stale)"unknown" else assessment.classification).put("reasonCode",if(stale)"stale_location" else assessment.reasonCode)
  }
  check(prefs(context).edit().putString("diagnostic:$placeId",diagnostic.toString()).commit())
 }
 private fun pendingEvidence(raw:JSONObject)=NodeGeoEngine.NativeEvidence(raw.getString("transition"),raw.optLong("detectedAt",0).takeIf{it>0},raw.getLong("receivedAt"),configurationId=raw.optString("configurationId").takeIf{it.isNotEmpty()},configurationRevision=raw.optString("configurationRevision").takeIf{it.isNotEmpty()},observerNodeId=raw.optString("observerNodeId").takeIf{it.isNotEmpty()})
 /** Receiver adapter only: not a command, plugin invocation or remote RPC. */
 internal fun nativeTransition(context:Context,evidence:NodeNativeTransition) = synchronized(applyGate) {
  NodeMonitoringSessions.recoverInterrupted(context)
  val value=manifest(context) ?: return@synchronized
  val allRules=value.getJSONArray("rules").objects()
  if(NodeNativeSession.read(context)?.nodeId!=value.optString("observerNodeId"))return@synchronized
  val preferences=prefs(context);val now=System.currentTimeMillis()
  val transition=when(evidence.transition){Geofence.GEOFENCE_TRANSITION_ENTER->"enter";Geofence.GEOFENCE_TRANSITION_EXIT->"exit";else->null}
  fun reject(code:String,current:Boolean=false){history(context,allRules,code,evidence.receivedAt,transition,evidence.observation?.capturedAt,regionCurrent=current)}
  if(transition==null){reject("native_event_invalid_transition");return@synchronized}
  val config=preferences.getString("installedConfiguration",null)?.let{JSONObject(it)}
  if(config==null || preferences.getBoolean("needsInstall",true) || config.optString("nodeId")!=value.getString("observerNodeId") || config.optString("revision")!=value.getString("revision") || preferences.getString("installedRevision",null)!=value.getString("revision") || stamp(value.getString("leaseUntil"))<=now || preferences.getLong("installedUntil",0)<=now){reject("native_configuration_stale");return@synchronized}
  val regions=config.getJSONArray("regions").objects()
  val selected=regions.filter{it.getString("requestId") in evidence.regionIds}
  if(evidence.regionIds.isEmpty() || evidence.regionIds.size>100 || selected.map{it.getString("requestId")}.toSet()!=evidence.regionIds.toSet()){reject("native_region_unknown");return@synchronized}
  if(evidence.receivedAt<config.getLong("installedAt") || evidence.receivedAt>now || evidence.observation?.capturedAt?.let{it<config.getLong("installedAt")}==true){reject("native_configuration_stale");return@synchronized}
  val native=NodeGeoEngine.NativeEvidence(transition,evidence.observation?.capturedAt,evidence.receivedAt,observation=evidence.observation,configurationId=config.getString("id"),configurationRevision=config.getString("revision"),observerNodeId=config.getString("nodeId"))
  if(now-evidence.receivedAt>NodeGeoEngine.NATIVE_MAX_DELAY_MILLIS || evidence.observation?.capturedAt?.let{now-it>NodeGeoEngine.NATIVE_MAX_DELAY_MILLIS}==true){reject("native_event_stale",true);return@synchronized}
  if(evidence.observation?.capturedAt?.let{it>now}==true){reject("native_event_future",true);return@synchronized}
  val relevant=activeRules(value,now).filter{rule->preferences.getLong("ruleRevision:${rule.getString("id")}",-1)==rule.getLong("revision") && !preferences.getBoolean("completed:${rule.getString("id")}",false) && rule.getJSONArray("placeIds").strings().any{place->selected.any{it.getString("placeId")==place}}}
  if(relevant.isEmpty()){reject("native_rule_inactive",true);return@synchronized}
  if(evidence.observation!=null && selected.map{it.getString("placeId")}.distinct().any{place->
   val previous=preferences.getString("hybridFix:$place",null)?.let{NodeLocation.engine(JSONObject(it))}
   previous!=null && evidence.observation.capturedAt>previous.capturedAt && !NodeGeoEngine.plausible(previous,evidence.observation)
  }){reject("location_implausible",true);return@synchronized}
  // Android supplies no event ID or detection clock. Fingerprint captures when
  // present; receipt-only duplicates are conservatively suppressed for 10 min.
  val regionEvidenceAt=evidence.observation?.capturedAt?:evidence.receivedAt
  val fingerprint=UUID.nameUUIDFromBytes((config.getString("id")+transition+evidence.regionIds.sorted().joinToString()+ (evidence.observation?.capturedAt?.toString()?:"no_location")).toByteArray()).toString()
  val seen=preferences.getString("nativeSeen",null)?.let{JSONArray(it).objects()}?:emptyList()
  val duplicate=seen.firstOrNull{it.getString("key")==fingerprint && now-it.getLong("at")<=NodeGeoEngine.NATIVE_MAX_DELAY_MILLIS}
  val operationalReentry=transition=="enter" && selected.any{it.optString("kind","real")=="operational" && !preferences.getBoolean("operationalInside:${it.getString("placeId")}",false)}
  val duplicateNeedsConfirmation=duplicate!=null && evidence.observation==null && selected.any{preferences.getLong("confirmedAt:${it.getString("placeId")}",0)>duplicate.getLong("at")}
  if(duplicate!=null && !duplicateNeedsConfirmation && !operationalReentry){history(context,relevant,"native_event_duplicate",evidence.receivedAt,transition,evidence.observation?.capturedAt);return@synchronized}
  val operational=selected.filter{it.optString("kind","real")=="operational"}
  if(evidence.observation!=null && operational.any{preferences.getLong("operationalAt:${it.getString("requestId")}",0)>evidence.observation.capturedAt}){history(context,relevant,"native_event_out_of_order",evidence.receivedAt,transition,evidence.observation.capturedAt);return@synchronized}
  for(placeId in operational.map{it.getString("placeId")}.distinct()) {
   val consumers=relevant.filter{placeId in it.getJSONArray("placeIds").strings()}
   history(context,consumers,if(transition=="enter")"operational_enter_received" else "operational_exit_received",evidence.receivedAt,transition,evidence.observation?.capturedAt,source="operational_event")
   val key="operationalInside:$placeId"
   val wasInside=preferences.getBoolean(key,false)
   val regionEdit=preferences.edit()
   for(region in operational.filter{it.getString("placeId")==placeId})regionEdit.putBoolean("operationalRegion:${region.getString("requestId")}",transition=="enter").putLong("operationalAt:${region.getString("requestId")}",regionEvidenceAt)
   check(regionEdit.commit())
   val isInside=regions.filter{it.optString("kind","real")=="operational" && it.getString("placeId")==placeId}.any{preferences.getBoolean("operationalRegion:${it.getString("requestId")}",false)}
   preferences.edit().putBoolean(key,isInside).commit()
   if(isInside && !wasInside)NodeMonitoringSessions.begin(context,placeId,config.getString("id"),config.getString("nodeId"),"operational_enter")
   // A fix belongs to the real geometry regardless of which region woke us.
   val cache=NodeNativeNotifications.prefs(context)
   val compatible=evidence.observation?:cache.getString("lastObservation",null)?.takeIf{cache.getString("observationScopeId",null)==config.getString("nodeId")}?.let{NodeLocation.engine(JSONObject(it))}
   compatible?.takeIf{now-it.capturedAt in 0..NodeGeoEngine.MAX_AGE_MILLIS}?.let{monitoringObservation(context,placeId,it,config.getString("id"),config.getString("nodeId"))}
  }
  val selectedReal=selected.filter{it.optString("kind","real")=="real"}
  if(selectedReal.isEmpty()) {
   check(preferences.edit().putString("nativeSeen",JSONArray((seen.filter{now-it.getLong("at")<=NodeGeoEngine.NATIVE_MAX_DELAY_MILLIS}+JSONObject().put("key",fingerprint).put("at",now)).takeLast(64)).toString()).commit())
   return@synchronized
  }
  val target=if(transition=="enter")"inside" else "outside"
  if(evidence.observation!=null && selected.any{preferences.getLong("regionAt:${it.getString("requestId")}",0)>regionEvidenceAt && preferences.getString("regionState:${it.getString("requestId")}","unknown") !in setOf("unknown",target)}){history(context,relevant,"native_event_out_of_order",evidence.receivedAt,transition,evidence.observation.capturedAt);return@synchronized}
  val edit=preferences.edit()
  for(region in selectedReal) {
   val key=region.getString("requestId")
   if(regionEvidenceAt>=preferences.getLong("regionAt:$key",0))edit.putString("regionState:$key",target).putLong("regionAt:$key",regionEvidenceAt)
  }
  check(edit.commit())
  var confirm=false;var changed=false
  for(placeId in selectedReal.map{it.getString("placeId")}.distinct()) {
   val consumers=relevant.filter{placeId in it.getJSONArray("placeIds").strings()}
   if(consumers.isEmpty())continue
   evidence.observation?.let{fix->
    val membership=preferences.edit()
    val place=value.getJSONArray("places").objects().first{it.getString("id")==placeId}
    for(region in regions.filter{it.optString("kind","real")=="real" && it.getString("placeId")==placeId && it.getString("requestId") !in evidence.regionIds}) {
     val key=region.getString("requestId")
     if(fix.capturedAt<preferences.getLong("regionAt:$key",0))continue
     val circle=place.getJSONArray("circles").objects().first{it.getString("id")==region.getString("circleId")}
     val classified=NodeGeoEngine.classify(listOf(NodeGeoEngine.Circle(circle.getDouble("latitude"),circle.getDouble("longitude"),circle.getDouble("radiusMeters"))),fix,fix.capturedAt)
     membership.putString("regionState:$key",classified).putLong("regionAt:$key",fix.capturedAt)
    }
    check(membership.commit())
   }
   val occupancies=regions.filter{it.optString("kind","real")=="real" && it.getString("placeId")==placeId}.map{preferences.getString("regionState:${it.getString("requestId")}","unknown")}
   val union=when{occupancies.any{it=="inside"}->"inside";occupancies.all{it=="outside"}->"outside";else->"unknown"}
   history(context,consumers,if(transition=="enter")"native_inner_enter_received" else "native_inner_exit_received",evidence.receivedAt,transition,evidence.observation?.capturedAt)
   val old=state(context,placeId)
   evidence.observation?.let{recordReliable(context,placeId,NodeGeoEngine.assess(geometry(value,placeId),it,it.capturedAt).classification,it.capturedAt)}
   val evaluated=NodeGeoEngine.native(old,native,nativeContext(context,placeId,config,union),geometry(value,placeId),now)
   val decision=if(duplicateNeedsConfirmation && evaluated.transition!=null)NodeGeoEngine.NativeDecision(old.copy(candidate=target,candidateAt=evidence.receivedAt),needsConfirmation=true,reasonCode="transition_needs_confirmation",detectedAt=regionEvidenceAt) else evaluated
   history(context,consumers,decision.reasonCode,evidence.receivedAt,transition,evidence.observation?.capturedAt,evidence.observation?.let{NodeGeoEngine.assess(geometry(value,placeId),it,it.capturedAt).classification}?:union)
   val diagnosticRejected=decision.reasonCode in setOf("native_event_future","native_event_replaced","native_event_stale","native_event_duplicate","native_event_out_of_order","native_transition_unknown")
   if(!diagnosticRejected && (evidence.observation!=null || decision.needsConfirmation || decision.transition!=null || decision.reasonCode=="native_event_initial"))recordNativeDiagnostic(context,value,placeId,evidence.observation,evidence.receivedAt,"android_event",now)
   val baselineAt=preferences.getLong("confirmedAt:$placeId",old.lastCapturedAt)
   if(decision.transition!=null) {
    changed=applyNativeRules(context,value,consumers,placeId,old,decision.state,decision.transition,native,baselineAt,evidence.observation)||changed
    history(context,consumers,"real_area_${decision.transition}_accepted",evidence.receivedAt,transition,evidence.observation?.capturedAt,decision.state.confirmed)
   }
   val update=preferences.edit()
   saveState(update,placeId,decision.state)
   if(decision.state.confirmed!=old.confirmed)update.putLong("confirmedAt:$placeId",evidence.observation?.capturedAt?:evidence.receivedAt)
   if(decision.needsConfirmation) {
    update.putString("nativePending:$placeId",JSONObject().put("transition",transition).put("receivedAt",evidence.receivedAt).put("detectedAt",evidence.observation?.capturedAt?:JSONObject.NULL).put("deadline",evidence.receivedAt+NodeGeoEngine.NATIVE_CONFIRMATION_WINDOW_MILLIS).put("baselineAt",baselineAt).put("configurationId",native.configurationId).put("configurationRevision",native.configurationRevision).put("observerNodeId",native.observerNodeId).toString())
    val hybridApproved=config.getJSONArray("regions").objects().any{it.getString("placeId")==placeId && it.optString("kind","real")=="operational"}
    if(!hybridApproved || !NodeMonitoringSessions.begin(context,placeId,config.getString("id"),config.getString("nodeId"),"real_$transition"))confirm=true
   } else update.remove("nativePending:$placeId")
   check(update.commit())
   if(decision.state.confirmed!="unknown")changed=armNativeRules(context,consumers,decision.state.confirmed)||changed
   if(decision.transition!=null)NodeMonitoringSessions.finish(context,placeId,"monitoring_transition_accepted")
  }
  check(preferences.edit().putString("nativeSeen",JSONArray((seen.filter{now-it.getLong("at")<=NodeGeoEngine.NATIVE_MAX_DELAY_MILLIS}+JSONObject().put("key",fingerprint).put("at",now)).takeLast(64)).toString()).commit())
  if(confirm) {
   val work=OneTimeWorkRequestBuilder<NodeGeoConfirmWorker>().setInputData(workDataOf("native" to true,"receivedAt" to evidence.receivedAt,"deadline" to (evidence.receivedAt+NodeGeoEngine.NATIVE_CONFIRMATION_WINDOW_MILLIS))).build()
   WorkManager.getInstance(context).enqueueUniqueWork("aegis-native-geofence-confirm",ExistingWorkPolicy.APPEND_OR_REPLACE,work)
  }
  if(changed)NodeBackgroundWork.enqueue(context)
 }
 private fun armNativeRules(context:Context,rules:List<JSONObject>,presence:String):Boolean {
  var changed=false
  for(rule in rules) {
   val id=rule.getString("id");val initial=if(rule.getString("transition")=="enter")"outside" else "inside"
   if(presence==initial && prefs(context).getString("accepted:$id",null)==null && !prefs(context).getBoolean("completed:$id",false) && !prefs(context).getBoolean("armed:$id",false)) {
    check(reportPending(context,prefs(context).edit()).putBoolean("armed:$id",true).commit());changed=true
   }
  }
  return changed
 }
 private fun applyNativeRules(context:Context,value:JSONObject,rules:List<JSONObject>,placeId:String,old:NodeGeoEngine.State,next:NodeGeoEngine.State,transition:String,evidence:NodeGeoEngine.NativeEvidence,baselineAt:Long,observation:NodeGeoEngine.Observation?):Boolean {
  var changed=false
  var observationQueued=false
  for(rule in rules) {
   val id=rule.getString("id")
   if(rule.getString("transition")!=transition)continue
   if(prefs(context).getString("accepted:$id",null)!=null)continue
   if(!prefs(context).getBoolean("armed:$id",false)){history(context,listOf(rule),"native_${transition}_unarmed",evidence.receivedAt,transition,evidence.detectedAt,next.confirmed);continue}
   val effectiveAt=evidence.detectedAt?:evidence.receivedAt
   if(!rule.isNull("startsAt") && effectiveAt<stamp(rule.getString("startsAt")))continue
   // Preserve the existing auxiliary location outbox only for a real, fresh fix
   // supporting an armed rule's accepted transition. Native-only evidence is not
   // a coordinate, and a delayed native fix does not become a current location.
   if(!observationQueued && observation!=null && NodeGeoEngine.assess(geometry(value,placeId),observation,System.currentTimeMillis()).classification==next.confirmed) {
    NodeLocation.queueTransition(context,observation)
    observationQueued=true
   }
   val identity="${value.getString("observerNodeId")}:$id:${rule.getLong("revision")}:$placeId:$baselineAt:$transition"
   // Persist the idempotency key and outbox row in one SQLite transaction before
   // committing presence. A replay after process death reuses that occurrence.
   history(context,listOf(rule),"transition_accepted",evidence.receivedAt,transition,evidence.detectedAt,next.confirmed)
   val confirmedAt=System.currentTimeMillis()
   val localEvidence=JSONObject().put("source",evidence.source).put("configurationId",evidence.configurationId).put("configurationRevision",evidence.configurationRevision).put("observerNodeId",evidence.observerNodeId).put("receivedAt",evidence.receivedAt).put("locationCapturedAt",evidence.detectedAt?:JSONObject.NULL).put("confirmedAt",confirmedAt)
   val created=createOccurrence(context,rule,confirmedAt,identity,localEvidence)
   if(created) {
    check(reportPending(context,prefs(context).edit()).putBoolean("armed:$id",false).putBoolean("completed:$id",!rule.getBoolean("repeat")).commit())
    history(context,listOf(rule),"occurrence_created",evidence.receivedAt,transition,evidence.detectedAt,next.confirmed,occurrenceCreated=true);changed=true
   } else {
    check(prefs(context).edit().putString("accepted:$id",JSONObject().put("ruleRevision",rule.getLong("revision")).put("identity",identity).put("acceptedAt",System.currentTimeMillis()).put("receivedAt",evidence.receivedAt).put("detectedAt",evidence.detectedAt?:JSONObject.NULL).put("transition",transition).toString()).putBoolean("armed:$id",false).commit())
    history(context,listOf(rule),"occurrence_outbox_full",evidence.receivedAt,transition,evidence.detectedAt,next.confirmed)
    changed=true
   }
  }
  return changed
 }
 /** Single supplemental acquisition; delayed WorkManager never resurrects it. */
 fun confirmNative(context:Context,observation:NodeGeoEngine.Observation?=null,receivedBefore:Long=Long.MAX_VALUE) = synchronized(applyGate) {
  val value=manifest(context) ?: return@synchronized
  val config=prefs(context).getString("installedConfiguration",null)?.let{JSONObject(it)}?:return@synchronized
  if(!currentDefinition(context,value) || prefs(context).getBoolean("needsInstall",true) || config.optString("nodeId")!=value.optString("observerNodeId") || config.optString("revision")!=value.optString("revision") || prefs(context).getString("installedRevision",null)!=value.optString("revision") || prefs(context).getLong("installedUntil",0)<=System.currentTimeMillis() || stamp(value.getString("leaseUntil"))<=System.currentTimeMillis())return@synchronized
  val now=System.currentTimeMillis();var changed=false
  for(key in prefs(context).all.keys.filter{it.startsWith("nativePending:")}) {
   val placeId=key.removePrefix("nativePending:")
   val raw=prefs(context).getString(key,null)?.let{JSONObject(it)}?:continue
   if(raw.getLong("receivedAt")>receivedBefore)continue
   val rules=activeRules(value,now).filter{placeId in it.getJSONArray("placeIds").strings() && prefs(context).getLong("ruleRevision:${it.getString("id")}",-1)==it.getLong("revision") && !prefs(context).getBoolean("completed:${it.getString("id")}",false)}
   val evidence=pendingEvidence(raw)
   if(rules.isEmpty()){check(prefs(context).edit().remove(key).commit());continue}
   if(observation!=null && observation.capturedAt<evidence.receivedAt)continue
   // A fresh diagnostic acquisition remains useful even if its old transition
   // candidate expired; expiry still prevents any occurrence below.
   if(observation!=null)recordNativeDiagnostic(context,value,placeId,observation,now,"one_shot",now)
   if(raw.getString("configurationId")!=config.getString("id") || now>raw.getLong("deadline") || observation==null) {
    val code=if(now>raw.getLong("deadline"))"transition_expired" else "confirmation_unavailable"
    history(context,rules,code,now,evidence.transition,evidence.detectedAt,source="one_shot")
    val expiredEdit=prefs(context).edit().remove("nativePending:$placeId")
    saveState(expiredEdit,placeId,state(context,placeId).copy(candidate="unknown",candidateAt=0))
    check(expiredEdit.commit());continue
   }
   val old=state(context,placeId)
   recordReliable(context,placeId,NodeGeoEngine.assess(geometry(value,placeId),observation,now).classification,observation.capturedAt)
   val membership=prefs(context).edit()
   for(region in config.getJSONArray("regions").objects().filter{it.optString("kind","real")=="real" && it.getString("placeId")==placeId}) {
    val place=value.getJSONArray("places").objects().first{it.getString("id")==placeId}
    val circle=place.getJSONArray("circles").objects().first{it.getString("id")==region.getString("circleId")}
    val classified=NodeGeoEngine.classify(listOf(NodeGeoEngine.Circle(circle.getDouble("latitude"),circle.getDouble("longitude"),circle.getDouble("radiusMeters"))),observation,now)
    membership.putString("regionState:${region.getString("requestId")}",classified).putLong("regionAt:${region.getString("requestId")}",observation.capturedAt)
   }
   check(membership.commit())
   val decision=NodeGeoEngine.confirmNative(old,evidence,nativeContext(context,placeId,config,"unknown"),geometry(value,placeId),observation,now)
   recordNativeDiagnostic(context,value,placeId,observation,now,"one_shot",now)
   if(decision.transition!=null)changed=applyNativeRules(context,value,rules,placeId,old,decision.state,decision.transition,evidence,raw.getLong("baselineAt"),observation)||changed
   val edit=prefs(context).edit().remove("nativePending:$placeId")
   saveState(edit,placeId,decision.state)
   if(decision.state.confirmed!=old.confirmed)edit.putLong("confirmedAt:$placeId",observation.capturedAt)
   check(edit.commit())
   if(decision.state.confirmed!="unknown")changed=armNativeRules(context,rules,decision.state.confirmed)||changed
   history(context,rules,decision.reasonCode,now,evidence.transition,observation.capturedAt,decision.state.confirmed,source="one_shot")
  }
  if(changed)NodeBackgroundWork.enqueue(context)
 }
 fun nativeConfirmationPending(context:Context):Boolean=prefs(context).all.keys.any{it.startsWith("nativePending:")}

 fun process(context:Context,observation:NodeGeoEngine.Observation,allowConfirmation:Boolean=true,baselineOnly:Boolean=false) = synchronized(applyGate) {
  val nativePlaces=if(baselineOnly)emptySet()else prefs(context).all.keys.filter{it.startsWith("nativePending:")}.map{it.removePrefix("nativePending:")}.toSet()
  if(nativePlaces.isNotEmpty())confirmNative(context,observation)
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
  for(placeId in rules.flatMap{it.getJSONArray("placeIds").strings()}.toSet()-nativePlaces) {
   val notBefore=prefs(context).getLong("notBefore:$placeId",0)
   if(observation.capturedAt<notBefore)continue
   val saved=prefs(context).getString("state:$placeId",null)?.let{JSONObject(it)}
   val old=if(saved==null) NodeGeoEngine.State() else NodeGeoEngine.State(saved.getString("confirmed"),saved.getString("candidate"),saved.getLong("candidateAt"),saved.getLong("lastCapturedAt"))
   if(observation.capturedAt<=old.lastCapturedAt)continue
   val assessment=NodeGeoEngine.assess(geometry(value,placeId),observation,now)
   val previousFix=prefs(context).getString("hybridFix:$placeId",null)?.let{NodeLocation.engine(JSONObject(it))}
   if(!NodeGeoEngine.plausible(previousFix,observation)){monitoringHistory(context,placeId,"location_implausible");continue}
   if(assessment.classification!="unknown")check(prefs(context).edit().putString("hybridFix:$placeId",JSONObject().put("latitude",observation.latitude).put("longitude",observation.longitude).put("accuracyMeters",observation.accuracyMeters).put("capturedAt",Instant.ofEpochMilli(observation.capturedAt).toString()).toString()).commit())
   recordReliable(context,placeId,assessment.classification,observation.capturedAt)
   // Future and stale deliveries are visible as diagnostics, but cannot replace
   // newer state or establish temporal persistence.
   val decision=if(assessment.reasonCode in setOf("future_location","stale_location"))NodeGeoEngine.Decision(old)
    else if(baselineOnly)NodeGeoEngine.Decision(if(assessment.classification in setOf("inside","outside"))NodeGeoEngine.State(assessment.classification,lastCapturedAt=observation.capturedAt)else old.copy(candidate="unknown",candidateAt=0,lastCapturedAt=observation.capturedAt))
    else NodeGeoEngine.observe(old,assessment.classification,observation.capturedAt,notBefore)
   val state=decision.state
   val presence=if(state.candidate!="unknown")"unknown" else assessment.classification
   val reason=if(state.candidate!="unknown")"awaiting_confirmation" else assessment.reasonCode
   val current=synchronized(applyGate) {
    if(prefs(context).getBoolean("needsInstall",true) || prefs(context).getLong("notBefore:$placeId",0)!=notBefore || manifest(context)?.optString("revision")!=value.getString("revision") || NodeNativeSession.read(context)?.nodeId!=observer)false
    else {
     val edit=prefs(context).edit()
     if(state.confirmed!=old.confirmed)edit.putLong("confirmedAt:$placeId",observation.capturedAt)
     if(state!=old)edit.putString("state:$placeId",JSONObject().put("confirmed",state.confirmed).put("candidate",state.candidate).put("candidateAt",state.candidateAt).put("lastCapturedAt",state.lastCapturedAt).toString())
     val priorDiagnostic=prefs(context).getString("diagnostic:$placeId",null)?.let{JSONObject(it)}
     if(priorDiagnostic==null || observation.capturedAt>priorDiagnostic.optLong("lastObservationAt",0))edit.putString("diagnostic:$placeId",JSONObject().put("lastObservationAt",observation.capturedAt).put("accuracyMeters",observation.accuracyMeters.takeIf{it.isFinite()&&it>0.0}?:JSONObject.NULL).put("presence",presence).put("reasonCode",reason).put("receivedAt",now).toString())
     prefs(context).getString("installedConfiguration",null)?.let{raw->
      for(region in JSONObject(raw).getJSONArray("regions").objects().filter{it.optString("kind","real")=="real" && it.getString("placeId")==placeId}) {
       val place=value.getJSONArray("places").objects().first{it.getString("id")==placeId}
       val circle=place.getJSONArray("circles").objects().first{it.getString("id")==region.getString("circleId")}
       val classification=NodeGeoEngine.classify(listOf(NodeGeoEngine.Circle(circle.getDouble("latitude"),circle.getDouble("longitude"),circle.getDouble("radiusMeters"))),observation,now)
       edit.putString("regionState:${region.getString("requestId")}",classification).putLong("regionAt:${region.getString("requestId")}",observation.capturedAt)
      }
     }
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
    if(baselineOnly && assessment.classification in setOf("inside","outside") && state.confirmed!=initial && prefs(context).getBoolean("armed:$id",false)) {check(reportPending(context,prefs(context).edit()).putBoolean("armed:$id",false).commit());needsSync=true}
    if(assessment.classification==initial && state.confirmed==initial && prefs(context).getString("accepted:$id",null)==null && !prefs(context).getBoolean("armed:$id",false)) {check(reportPending(context,prefs(context).edit()).putBoolean("armed:$id",true).commit());needsSync=true}
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
     val keep=current.getJSONArray("places").objects().filter{it.getString("id") in consumers}.flatMap{physicalRegions(it).map{circle->circle.getString("id")}}.toSet()
     val removed=current.getJSONArray("places").objects().flatMap{physicalRegions(it).map{circle->circle.getString("id")}}.toSet()-keep
     val retired=prefs(context).getString("installedConfiguration",null)?.let{JSONObject(it).getJSONArray("regions").objects().filter{region->region.getString("circleId") in removed}.map{region->region.getString("requestId")}}?:emptyList()
     if(retired.isNotEmpty())try{Tasks.await(LocationServices.getGeofencingClient(context).removeGeofences(retired),15,TimeUnit.SECONDS);if(keep.isEmpty())prefs(context).edit().putBoolean("hasRegions",false).commit()}catch(_:Exception){}
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
 private fun createOccurrence(context:Context,rule:JSONObject,capturedAt:Long,identity:String?=null,evidence:JSONObject?=null):Boolean {
  if(identity!=null) {
   val exists=Store(context).use{store->store.readableDatabase.rawQuery("SELECT 1 FROM transition_keys WHERE identity=?",arrayOf(identity)).use{it.moveToFirst()}}
   if(exists){finishLocalPosts(context);return true}
  }
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
     put("transition",rule.getString("transition"));put("occurredAt",Instant.ofEpochMilli(capturedAt).toString());put("sequence",sequence);put("payload",JSONObject(rule.toString()).put("observerNodeId",NodeNativeSession.read(context)?.nodeId).apply{if(evidence!=null)put("localEvidence",evidence)}.toString())
    })
    if(identity!=null)db.insertOrThrow("transition_keys",null,ContentValues().apply{put("identity",identity);put("recordedAt",System.currentTimeMillis())})
    db.delete("transition_keys","recordedAt<?",arrayOf((System.currentTimeMillis()-TimeUnit.DAYS.toMillis(8)).toString()))
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
    synchronized(applyGate) {
     history(context,listOf(rule),if(result.status in setOf("success","duplicate"))"notification_posted" else "notification_failed",now,rule.optString("transition"),at,source="local_notification")
    }
    db.update("events",ContentValues().apply {put("postResult",result.status);if(result.status in setOf("success","duplicate")) put("postedAt",Instant.now().toString())},"eventId=?",arrayOf(eventId))
   }
  }
 }
 fun pendingOccurrences(context:Context):Set<String> = Store(context).use{store->store.readableDatabase.rawQuery("SELECT occurrenceId FROM events",null).use{cursor->val result=mutableSetOf<String>();while(cursor.moveToNext())result.add(cursor.getString(0));result}}
 private fun recoverAccepted(context:Context) = synchronized(applyGate) {
  val value=manifest(context)?:return@synchronized
  if(!currentDefinition(context,value))return@synchronized
  val now=System.currentTimeMillis()
  for(key in prefs(context).all.keys.filter{it.startsWith("accepted:")}) {
   val id=key.removePrefix("accepted:");val pending=JSONObject(prefs(context).getString(key,null)?:continue)
   val rule=activeRules(value,now).firstOrNull{it.getString("id")==id && it.getLong("revision")==pending.getLong("ruleRevision")}
   if(rule==null || now-pending.getLong("receivedAt")>NodeGeoEngine.NATIVE_MAX_DELAY_MILLIS){
    if(rule!=null)history(context,listOf(rule),"transition_expired",now,pending.getString("transition"),pending.optLong("detectedAt",0).takeIf{it>0},source="local_recovery")
    check(prefs(context).edit().remove(key).commit());continue
   }
   if(createOccurrence(context,rule,pending.getLong("acceptedAt"),pending.getString("identity"))) {
    check(reportPending(context,prefs(context).edit()).remove(key).putBoolean("completed:$id",!rule.getBoolean("repeat")).commit())
    history(context,listOf(rule),"occurrence_created",now,pending.getString("transition"),pending.optLong("detectedAt",0).takeIf{it>0},source="local_recovery",occurrenceCreated=true)
    NodeBackgroundWork.enqueue(context)
   }
  }
 }
 fun flushEvents(context:Context,maxEvents:Int=100,deadlineElapsed:Long=Long.MAX_VALUE,occurrenceIds:Set<String>?=null,connectTimeoutMillis:Int=10000,readTimeoutMillis:Int=15000) {
  require(maxEvents in 1..100)
  if(!eventFlushing.compareAndSet(false,true))return
  try {
  val scope=NodeNativeSession.read(context)?.nodeId ?: return
  // A foreground dependency drain must spend its budget on HTTP only; pending
  // local posts and accepted-record recovery remain the worker's separate job.
  if(deadlineElapsed==Long.MAX_VALUE)finishLocalPosts(context)
  Store(context).use { store ->
   val db=store.writableDatabase;val events=mutableListOf<JSONObject>()
   db.rawQuery("SELECT eventId,occurrenceId,ruleId,ruleRevision,transition,occurredAt,postedAt,sequence FROM events ORDER BY sequence LIMIT 100",null).use {
    while(it.moveToNext()) events.add(JSONObject().put("eventId",it.getString(0)).put("occurrenceId",it.getString(1)).put("ruleId",it.getString(2)).put("ruleRevision",it.getLong(3)).put("transition",it.getString(4)).put("occurredAt",it.getString(5)).put("sequence",it.getLong(7)).apply{if(!it.isNull(6))put("postedAt",it.getString(6))})
   }
   var sent=0
   for(event in events) {
    if(occurrenceIds!=null && event.getString("occurrenceId") !in occurrenceIds)continue
    if(sent>=maxEvents || android.os.SystemClock.elapsedRealtime()>=deadlineElapsed)break
    sent++
    try {
     val response=NodeNativeSession.call(context,"POST","/api/nodes/geofences/events",event,expectedScope=scope,connectTimeoutMillis=connectTimeoutMillis,readTimeoutMillis=readTimeoutMillis)
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
  if(deadlineElapsed==Long.MAX_VALUE)recoverAccepted(context)
  }finally{eventFlushing.set(false)}
 }
 fun scheduleRestore(context:Context) {
  val work=OneTimeWorkRequestBuilder<NodeGeofenceRestoreWorker>().setBackoffCriteria(BackoffPolicy.EXPONENTIAL,30,TimeUnit.SECONDS).build()
  WorkManager.getInstance(context).enqueueUniqueWork("aegis-geofence-restore",ExistingWorkPolicy.KEEP,work)
 }
 fun restore(context:Context) { synchronized(manifestGate) {
  if(NodeNativeSession.read(context)==null){clear(context);return}
  val restoreBootCount=android.provider.Settings.Global.getInt(context.contentResolver,android.provider.Settings.Global.BOOT_COUNT,-1)
  check(prefs(context).edit().putLong("lastRestoreAt",System.currentTimeMillis()).putInt("lastRestoreBootCount",restoreBootCount).commit())
  NodeMonitoringSessions.cancelAll(context,"monitoring_interrupted")
  prefs(context).edit().putBoolean("needsInstall",true).commit()
  val current=manifest(context)
  if(current!=null && current.optString("observerNodeId")!=NodeNativeSession.read(context)?.nodeId){clear(context);return}
  if(current!=null && stamp(current.getString("leaseUntil"))>System.currentTimeMillis()) try{install(context,current)}catch(_:Exception){}
  check(prefs(context).edit().putLong("lastRestoreCompletedAt",System.currentTimeMillis()).putInt("lastRestoreCompletedBootCount",restoreBootCount).commit())
  NodeBackgroundWork.enqueue(context)
 }
 }
}
internal fun JSONArray.objects():List<JSONObject> =(0 until length()).map{getJSONObject(it)}
internal fun JSONArray.strings():List<String> =(0 until length()).map{getString(it)}
class NodeGeofenceReceiver:BroadcastReceiver() {
 override fun onReceive(context:Context,intent:Intent) {
  val receivedAt=System.currentTimeMillis()
  val pending=goAsync()
  NodeGeofences.event {
   try {
    val event=GeofencingEvent.fromIntent(intent) ?: return@event
    if(event.hasError()) {NodeGeofences.nativeError(context,event.errorCode);return@event}
    NodeGeofenceRuntime.nativeTransition(context,event.geofenceTransition,event.triggeringGeofences?.map{it.requestId}?.toTypedArray()?:emptyArray(),event.triggeringLocation,receivedAt)
   }catch(error:Exception){notificationLog("geofence_event",error)}finally{pending.finish()}
  }
 }
}
class NodeGeoConfirmWorker(context:Context,params:WorkerParameters):Worker(context,params) {
 override fun doWork():Result {return try {
  val deadline=inputData.getLong("deadline",0);val attempt=inputData.getInt("attempt",1)
  val remaining=deadline-System.currentTimeMillis()
  if(inputData.getBoolean("native",false)) {
   val receivedBefore=inputData.getLong("receivedAt",Long.MAX_VALUE)
   if(!NodeGeofences.nativeConfirmationPending(applicationContext))return Result.success()
   if(remaining<=0 || !NodeGeofences.hasConsumers(applicationContext)){NodeGeofences.confirmNative(applicationContext,receivedBefore=receivedBefore);return Result.success()}
   val observation=NodeLocation.current(applicationContext,upload=false,fresh=true,durationMillis=minOf(12000,remaining),highAccuracy=true)
   NodeGeofences.confirmNative(applicationContext,if(observation.optString("status")=="available")NodeLocation.engine(observation)else null,receivedBefore)
   return Result.success()
  }
  if(remaining<=0 || attempt !in 1..2 || !NodeGeofences.hasConsumers(applicationContext))Result.success() else {
   val observation=NodeLocation.current(applicationContext,upload=false,fresh=true,durationMillis=minOf(15000,remaining))
   if(observation.optString("status")=="available")NodeGeofenceRuntime.observe(applicationContext,observation.getDouble("latitude"),observation.getDouble("longitude"),observation.getDouble("accuracyMeters"),NodeLocation.engine(observation).capturedAt,false)
   if(NodeGeoEngine.canConfirmAgain(attempt,System.currentTimeMillis(),deadline,NodeGeofences.confirmationPending(applicationContext))) {
    val next=OneTimeWorkRequestBuilder<NodeGeoConfirmWorker>().setInitialDelay(30,TimeUnit.SECONDS).setInputData(workDataOf("attempt" to (attempt+1),"deadline" to deadline)).build()
    WorkManager.getInstance(applicationContext).enqueueUniqueWork("aegis-geofence-confirm",ExistingWorkPolicy.APPEND_OR_REPLACE,next)
   }
   Result.success()
  }
 }catch(_:Exception){if(inputData.getBoolean("native",false))NodeGeofences.confirmNative(applicationContext,receivedBefore=inputData.getLong("receivedAt",Long.MAX_VALUE));Result.success()}
 }
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
