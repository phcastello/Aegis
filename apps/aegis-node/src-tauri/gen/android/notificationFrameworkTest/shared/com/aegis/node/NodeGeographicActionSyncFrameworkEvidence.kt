package com.aegis.node

import android.app.NotificationManager
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.os.SystemClock
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import java.util.UUID

/** A real offline native occurrence + OK must drain on resume while WorkManager is deferred. */
internal fun exerciseGeographicActionForegroundDependency(context:Context,nodeId:String,setBackground:((Boolean)->Unit)?,invoke:(String,JSONObject,Long)->JSONObject) {
 fun ipc(name:String,operation:String,payload:JSONObject?=null,id:String?=null)=invoke(name,JSONObject().put("operation",operation).apply{if(payload!=null)put("payload",payload);if(id!=null)put("id",id)},90000)
 fun await(condition:()->Boolean) {
  val end=SystemClock.elapsedRealtime()+45000
  var observed=condition()
  while(!observed && SystemClock.elapsedRealtime()<end){SystemClock.sleep(50);observed=condition()}
  assertTrue("Expected geographic action condition before the bounded deadline",observed)
 }
 val runtime=Class.forName("com.aegis.node.NodeGeofenceRuntime",true,context.classLoader)
 fun defer(value:Boolean)=JSONObject(runtime.getMethod("deferReminderSyncFixture",Context::class.java,Boolean::class.javaPrimitiveType).invoke(null,context,value) as String)
 val manager=context.getSystemService(NotificationManager::class.java)
 val preferences=context.getSharedPreferences("aegis-geofences-v1",Context.MODE_PRIVATE)
 val place=ipc("node_places","create",JSONObject().put("name","Foreground dependency synthetic home").put("type","property").put("aliases",JSONArray()).put("circles",JSONArray().put(JSONObject().put("id",UUID.randomUUID().toString()).put("latitude",0).put("longitude",0).put("radiusMeters",50))))
 val placeId=place.getString("id")
 val rule=ipc("node_geofences","create",JSONObject().put("text","Foreground action dependency fixture").put("placeIds",JSONArray().put(placeId)).put("observerNodeId",nodeId).put("transition","enter").put("repeat",true).put("timeZoneId","UTC"));val ruleId=rule.getString("id")
 var occurrence:String?=null
 fun events():List<String> {
  val file=context.getDatabasePath("aegis-geofence-events-v1.db");if(!file.exists())return emptyList()
  return SQLiteDatabase.openDatabase(file.path,null,SQLiteDatabase.OPEN_READONLY).use{db->db.rawQuery("SELECT occurrenceId FROM events WHERE ruleId=?",arrayOf(ruleId)).use{rows->buildList<String>{while(rows.moveToNext())add(rows.getString(0))}}}
 }
 fun pendingAction():Boolean {
  val file=context.getDatabasePath("aegis-native-reminders-v1.db")
  return SQLiteDatabase.openDatabase(file.path,null,SQLiteDatabase.OPEN_READONLY).use{db->db.rawQuery("SELECT 1 FROM actions WHERE reminderId=?",arrayOf(occurrence?:"missing")).use{it.moveToFirst()}}
 }
 try {
  ipc("node_geofences","sync")
  fixtureJson("backend-offline",JSONObject().put("seconds",120))
  assertTrue(defer(true).getBoolean("workerDeferred"))
  val manifest=JSONObject(preferences.getString("manifest",null)?:error("dependency_manifest_missing"))
  val now=System.currentTimeMillis();val generation=UUID.randomUUID().toString();val region="$generation:inner"
  val configuration=JSONObject().put("id",generation).put("nodeId",nodeId).put("revision",manifest.getString("revision")).put("installedAt",now-120000).put("regions",JSONArray().put(JSONObject().put("requestId",region).put("placeId",placeId).put("circleId",place.getJSONArray("circles").getJSONObject(0).getString("id")).put("kind","real")))
  check(preferences.edit().putString("installedConfiguration",configuration.toString()).putString("installedRevision",manifest.getString("revision")).putBoolean("needsInstall",false).putBoolean("hasRegions",true).putLong("installedUntil",now+3600000).putLong("ruleRevision:$ruleId",rule.getLong("revision")).putBoolean("armed:$ruleId",true).putLong("notBefore:$placeId",now-120000).putLong("confirmedAt:$placeId",now-60000).putString("state:$placeId",JSONObject().put("confirmed","outside").put("candidate","unknown").put("candidateAt",0).put("lastCapturedAt",now-60000).toString()).remove("nativeSeen").remove("hybridFix:$placeId").commit())
  NodeGeofenceReceiverFrameworkTest().callback(1,listOf(region),longitude=0.0)
  await{events().size==1};occurrence=events().single()
  awaitFrameworkReminder(manager,occurrence!!,1).notification.actions[0].actionIntent.send()
  await{pendingAction()}
  await{invoke("node_notification_settings",JSONObject(),20000).optString("actionSyncState")!="syncing"}
  assertEquals(1,events().size)
  setBackground?.invoke(true)
  fixtureJson("backend-offline",JSONObject().put("seconds",0))
  val unavailableBefore=fixtureJson("idle").opt("geofence_event_requests")
  var unavailable=JSONObject()
  await{
   unavailable=JSONObject(runtime.getMethod("transientIdentityOutboxFixture",Context::class.java,String::class.java).invoke(null,context,"occurrences") as String)
   unavailable.getBoolean("consumed")
  }
  assertEquals("credential_unavailable",unavailable.getString("result"))
  assertEquals("A temporary identity outage preserves the occurrence",1,events().size)
  assertTrue("Its pending OK action is retained",pendingAction())
  assertEquals("No occurrence HTTP is attempted without identity",unavailableBefore,fixtureJson("idle").opt("geofence_event_requests"))
  val started=SystemClock.elapsedRealtime()
  setBackground?.invoke(false)
  await{events().isEmpty()&&!pendingAction()}
  assertTrue("Foreground dependency and action complete within the bounded drain",SystemClock.elapsedRealtime()-started<40000)
  val receipts=fixtureJson("reminder-actions").getJSONArray("items")
  assertEquals(1,(0 until receipts.length()).map{receipts.getJSONObject(it)}.count{it.getString("reminderId")==occurrence&&it.optBoolean("applied")})
  android.util.Log.i("AegisNodeNativeTest","phase=outbox_geo_dependency_foreground_worker_deferred errorType=Checkpoint")
 }finally {
  fixtureJson("backend-offline",JSONObject().put("seconds",0));defer(false)
  occurrence?.let{manager.cancel("reminder:$it:1",1)}
  runCatching{ipc("node_geofences","cancel",JSONObject().put("expectedRevision",rule.getLong("revision")),ruleId)}
 }
}
