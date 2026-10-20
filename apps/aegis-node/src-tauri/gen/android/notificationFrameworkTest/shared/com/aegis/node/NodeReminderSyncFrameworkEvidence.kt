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
internal fun exerciseReminderSyncFramework(context:Context,nodeId:String,peerId:String,peerCredential:String,setBackground:((Boolean)->Unit)?,invoke:(String,JSONObject,Long)->JSONObject) {
 fun ipc(name:String,operation:String,payload:JSONObject?=null,id:String?=null)=invoke(name,JSONObject().put("operation",operation).apply{if(payload!=null)put("payload",payload);if(id!=null)put("id",id)},90000)
 fun mode(endpoint:String,value:String) {fixtureJson(endpoint,JSONObject().put("mode",value))}
 fun await(condition:()->Boolean) {
  val deadline=SystemClock.elapsedRealtime()+45000
  var observed=condition()
  while(!observed && SystemClock.elapsedRealtime()<deadline) {SystemClock.sleep(50);observed=condition()}
  assertTrue("Expected native reminder condition before the bounded deadline",observed)
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

  // A one-read vault outage during token-only scope validation is temporary.
  // Native scope checks recover immediately, reproducing the old false-409 loss.
  mode("reminder-action-failure","503")
  val unavailable=post();tap(unavailable,0)
  await{lastResult(unavailable)=="temporary_http"}
  mode("reminder-action-failure","none")
  database{it.execSQL("UPDATE actions SET lastAttempt=?,nextAttempt=? WHERE reminderId=?",arrayOf<Any>(System.currentTimeMillis()-31000,System.currentTimeMillis()+3600000,unavailable))}
  val beforeUnavailable=fixtureJson("idle").getLong("reminder_action_requests")
  val runtime=Class.forName("com.aegis.node.NodeGeofenceRuntime",true,context.classLoader)
  await{JSONObject(runtime.getMethod("transientIdentityOutboxFixture",Context::class.java,String::class.java).invoke(null,context,"actions") as String).getBoolean("consumed")}
  assertEquals("A transient identity read preserves the queued action",1,count(unavailable))
  assertEquals("A transient identity read is not a permanent conflict",0,count(unavailable,"action_history"))
  assertEquals("credential_unavailable",lastResult(unavailable))
  assertEquals("No HTTP is attempted without the current identity",beforeUnavailable,fixtureJson("idle").getLong("reminder_action_requests"))
  eligibleOnReopen(unavailable)
  await{count(unavailable)==0 && receipts(unavailable).singleOrNull()?.optBoolean("applied")==true}
  android.util.Log.i("AegisNodeNativeTest","phase=outbox_transient_identity_no_http_retained_recovered errorType=Checkpoint")

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
 val cancellationRules=mutableListOf<JSONObject>()
 val native=context.getSharedPreferences("aegis-geofences-v1",Context.MODE_PRIVATE)
 fun rule():JSONObject=ipc("node_geofences","create",JSONObject().put("text","Cancellation fixture").put("placeIds",JSONArray().put(place.getString("id"))).put("observerNodeId",nodeId).put("transition","enter").put("repeat",false).put("timeZoneId","UTC")).also{cancellationRules.add(it)}
 fun backendRule(id:String):JSONObject {val rows=ipc("node_geofences","list").getJSONArray("items");return (0 until rows.length()).map{rows.getJSONObject(it)}.single{it.getString("id")==id}}
 fun installed(rule:JSONObject):Pair<String,String> {
  ipc("node_geofences","sync")
  await{!native.getBoolean("needsInstall",true) && native.getBoolean("hasRegions",false)}
  val manifestRaw=native.getString("manifest",null)?:error("cancellation_manifest_missing")
  val configurationRaw=native.getString("installedConfiguration",null)?:error("cancellation_configuration_missing")
  val manifest=JSONObject(manifestRaw);val configuration=JSONObject(configurationRaw)
  assertTrue("Installed generation belongs to the current authenticated manifest",configuration.getString("revision")==manifest.getString("revision") && native.getString("installedRevision",null)==manifest.getString("revision"))
  val installedRules=manifest.getJSONArray("rules")
  assertTrue("The real installed manifest includes this active rule revision",(0 until installedRules.length()).map{installedRules.getJSONObject(it)}.any{it.getString("id")==rule.getString("id") && it.getLong("revision")==rule.getLong("revision") && it.getString("status")=="active"})
  val regions=configuration.getJSONArray("regions")
  assertTrue("Actual Google registration includes this real circle",(0 until regions.length()).map{regions.getJSONObject(it)}.any{it.getString("placeId")==place.getString("id") && it.getString("circleId")==place.getJSONArray("circles").getJSONObject(0).getString("id") && it.optString("kind","real")=="real"})
  assertFalse("Installation receipt is already settled before cancellation",native.getBoolean("reportPending",true))
  assertEquals("active",backendRule(rule.getString("id")).getString("status"))
  return configurationRaw to manifestRaw
 }
 fun removed(rule:JSONObject,requestsBefore:Long) {
  val backend=backendRule(rule.getString("id"))
  assertEquals("cancelled",backend.getString("status"));assertEquals("removed",backend.getString("syncStatus"))
  assertFalse("The fresh removal receipt reaches the backend",backend.isNull("syncedAt"))
  assertTrue("Cancellation sends a fresh authenticated replacement receipt",fixtureJson("idle").optLong("geofence_sync_requests",0)>requestsBefore)
  assertFalse("The cancelled region is removed locally",native.getBoolean("hasRegions",true))
  val rows=JSONObject(native.getString("manifest",null)!!).getJSONArray("rules")
  assertTrue("The installed manifest omits the cancelled rule",(0 until rows.length()).none{rows.getJSONObject(it).getString("id")==rule.getString("id")})
  assertFalse("Local receipt is confirmed rather than pending",native.getBoolean("reportPending",true))
 }
 try {
  // Preserve the immediate create/cancel empty-manifest regression. No explicit
  // installation is inserted between these original two operations.
  val connected=rule()
  val cancelled=ipc("node_geofences","cancel",JSONObject().put("expectedRevision",connected.getLong("revision")),connected.getString("id"))
  assertEquals("confirmed",cancelled.getJSONObject("localRemoval").getString("state"))
  assertEquals("removed",backendRule(connected.getString("id")).getString("syncStatus"))
  val connectedInstalled=rule();installed(connectedInstalled)
  val localBefore=fixtureJson("idle").optLong("geofence_sync_requests",0)
  val localRemoved=ipc("node_geofences","cancel",JSONObject().put("expectedRevision",connectedInstalled.getLong("revision")),connectedInstalled.getString("id"))
  assertEquals("confirmed",localRemoved.getJSONObject("localRemoval").getString("state"))
  removed(connectedInstalled,localBefore)
  android.util.Log.i("AegisNodeNativeTest","phase=cancellation_installed_local_receipt errorType=Checkpoint")

  val partial=rule()
  val beforePartial=installed(partial)
  val failedManifests=fixtureJson("idle").optLong("geofence_cancellation_manifest_failures",0)
  mode("geofence-cancellation-failure","manifest_503")
  val pending=ipc("node_geofences","cancel",JSONObject().put("expectedRevision",partial.getLong("revision")),partial.getString("id"))
  assertEquals("pending",pending.getJSONObject("localRemoval").getString("state"))
  assertEquals("pending_sync",backendRule(partial.getString("id")).getString("syncStatus"))
  assertTrue("Manifest HTTP failure was actually exercised",fixtureJson("idle").optLong("geofence_cancellation_manifest_failures",0)>failedManifests)
  assertTrue("A failed manifest fetch preserves the installed configuration",beforePartial.first==native.getString("installedConfiguration",null))
  assertTrue("A failed manifest fetch cannot silently discard the old rule",beforePartial.second==native.getString("manifest",null))
  val partialBefore=fixtureJson("idle").optLong("geofence_sync_requests",0)
  mode("geofence-cancellation-failure","none")
  ipc("node_geofences","sync")
  removed(partial,partialBefore)
  android.util.Log.i("AegisNodeNativeTest","phase=cancellation_installed_manifest_failure_reconciled errorType=Checkpoint")

  val peer=NodeGeofenceReceiverFrameworkTest()
  val authenticatedPeer=peer.request("/me",peerCredential)
  assertTrue("Peer cancellation uses a distinct authenticated Node",peerId!=nodeId && authenticatedPeer.getString("id")==peerId)
  val remote=rule();val beforeRemote=installed(remote)
  // Only the observer's manifest pull is unavailable. The separate peer stays
  // authenticated and connected; this is not labelled a physical radio outage.
  mode("geofence-cancellation-failure","manifest_503")
  setBackground?.invoke(true)
  try {
   val remoteResult=peer.request("/geofences/${remote.getString("id")}/actions",peerCredential,JSONObject().put("action","cancel").put("expectedRevision",remote.getLong("revision")))
   assertEquals("cancelled",remoteResult.getString("status"))
   val remoteRows=peer.request("/geofences",peerCredential).getJSONArray("items")
   assertEquals("pending_sync",(0 until remoteRows.length()).map{remoteRows.getJSONObject(it)}.single{it.getString("id")==remote.getString("id")}.getString("syncStatus"))
   assertTrue("Another Node cannot fabricate local removal",beforeRemote.first==native.getString("installedConfiguration",null))
  }finally{mode("geofence-cancellation-failure","none");setBackground?.invoke(false)}
  val remoteBefore=fixtureJson("idle").optLong("geofence_sync_requests",0)
  ipc("node_geofences","sync")
  removed(remote,remoteBefore)
  android.util.Log.i("AegisNodeNativeTest","phase=cancellation_peer_authenticated_pull_receipt errorType=Checkpoint")

  val disconnected=rule();val beforeDisconnect=installed(disconnected)
  val beforeFailure=fixtureJson("idle")
  mode("geofence-cancellation-failure","cancel_before_commit_disconnect")
  val attempted=try{runCatching{ipc("node_geofences","cancel",JSONObject().put("expectedRevision",disconnected.getLong("revision")),disconnected.getString("id"))}}
   finally{mode("geofence-cancellation-failure","none")}
  assertTrue("A request lost before commit cannot report remote cancellation",attempted.isFailure)
  val afterFailure=fixtureJson("idle")
  assertTrue("Transport was disconnected before the handler",afterFailure.optLong("geofence_cancellation_precommit_disconnects",0)>beforeFailure.optLong("geofence_cancellation_precommit_disconnects",0))
  assertEquals("No cancellation was committed while transport was unavailable",beforeFailure.optLong("geofence_cancellation_commits",0),afterFailure.optLong("geofence_cancellation_commits",0))
  val stillActive=backendRule(disconnected.getString("id"))
  assertEquals("active",stillActive.getString("status"));assertEquals(disconnected.getLong("revision"),stillActive.getLong("revision"))
  assertTrue("Unavailable cancellation preserves the local registered region",beforeDisconnect.first==native.getString("installedConfiguration",null))
  assertTrue("Unavailable cancellation preserves the durable local rule",beforeDisconnect.second==native.getString("manifest",null))
  ipc("node_geofences","sync");installed(disconnected)
  val retryBefore=fixtureJson("idle").optLong("geofence_sync_requests",0)
  val retried=ipc("node_geofences","cancel",JSONObject().put("expectedRevision",stillActive.getLong("revision")),disconnected.getString("id"))
  assertEquals("confirmed",retried.getJSONObject("localRemoval").getString("state"));removed(disconnected,retryBefore)
  android.util.Log.i("AegisNodeNativeTest","phase=cancellation_precommit_disconnect_preserves_rule errorType=Checkpoint")
  android.util.Log.i("AegisNodeNativeTest","phase=cancellation_local_receipt_and_manifest_failure_reconciliation errorType=Checkpoint")
 }finally {
  mode("geofence-cancellation-failure","none")
  for(rule in cancellationRules)runCatching{val current=backendRule(rule.getString("id"));if(current.getString("status")=="active")ipc("node_geofences","cancel",JSONObject().put("expectedRevision",current.getLong("revision")),current.getString("id"))}
 }
 exerciseGeographicActionForegroundDependency(context,nodeId,setBackground,invoke)
}
