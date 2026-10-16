package com.aegis.node

import android.Manifest
import android.app.NotificationManager
import android.content.Context
import android.content.Intent
import android.database.sqlite.SQLiteDatabase
import android.os.Build
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import java.net.HttpURLConnection
import java.net.URL
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone
import java.util.UUID

private fun geoRuntime(context:Context)=Class.forName("com.aegis.node.NodeGeofenceRuntime",true,context.classLoader)
internal fun exerciseLocationPermissionFramework(context:Context) {
 val state=JSONObject(geoRuntime(context).getMethod("permissions",Context::class.java).invoke(null,context) as String)
 assertTrue(state.getBoolean("supported"))
 assertEquals(context.checkSelfPermission(Manifest.permission.ACCESS_COARSE_LOCATION)==android.content.pm.PackageManager.PERMISSION_GRANTED || context.checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION)==android.content.pm.PackageManager.PERMISSION_GRANTED,state.getBoolean("foreground"))
 assertEquals(context.checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION)==android.content.pm.PackageManager.PERMISSION_GRANTED,state.getBoolean("precise"))
 assertEquals(Build.VERSION.SDK_INT<29 || context.checkSelfPermission(Manifest.permission.ACCESS_BACKGROUND_LOCATION)==android.content.pm.PackageManager.PERMISSION_GRANTED,state.getBoolean("background"))
}
private fun geoStamp(time:Long)=SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'",Locale.ROOT).apply{timeZone=TimeZone.getTimeZone("UTC")}.format(Date(time))
private fun fixtureOffline(seconds:Int,allowPairing:Boolean=false) {
 val connection=URL("http://127.0.0.1:18104/fixture/backend-offline").openConnection() as HttpURLConnection
 try {connection.requestMethod="POST";connection.connectTimeout=5000;connection.readTimeout=5000;connection.doOutput=true;connection.setRequestProperty("Content-Type","application/json");connection.outputStream.use{it.write(JSONObject().put("seconds",seconds).put("allowPairing",allowPairing).toString().toByteArray())};check(connection.responseCode==200)}finally{connection.disconnect()}
}
private fun fixtureMode(path:String,mode:String) {
 val connection=URL("http://127.0.0.1:18104/fixture/$path").openConnection() as HttpURLConnection
 try {connection.requestMethod="POST";connection.connectTimeout=5000;connection.readTimeout=5000;connection.doOutput=true;connection.setRequestProperty("Content-Type","application/json");connection.outputStream.use{it.write(JSONObject().put("mode",mode).toString().toByteArray())};check(connection.responseCode==200)}finally{connection.disconnect()}
}
/** Injects a real SDK RemoteMessage into the native callback; Google's provider is simulated. */
internal fun nativeFcm(context:Context,nodeId:String,capability:String,input:JSONObject,commandId:String=UUID.randomUUID().toString(),expiresAt:String=geoStamp(System.currentTimeMillis()+240000)) {
 val type=Class.forName("com.aegis.node.NodeMessagingService",true,context.classLoader)
 val service=type.getConstructor().newInstance()
 val attach=android.content.ContextWrapper::class.java.getDeclaredMethod("attachBaseContext",Context::class.java).apply{isAccessible=true}
 attach.invoke(service,context)
 val callback=type.methods.single{it.name=="onMessageReceived"&&it.parameterCount==1}
 val bundle=android.os.Bundle().apply {
  putString("google.message_id",UUID.randomUUID().toString());putString("nodeId",nodeId);putString("type",if(capability=="geofence.monitor")"domain.sync" else capability)
  putString("version","1");putString("commandId",commandId);putString("expiresAt",expiresAt);putString("input",input.toString())
  if(capability=="geofence.monitor")putString("capability",capability)
 }
 try{callback.invoke(service,callback.parameterTypes.single().getConstructor(android.os.Bundle::class.java).newInstance(bundle))}
 finally{(service as android.app.Service).onDestroy()}
}
internal fun fixtureJson(path:String,body:JSONObject?=null):JSONObject {
 val connection=URL("http://127.0.0.1:18104/fixture/$path").openConnection() as HttpURLConnection
 try {
  connection.connectTimeout=5000;connection.readTimeout=10000;connection.instanceFollowRedirects=false
  if(body!=null){connection.requestMethod="POST";connection.doOutput=true;connection.setRequestProperty("Content-Type","application/json");connection.outputStream.use{it.write(body.toString().toByteArray())}}
  check(connection.responseCode==200){"fixture_http_${connection.responseCode}"}
  val result=connection.inputStream.bufferedReader().use{it.readText()};check(result.length<=262144)
  return if(result.startsWith("["))JSONObject().put("items",JSONArray(result))else JSONObject(result)
 }finally{connection.disconnect()}
}
private fun reminderDeliveryDiagnostic(response:JSONObject,receivedAt:Long):String {
 val allowed=setOf(
  "android_jni_unavailable","android_jni_call_failed","android_native_timeout",
  "android_payload_parse_failed","android_expiry_parse_failed","android_invalid_command_id",
  "android_invalid_notification_payload","android_permission_denied","android_permission_check_failed",
  "android_channel_missing","android_pending_intent_failed","android_notification_build_failed",
  "android_notification_post_failed","android_dedupe_read_failed","android_dedupe_persist_failed"
 )
 val code=response.optString("diagnosticCode").takeIf{it in allowed}?:"unavailable"
 fun delta(field:String):String=runCatching {
  (java.time.OffsetDateTime.parse(response.optString(field)).toInstant().toEpochMilli()-receivedAt).toString()
 }.getOrDefault("unavailable")
 return "diagnosticCode=$code remainingMs=${delta("commandExpiresAt")} serverClockDeltaMs=${delta("serverRespondedAt")}"
}
private fun awaitGeofenceReport(context:Context,pending:Boolean) {
 val prefs=context.getSharedPreferences("aegis-geofences-v1",Context.MODE_PRIVATE)
 val deadline=android.os.SystemClock.elapsedRealtime()+25000
 while(prefs.getBoolean("reportPending",false)!=pending && android.os.SystemClock.elapsedRealtime()<deadline)android.os.SystemClock.sleep(50)
 assertEquals(pending,prefs.getBoolean("reportPending",false))
}
private fun fixtureNodeRequest(path:String,credential:String?=null,body:JSONObject):JSONObject {
 val connection=URL("http://127.0.0.1:18104/api/nodes$path").openConnection() as HttpURLConnection
 try {
  connection.requestMethod="POST";connection.connectTimeout=10000;connection.readTimeout=15000;connection.instanceFollowRedirects=false;connection.doOutput=true
  connection.setRequestProperty("Content-Type","application/json")
  if(credential!=null)connection.setRequestProperty("Authorization","AegisNode $credential")
  connection.outputStream.use{it.write(body.toString().toByteArray())}
  val status=connection.responseCode;check(status==200){"fixture_node_http_$status"}
  val result=connection.inputStream.bufferedReader().use{it.readText()};check(result.length<=65536){"fixture_node_response_too_large"}
  return JSONObject(result)
 }finally{connection.disconnect()}
}
private fun assertFrameworkIdentityBinding(context:Context,nodeId:String) {
 assertEquals(nodeId,context.getSharedPreferences("aegis-geofences-v1",Context.MODE_PRIVATE).getString("scopeNodeId",null))
 assertEquals(nodeId,context.getSharedPreferences("aegis-node-push-v1",Context.MODE_PRIVATE).getString("nodeId",null))
 SQLiteDatabase.openDatabase(context.getDatabasePath("aegis-native-reminders-v1.db").path,null,SQLiteDatabase.OPEN_READONLY).use{db->
  db.rawQuery("SELECT value FROM metadata WHERE key='scopeId'",null).use{row->assertTrue(row.moveToFirst());assertEquals(nodeId,row.getString(0))}
 }
}

