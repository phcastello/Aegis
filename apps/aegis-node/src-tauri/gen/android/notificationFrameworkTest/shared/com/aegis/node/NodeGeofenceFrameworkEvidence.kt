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
  exercise(replacementCode)
 }finally {
  fixtureOffline(0)
  fixtureNodeRequest("/$peer/revoke",credential,JSONObject())
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
 try {
  // Restoring the same rule revision must discard its old armed presentation state.
  prefs.edit().putLong("ruleRevision:$ruleId",rule.getLong("revision")).putBoolean("armed:$ruleId",true).putBoolean("needsInstall",true).commit()
  val registered=ipc("node_geofences","sync",timeout=90000)
  if(registered.getString("status") in setOf("installed","waiting_initial","active")) {
   assertFalse(prefs.getBoolean("needsInstall",true));assertTrue(prefs.getBoolean("hasRegions",false))
   val initial=prefs.getString("state:$placeId",null)?.let{JSONObject(it).optString("confirmed")}
   assertEquals(initial=="inside",prefs.getBoolean("armed:$ruleId",false))
   checkpoint("geofence_google_registration_task_succeeded")
  } else {
   assertEquals("recoverable_error",registered.getString("status"))
   assertTrue(registered.getString("errorCode") in setOf("location_provider_unavailable","location_permission_required","precise_location_required","background_location_required","location_services_disabled","native_registration_failed"))
   checkpoint("geofence_google_registration_unavailable")
  }
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
  // Authoritative cleanup is revision checked. Normal reconciliation may already have completed it.
  try {val list=ipc("node_geofences","list").getJSONArray("items");val current=(0 until list.length()).map{list.getJSONObject(it)}.firstOrNull{it.getString("id")==ruleId};if(current!=null)ipc("node_geofences","cancel",JSONObject().put("expectedRevision",current.getInt("revision")),ruleId)}catch(_:Exception){}
  for(id in occurrences)manager.cancel("reminder:$id:1",1)
  prefs.edit().clear().commit()
 }
}
