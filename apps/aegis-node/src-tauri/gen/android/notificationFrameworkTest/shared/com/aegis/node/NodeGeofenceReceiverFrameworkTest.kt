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
 * GMS delivery uses a controlled SafeParcel fixture; registration storage is seeded. The actual
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
 internal fun shell(command:String)=instrumentation.uiAutomation.executeShellCommand(command).use{fd->android.os.ParcelFileDescriptor.AutoCloseInputStream(fd).bufferedReader().use{it.readText()}}
 private fun broadcastBarrier(){shell("timeout 5 am wait-for-broadcast-barrier")}
 private fun battery()=context.registerReceiver(null,IntentFilter(Intent.ACTION_BATTERY_CHANGED))
 private fun batteryPlugged()=battery()?.getIntExtra(BatteryManager.EXTRA_PLUGGED,-1)?:-1
 private fun await(reason:String,timeoutMillis:Long=15000,condition:()->Boolean) {
  val end=SystemClock.elapsedRealtime()+timeoutMillis
  while(!condition() && SystemClock.elapsedRealtime()<end)SystemClock.sleep(25)
  assertTrue(reason,condition())
 }
 internal fun request(path:String,credential:String?=null,body:JSONObject?=null,method:String=if(body==null)"GET" else "POST"):JSONObject {
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
 internal fun announce(credential:String) {
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
 internal fun vault(node:JSONObject,credential:String) {
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
 /**
  * Controlled wire fixture matching play-services-location 21.3.0's geofence
  * serializer (request ID, expiry, circle, transitions, responsiveness, dwell).
  * Android writes the actual Parcel; the target SDK decodes it in the receiver.
  * Keep GMS classes out of the independent instrumentation APK: their original
  * names would shadow the target's optimized GMS methods after R8.
  */
 private fun regionParcel(id:String):ByteArray {
  val parcel=Parcel.obtain()
  fun begin(field:Int):Int {parcel.writeInt(0xffff0000.toInt() or field);parcel.writeInt(0);return parcel.dataPosition()}
  fun end(start:Int) {val finish=parcel.dataPosition();parcel.setDataPosition(start-4);parcel.writeInt(finish-start);parcel.setDataPosition(finish)}
  fun header(field:Int,size:Int)=parcel.writeInt((size shl 16) or field)
  try {
   val objectStart=begin(20293)
   val requestStart=begin(1);parcel.writeString(id);end(requestStart)
   header(2,8);parcel.writeLong(SystemClock.elapsedRealtime()+3600000)
   header(3,4);parcel.writeInt(1) // Circular region type, represented as a Parcel int.
   header(4,8);parcel.writeDouble(0.0)
   header(5,8);parcel.writeDouble(0.0)
   header(6,4);parcel.writeFloat(65f)
   header(7,4);parcel.writeInt(3) // ENTER | EXIT.
   header(8,4);parcel.writeInt(0)
   header(9,4);parcel.writeInt(-1)
   end(objectStart)
   return parcel.marshall()
  }finally{parcel.recycle()}
 }
 internal fun callback(transition:Int,ids:List<String>,longitude:Double?=0.0,accuracy:Double=13.0,age:Long=0,error:Int=-1) {
  val parcels=ArrayList<ByteArray>()
  for(id in ids)parcels.add(regionParcel(id))
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
  val snapshots=listOf("keyring-aegis-node-identity-v1","aegis-node-push-v1","aegis-geofences-v1","aegis-hybrid-sessions-v1").associateWith{context.getSharedPreferences(it,Context.MODE_PRIVATE).all.toMap()}
  val vaultAlias="keyring-aegis-node-identity-v1"
  val keyStore=KeyStore.getInstance("AndroidKeyStore").apply{load(null)}
  val previousVaultKey=keyStore.containsAlias(vaultAlias)
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
   val positionPreferences=context.getSharedPreferences("aegis-node-push-v1",Context.MODE_PRIVATE)
   val manifest=request("/geofences/manifest",credential)
   // Offline work cannot overwrite the fixture with an unrelated acquisition.
   fixtureJson("backend-offline",JSONObject().put("seconds",300))
   val generation=UUID.randomUUID().toString();val ids=listOf("$generation:0","$generation:1")
   val now=System.currentTimeMillis()
   val configuration=JSONObject().put("id",generation).put("nodeId",nodeId).put("revision",manifest.getString("revision")).put("installedAt",now-7200000)
    .put("regions",JSONArray().put(JSONObject().put("requestId",ids[0]).put("placeId",placeId).put("circleId",circleA)).put(JSONObject().put("requestId",ids[1]).put("placeId",placeId).put("circleId",circleB)))
   check(preferences.edit().putString("manifest",manifest.toString()).putString("installedConfiguration",configuration.toString()).putString("installedRevision",manifest.getString("revision")).putLong("installedUntil",now+3600000).putBoolean("needsInstall",false).putBoolean("hasRegions",true).putLong("notBefore:$placeId",now-600000).putLong("ruleRevision:$ruleId",rule.getLong("revision")).putBoolean("armed:$ruleId",false).commit())
   fun seed(presence:String,armed:Boolean,age:Long=300000) {
    val at=System.currentTimeMillis()-age
    check(preferences.edit().putString("state:$placeId",JSONObject().put("confirmed",presence).put("candidate","unknown").put("candidateAt",0).put("lastCapturedAt",at).toString()).putLong("confirmedAt:$placeId",at).putBoolean("armed:$ruleId",armed).remove("completed:$ruleId").remove("nativePending:$placeId").remove("diagnostic:$placeId").remove("reliable:$placeId").remove("hybridFix:$placeId").commit())
   }
   fun settled(){SystemClock.sleep(250)}
   fun recorded(code:String):Boolean {
    val rows=JSONArray(preferences.getString("history:$ruleId","[]"))
    return (0 until rows.length()).any{rows.getJSONObject(it).getString("code")==code}
   }
   fun requireRecorded(code:String) {
    val rows=JSONArray(preferences.getString("history:$ruleId","[]"))
    val codes=(0 until rows.length()).map{rows.getJSONObject(it).optString("code")}.filter{it.matches(Regex("[a-z_]{1,64}"))}.joinToString(",")
    assertTrue("Expected closed history code=$code; observed=$codes",recorded(code))
   }
   // Initial inside, an unarmed callback and installation replay cannot fire.
   seed("inside",false);callback(1,ids);settled();assertTrue(events(ruleId).isEmpty());assertFalse(preferences.getBoolean("armed:$ruleId",false))
   seed("outside",false);callback(1,ids);settled();assertTrue(events(ruleId).isEmpty());requireRecorded("native_enter_unarmed")
   seed("unknown",false);callback(1,ids);settled();assertTrue(events(ruleId).isEmpty())
   requireRecorded("native_event_initial")
   // Unknown IDs and old-generation IDs must not be trusted as UUIDs/regions.
   seed("outside",true);callback(1,listOf(UUID.randomUUID().toString()));settled();assertTrue(events(ruleId).isEmpty())
   requireRecorded("native_region_unknown")
   callback(1,listOf("${UUID.randomUUID()}:0"));settled();assertTrue(events(ruleId).isEmpty())
   configuration.put("revision","replaced-fixture-revision")
   check(preferences.edit().putString("installedConfiguration",configuration.toString()).commit())
   callback(1,ids);settled();assertTrue(events(ruleId).isEmpty())
   requireRecorded("native_configuration_stale")
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
   requireRecorded("native_region_limit")
   callback(4,ids);settled();assertTrue(events(ruleId).isEmpty())
   // A location older than the native-event delivery budget cannot create a late reminder.
   callback(1,ids,age=3600000);settled();assertTrue(events(ruleId).isEmpty())
   requireRecorded("native_event_stale")
   // Conflicting high-quality location cannot be accepted blindly.
   callback(1,ids,longitude=0.01);settled();assertTrue(events(ruleId).isEmpty())
   requireRecorded("transition_contradictory_location")
   // Forty minutes predating the current real-area activation is not a valid
   // baseline. Keep that protection distinct from a valid but old baseline.
   seed("outside",true,age=2400000)
   callback(1,ids,accuracy=200.0);settled();assertTrue(events(ruleId).isEmpty())
   requireRecorded("native_baseline_unreliable")
   val currentNotBefore=preferences.getLong("notBefore:$placeId",0)
   check(preferences.edit().putLong("notBefore:$placeId",configuration.getLong("installedAt")).commit())
   seed("outside",true,age=2400000)
   callback(1,ids,accuracy=200.0);settled();assertTrue(events(ruleId).isEmpty())
   requireRecorded("transition_needs_confirmation")
   check(preferences.edit().putLong("notBefore:$placeId",currentNotBefore).commit())
   // Delay the pending confirmation beyond its budget; no acquisition is required
   // for the independent high-confidence callback tested next.
   // The OS scheduler delay is represented by an expired persisted deadline;
   // run the same production domain boundary called by the late worker. This
   // avoids requiring WorkManager to run at a precise wall-clock interval.
   val delayed=System.currentTimeMillis()-180000
   val pending=JSONObject().put("transition","enter").put("receivedAt",delayed).put("detectedAt",delayed).put("deadline",delayed+120000).put("baselineAt",now-300000).put("configurationId",generation)
   check(preferences.edit().putString("nativePending:$placeId",pending.toString()).commit())
   runtime.getMethod("confirmPending",Context::class.java).invoke(null,context)
   assertFalse(preferences.contains("nativePending:$placeId"));requireRecorded("transition_expired")
   assertEquals("outside",JSONObject(preferences.getString("state:$placeId",null)!!).getString("confirmed"))
   assertTrue(events(ruleId).isEmpty())
   // Same CTS setup as the existing residential adapter test: the sticky
   // battery Intent can update before PowerManager handles the unplug broadcast.
   shell("cmd battery set level 50");shell("cmd battery unplug")
   await("Framework battery unplugged before enabling saver"){batteryPlugged()==0}
   assertEquals(50,battery()?.getIntExtra(BatteryManager.EXTRA_LEVEL,-1))
   broadcastBarrier()
   for((index,saver)in listOf(true,false).withIndex()) {
    shell("cmd power set-mode ${if(saver)1 else 0}");broadcastBarrier()
    shell("settings put global low_power ${if(saver)1 else 0}")
    if(!saver)shell("settings put global low_power_sticky 0")
    broadcastBarrier()
    val powerManager=context.getSystemService(PowerManager::class.java)
    val saverDeadline=SystemClock.elapsedRealtime()+15000
    while(powerManager.isPowerSaveMode!=saver && SystemClock.elapsedRealtime()<saverDeadline)SystemClock.sleep(50)
    assertEquals("Framework power saver level=50 plugged=${batteryPlugged()}",saver,powerManager.isPowerSaveMode)
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
    val previousObservation=positionPreferences.getString("lastObservation",null)
    val previousObservationPending=positionPreferences.getBoolean("observationPending",false)
    callback(1,ids,longitude=if(saver)null else 0.0)
    await("Native ENTER creates one durable occurrence"){events(ruleId).size==before+1}
    val event=events(ruleId).last();occurrences.add(event.getString("occurrenceId"));awaitFrameworkReminder(notifications,event.getString("occurrenceId"),1)
    assertEquals("inside",JSONObject(preferences.getString("state:$placeId",null)!!).getString("confirmed"))
    assertFalse(preferences.getBoolean("armed:$ruleId",false))
    val diagnostic=JSONObject(preferences.getString("diagnostic:$placeId",null)?:error("native_diagnostic_missing"))
    if(saver) {
     assertEquals("unknown",diagnostic.getString("presence"))
     assertFalse("Receipt time cannot impersonate a missing position capture",diagnostic.has("lastObservationAt"))
     assertTrue(diagnostic.isNull("accuracyMeters"))
     assertTrue("A native event without coordinates cannot queue a synthetic position",previousObservation==positionPreferences.getString("lastObservation",null))
     assertEquals(previousObservationPending,positionPreferences.getBoolean("observationPending",false))
    } else {
     assertEquals("inside",diagnostic.getString("presence"))
     assertEquals(13.0,diagnostic.getDouble("accuracyMeters"),0.0)
     assertTrue(diagnostic.getLong("lastObservationAt")<=System.currentTimeMillis())
     assertTrue("A compatible fresh native fix queues the existing auxiliary location outbox",positionPreferences.getBoolean("observationPending",false))
     assertEquals(13.0,JSONObject(positionPreferences.getString("lastObservation",null)?:error("native_location_outbox_missing")).getDouble("accuracyMeters"),0.0)
    }
    // Repeated delivery and another member circle never create a second arrival.
    callback(1,ids,longitude=if(saver)null else 0.0);callback(1,listOf(ids[1]),longitude=0.00045);settled()
    assertEquals(before+1,events(ruleId).size)
    // Leaving only circle A while remaining in B preserves the logical place.
    callback(2,listOf(ids[0]),longitude=0.0008);settled()
    assertEquals("inside",JSONObject(preferences.getString("state:$placeId",null)!!).getString("confirmed"));assertFalse(preferences.getBoolean("armed:$ruleId",false))
   }
   assertEquals(2,events(ruleId).size)
   // Actual Android Service lifecycle + receiver path on API 26/35 and R8 target.
   // Synthetic FLP seam isolates service permissions, cancellation and persistence;
   // this does not certify physical Google geofence delivery or real GPS availability.
   instrumentation.uiAutomation.grantRuntimePermission(context.packageName,android.Manifest.permission.ACCESS_COARSE_LOCATION)
   instrumentation.uiAutomation.grantRuntimePermission(context.packageName,android.Manifest.permission.ACCESS_FINE_LOCATION)
   if(Build.VERSION.SDK_INT>=29)instrumentation.uiAutomation.grantRuntimePermission(context.packageName,android.Manifest.permission.ACCESS_BACKGROUND_LOCATION)
   val hybridMethod=runtime.getMethod("hybridSessionFixture",Context::class.java,String::class.java,String::class.java,String::class.java)
   fun hybrid(operation:String,scenario:String="real")=JSONObject(hybridMethod.invoke(null,context,operation,placeId,scenario) as String)
   fun activeSession()=hybrid("snapshot").getJSONArray("sessions").optJSONObject(0)
   fun stopped(){await("Temporary location service removes its foreground notification"){!hybrid("snapshot").getBoolean("running") && notifications.activeNotifications.none{it.id==7351}}}
   val operationalId="$generation:operational"
   configuration.getJSONArray("regions").put(JSONObject().put("requestId",operationalId).put("placeId",placeId).put("circleId","operational-$circleA").put("kind","operational"))
   check(preferences.edit().putString("installedConfiguration",configuration.toString()).commit())
   fun approach(scenario:String) {
    hybrid("reset");stopped();seed("outside",true)
    hybrid("configure",scenario)
    check(positionPreferences.edit().remove("lastObservation").remove("observationScopeId").putBoolean("observationPending",false).commit())
    check(preferences.edit().remove("nativeSeen").putBoolean("operationalInside:$placeId",false).putBoolean("operationalRegion:$operationalId",false).commit())
    callback(1,listOf(operationalId),longitude=null)
   }
   approach("outside")
   await("Operational ENTER starts actual location foreground service"){hybrid("snapshot").getBoolean("running") && (activeSession()?.optInt("acquired",0)?:0)>=1}
   assertEquals("Approach alone never creates an occurrence",2,events(ruleId).size)
   assertEquals(1,hybrid("snapshot").getInt("serviceStarts"))
   hybrid("expire");stopped()
   assertEquals("monitoring_session_expired",activeSession()?.getString("status"))
   val attemptsAfterExpiry=activeSession()?.getInt("attempts")
   callback(1,listOf(operationalId),longitude=null);settled()
   assertFalse(hybrid("snapshot").getBoolean("running"));assertEquals(attemptsAfterExpiry,activeSession()?.getInt("attempts"))
   // Provider/Android denial is a result, never a fictional coordinate fix.
   approach("denied")
   await("Denied acquisition is recorded without transition"){activeSession()?.optString("lastResult")=="permission_denied"}
   assertEquals(0,activeSession()?.optInt("acquired"));assertEquals(2,events(ruleId).size)
   hybrid("expire");stopped()
   // Start denial is injected at the production permission gate. Revoking runtime
   // permissions with pm during instrumentation kills the very target under test.
   approach("start_denied")
   await("Permission/start rejection closes the actual service safely"){activeSession()?.optString("status")=="monitoring_start_denied"}
   stopped();assertEquals(0,activeSession()?.optInt("attempts"));assertEquals(2,events(ruleId).size)
   approach("inside")
   await("Two independent bounded session fixes create one real arrival",timeoutMillis=45000){events(ruleId).size==3}
   stopped()
   assertEquals("monitoring_transition_accepted",activeSession()?.getString("status"))
   assertTrue((activeSession()?.optInt("attempts")?:100)<=6)
   val hybridEvent=events(ruleId).last();occurrences.add(hybridEvent.getString("occurrenceId"))
   awaitFrameworkReminder(notifications,hybridEvent.getString("occurrenceId"),1)
   callback(1,listOf(operationalId),longitude=null);settled();assertEquals(3,events(ruleId).size)
   requireRecorded("real_area_enter_accepted")
   hybrid("reset");stopped()
   android.util.Log.i("AegisNodeNativeTest","phase=hybrid_service_lifecycle_bounded_denied_arrival errorType=Checkpoint")
   val durable=events(ruleId)
   assertEquals(3,durable.size)
   val diagnostics=runtime.getMethod("diagnostics",Context::class.java).invoke(null,context) as String
   for(key in listOf("latitude","longitude","text","token","credential","stackTrace"))assertFalse("Private fields are absent from monitoring diagnostics",diagnostics.contains("\"$key\""))
   assertTrue(JSONArray(preferences.getString("history:$ruleId","[]")).length()<=32)
   fixtureJson("backend-offline",JSONObject().put("seconds",0))
   nativeFcm(context,nodeId,"geofence.monitor",JSONObject().put("operation","sync"))
   await("Native durable outbox reconciles after reconnect",timeoutMillis=60000){events(ruleId).isEmpty()}
   for(event in durable) {
    val replay=request("/geofences/events",credential,event)
    assertEquals("accepted",replay.getString("status"));assertTrue(replay.getBoolean("duplicate"))
   }
   exerciseHybridAdvancedFramework(context,nodeId,credential,this)
   android.util.Log.i("AegisNodeNativeTest","phase=geofence_receiver_native_transition_idempotent_saver_reconcile errorType=Checkpoint")
  }finally {
   runCatching{runtime.getMethod("hybridSessionFixture",Context::class.java,String::class.java,String::class.java,String::class.java).invoke(null,context,"stop","","real")}
   shell("pm grant ${context.packageName} android.permission.ACCESS_FINE_LOCATION")
   if(Build.VERSION.SDK_INT>=29)shell("pm grant ${context.packageName} android.permission.ACCESS_BACKGROUND_LOCATION")
   fixtureJson("backend-offline",JSONObject().put("seconds",0))
   if(credential!=null && nodeId!=null)runCatching{request("/$nodeId/revoke",credential,JSONObject())}
   for(id in occurrences)notifications.cancel("reminder:$id:1",1)
   shell("cmd power set-mode 0");shell("cmd battery reset")
   for((key,value)in listOf("low_power" to previousSaver,"low_power_sticky" to previousSticky))if(value in setOf("0","1"))shell("settings put global $key $value") else shell("settings delete global $key")
   for((name,old)in snapshots)restore(context.getSharedPreferences(name,Context.MODE_PRIVATE),old)
   // A fresh-install fixture creates its own temporary key. Restoring empty
   // preferences must also restore key absence so Rust can create its vault.
   // An existing installation key is preserved byte for byte.
   if(!previousVaultKey && keyStore.containsAlias(vaultAlias))keyStore.deleteEntry(vaultAlias)
   assertEquals("Receiver fixture preserves the original vault key lifecycle",previousVaultKey,keyStore.containsAlias(vaultAlias))
  }
 }
}