/** Peer credentials remain solely in the instrumentation process and never enter its WebView. */
internal fun withReenabledIdentityFramework(context:Context,nodeId:String,reserveCode:String,appVersion:String,invoke:(String,JSONObject,Long)->JSONObject,exercise:(String)->Unit) {
 val attempt=UUID.randomUUID().toString()
 val recovery=ByteArray(32).also{java.security.SecureRandom().nextBytes(it)}
 val receipt=fixtureNodeRequest("/pair",body=JSONObject().put("attemptId",attempt).put("code",reserveCode).put("recoveryKey",android.util.Base64.encodeToString(recovery,android.util.Base64.URL_SAFE or android.util.Base64.NO_WRAP or android.util.Base64.NO_PADDING)).put("name","Android framework peer").put("platform","android").put("appVersion",appVersion).put("protocolVersion",1))
 val credential=receipt.getString("credential")
 val peer=fixtureNodeRequest("/pair/finalize",body=JSONObject().put("attemptId",attempt).put("credential",credential)).getString("id")
 try {
  val replacementCode=fixtureNodeRequest("/pairing-codes",credential,JSONObject()).getString("code")
  invoke("node_set_enabled",JSONObject().put("id",nodeId).put("enabled",false),20000)
  assertNull(context.getSharedPreferences("aegis-node-push-v1",Context.MODE_PRIVATE).getString("nodeId",null))
  fixtureNodeRequest("/$nodeId/enable",credential,JSONObject())
  // The Activity remains foreground: no lifecycle/resume callback can mask this
  // explicit status interaction restoring the native operational binding.
  assertEquals("paired",invoke("node_status",JSONObject(),20000).getString("state"))
  assertFrameworkIdentityBinding(context,nodeId)
  val onlineDeadline=android.os.SystemClock.elapsedRealtime()+30000
  while(invoke("node_transport_status",JSONObject(),20000).getString("transportState")!="online"&&android.os.SystemClock.elapsedRealtime()<onlineDeadline)android.os.SystemClock.sleep(100)
  assertEquals("online",invoke("node_transport_status",JSONObject(),20000).getString("transportState"))
  val delivered=invoke("node_test_notification",JSONObject().put("id",nodeId),20000)
  val commandId=delivered.getString("commandId")
  val manager=context.getSystemService(NotificationManager::class.java)
  try {
   assertEquals("live_websocket",delivered.getString("transport"));assertEquals("success",delivered.getString("status"))
   val postDeadline=android.os.SystemClock.elapsedRealtime()+5000
   while(manager.activeNotifications.none{it.tag==commandId&&it.id==1}&&android.os.SystemClock.elapsedRealtime()<postDeadline)android.os.SystemClock.sleep(50)
   assertTrue(manager.activeNotifications.any{it.tag==commandId&&it.id==1})
  }finally{manager.cancel(commandId,1)}
  assertEquals("installed",invoke("node_geofences",JSONObject().put("operation","sync"),20000).getString("status"))
  android.util.Log.i("AegisNodeNativeTest","phase=native_identity_reenabled errorType=Checkpoint")
  exerciseWithdrawalReceiptFramework(context,nodeId,credential)
  exercise(replacementCode)
 }finally {
  fixtureOffline(0)
  fixtureNodeRequest("/$peer/revoke",credential,JSONObject())
 }
}

