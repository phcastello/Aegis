package com.aegis.node

import android.app.NotificationManager
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.os.SystemClock
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import java.util.UUID

/** Real SQLite, PendingIntent receiver, Activity resume, authenticated HTTP and fixture controllers. */
internal fun exerciseReminderSyncFramework(context:Context,nodeId:String,setBackground:((Boolean)->Unit)?,invoke:(String,JSONObject,Long)->JSONObject) {
 fun ipc(name:String,operation:String,payload:JSONObject?=null,id:String?=null)=invoke(name,JSONObject().put("operation",operation).apply{if(payload!=null)put("payload",payload);if(id!=null)put("id",id)},90000)
 fun mode(endpoint:String,value:String) {fixtureJson(endpoint,JSONObject().put("mode",value))}
 fun await(condition:()->Boolean) {
  val deadline=SystemClock.elapsedRealtime()+45000
  while(!condition() && SystemClock.elapsedRealtime()<deadline)SystemClock.sleep(50)
  assertTrue(condition())
 }
 fun database(block:(SQLiteDatabase)->Unit) {SQLiteDatabase.openDatabase(context.getDatabasePath("aegis-native-reminders-v1.db").path,null,SQLiteDatabase.OPEN_READWRITE).use(block)}
 fun count(id:String,table:String="actions"):Int {var count=0;database{db->db.rawQuery("SELECT COUNT(*) FROM $table WHERE reminderId=?",arrayOf(id)).use{it.moveToFirst();count=it.getInt(0)}};return count}
 fun lastResult(id:String):String? {var result:String?=null;database{db->db.rawQuery("SELECT lastResult FROM actions WHERE reminderId=?",arrayOf(id)).use{if(it.moveToFirst())result=it.getString(0)}};return result}
 fun eligibleOnReopen(id:String) {
  // Advance only retry metadata; no action/credential or request/response is injected.
  database{it.execSQL("UPDATE actions SET lastAttempt=?,nextAttempt=? WHERE reminderId=?",arrayOf<Any>(System.currentTimeMillis()-31000,System.currentTimeMillis()+3600000,id))}
  setBackground?.invoke(true);setBackground?.invoke(false)
 }
 fun receipts(id:String):List<JSONObject> {val rows=fixtureJson("reminder-actions").getJSONArray("items");return (0 until rows.length()).map{rows.getJSONObject(it)}.filter{it.getString("reminderId")==id}}
 val manager=context.getSystemService(NotificationManager::class.java)
 val ids=mutableListOf<String>()
 fun post():String {
  // Resume drains actions through native HTTP independently of the live socket.
  // Its success does not mean the replacement WS has received hello_ack yet.
  val connectedBy=SystemClock.elapsedRealtime()+30000
  var transport=invoke("node_transport_status",JSONObject(),20000).getString("transportState")
  while(transport!="online" && SystemClock.elapsedRealtime()<connectedBy) {
   SystemClock.sleep(100)
   transport=invoke("node_transport_status",JSONObject(),20000).getString("transportState")
  }
  assertEquals("The resumed live route must finish its authenticated handshake before the next reminder command","online",transport.takeIf{it in setOf("offline","connecting","reconnecting","online")}?:"unknown")
  assertTrue("The reminder command still targets the active native identity",nodeId==context.getSharedPreferences("aegis-node-push-v1",Context.MODE_PRIVATE).getString("nodeId",null))
  val id=UUID.randomUUID().toString();ids.add(id)
  assertEquals("success",fixtureJson("reminder-command",JSONObject().put("nodeId",nodeId).put("reminderId",id).put("commandId",UUID.randomUUID().toString())).getString("status"))
  awaitFrameworkReminder(manager,id,1);return id
 }
 fun tap(id:String,index:Int) {manager.activeNotifications.single{it.tag=="reminder:$id:1"}.notification.actions[index].actionIntent.send()}
 try {
  mode("reminder-action-failure","503")
  val temporary=post();tap(temporary,0)
  await{lastResult(temporary)=="temporary_http"}
  val requests=fixtureJson("idle").optLong("reminder_action_requests",0)
  val metadata=invoke("node_notification_settings",JSONObject(),20000)
  assertTrue(metadata.getInt("pendingActions")>0)
  assertEquals(requests,fixtureJson("idle").optLong("reminder_action_requests",0))
  mode("reminder-action-failure","none");eligibleOnReopen(temporary)
  await{count(temporary)==0 && receipts(temporary).singleOrNull()?.optBoolean("applied")==true}

  mode("reminder-action-failure","disconnect_after_apply")
  val lost=post();tap(lost,1)
  await{fixtureJson("idle").optLong("reminder_action_responses_dropped",0)>0 && count(lost)==1 && lastResult(lost) in setOf("network_unavailable","temporary_http")}
  assertEquals(1,receipts(lost).size)
  mode("reminder-action-failure","none");eligibleOnReopen(lost)
  await{count(lost)==0};assertEquals(1,receipts(lost).size)

  mode("reminder-action-failure","400")
  val rejected=post();tap(rejected,0)
  await{count(rejected)==0 && count(rejected,"action_history")==1}
  val rejectedState=invoke("node_notification_settings",JSONObject(),20000)
  assertTrue(rejectedState.getInt("failedActions")>0)
  val diagnostics=rejectedState.getJSONArray("actionDiagnostics")
  assertTrue((0 until diagnostics.length()).map{diagnostics.getJSONObject(it)}.any{it.optString("lastResult")=="rejected_invalid" && it.optString("state")=="failed"})
  for(index in 0 until diagnostics.length())assertTrue(diagnostics.getJSONObject(index).keys().asSequence().none{it in setOf("token","reminderId","actionId","text","credential")})
  mode("reminder-action-failure","none")

  // A queued action older than seven days must be attempted, never silently deleted.
  fixtureJson("backend-offline",JSONObject().put("seconds",120))
  val old=UUID.randomUUID().toString();ids.add(old)
  val queued=ipc("node_reminders","action",JSONObject().put("actionId",UUID.randomUUID().toString()).put("expectedRevision",1).put("generation",1).put("action","acknowledge"),old)
  assertTrue(queued.getBoolean("pending"));await{count(old)==1}
  database{it.execSQL("UPDATE actions SET created=? WHERE reminderId=?",arrayOf<Any>(System.currentTimeMillis()-8L*86400000,old))}
  fixtureJson("backend-offline",JSONObject().put("seconds",0))
  eligibleOnReopen(old)
  // This occurrence never existed: classify the server rejection and preserve traceability.
  await{count(old)==0 && count(old,"action_history")==1}
  android.util.Log.i("AegisNodeNativeTest","phase=outbox_foreground_temporary_lost_response_permanent_old_action errorType=Checkpoint")
 }finally {
  fixtureJson("backend-offline",JSONObject().put("seconds",0));mode("reminder-action-failure","none")
  for(id in ids)manager.cancel("reminder:$id:1",1)
 }

 // Cancellation uses backend mutation -> native replacement -> receipt before success.
 val place=ipc("node_places","create",JSONObject().put("name","CI local cancellation").put("type","property").put("aliases",JSONArray()).put("circles",JSONArray().put(JSONObject().put("id",UUID.randomUUID().toString()).put("latitude",0).put("longitude",0).put("radiusMeters",50))))
 fun rule():JSONObject=ipc("node_geofences","create",JSONObject().put("text","Cancellation fixture").put("placeIds",JSONArray().put(place.getString("id"))).put("observerNodeId",nodeId).put("transition","enter").put("repeat",false).put("timeZoneId","UTC"))
 fun backendRule(id:String):JSONObject {val rows=ipc("node_geofences","list").getJSONArray("items");return (0 until rows.length()).map{rows.getJSONObject(it)}.single{it.getString("id")==id}}
 try {
  val connected=rule()
  val cancelled=ipc("node_geofences","cancel",JSONObject().put("expectedRevision",connected.getLong("revision")),connected.getString("id"))
  assertEquals("confirmed",cancelled.getJSONObject("localRemoval").getString("state"))
  assertEquals("removed",backendRule(connected.getString("id")).getString("syncStatus"))
  val partial=rule()
  mode("geofence-cancellation-failure","manifest_503")
  val pending=ipc("node_geofences","cancel",JSONObject().put("expectedRevision",partial.getLong("revision")),partial.getString("id"))
  assertEquals("pending",pending.getJSONObject("localRemoval").getString("state"))
  assertEquals("pending_sync",backendRule(partial.getString("id")).getString("syncStatus"))
  mode("geofence-cancellation-failure","none")
  ipc("node_geofences","sync")
  assertEquals("removed",backendRule(partial.getString("id")).getString("syncStatus"))
  android.util.Log.i("AegisNodeNativeTest","phase=cancellation_local_receipt_and_manifest_failure_reconciliation errorType=Checkpoint")
 }finally {mode("geofence-cancellation-failure","none")}
 exerciseGeographicActionForegroundDependency(context,nodeId,setBackground,invoke)
}
