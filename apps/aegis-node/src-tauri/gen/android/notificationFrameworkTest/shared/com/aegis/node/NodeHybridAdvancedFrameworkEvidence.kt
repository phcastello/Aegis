package com.aegis.node

import android.app.NotificationManager
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.location.LocationManager
import android.os.Build
import android.os.SystemClock
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import java.util.UUID

/** Actual native session fan-out, inner EXIT, partial HTTP drain and mid-session cancellation.
 * Coordinates/Google transitions are controlled fixtures; the FGS/FLP adapter,
 * GeofencingClient replacement, SQLite, notifications and authenticated HTTP are real. */
internal fun exerciseHybridAdvancedFramework(context:Context,nodeId:String,credential:String,helper:NodeGeofenceReceiverFrameworkTest) {
 val runtime=Class.forName("com.aegis.node.NodeGeofenceRuntime",true,context.classLoader)
 val prefs=context.getSharedPreferences("aegis-geofences-v1",Context.MODE_PRIVATE)
 val positions=context.getSharedPreferences("aegis-node-push-v1",Context.MODE_PRIVATE)
 val notifications=context.getSystemService(NotificationManager::class.java)
 fun session(op:String,place:String="",scenario:String="real")=JSONObject(runtime.getMethod("hybridSessionFixture",Context::class.java,String::class.java,String::class.java,String::class.java).invoke(null,context,op,place,scenario) as String)
 fun await(reason:String,timeout:Long=20000,condition:()->Boolean) {
  val end=SystemClock.elapsedRealtime()+timeout
  while(!condition() && SystemClock.elapsedRealtime()<end)SystemClock.sleep(25)
  assertTrue(reason,condition())
 }
 fun stopped(){await("Location service and its notification are removed"){!session("snapshot").getBoolean("running") && notifications.activeNotifications.none{it.id==7351}}}
 fun eventRows(ids:Set<String>):List<JSONObject> {
  return SQLiteDatabase.openDatabase(context.getDatabasePath("aegis-geofence-events-v1.db").path,null,SQLiteDatabase.OPEN_READONLY).use{db->db.rawQuery("SELECT eventId,occurrenceId,ruleId,ruleRevision,transition,occurredAt,sequence,postedAt FROM events ORDER BY sequence",null).use{rows->
   val result=mutableListOf<JSONObject>()
   while(rows.moveToNext())if(rows.getString(2) in ids)result.add(JSONObject().put("eventId",rows.getString(0)).put("occurrenceId",rows.getString(1)).put("ruleId",rows.getString(2)).put("ruleRevision",rows.getLong(3)).put("transition",rows.getString(4)).put("occurredAt",rows.getString(5)).put("sequence",rows.getLong(6)).apply{if(!rows.isNull(7))put("postedAt",rows.getString(7))})
   result
  }}
 }
 fun place(name:String)=helper.request("/places",credential,JSONObject().put("name",name).put("type","property").put("aliases",JSONArray()).put("circles",JSONArray().put(JSONObject().put("id",UUID.randomUUID().toString()).put("latitude",0).put("longitude",0).put("radiusMeters",50))).put("monitoringEnvelope",JSONObject().put("enabled",true).put("paddingMeters",150).put("confirmed",true).put("expectedRevision",0)))
 fun rule(place:JSONObject,transition:String)=helper.request("/geofences",credential,JSONObject().put("text","Advanced hybrid framework fixture").put("placeIds",JSONArray().put(place.getString("id"))).put("observerNodeId",nodeId).put("transition",transition).put("repeat",true).put("timeZoneId","UTC"))
 val home=place("Hybrid 50m shared home")
 val cancellationHome=place("Hybrid cancellation home")
 val arrivals=listOf(rule(home,"enter"),rule(home,"enter"))
 val leaving=rule(home,"exit")
 val cancelling=rule(cancellationHome,"enter")
 val rules=arrivals+leaving+cancelling
 val ids=rules.map{it.getString("id")}.toSet()
 val occurrenceIds=mutableSetOf<String>()
 try {
  session("reset");stopped()
  if(Build.VERSION.SDK_INT<28) {
   // GMS explicitly does not support geofencing in pre-P "device only" mode.
   // The emulator's GPS can work while its network provider is disabled.
   // The host configures high accuracy across the complete disposable fixture,
   // including its later process-death and reboot stages.
   val location=context.getSystemService(LocationManager::class.java)
   await("The API 26 fixture enables GPS and network location for registration and acquisition") {
    location.isProviderEnabled(LocationManager.GPS_PROVIDER) && location.isProviderEnabled(LocationManager.NETWORK_PROVIDER)
   }
  }
  // This is the ordinary authenticated manifest/GMS installation, not seeded registration.
  session("synchronize")
  val manifest=JSONObject(prefs.getString("manifest",null)?:error("hybrid_shared_manifest_missing"))
  val manifestRules=manifest.getJSONArray("rules").let{rows->(0 until rows.length()).map{rows.getJSONObject(it).getString("id")}.toSet()}
  assertTrue("Authenticated manifest contains every new active rule",ids.all{it in manifestRules})
  fun closed(key:String)=prefs.getString(key,"none")?.takeIf{it.matches(Regex("[a-z_]{1,64}"))}?:"unknown"
  val registration="status=${closed("status")} error=${closed("error")}"
  assertFalse("Actual Google registration must complete; $registration",prefs.getBoolean("needsInstall",true))
  assertTrue("Actual Google registration must contain active regions; $registration",prefs.getBoolean("hasRegions",false))
  assertTrue("The installed generation must match the current authenticated manifest; $registration",prefs.getString("installedRevision",null)==manifest.getString("revision"))
  val configuration=JSONObject(prefs.getString("installedConfiguration",null)?:error("hybrid_shared_registration_missing"))
  assertTrue("A stale allowlist cannot prove shared-region installation; $registration",configuration.getString("revision")==manifest.getString("revision"))
  fun regions(placeId:String)=configuration.getJSONArray("regions").let{rows->(0 until rows.length()).map{rows.getJSONObject(it)}.filter{it.getString("placeId")==placeId}}
  val homeId=home.getString("id");val cancelPlaceId=cancellationHome.getString("id")
  assertEquals("Three rules share two physical regions, rather than six",2,regions(homeId).size)
  assertEquals(setOf("real","operational"),regions(homeId).map{it.getString("kind")}.toSet())
  assertEquals(50.0,home.getJSONArray("circles").getJSONObject(0).getDouble("radiusMeters"),0.0)
  assertEquals(200.0,home.getJSONObject("monitoringEnvelope").getJSONArray("circles").getJSONObject(0).getDouble("radiusMeters"),0.0)
  val inner=regions(homeId).single{it.getString("kind")=="real"}.getString("requestId")
  val operational=regions(homeId).single{it.getString("kind")=="operational"}.getString("requestId")
  val cancelOperational=regions(cancelPlaceId).single{it.getString("kind")=="operational"}.getString("requestId")
  fun seedOutside(placeId:String,consumers:List<JSONObject>) {
   val at=System.currentTimeMillis()-60000
   val edit=prefs.edit().putString("state:$placeId",JSONObject().put("confirmed","outside").put("candidate","unknown").put("candidateAt",0).put("lastCapturedAt",at).toString()).putLong("confirmedAt:$placeId",at).putLong("notBefore:$placeId",at-1000).remove("hybridFix:$placeId").remove("diagnostic:$placeId").remove("reliable:$placeId").remove("nativeSeen").putBoolean("operationalInside:$placeId",false)
   for(region in regions(placeId))edit.putBoolean("operationalRegion:${region.getString("requestId")}",false)
   for(consumer in consumers)edit.putLong("ruleRevision:${consumer.getString("id")}",consumer.getLong("revision")).putBoolean("armed:${consumer.getString("id")}",consumer.getString("transition")=="enter").remove("completed:${consumer.getString("id")}")
   check(edit.commit());check(positions.edit().remove("lastObservation").remove("observationScopeId").putBoolean("observationPending",false).commit())
  }
  seedOutside(homeId,arrivals+leaving)
  fixtureJson("backend-offline",JSONObject().put("seconds",300))
  session("configure",homeId,"outside")
  helper.callback(1,listOf(operational),longitude=null)
  await("One native session begins for both arrivals"){session("snapshot").getBoolean("running") && session("snapshot").getJSONArray("sessions").getJSONObject(0).optInt("acquired")>=1}
  // Duplicate operational delivery while active must join/suppress the same session.
  helper.callback(1,listOf(operational),longitude=null)
  assertEquals(1,session("snapshot").getInt("serviceStarts"));assertTrue(eventRows(ids).isEmpty())
  session("configure",homeId,"inside")
  await("Two arrival consumers each create one occurrence from the same session",75000){eventRows(ids).size==2}
  stopped()
  val arrived=eventRows(ids)
  assertTrue("Each arrival consumer creates its own occurrence",arrivals.map{it.getString("id")}.toSet()==arrived.map{it.getString("ruleId")}.toSet())
  assertEquals(2,arrived.map{it.getString("occurrenceId")}.toSet().size)
  assertEquals(1,session("snapshot").getInt("serviceStarts"))
  for(row in arrived){occurrenceIds.add(row.getString("occurrenceId"));awaitFrameworkReminder(notifications,row.getString("occurrenceId"),1)}
  helper.callback(1,listOf(inner),longitude=0.0);helper.callback(1,listOf(operational),longitude=null)
  assertEquals(2,eventRows(ids).size)
  assertTrue(prefs.getBoolean("armed:${leaving.getString("id")}",false))
  // 111 m lies outside the real 50 m home and inside the operational 200 m circle.
  // Native inner EXIT must notify without waiting for an outer EXIT.
  SystemClock.sleep(2000) // A plausible interval after the preceding center capture.
  helper.callback(2,listOf(inner),longitude=0.001)
  await("Native inner EXIT posts while the envelope still contains the person"){eventRows(ids).size==3}
  assertTrue(prefs.getBoolean("operationalInside:$homeId",false))
  val exit=eventRows(ids).single{it.getString("ruleId")==leaving.getString("id")}
  assertEquals("exit",exit.getString("transition"));occurrenceIds.add(exit.getString("occurrenceId"));awaitFrameworkReminder(notifications,exit.getString("occurrenceId"),1)
  helper.callback(2,listOf(inner),longitude=0.001)
  assertEquals(3,eventRows(ids).size)
  android.util.Log.i("AegisNodeNativeTest","phase=hybrid_shared_session_two_arrivals_inner_exit_50_200 errorType=Checkpoint")

  // The real fixture commits one event and rejects the rest with temporary HTTP503.
  val before=eventRows(ids)
  fixtureJson("geofence-event-failure",JSONObject().put("mode","after_one"))
  fixtureJson("backend-offline",JSONObject().put("seconds",0))
  session("drain")
  await("Partial HTTP success removes exactly the accepted occurrence"){eventRows(ids).size==2}
  val pending=eventRows(ids)
  assertTrue(pending.all{row->before.any{it.toString()==row.toString()}})
  assertTrue(fixtureJson("idle").optLong("geofence_event_temporary_failures",0)>0)
  val committed=before.single{row->pending.none{it.getString("eventId")==row.getString("eventId")}}
  fixtureJson("geofence-event-failure",JSONObject().put("mode","none"))
  assertTrue("First response represented a committed backend event",helper.request("/geofences/events",credential,committed).getBoolean("duplicate"))
  session("drain");await("Only the remaining durable events drain after recovery"){eventRows(ids).isEmpty()}
  for(row in before)assertTrue(helper.request("/geofences/events",credential,row).getBoolean("duplicate"))
  android.util.Log.i("AegisNodeNativeTest","phase=hybrid_occurrence_outbox_partial_commit_recovery errorType=Checkpoint")

  seedOutside(cancelPlaceId,listOf(cancelling))
  session("configure",cancelPlaceId,"blocking")
  helper.callback(1,listOf(cancelOperational),longitude=null)
  await("A cancellable SDK acquisition is pending before rule removal"){session("snapshot").getBoolean("running") && session("snapshot").getInt("fixtureAcquisitionInFlight")==1}
  val starts=session("snapshot").getInt("serviceStarts")
  val processBeforeCancellation=android.os.Process.myPid()
  helper.request("/geofences/${cancelling.getString("id")}/actions",credential,JSONObject().put("action","cancel").put("expectedRevision",cancelling.getLong("revision")))
  session("synchronize");stopped()
  await("Cancelling the real service releases its SDK task and acquisition thread") {
   val value=session("snapshot")
   value.getInt("fixtureAcquisitionInFlight")==0 && value.getInt("fixtureAcquisitionsReleased")==value.getInt("fixtureAcquisitionsStarted") && value.getInt("fixtureCancellationObserved")>=1
  }
  assertTrue("Cancellation does not crash or restart the Android process",processBeforeCancellation==android.os.Process.myPid())
  android.util.Log.i("AegisNodeNativeTest","phase=hybrid_inflight_acquisition_cancelled_process_alive errorType=Checkpoint")
  val after=JSONObject(prefs.getString("installedConfiguration",null)!!).getJSONArray("regions")
  assertTrue((0 until after.length()).none{after.getJSONObject(it).getString("placeId")==cancelPlaceId})
  val backend=helper.request("/geofences",credential).getJSONArray("items").let{rows->(0 until rows.length()).map{rows.getJSONObject(it)}.single{it.getString("id")==cancelling.getString("id")}}
  assertEquals("cancelled",backend.getString("status"));assertEquals("removed",backend.getString("syncStatus"))
  assertTrue(eventRows(setOf(cancelling.getString("id"))).isEmpty())
  val cancelled=session("snapshot").getJSONArray("sessions").let{rows->(0 until rows.length()).map{rows.getJSONObject(it)}.single{it.optString("status")!="monitoring_transition_accepted"}}
  val attempts=cancelled.getInt("attempts")
  helper.callback(1,listOf(cancelOperational),longitude=null);SystemClock.sleep(1200)
  assertFalse(session("snapshot").getBoolean("running"));assertEquals(starts,session("snapshot").getInt("serviceStarts"))
  val terminal=session("snapshot").getJSONArray("sessions").let{rows->(0 until rows.length()).map{rows.getJSONObject(it)}.single{it.optString("status")!="monitoring_transition_accepted"}}
  assertEquals(attempts,terminal.getInt("attempts"))
  android.util.Log.i("AegisNodeNativeTest","phase=hybrid_cancellation_during_active_session_removed_receipt errorType=Checkpoint")
 }finally {
  fixtureJson("geofence-event-failure",JSONObject().put("mode","none"));fixtureJson("backend-offline",JSONObject().put("seconds",0))
  runCatching{session("stop")}
  for(rule in rules)runCatching{val rows=helper.request("/geofences",credential).getJSONArray("items");val current=(0 until rows.length()).map{rows.getJSONObject(it)}.single{it.getString("id")==rule.getString("id")};if(current.optString("status")!="cancelled")helper.request("/geofences/${current.getString("id")}/actions",credential,JSONObject().put("action","cancel").put("expectedRevision",current.getLong("revision")))}
  runCatching{session("synchronize")}
  for(id in occurrenceIds)notifications.cancel("reminder:$id:1",1)
 }
}