/** Provider acceptance is simulated; cancellation, SQLite and authenticated receipts are real. */
private fun exerciseWithdrawalReceiptFramework(context:Context,nodeId:String,peerCredential:String) {
 val id=UUID.randomUUID().toString();val commandId=UUID.randomUUID().toString();val expiresAt=geoStamp(System.currentTimeMillis()+240000)
 val manager=context.getSystemService(NotificationManager::class.java)
 fun receipt():Pair<Int,Long?> = SQLiteDatabase.openDatabase(context.getDatabasePath("aegis-native-reminders-v1.db").path,null,SQLiteDatabase.OPEN_READONLY).use{db->db.rawQuery("SELECT created FROM withdrawal_receipts WHERE reminderId=? AND generation=1",arrayOf(id)).use{rows->if(rows.moveToFirst())1 to rows.getLong(0)else 0 to null}}
 fun withdrawnAt():String? {
  val rows=fixtureJson("reminder-recipients").getJSONArray("items")
  return (0 until rows.length()).map{rows.getJSONObject(it)}.single{it.getString("reminderId")==id&&it.getString("nodeId")==nodeId&&it.getInt("generation")==1}.optString("withdrawnAt").takeIf{it.isNotEmpty()&&it!="null"}
 }
 fun await(condition:()->Boolean) {
  val deadline=android.os.SystemClock.elapsedRealtime()+45000
  while(!condition()&&android.os.SystemClock.elapsedRealtime()<deadline)android.os.SystemClock.sleep(50)
  assertTrue(condition())
 }
 try {
  val posted=fixtureJson("reminder-command",JSONObject().put("nodeId",nodeId).put("reminderId",id).put("commandId",UUID.randomUUID().toString()))
  val receivedAt=System.currentTimeMillis()
  assertEquals(reminderDeliveryDiagnostic(posted,receivedAt),"success",posted.getString("status"));awaitFrameworkReminder(manager,id,1)
  val acknowledged=fixtureNodeRequest("/reminders/$id/actions",peerCredential,JSONObject().put("actionId",UUID.randomUUID().toString()).put("expectedRevision",1).put("generation",1).put("action","acknowledge"))
  assertTrue(acknowledged.getBoolean("applied"))
  assertNull(withdrawnAt())
  fixtureOffline(120)
  val input=JSONObject().put("reminderId",id).put("generation",1)
  nativeFcm(context,nodeId,"reminder.withdraw",input,commandId,expiresAt)
  awaitFrameworkReminderWithdrawal(manager,id,1);await{receipt().first==1}
  val created=receipt().second
  nativeFcm(context,nodeId,"reminder.withdraw",input,commandId,expiresAt)
  assertEquals(1,receipt().first);assertEquals(created,receipt().second)
  assertNull(withdrawnAt())
  fixtureMode("withdrawal-receipt-failure","disconnect_after_apply")
  val dropsBefore=fixtureJson("idle").optLong("withdrawal_receipt_responses_dropped",0)
  fixtureOffline(0)
  nativeFcm(context,nodeId,"geofence.monitor",JSONObject().put("operation","sync"))
  await{fixtureJson("idle").optLong("withdrawal_receipt_responses_dropped",0)>dropsBefore}
  assertNotNull(withdrawnAt());assertEquals(1,receipt().first);assertEquals(created,receipt().second)
  // The dropped response cannot acknowledge SQLite. The ordinary bounded retry
  // resends the same pair; the server receipt is idempotent and clears it.
  val acceptedAt=withdrawnAt()
  fixtureMode("withdrawal-receipt-failure","none")
  nativeFcm(context,nodeId,"geofence.monitor",JSONObject().put("operation","sync"))
  await{receipt().first==0}
  assertEquals(acceptedAt,withdrawnAt())
  val requestsBeforeReplay=fixtureJson("idle").optLong("withdrawal_receipt_requests",0)
  nativeFcm(context,nodeId,"reminder.withdraw",input,commandId,expiresAt)
  await{fixtureJson("idle").optLong("withdrawal_receipt_requests",0)>requestsBeforeReplay && receipt().first==0}
  assertEquals(acceptedAt,withdrawnAt())
  assertTrue(manager.activeNotifications.none{it.tag=="reminder:$id:1"})
  android.util.Log.i("AegisNodeNativeTest","phase=withdrawal_receipt_fcm_offline_replay_lost_response errorType=Checkpoint")
 }finally {
  fixtureOffline(0);fixtureMode("withdrawal-receipt-failure","none")
  manager.cancel("reminder:$id:1",1)
 }
}

