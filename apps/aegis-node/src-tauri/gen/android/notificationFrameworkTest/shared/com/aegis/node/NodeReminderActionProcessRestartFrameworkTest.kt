package com.aegis.node

import android.app.NotificationManager
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.os.SystemClock
import android.util.Base64
import android.webkit.WebView
import androidx.lifecycle.Lifecycle
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.KeyStore
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec

/** Real WS notifications and PendingIntents -> durable SQLite -> host force-stop ->
 * new OS process -> real authenticated HTTP worker, with a committed response lost.
 * All state/backups stay in the disposable target's private cache; logs have no IDs. */
@RunWith(AndroidJUnit4::class)
class NodeReminderActionProcessRestartFrameworkTest {
 private val instrumentation=InstrumentationRegistry.getInstrumentation()
 private val context=instrumentation.targetContext
 private val live=NodeNotificationLiveTest()
 private val helper=NodeGeofenceReceiverFrameworkTest()
 private val runtime=Class.forName("com.aegis.node.NodeGeofenceRuntime",true,context.classLoader)
 private val vaultAlias="keyring-aegis-node-identity-v1"
 private val vaultEntry="installation\uFEFF@\uFEFFcom.aegis.node.identity.v1"
 private val marker=File(context.cacheDir,"aegis-reminder-process-fixture.json")
 private val databaseNames=listOf("aegis-native-reminders-v1.db","aegis-geofence-events-v1.db")
 private val preferenceNames=listOf(vaultAlias,"aegis-node-push-v1","aegis-geofences-v1","aegis-hybrid-sessions-v1")
 private fun checkpoint(phase:String)=android.util.Log.i("AegisNodeNativeTest","phase=$phase errorType=Checkpoint")
 private fun await(reason:String,timeout:Long=45000,condition:()->Boolean) {
  val end=SystemClock.elapsedRealtime()+timeout
  var observed=condition()
  while(!observed && SystemClock.elapsedRealtime()<end){SystemClock.sleep(100);observed=condition()}
  assertTrue(reason,observed)
 }
 private fun defer(value:Boolean)=JSONObject(runtime.getMethod("deferReminderSyncFixture",Context::class.java,Boolean::class.javaPrimitiveType).invoke(null,context,value) as String)
 private fun database(block:(SQLiteDatabase)->Unit) = SQLiteDatabase.openDatabase(context.getDatabasePath(databaseNames[0]).path,null,SQLiteDatabase.OPEN_READWRITE).use(block)
 private fun actions():List<JSONObject> {
  val result=mutableListOf<JSONObject>()
  database{db->db.rawQuery("SELECT actionId,reminderId,revision,generation,action,token,attempts,lastResult FROM actions ORDER BY created",null).use{rows->while(rows.moveToNext())result.add(JSONObject().put("actionId",rows.getString(0)).put("reminderId",rows.getString(1)).put("expectedRevision",rows.getLong(2)).put("generation",rows.getLong(3)).put("action",rows.getString(4)).put("token",rows.getString(5)).put("attempts",rows.getInt(6)).put("lastResult",rows.getString(7)))}}
  return result
 }
 private fun receipts(id:String):List<JSONObject> {
  val rows=fixtureJson("reminder-actions").getJSONArray("items")
  return (0 until rows.length()).map{rows.getJSONObject(it)}.filter{it.getString("reminderId")==id}
 }
 private fun syncWorkStates():Map<String,Int> {
  // Read-only evidence of the real WorkManager job, not a forced Worker call.
  // This fixture pins WorkManager's schema; no production code depends on it.
  val file=File(context.noBackupFilesDir,"androidx.work.workdb").takeIf{it.isFile}?:context.getDatabasePath("androidx.work.workdb")
  check(file.isFile){"action_fixture_work_database_missing"}
  return SQLiteDatabase.openDatabase(file.path,null,SQLiteDatabase.OPEN_READONLY).use{db->db.rawQuery("SELECT w.id,w.state FROM WorkSpec w JOIN WorkName n ON n.work_spec_id=w.id WHERE n.name=?",arrayOf("aegis-native-sync")).use{rows->buildMap<String,Int>{while(rows.moveToNext())put(rows.getString(0),rows.getInt(1))}}}
 }
 private fun snapshotPreferences():JSONObject=JSONObject().apply {
  for(name in preferenceNames)put(name,JSONObject().apply{for((key,value)in context.getSharedPreferences(name,Context.MODE_PRIVATE).all)put(key,JSONObject().put("type",when(value){is Long->"long";is Int->"int";is Boolean->"boolean";is Float->"float";is Set<*>->"set";else->"string"}).put("value",if(value is Set<*>)JSONArray(value.toList())else value))})
 }
 private fun restorePreferences(saved:JSONObject) {
  for(name in preferenceNames) {
   val edit=context.getSharedPreferences(name,Context.MODE_PRIVATE).edit().clear();val rows=saved.getJSONObject(name)
   for(key in rows.keys()){val row=rows.getJSONObject(key);when(row.getString("type")) {
    "long"->edit.putLong(key,row.getLong("value"));"int"->edit.putInt(key,row.getInt("value"));"boolean"->edit.putBoolean(key,row.getBoolean("value"));"float"->edit.putFloat(key,row.getDouble("value").toFloat());"set"->edit.putStringSet(key,row.getJSONArray("value").let{a->(0 until a.length()).map{a.getString(it)}.toSet()});else->edit.putString(key,row.getString("value"))
   }}
   check(edit.commit())
  }
 }
 private fun backupDatabases():JSONArray=JSONArray().apply {
  for(name in databaseNames) {
   val source=context.getDatabasePath(name);put(JSONObject().put("name",name).put("present",source.exists()))
   if(source.exists()) {
    SQLiteDatabase.openDatabase(source.path,null,SQLiteDatabase.OPEN_READWRITE).use{db->db.rawQuery("PRAGMA wal_checkpoint(FULL)",null).use{row->assertTrue("Original native SQLite checkpoint is available",row.moveToFirst() && row.getInt(0)==0)}}
    source.copyTo(File(context.cacheDir,"$name.action-process-backup"),overwrite=true)
   }
  }
 }
 private fun restoreDatabases(saved:JSONArray) {
  for(index in 0 until saved.length()) {
   val row=saved.getJSONObject(index);val name=row.getString("name")
   check(name in databaseNames)
   val target=context.getDatabasePath(name)
   for(suffix in listOf("","-wal","-shm","-journal"))File(target.path+suffix).delete()
   val backup=File(context.cacheDir,"$name.action-process-backup")
   if(row.getBoolean("present")){check(backup.isFile);backup.copyTo(target,overwrite=true)}
   backup.delete()
  }
 }
 private fun fixtureCredential():String {
  val bytes=Base64.decode(context.getSharedPreferences(vaultAlias,Context.MODE_PRIVATE).getString(vaultEntry,null),Base64.DEFAULT)
  val key=KeyStore.getInstance("AndroidKeyStore").apply{load(null)}.getKey(vaultAlias,null)
  val cipher=Cipher.getInstance("AES/GCM/NoPadding").apply{init(Cipher.DECRYPT_MODE,key,GCMParameterSpec(128,bytes.copyOfRange(1,13)))}
  return JSONObject(String(cipher.doFinal(bytes.copyOfRange(13,bytes.size)))).getString("credential")
 }
 private fun replay(row:JSONObject):JSONObject {
  // Fixed test-side scoped action endpoint; no WebView HTTP bridge is added.
  val id=UUID.fromString(row.getString("reminderId")).toString()
  val body=JSONObject().put("actionId",row.getString("actionId")).put("token",row.getString("token")).put("expectedRevision",row.getLong("expectedRevision")).put("generation",row.getLong("generation")).put("action",row.getString("action")).put("snoozeMinutes",10)
  val connection=URL("http://127.0.0.1:18104/api/reminder-actions/$id").openConnection() as HttpURLConnection
  try {
   connection.requestMethod="POST";connection.connectTimeout=5000;connection.readTimeout=10000;connection.doOutput=true;connection.setRequestProperty("Content-Type","application/json")
   connection.outputStream.use{it.write(body.toString().toByteArray())}
   assertEquals("Real scoped action replay succeeds",200,connection.responseCode)
   return connection.inputStream.bufferedReader().use{reader->val text=reader.readText();check(text.length<1048576);JSONObject(text)}
  }finally{connection.disconnect()}
 }
 @Test fun prepareActionsBeforeProcessDeath() {
  assertTrue("The disposable target has notification permission",frameworkNotificationPermission(context))
  val code=InstrumentationRegistry.getArguments().getString("actionPairingCode")?:error("action_fixture_bootstrap_missing")
  val keyStore=KeyStore.getInstance("AndroidKeyStore").apply{load(null)}
  assertTrue(defer(true).getBoolean("workerDeferred"))
  val saved=JSONObject().put("originalPreferences",snapshotPreferences()).put("originalKeyPresent",keyStore.containsAlias(vaultAlias)).put("originalDatabases",backupDatabases())
  marker.writeText(saved.toString())
  // The host passes waitForActivitiesToComplete=false and kills this real process
  // after JUnit returns; destroying Tauri's last Activity would exit before OK.
  val scenario=ActivityScenario.launch(notificationActivityClass())
  var candidate:WebView?=null
  await("The actual target WebView starts",30000){scenario.onActivity{candidate=live.findWebView(it.window.decorView)};candidate!=null}
  val webview=candidate?:error("action_webview_missing")
  await("The real Tauri IPC initializes",30000){live.evaluate(webview,"Boolean(window.__TAURI_INTERNALS__ && window.__TAURI_INTERNALS__.invoke)")=="true"}
  val info=live.invoke(webview,"runtime_info")
  assertEquals("http://127.0.0.1:18104",info.getString("backendUrl"))
  val identity=live.invoke(webview,"node_pair",JSONObject().put("name","Action process restart fixture").put("code",code))
  assertEquals("paired",identity.getString("state"));val nodeId=identity.getJSONObject("node").getString("id")
  saved.put("nodeId",nodeId);marker.writeText(saved.toString())
  await("The real reminder WebSocket handshake completes",30000){live.invoke(webview,"node_transport_status").getString("transportState")=="online"}
  val manager=context.getSystemService(NotificationManager::class.java)
  fun postAndTap(actionIndex:Int):String {
   val id=UUID.randomUUID().toString()
   val posted=fixtureJson("reminder-command",JSONObject().put("nodeId",nodeId).put("reminderId",id).put("commandId",UUID.randomUUID().toString()))
   assertEquals("success",posted.getString("status"))
   val notification=awaitFrameworkReminder(manager,id,1)
   assertEquals(2,notification.notification.actions.size)
   notification.notification.actions[actionIndex].actionIntent.send()
   return id
  }
  // Snooze is committed first, but its response is really aborted by Kestrel.
  fixtureJson("reminder-action-failure",JSONObject().put("mode","disconnect_after_apply"))
  val lost=postAndTap(1)
  await("Lost-response Snooze remains durable despite one server commit") {
   actions().any{it.getString("reminderId")==lost && it.getString("lastResult") in setOf("network_unavailable","temporary_http")} && receipts(lost).singleOrNull()?.optBoolean("applied")==true && fixtureJson("idle").optLong("reminder_action_responses_dropped",0)>0
  }
  fixtureJson("reminder-action-failure",JSONObject().put("mode","503"))
  val acknowledged=postAndTap(0)
  await("Temporary HTTP failure retains the actual OK action") {actions().any{it.getString("reminderId")==acknowledged && it.getString("lastResult")=="temporary_http"}}
  assertTrue("OK was not remotely committed while unavailable",receipts(acknowledged).isEmpty())
  assertTrue(defer(true).getBoolean("workerDeferred"))
  await("No action HTTP owner is in flight before the host kills the process") {live.invoke(webview,"node_notification_settings").getString("actionSyncState")!="syncing"}
  scenario.moveToState(Lifecycle.State.CREATED)
  val pending=actions().filter{it.getString("reminderId") in setOf(lost,acknowledged)}
  assertEquals("Both actions are in the durable SQLite outbox",2,pending.size)
  assertEquals(setOf("acknowledge","snooze"),pending.map{it.getString("action")}.toSet())
  database{it.execSQL("UPDATE actions SET lastAttempt=?,nextAttempt=? WHERE reminderId IN (?,?)",arrayOf<Any>(System.currentTimeMillis()-31000,System.currentTimeMillis()-1,lost,acknowledged))}
  saved.put("actions",JSONArray(pending)).put("pid",android.os.Process.myPid()).put("activeVault",context.getSharedPreferences(vaultAlias,Context.MODE_PRIVATE).getString(vaultEntry,null))
  marker.writeText(saved.toString())
  checkpoint("outbox_restart_actions_durable_before_force_stop")
 }
 @Test fun verifyActionsAfterProcessDeath() {
  assertTrue("Host preserves the private durable action marker",marker.isFile)
  val saved=JSONObject(marker.readText())
  try {
   assertNotEquals("A genuinely new OS process performs reconciliation",saved.getInt("pid"),android.os.Process.myPid())
   assertTrue("Node binding survives actual process death",saved.getString("nodeId")==context.getSharedPreferences("aegis-node-push-v1",Context.MODE_PRIVATE).getString("nodeId",null))
   assertTrue("Encrypted credential is not replaced by restarting",saved.getString("activeVault")==context.getSharedPreferences(vaultAlias,Context.MODE_PRIVATE).getString(vaultEntry,null))
   val expected=saved.getJSONArray("actions").let{rows->(0 until rows.length()).map{rows.getJSONObject(it)}}
   val after=actions()
   for(row in expected)assertTrue("Action identity, type, token and attempt history survive process death",after.any{it.getString("actionId")==row.getString("actionId") && it.getString("reminderId")==row.getString("reminderId") && it.getString("action")==row.getString("action") && it.getString("token")==row.getString("token") && it.getInt("attempts")==row.getInt("attempts")})
   fixtureJson("reminder-action-failure",JSONObject().put("mode","none"))
   val previousWork=syncWorkStates().keys
   defer(false)
   await("The real native worker drains both restored actions through HTTP",90000){actions().none{current->expected.any{it.getString("actionId")==current.getString("actionId")}}}
   await("The newly enqueued real native worker finishes before restoring fixture storage",90000){syncWorkStates().any{(id,state)->id !in previousWork && state==2}}
   for(row in expected) {
    val receipt=receipts(row.getString("reminderId"))
    assertEquals("Each restored action has exactly one server receipt",1,receipt.size)
    assertTrue("The server receipt preserves the original action identity and applies it once",receipt.single().getString("actionId")==row.getString("actionId") && receipt.single().getBoolean("applied"))
    database{db->db.rawQuery("SELECT lastResult,outcome,attempts FROM action_history WHERE actionId=?",arrayOf(row.getString("actionId"))).use{history->assertTrue("Successful HTTP reconciliation retains native traceability",history.moveToFirst() && history.getString(0)=="accepted" && history.getString(1)=="resolved" && history.getInt(2)>row.getInt("attempts"))}}
    val replayed=replay(row)
    assertTrue("Real HTTP replay after process restart is idempotent",replayed.getBoolean("applied") && replayed.getBoolean("replayed") && !replayed.getBoolean("conflict"))
    assertEquals("Replay cannot create a second commit",1,receipts(row.getString("reminderId")).size)
   }
   checkpoint("outbox_ok_snooze_process_restart_http_deduplicated")
  }finally {
   fixtureJson("reminder-action-failure",JSONObject().put("mode","none"))
   assertTrue(defer(true).getBoolean("workerDeferred"))
   saved.optString("nodeId").takeIf{it.isNotEmpty()}?.let{runCatching{helper.request("/$it/revoke",fixtureCredential(),JSONObject())}}
   runCatching{runtime.getMethod("deactivate",Context::class.java).invoke(null,context)}
   val manager=context.getSystemService(NotificationManager::class.java)
   saved.optJSONArray("actions")?.let{rows->for(index in 0 until rows.length())manager.cancel("reminder:${rows.getJSONObject(index).getString("reminderId")}:1",1)}
   manager.cancel("aegis-action-pending",709)
   restoreDatabases(saved.getJSONArray("originalDatabases"));restorePreferences(saved.getJSONObject("originalPreferences"))
   val keyStore=KeyStore.getInstance("AndroidKeyStore").apply{load(null)}
   if(!saved.getBoolean("originalKeyPresent") && keyStore.containsAlias(vaultAlias))keyStore.deleteEntry(vaultAlias)
   assertEquals(saved.getBoolean("originalKeyPresent"),keyStore.containsAlias(vaultAlias))
   defer(false)
   marker.delete()
  }
 }
}
