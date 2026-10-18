package com.aegis.node

import android.app.NotificationManager
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.database.sqlite.SQLiteDatabase
import android.location.Location
import android.os.Build
import android.os.BatteryManager
import android.os.Handler
import android.os.Looper
import android.os.Parcel
import android.os.PowerManager
import android.os.SystemClock
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.google.android.gms.location.Geofence
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.net.HttpURLConnection
import java.net.Socket
import java.net.URL
import java.security.KeyStore
import java.security.SecureRandom
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import javax.crypto.Cipher
import javax.crypto.KeyGenerator

/**
 * API 26/35, including the optimized target APK, without launching an Activity.
 * Only GMS delivery and registration storage are controlled fixtures: the actual
 * receiver, Android Keystore, persistence, notification IPC and HTTP reconcile run.
 * This cannot prove Google's physical movement detection, latency or OEM delivery.
 */
@RunWith(AndroidJUnit4::class)
class NodeGeofenceReceiverFrameworkTest {
 private val instrumentation=InstrumentationRegistry.getInstrumentation()
 private val context=instrumentation.targetContext
 private val preferences=context.getSharedPreferences("aegis-geofences-v1",Context.MODE_PRIVATE)
 private val notifications=context.getSystemService(NotificationManager::class.java)
 private val runtime=Class.forName("com.aegis.node.NodeGeofenceRuntime",true,context.classLoader)
 private val occurrences=mutableListOf<String>()
 private fun stamp(at:Long)=SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'",Locale.ROOT).apply{timeZone=TimeZone.getTimeZone("UTC")}.format(Date(at))
 private fun shell(command:String)=instrumentation.uiAutomation.executeShellCommand(command).use{fd->android.os.ParcelFileDescriptor.AutoCloseInputStream(fd).bufferedReader().use{it.readText()}}
 private fun await(reason:String,timeoutMillis:Long=15000,condition:()->Boolean) {
  val end=SystemClock.elapsedRealtime()+timeoutMillis
  while(!condition() && SystemClock.elapsedRealtime()<end)SystemClock.sleep(25)
  assertTrue(reason,condition())
 }
 private fun request(path:String,credential:String?=null,body:JSONObject?=null,method:String=if(body==null)"GET" else "POST"):JSONObject {
  val connection=URL("http://127.0.0.1:18104/api/nodes$path").openConnection() as HttpURLConnection
  try {
   connection.requestMethod=method;connection.connectTimeout=5000;connection.readTimeout=15000;connection.instanceFollowRedirects=false
   if(credential!=null)connection.setRequestProperty("Authorization","AegisNode $credential")
   if(body!=null){connection.doOutput=true;connection.setRequestProperty("Content-Type","application/json");connection.outputStream.use{it.write(body.toString().toByteArray())}}
   check(connection.responseCode==200){"receiver_fixture_http_${connection.responseCode}"}
   val raw=connection.inputStream.bufferedReader().use{it.readText()};check(raw.length<=1048576)
   return if(raw.startsWith("["))JSONObject().put("items",JSONArray(raw)) else JSONObject(raw)
  }finally{connection.disconnect()}
 }
 /** Ordinary authenticated hello registers capability through the real transport. */
 private fun announce(credential:String) {
  Socket("127.0.0.1",18104).use{socket->
   socket.soTimeout=15000
   val input=java.io.DataInputStream(socket.getInputStream());val output=socket.getOutputStream()
   val key=Base64.encodeToString(ByteArray(16).also{SecureRandom().nextBytes(it)},Base64.NO_WRAP)
   output.write(("GET /api/nodes/connect HTTP/1.1\r\nHost: 127.0.0.1:18104\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: $key\r\nAuthorization: AegisNode $credential\r\nX-Aegis-Node-Protocol: 1\r\n\r\n").toByteArray());output.flush()
   val header=StringBuilder()
   while(!header.endsWith("\r\n\r\n")){check(header.length<8192);header.append(input.readUnsignedByte().toChar())}
   check(header.startsWith("HTTP/1.1 101")){"receiver_fixture_websocket_upgrade_failed"}
   val hello=JSONObject().put("protocolVersion",1).put("type","hello").put("messageId",UUID.randomUUID().toString()).put("sentAt",stamp(System.currentTimeMillis())).put("payload",JSONObject().put("appVersion","0.0.0-framework-fixture").put("capabilities",JSONArray().put(JSONObject().put("name","geofence.monitor").put("version",1)))).toString().toByteArray()
   check(hello.size<65536)
   output.write(0x81)
   if(hello.size<126)output.write(0x80 or hello.size) else {output.write(0xfe);output.write(hello.size ushr 8);output.write(hello.size and 255)}
   val mask=ByteArray(4).also{SecureRandom().nextBytes(it)};output.write(mask)
   output.write(ByteArray(hello.size){(hello[it].toInt() xor mask[it%4].toInt()).toByte()});output.flush()
   check(input.readUnsignedByte() and 15==1){"receiver_fixture_websocket_ack_type"}
   val length=input.readUnsignedByte();check(length and 0x80==0)
   val size=when(length and 127){126->input.readUnsignedShort();127->error("receiver_fixture_ack_too_large");else->length and 127}
   check(size in 1..4096);val reply=ByteArray(size);input.readFully(reply)
   check(JSONObject(String(reply)).getString("type")=="hello_ack"){"receiver_fixture_websocket_ack_missing"}
  }
 }
 private fun vault(node:JSONObject,credential:String) {
  val alias="keyring-aegis-node-identity-v1"
  val store=KeyStore.getInstance("AndroidKeyStore").apply{load(null)}
  val key=store.getKey(alias,null)?:KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES,"AndroidKeyStore").apply {
   init(KeyGenParameterSpec.Builder(alias,KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT).setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build())
  }.generateKey()
  val record=JSONObject().put("origin","http://127.0.0.1:18104").put("node",node).put("credential",credential).put("pending",JSONObject.NULL).put("revoked",false)
  val cipher=Cipher.getInstance("AES/GCM/NoPadding").apply{init(Cipher.ENCRYPT_MODE,key)}
  val bytes=byteArrayOf(12)+cipher.iv+cipher.doFinal(record.toString().toByteArray())
  check(context.getSharedPreferences(alias,Context.MODE_PRIVATE).edit().putString("installation\uFEFF@\uFEFFcom.aegis.node.identity.v1",Base64.encodeToString(bytes,Base64.NO_WRAP)).commit())
  check(context.getSharedPreferences("aegis-node-push-v1",Context.MODE_PRIVATE).edit().putString("nodeId",node.getString("id")).putString("origin","http://127.0.0.1:18104").commit())
  runtime.getMethod("bindIdentity",Context::class.java,String::class.java).invoke(null,context,node.getString("id"))
 }
 /** Serialize the public SDK's real Geofence, matching GeofencingEvent.fromIntent. */
 private fun callback(transition:Int,ids:List<String>,longitude:Double?=0.0,accuracy:Double=13.0,age:Long=0,error:Int=-1) {
  val parcels=ArrayList<ByteArray>()
  for(id in ids) {
   val region=Geofence.Builder().setRequestId(id).setCircularRegion(0.0,0.0,65f).setTransitionTypes(3).setExpirationDuration(3600000).build()
   val parcel=Parcel.obtain()
   try{(region as android.os.Parcelable).writeToParcel(parcel,0);parcels.add(parcel.marshall())}finally{parcel.recycle()}
  }
  val intent=Intent("com.aegis.node.GEOFENCE").setClassName(context.packageName,"com.aegis.node.NodeGeofenceReceiver")
   .putExtra("gms_error_code",error).putExtra("com.google.android.location.intent.extra.transition",transition)
   .putExtra("com.google.android.location.intent.extra.geofence_list",parcels as java.io.Serializable)
  if(longitude!=null)intent.putExtra("com.google.android.location.intent.extra.triggering_location",Location("fused").apply {
   latitude=0.0;this.longitude=longitude;this.accuracy=accuracy.toFloat();time=System.currentTimeMillis()-age;elapsedRealtimeNanos=SystemClock.elapsedRealtimeNanos()-age*1000000
  })
  val completed=CountDownLatch(1)
  context.sendOrderedBroadcast(intent,null,object:BroadcastReceiver(){override fun onReceive(context:Context,intent:Intent){completed.countDown()}},Handler(Looper.getMainLooper()),0,null,null)
  assertTrue("Native receiver finishes its bounded broadcast",completed.await(20,TimeUnit.SECONDS))
 }
 private fun events(ruleId:String):List<JSONObject> {
  val file=context.getDatabasePath("aegis-geofence-events-v1.db")
  if(!file.exists())return emptyList()
  return SQLiteDatabase.openDatabase(file.path,null,SQLiteDatabase.OPEN_READONLY).use{db->db.rawQuery("SELECT eventId,occurrenceId,ruleId,ruleRevision,transition,occurredAt,sequence,postedAt FROM events WHERE ruleId=? ORDER BY sequence",arrayOf(ruleId)).use{rows->
   val result=mutableListOf<JSONObject>()
   while(rows.moveToNext())result.add(JSONObject().put("eventId",rows.getString(0)).put("occurrenceId",rows.getString(1)).put("ruleId",rows.getString(2)).put("ruleRevision",rows.getLong(3)).put("transition",rows.getString(4)).put("occurredAt",rows.getString(5)).put("sequence",rows.getLong(6)).apply{if(!rows.isNull(7))put("postedAt",rows.getString(7))})
   result
  }}
 }
 private fun restore(prefs:android.content.SharedPreferences,old:Map<String,*>) {
  val edit=prefs.edit().clear()
  for((key,value)in old)when(value){is String->edit.putString(key,value);is Boolean->edit.putBoolean(key,value);is Long->edit.putLong(key,value);is Int->edit.putInt(key,value);is Float->edit.putFloat(key,value);is Set<*>->edit.putStringSet(key,value.filterIsInstance<String>().toSet())}
  check(edit.commit())
 }
 @Test fun nativeReceiverPersistsPostsAndReconcilesWithoutASecondAcquisition() {
  val code=InstrumentationRegistry.getArguments().getString("geofencePairingCode")?:error("receiver_fixture_bootstrap_missing")
  val snapshots=listOf("keyring-aegis-node-identity-v1","aegis-node-push-v1","aegis-geofences-v1").associateWith{context.getSharedPreferences(it,Context.MODE_PRIVATE).all.toMap()}
  val previousSaver=shell("settings get global low_power").trim();val previousSticky=shell("settings get global low_power_sticky").trim()
  var credential:String?=null;var nodeId:String?=null
  try {
   val attempt=UUID.randomUUID().toString()
   val receipt=request("/pair",body=JSONObject().put("attemptId",attempt).put("code",code).put("recoveryKey",Base64.encodeToString(ByteArray(32).also{SecureRandom().nextBytes(it)},Base64.NO_WRAP or Base64.URL_SAFE or Base64.NO_PADDING)).put("name","Receiver framework fixture").put("platform","android").put("appVersion","0.0.0-framework-fixture").put("protocolVersion",1))
   credential=receipt.getString("credential")
   val node=request("/pair/finalize",body=JSONObject().put("attemptId",attempt).put("credential",credential));nodeId=node.getString("id")
   announce(credential)
   request("/location/settings",credential,JSONObject().put("nodeId",nodeId).put("sourceNodeId",nodeId).put("mode","dynamic"),"PUT")
   vault(node,credential)
   val circleA=UUID.randomUUID().toString();val circleB=UUID.randomUUID().toString()
   val circles=JSONArray().put(JSONObject().put("id",circleA).put("latitude",0).put("longitude",0).put("radiusMeters",65)).put(JSONObject().put("id",circleB).put("latitude",0).put("longitude",0.00045).put("radiusMeters",65))
   val place=request("/places",credential,JSONObject().put("name","Receiver synthetic union").put("type","property").put("circles",circles).put("aliases",JSONArray()))
   val placeId=place.getString("id")
   val rule=request("/geofences",credential,JSONObject().put("text","Receiver geographic fixture").put("placeIds",JSONArray().put(placeId)).put("observerNodeId",nodeId).put("transition","enter").put("repeat",true).put("timeZoneId","UTC"))
   val ruleId=rule.getString("id")
   val manifest=request("/geofences/manifest",credential)
   // Offline work cannot overwrite the fixture with an unrelated acquisition.
   fixtureJson("backend-offline",JSONObject().put("seconds",120))
   val generation=UUID.randomUUID().toString();val ids=listOf("$generation:0","$generation:1")
   val now=System.currentTimeMillis()
   val configuration=JSONObject().put("id",generation).put("nodeId",nodeId).put("revision",manifest.getString("revision")).put("installedAt",now-7200000)
    .put("regions",JSONArray().put(JSONObject().put("requestId",ids[0]).put("placeId",placeId).put("circleId",circleA)).put(JSONObject().put("requestId",ids[1]).put("placeId",placeId).put("circleId",circleB)))
   check(preferences.edit().putString("manifest",manifest.toString()).putString("installedConfiguration",configuration.toString()).putString("installedRevision",manifest.getString("revision")).putLong("installedUntil",now+3600000).putBoolean("needsInstall",false).putBoolean("hasRegions",true).putLong("notBefore:$placeId",now-600000).putLong("ruleRevision:$ruleId",rule.getLong("revision")).putBoolean("armed:$ruleId",false).commit())
   fun seed(presence:String,armed:Boolean,age:Long=300000) {
    val at=System.currentTimeMillis()-age
    check(preferences.edit().putString("state:$placeId",JSONObject().put("confirmed",presence).put("candidate","unknown").put("candidateAt",0).put("lastCapturedAt",at).toString()).putLong("confirmedAt:$placeId",at).putBoolean("armed:$ruleId",armed).remove("completed:$ruleId").remove("nativePending:$placeId").remove("diagnostic:$placeId").remove("reliable:$placeId").commit())
   }
   fun settled(){SystemClock.sleep(250)}
   fun recorded(code:String):Boolean {
    val rows=JSONArray(preferences.getString("history:$ruleId","[]"))
    return (0 until rows.length()).any{rows.getJSONObject(it).getString("code")==code}
   }
   // Initial inside, an unarmed callback and installation replay cannot fire.
   seed("inside",false);callback(1,ids);settled();assertTrue(events(ruleId).isEmpty());assertFalse(preferences.getBoolean("armed:$ruleId",false))
   seed("outside",false);callback(1,ids);settled();assertTrue(events(ruleId).isEmpty());assertTrue(recorded("native_enter_unarmed"))
   seed("unknown",false);callback(1,ids);settled();assertTrue(events(ruleId).isEmpty())
   assertTrue(recorded("native_event_initial"))
   // Unknown IDs and old-generation IDs must not be trusted as UUIDs/regions.
   seed("outside",true);callback(1,listOf(UUID.randomUUID().toString()));settled();assertTrue(events(ruleId).isEmpty())
   assertTrue(recorded("native_region_unknown"))
   callback(1,listOf("${UUID.randomUUID()}:0"));settled();assertTrue(events(ruleId).isEmpty())
   configuration.put("revision","replaced-fixture-revision")
   check(preferences.edit().putString("installedConfiguration",configuration.toString()).commit())
   callback(1,ids);settled();assertTrue(events(ruleId).isEmpty())
   assertTrue(recorded("native_configuration_stale"))
   configuration.put("revision",manifest.getString("revision"))
   configuration.put("nodeId",UUID.randomUUID().toString())
   check(preferences.edit().putString("installedConfiguration",configuration.toString()).commit())
   callback(1,ids);settled();assertTrue(events(ruleId).isEmpty())
   configuration.put("nodeId",nodeId)
   check(preferences.edit().putString("installedConfiguration",configuration.toString()).commit())
   val inactive=JSONObject(manifest.toString())
   inactive.getJSONArray("rules").getJSONObject(0).put("status","cancelled")
   check(preferences.edit().putString("manifest",inactive.toString()).commit())
   callback(1,ids);settled();assertTrue(events(ruleId).isEmpty())
   check(preferences.edit().putString("manifest",manifest.toString()).commit())
   callback(1,ids,error=1001);settled();assertTrue(events(ruleId).isEmpty())
   assertTrue(recorded("native_region_limit"))
   callback(4,ids);settled();assertTrue(events(ruleId).isEmpty())
   // A location older than the native-event delivery budget cannot create a late reminder.
   callback(1,ids,age=3600000);settled();assertTrue(events(ruleId).isEmpty())
   assertTrue(recorded("native_event_stale"))
   // Conflicting high-quality location cannot be accepted blindly.
   callback(1,ids,longitude=0.01);settled();assertTrue(events(ruleId).isEmpty())
   assertTrue(recorded("transition_contradictory_location"))
   seed("outside",true,age=2400000)
   callback(1,ids,accuracy=200.0);settled();assertTrue(events(ruleId).isEmpty())
   assertTrue(recorded("transition_needs_confirmation"))
   // Delay the pending confirmation beyond its budget; no acquisition is required
   // for the independent high-confidence callback tested next.
   // The OS scheduler delay is represented by an expired persisted deadline;
   // run the same production domain boundary called by the late worker. This
   // avoids requiring WorkManager to run at a precise wall-clock interval.
   val delayed=System.currentTimeMillis()-180000
   val pending=JSONObject().put("transition","enter").put("receivedAt",delayed).put("detectedAt",delayed).put("deadline",delayed+120000).put("baselineAt",now-300000).put("configurationId",generation)
   check(preferences.edit().putString("nativePending:$placeId",pending.toString()).commit())
   runtime.getMethod("confirmPending",Context::class.java).invoke(null,context)
   assertFalse(preferences.contains("nativePending:$placeId"));assertTrue(recorded("transition_expired"))
   assertEquals("outside",JSONObject(preferences.getString("state:$placeId",null)!!).getString("confirmed"))
   assertTrue(events(ruleId).isEmpty())
   shell("cmd battery set level 50");shell("cmd battery unplug")
   await("Framework battery unplugged before enabling saver"){context.registerReceiver(null,IntentFilter(Intent.ACTION_BATTERY_CHANGED))?.getIntExtra(BatteryManager.EXTRA_PLUGGED,-1)==0}
   for((index,saver)in listOf(true,false).withIndex()) {
    shell("cmd power set-mode ${if(saver)1 else 0}");shell("settings put global low_power ${if(saver)1 else 0}")
    if(!saver)shell("settings put global low_power_sticky 0")
    await("Framework power saver state"){context.getSystemService(PowerManager::class.java).isPowerSaveMode==saver}
    val before=events(ruleId).size
    if(index==0)seed("outside",true) else {
     // A repeating arrival is rearmed only by a real native EXIT from the
     // complete union; starting the next loop alone cannot rearm it.
     callback(2,ids,longitude=0.01)
     assertEquals(before,events(ruleId).size)
     assertEquals("outside",JSONObject(preferences.getString("state:$placeId",null)!!).getString("confirmed"))
     assertTrue(preferences.getBoolean("armed:$ruleId",false))
    }
    // No triggeringLocation: a trusted outside baseline and complete native
    // ENTER evidence are sufficient under the documented bounded policy.
    callback(1,ids,longitude=if(saver)null else 0.0)
    await("Native ENTER creates one durable occurrence"){events(ruleId).size==before+1}
    val event=events(ruleId).last();occurrences.add(event.getString("occurrenceId"));awaitFrameworkReminder(notifications,event.getString("occurrenceId"),1)
    assertEquals("inside",JSONObject(preferences.getString("state:$placeId",null)!!).getString("confirmed"))
    assertFalse(preferences.getBoolean("armed:$ruleId",false))
    // Repeated delivery and another member circle never create a second arrival.
    callback(1,ids,longitude=if(saver)null else 0.0);callback(1,listOf(ids[1]),longitude=0.00045);settled()
    assertEquals(before+1,events(ruleId).size)
    // Leaving only circle A while remaining in B preserves the logical place.
    callback(2,listOf(ids[0]),longitude=0.0008);settled()
    assertEquals("inside",JSONObject(preferences.getString("state:$placeId",null)!!).getString("confirmed"));assertFalse(preferences.getBoolean("armed:$ruleId",false))
   }
   val durable=events(ruleId)
   assertEquals(2,durable.size)
   val diagnostics=runtime.getMethod("diagnostics",Context::class.java).invoke(null,context) as String
   for(key in listOf("latitude","longitude","text","token","credential","stackTrace"))assertFalse("Private fields are absent from monitoring diagnostics",diagnostics.contains("\"$key\""))
   assertTrue(JSONArray(preferences.getString("history:$ruleId","[]")).length()<=12)
   fixtureJson("backend-offline",JSONObject().put("seconds",0))
   nativeFcm(context,nodeId,"geofence.monitor",JSONObject().put("operation","sync"))
   await("Native durable outbox reconciles after reconnect",timeoutMillis=60000){events(ruleId).isEmpty()}
   for(event in durable) {
    val replay=request("/geofences/events",credential,event)
    assertEquals("accepted",replay.getString("status"));assertTrue(replay.getBoolean("duplicate"))
   }
   android.util.Log.i("AegisNodeNativeTest","phase=geofence_receiver_native_transition_idempotent_saver_reconcile errorType=Checkpoint")
  }finally {
   fixtureJson("backend-offline",JSONObject().put("seconds",0))
   if(credential!=null && nodeId!=null)runCatching{request("/$nodeId/revoke",credential,JSONObject())}
   for(id in occurrences)notifications.cancel("reminder:$id:1",1)
   shell("cmd power set-mode 0");shell("cmd battery reset")
   for((key,value)in listOf("low_power" to previousSaver,"low_power_sticky" to previousSticky))if(value in setOf("0","1"))shell("settings put global $key $value") else shell("settings delete global $key")
   for((name,old)in snapshots)restore(context.getSharedPreferences(name,Context.MODE_PRIVATE),old)
  }
 }
}