/** Authenticated Kotlin HTTP and real Google Task are separate from seeded deterministic state tests. */
internal fun exerciseGeofenceLiveFramework(context:Context,nodeId:String,replacementCode:String,invoke:(String,JSONObject,Long)->JSONObject):String {
 fun ipc(name:String,operation:String,payload:JSONObject?=null,id:String?=null,timeout:Long=20000):JSONObject=invoke(name,JSONObject().put("operation",operation).apply{if(payload!=null)put("payload",payload);if(id!=null)put("id",id)},timeout)
 fun checkpoint(phase:String)=android.util.Log.i("AegisNodeNativeTest","phase=$phase errorType=Checkpoint")
 // This empty-manifest round trip must read the actual Rust keyring credential in Kotlin.
 assertEquals("installed",ipc("node_geofences","sync").getString("status"))
 checkpoint("geofence_authenticated_native_http")
 val instrumentation=InstrumentationRegistry.getInstrumentation()
 instrumentation.uiAutomation.grantRuntimePermission(context.packageName,Manifest.permission.ACCESS_COARSE_LOCATION)
 instrumentation.uiAutomation.grantRuntimePermission(context.packageName,Manifest.permission.ACCESS_FINE_LOCATION)
 if(Build.VERSION.SDK_INT>=29)instrumentation.uiAutomation.grantRuntimePermission(context.packageName,Manifest.permission.ACCESS_BACKGROUND_LOCATION)
 exerciseLocationPermissionFramework(context)
 ipc("node_location","configureNode",JSONObject().put("nodeId",nodeId).put("mode","dynamic").put("sourceNodeId",nodeId))
 val circleA=UUID.randomUUID().toString();val circleB=UUID.randomUUID().toString()
 val circles=JSONArray().put(JSONObject().put("id",circleA).put("latitude",0).put("longitude",0).put("radiusMeters",200)).put(JSONObject().put("id",circleB).put("latitude",0).put("longitude",0.002).put("radiusMeters",200))
 val place=ipc("node_places","create",JSONObject().put("name","CI synthetic union").put("type","property").put("circles",circles).put("aliases",JSONArray()))
 val placeId=place.getString("id")
 val rule=ipc("node_geofences","create",JSONObject().put("text","Synthetic geographic fixture").put("placeIds",JSONArray().put(placeId)).put("observerNodeId",nodeId).put("transition","exit").put("repeat",true).put("timeZoneId","UTC"))
 val ruleId=rule.getString("id")
 val prefs=context.getSharedPreferences("aegis-geofences-v1",Context.MODE_PRIVATE)
 val manager=context.getSystemService(NotificationManager::class.java)
 val occurrences=mutableListOf<String>()
 val receiptOccurrences=mutableListOf<String>()
 try {
  // Restoring the same rule revision must discard its old armed presentation state.
  prefs.edit().putLong("ruleRevision:$ruleId",rule.getLong("revision")).putBoolean("armed:$ruleId",true).putBoolean("needsInstall",true).commit()
  fixtureMode("geofence-sync-failure","503")
  val registered=ipc("node_geofences","sync",timeout=90000)
  if(registered.getString("status") in setOf("installed","waiting_initial","active")) {
   assertFalse(prefs.getBoolean("needsInstall",true));assertTrue(prefs.getBoolean("hasRegions",false))
   val initial=prefs.getString("state:$placeId",null)?.let{JSONObject(it).optString("confirmed")}
   assertEquals(initial=="inside",prefs.getBoolean("armed:$ruleId",false))
   assertTrue(registered.getBoolean("syncPending"));assertTrue(prefs.getBoolean("reportPending",false))
   checkpoint("geofence_google_registration_task_succeeded")
   exerciseGeofenceReportFailures(context,nodeId,ruleId,placeId,receiptOccurrences){ipc("node_geofences","sync",timeout=60000)}
  } else {
   assertEquals("recoverable_error",registered.getString("status"))
   assertTrue(registered.getString("errorCode") in setOf("location_provider_unavailable","location_permission_required","precise_location_required","background_location_required","location_services_disabled","native_registration_failed"))
   checkpoint("geofence_google_registration_unavailable")
  }
  fixtureMode("geofence-sync-failure","none")
  // Pause through the real backend and remove native regions before deterministic observations.
  val paused=ipc("node_geofences","pause",JSONObject().put("expectedRevision",rule.getInt("revision")),ruleId)
  assertEquals("installed",ipc("node_geofences","sync",timeout=45000).getString("status"))
  val resumed=ipc("node_geofences","resume",JSONObject().put("expectedRevision",paused.getInt("revision")),ruleId)
  fixtureOffline(120)
  geoRuntime(context).getMethod("deactivate",Context::class.java).invoke(null,context)
  // Direct private storage fixture, never a product RPC or claimed Google registration.
  // OS registration was independently checked above; the following proves persisted local execution.
  val now=System.currentTimeMillis()
  val definition=JSONObject().put("id",ruleId).put("revision",resumed.getInt("revision")).put("placeIds",JSONArray().put(placeId)).put("text","Synthetic geographic fixture").put("transition","exit").put("repeat",true).put("status","active").put("startsAt",JSONObject.NULL).put("endsAt",JSONObject.NULL).put("leaseUntil",geoStamp(now+3600000)).put("timeZoneId","UTC").put("deliveryPolicy","allEligible")
  val manifest=JSONObject().put("revision","instrumented-${UUID.randomUUID()}").put("generatedAt",geoStamp(now)).put("leaseUntil",geoStamp(now+3600000)).put("observerNodeId",nodeId).put("places",JSONArray().put(place)).put("rules",JSONArray().put(definition))
  prefs.edit().putString("manifest",manifest.toString()).putString("installedRevision",manifest.getString("revision")).putBoolean("needsInstall",false).putBoolean("hasRegions",false).putLong("notBefore:$placeId",now-119000).putLong("ruleRevision:$ruleId",resumed.getLong("revision")).putBoolean("armed:$ruleId",false).remove("completed:$ruleId").remove("state:$placeId").commit()
  exerciseResidentialLocationAdapterFramework(context,manifest,ruleId,placeId)
  val observe=geoRuntime(context).getMethod("observe",Context::class.java,Double::class.javaPrimitiveType,Double::class.javaPrimitiveType,Double::class.javaPrimitiveType,Long::class.javaPrimitiveType,Boolean::class.javaPrimitiveType)
  fun position(longitude:Double,ago:Long,accuracy:Double=5.0)=observe.invoke(null,context,0.0,longitude,accuracy,now-ago,false)
  fun events():List<String> = SQLiteDatabase.openDatabase(context.getDatabasePath("aegis-geofence-events-v1.db").path,null,SQLiteDatabase.OPEN_READONLY).use{db->db.rawQuery("SELECT occurrenceId FROM events WHERE ruleId=? ORDER BY sequence",arrayOf(ruleId)).use{rows->val ids=mutableListOf<String>();while(rows.moveToNext())ids.add(rows.getString(0));ids}}
  // A cached inside sample predating registration cannot arm a newly created EXIT.
  position(0.0,119500);assertFalse(prefs.getBoolean("armed:$ruleId",false))
  assertFalse(prefs.contains("state:$placeId"));assertTrue(events().isEmpty())
  // Already outside does not constitute an exit; poor precision does not establish presence.
  position(0.01,119000);assertFalse(prefs.getBoolean("armed:$ruleId",false))
  assertEquals("outside",JSONObject(prefs.getString("state:$placeId",null)!!).getString("confirmed"));assertTrue(events().isEmpty())
  checkpoint("geofence_pre_activation_cache_ignored")
  position(0.0,118000,1000.0);assertFalse(prefs.getBoolean("armed:$ruleId",false))
  prefs.edit().remove("state:$placeId").commit()
  position(0.0,117000)
  assertTrue(prefs.getBoolean("armed:$ruleId",false))
  // Outside circle A but inside circle B remains inside the logical place.
  position(0.0028,116000)
  assertEquals("inside",JSONObject(prefs.getString("state:$placeId",null)!!).getString("confirmed"))
  position(0.01,115000);position(0.01,84000)
  assertEquals(1,events().size)
  val first=events().single();occurrences.add(first)
  awaitFrameworkReminder(manager,first,1)
  position(0.01,84000);position(0.0,90000)
  assertEquals(listOf(first),events()) // repeated and out of order observations cannot replay.
  position(0.0,83000);position(0.0,52000)
  position(0.01,51000);position(0.01,20000)
  assertEquals(2,events().size);occurrences.addAll(events().filter{it!=first})
  assertTrue(occurrences.distinct().size==2)
  // SQLite reopened after every assertion; event/occurrence IDs survive native component recreation.
  assertEquals(occurrences,events())
  checkpoint("geofence_union_offline_outbox_repeat_persisted")
  // Boot/package restoration uses the same real native domain operation. A consumed once rule
  // has no remaining region consumer and must not replay its occurrence after restoration.
  definition.put("repeat",false)
  manifest.put("rules",JSONArray().put(definition))
  prefs.edit().putString("manifest",manifest.toString()).putBoolean("armed:$ruleId",false).remove("completed:$ruleId").remove("state:$placeId").commit()
  position(0.0,117000);position(0.01,115000);position(0.01,84000)
  assertTrue(prefs.getBoolean("completed:$ruleId",false))
  assertEquals(3,events().size);occurrences.addAll(events().filter{it !in occurrences})
  geoRuntime(context).getMethod("restore",Context::class.java).invoke(null,context)
  assertFalse(prefs.getBoolean("hasRegions",true))
  assertEquals(occurrences,events())
  assertTrue(context.packageManager.queryBroadcastReceivers(Intent(Intent.ACTION_BOOT_COMPLETED).setPackage(context.packageName),0).isNotEmpty())
  checkpoint("geofence_restore_no_duplicate")
  val bind=geoRuntime(context).getMethod("bindIdentity",Context::class.java,String::class.java)
  bind.invoke(null,context,nodeId)
  assertEquals(occurrences,events()) // Same identity preserves its persisted pending delivery.
  // Keep the event/action endpoints unavailable while allowing only the real
  // authenticated pairing lifecycle. An empty outbox must result from the new
  // identity binding, never a background worker flushing it before revocation.
  fixtureOffline(120,allowPairing=true)
  // Use the fixture's independent one-time reserve. A code issued by the old
  // Node is correctly invalid after its issuer is revoked.
  assertEquals(occurrences,events())
  invoke("node_revoke",JSONObject().put("id",nodeId),20000)
  assertEquals(occurrences,events())
  val replacement=invoke("node_pair",JSONObject().put("name","Android replacement fixture").put("code",replacementCode),30000)
  assertEquals("paired",replacement.getString("state"))
  val nextId=replacement.getJSONObject("node").getString("id")
  assertNotEquals(nodeId,nextId)
  assertFrameworkIdentityBinding(context,nextId)
  assertTrue(events().isEmpty())
  for(id in occurrences)awaitFrameworkReminderWithdrawal(manager,id,1)
  checkpoint("native_identity_scope_reset")
  return nextId
 } finally {
  fixtureOffline(0)
  fixtureMode("geofence-sync-failure","none")
  // Authoritative cleanup is revision checked. Normal reconciliation may already have completed it.
  try {val list=ipc("node_geofences","list").getJSONArray("items");val current=(0 until list.length()).map{list.getJSONObject(it)}.firstOrNull{it.getString("id")==ruleId};if(current!=null)ipc("node_geofences","cancel",JSONObject().put("expectedRevision",current.getInt("revision")),ruleId)}catch(_:Exception){}
  for(id in occurrences+receiptOccurrences)manager.cancel("reminder:$id:1",1)
  prefs.edit().clear().commit()
 }
}

/** Real GMS registration is kept separate from the synthetic local observation trajectory. */
private fun exerciseGeofenceReportFailures(context:Context,nodeId:String,ruleId:String,placeId:String,occurrences:MutableList<String>,sync:()->JSONObject) {
 val prefs=context.getSharedPreferences("aegis-geofences-v1",Context.MODE_PRIVATE)
 val manager=context.getSystemService(NotificationManager::class.java)
 val observe=geoRuntime(context).getMethod("observe",Context::class.java,Double::class.javaPrimitiveType,Double::class.javaPrimitiveType,Double::class.javaPrimitiveType,Long::class.javaPrimitiveType,Boolean::class.javaPrimitiveType)
 fun presented():Set<String> = SQLiteDatabase.openDatabase(context.getDatabasePath("aegis-native-reminders-v1.db").path,null,SQLiteDatabase.OPEN_READONLY).use{db->db.rawQuery("SELECT id FROM presentations WHERE ruleId=?",arrayOf(ruleId)).use{rows->val result=mutableSetOf<String>();while(rows.moveToNext())result.add(rows.getString(0));result}}
 for((index,case)in listOf("503" to false,"disconnect" to false,"503" to true,"disconnect" to true).withIndex()) {
  val(mode,restore)=case
  fixtureMode("geofence-sync-failure",mode)
  if(index>0) {
   if(restore)geoRuntime(context).getMethod("restore",Context::class.java).invoke(null,context)
   else {prefs.edit().putBoolean("needsInstall",true).commit();sync()}
  }
  assertFalse("A failed backend receipt cannot invalidate Google registration",prefs.getBoolean("needsInstall",true))
  assertTrue(prefs.getBoolean("hasRegions",false));assertTrue(prefs.getBoolean("reportPending",false))
  assertNotEquals("native_registration_failed",prefs.getString("error",null))
  val before=presented()
  val now=System.currentTimeMillis()
  // Controlled captures cover the confirmation interval without waiting for a
  // street journey. The registration above is real; this trajectory is synthetic.
  prefs.edit().putLong("notBefore:$placeId",now-65000).remove("state:$placeId").putBoolean("armed:$ruleId",false).commit()
  observe.invoke(null,context,0.0,0.0,5.0,now-64000,false)
  assertTrue(prefs.getBoolean("armed:$ruleId",false))
  observe.invoke(null,context,0.0,0.01,5.0,now-32000,false)
  observe.invoke(null,context,0.0,0.01,5.0,now-1000,false)
  val occurrence=(presented()-before).single()
  occurrences.add(occurrence);awaitFrameworkReminder(manager,occurrence,1)
  val notBefore=prefs.getLong("notBefore:$placeId",0)
  val state=JSONObject(prefs.getString("state:$placeId",null)!!)
  val armed=prefs.getBoolean("armed:$ruleId",false)
  fixtureMode("geofence-sync-failure","none")
  // An ordinary native background signal reconciles the receipt with no WebView
  // invocation and must preserve the accepted native registration/presence.
  nativeFcm(context,nodeId,"geofence.monitor",JSONObject().put("operation","sync"))
  awaitGeofenceReport(context,false)
  assertFalse(prefs.getBoolean("needsInstall",true));assertEquals(notBefore,prefs.getLong("notBefore:$placeId",0))
  val after=JSONObject(prefs.getString("state:$placeId",null)!!)
  assertTrue(after.getLong("lastCapturedAt")>=state.getLong("lastCapturedAt"))
  if(after.getLong("lastCapturedAt")==state.getLong("lastCapturedAt"))assertEquals(armed,prefs.getBoolean("armed:$ruleId",false))
  android.util.Log.i("AegisNodeNativeTest","phase=geofence_receipt_${mode}_${if(restore)"restore" else "install"} errorType=Checkpoint")
 }
}


/** Framework Location objects exercise the actual bounded adapter; only provider delivery is simulated. */
private fun exerciseResidentialLocationAdapterFramework(context:Context,original:JSONObject,ruleId:String,placeId:String) {
 val instrumentation=InstrumentationRegistry.getInstrumentation()
 val prefs=context.getSharedPreferences("aegis-geofences-v1",Context.MODE_PRIVATE)
 val runtime=geoRuntime(context)
 val adapter=runtime.getMethod("locationAdapterFixture",Context::class.java,String::class.java,Double::class.javaPrimitiveType,Long::class.javaPrimitiveType)
 val diagnostics=runtime.getMethod("diagnostics",Context::class.java)
 val observe=runtime.getMethod("observe",Context::class.java,Double::class.javaPrimitiveType,Double::class.javaPrimitiveType,Double::class.javaPrimitiveType,Long::class.javaPrimitiveType,Boolean::class.javaPrimitiveType)
 val previous=prefs.all.toMap()
 val positionPrefs=context.getSharedPreferences("aegis-node-push-v1",Context.MODE_PRIVATE)
 val previousPosition=positionPrefs.getString("lastObservation",null)
 val previousPending=positionPrefs.getBoolean("observationPending",false)
 fun shell(command:String)=instrumentation.uiAutomation.executeShellCommand(command).use{descriptor->android.os.ParcelFileDescriptor.AutoCloseInputStream(descriptor).bufferedReader().use{it.readText()}}
 fun details(result:JSONObject)=(0 until result.getJSONArray("rules").length()).map{result.getJSONArray("rules").getJSONObject(it)}.single{it.getString("id")==ruleId}
 fun acquisition(scenario:String,accuracy:Double=13.0,age:Long=0):JSONObject {
  android.os.SystemClock.sleep(5)
  val result=JSONObject(adapter.invoke(null,context,scenario,accuracy,age) as String)
  val request=result.getJSONObject("fixtureRequest")
  assertEquals(100,request.getInt("priority")) // Android HIGH_ACCURACY, a request rather than a guarantee.
  assertEquals(0L,request.getLong("maxUpdateAgeMillis"))
  assertEquals(12000L,request.getLong("durationMillis"));assertEquals(13000L,request.getLong("timeoutMillis"))
  assertTrue(request.getBoolean("cancelled"))
  assertEquals(original.getString("revision"),prefs.getString("installedRevision",null))
  return result
 }
 try {
  val home=JSONObject(original.toString())
  home.getJSONArray("places").getJSONObject(0).put("circles",JSONArray().put(JSONObject().put("id",UUID.randomUUID().toString()).put("latitude",0).put("longitude",0).put("radiusMeters",65)).put(JSONObject().put("id",UUID.randomUUID().toString()).put("latitude",0).put("longitude",0.00045).put("radiusMeters",65)))
  prefs.edit().putString("manifest",home.toString()).putLong("installedUntil",System.currentTimeMillis()+3600000).putBoolean("hasRegions",true).putLong("notBefore:$placeId",System.currentTimeMillis()-120000).putBoolean("armed:$ruleId",false).remove("state:$placeId").remove("diagnostic:$placeId").remove("lastAcquisitionStatus").remove("lastAcquisitionAt").commit()
  val unknown=details(acquisition("available",100.0))
  assertEquals("installed",unknown.getString("installationStatus"));assertEquals("unknown",unknown.getString("presence"));assertEquals("accuracy_insufficient",unknown.getString("reasonCode"));assertFalse(unknown.getBoolean("armed"))
  // A fresh explicit check arms the existing registration without a reinstall.
  val good=details(acquisition("available",13.0))
  assertEquals("inside",good.getString("presence"));assertTrue(good.getBoolean("armed"));assertEquals(13.0,good.getDouble("accuracyMeters"),0.0)
  val low=details(acquisition("available",100.0))
  assertEquals("unknown",low.getString("presence"));assertEquals("inside",low.getString("confirmedPresence"));assertTrue(low.getBoolean("armed"))
  val absent=details(acquisition("unavailable"));assertEquals("unknown",absent.getString("presence"));assertEquals("location_unavailable",absent.getString("reasonCode"))
  assertEquals("unavailable",acquisition("timeout").getString("acquisitionStatus"))
  assertEquals("stale",acquisition("available",13.0,30000).getString("acquisitionStatus"))
  // Future/out-of-order deliveries cannot poison diagnostic ordering.
  observe.invoke(null,context,0.0,0.01,13.0,System.currentTimeMillis()+3600000,false)
  val recovered=details(acquisition("available",13.0));assertEquals("inside",recovered.getString("presence"))
  assertTrue(recovered.getLong("lastObservationAt")<=System.currentTimeMillis())
  assertEquals(previousPosition,positionPrefs.getString("lastObservation",null));assertEquals(previousPending,positionPrefs.getBoolean("observationPending",false))
  // The framework adapter reports actual saver state. Provider accuracy stays
  // uncertain even while the OS accepts a high-accuracy request.
  shell("dumpsys battery unplug")
  for(enabled in listOf(true,false)) {
   shell("cmd power set-mode ${if(enabled)1 else 0}")
   val deadline=android.os.SystemClock.elapsedRealtime()+3000
   while(context.getSystemService(android.os.PowerManager::class.java).isPowerSaveMode!=enabled && android.os.SystemClock.elapsedRealtime()<deadline)android.os.SystemClock.sleep(50)
   assertEquals(enabled,context.getSystemService(android.os.PowerManager::class.java).isPowerSaveMode)
   val state=acquisition("available",100.0)
   assertEquals(enabled,state.getBoolean("powerSaveMode"));assertEquals("unknown",details(state).getString("presence"))
   assertEquals("inside",details(acquisition("available",13.0)).getString("presence"))
  }
  // An installed fence with no observation remains unarmed and explainable.
  prefs.edit().remove("state:$placeId").remove("diagnostic:$placeId").remove("lastAcquisitionStatus").remove("lastAcquisitionAt").putBoolean("armed:$ruleId",false).commit()
  assertEquals("not_observed",details(JSONObject(diagnostics.invoke(null,context) as String)).getString("reasonCode"))
  android.util.Log.i("AegisNodeNativeTest","phase=residential65_adapter_fresh_accuracy_timeout_saver errorType=Checkpoint")
 }finally {
  shell("cmd power set-mode 0");shell("dumpsys battery reset")
  val edit=prefs.edit()
  for(key in listOf("manifest","installedUntil","hasRegions","notBefore:$placeId","armed:$ruleId","state:$placeId","diagnostic:$placeId","lastAcquisitionStatus","lastAcquisitionAt")) {
   when(val old=previous[key]) {null->edit.remove(key);is String->edit.putString(key,old);is Long->edit.putLong(key,old);is Int->edit.putInt(key,old);is Boolean->edit.putBoolean(key,old)}
  }
  check(edit.commit())
 }
}
