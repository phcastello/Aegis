package com.aegis.node

import android.app.NotificationManager
import android.content.Context
import android.os.BatteryManager
import android.os.Build
import android.os.PowerManager
import android.os.SystemClock
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.security.SecureRandom
import java.util.UUID
import android.util.Base64

/** Ten minutes per scenario, including the active segment and its bounded cleanup.
 * Provider delivery is controlled; service, FLP adapter requests, OS lifecycle,
 * GeofencingClient registration, SQLite and authenticated HTTP are exercised.
 * These windows measure emulator work, not a physical battery or movement. */
@RunWith(AndroidJUnit4::class)
class NodeHybridEfficiencyFrameworkTest {
 private val instrumentation=InstrumentationRegistry.getInstrumentation()
 private val context=instrumentation.targetContext
 private val helper=NodeGeofenceReceiverFrameworkTest()
 private val runtime=Class.forName("com.aegis.node.NodeGeofenceRuntime",true,context.classLoader)
 private val preferences=context.getSharedPreferences("aegis-geofences-v1",Context.MODE_PRIVATE)
 private fun session(op:String,place:String="",scenario:String="real")=JSONObject(runtime.getMethod("hybridSessionFixture",Context::class.java,String::class.java,String::class.java,String::class.java).invoke(null,context,op,place,scenario) as String)
 private fun await(reason:String,timeout:Long=20000,condition:()->Boolean) {
  val end=SystemClock.elapsedRealtime()+timeout
  while(!condition()&&SystemClock.elapsedRealtime()<end)Thread.sleep(50)
  assertTrue(reason,condition())
 }
 private fun saver(enabled:Boolean) {
  helper.shell("cmd battery set level 50");helper.shell("cmd battery unplug")
  await("Battery unplugged") {context.registerReceiver(null,android.content.IntentFilter(android.content.Intent.ACTION_BATTERY_CHANGED))?.getIntExtra(BatteryManager.EXTRA_PLUGGED,-1)==0}
  helper.shell("timeout 5 am wait-for-broadcast-barrier")
  helper.shell("cmd power set-mode ${if(enabled)1 else 0}")
  helper.shell("settings put global low_power ${if(enabled)1 else 0}")
  if(!enabled)helper.shell("settings put global low_power_sticky 0")
  helper.shell("timeout 5 am wait-for-broadcast-barrier")
  await("Framework battery saver matches the scenario") {context.getSystemService(PowerManager::class.java).isPowerSaveMode==enabled}
 }
 @Test fun sixEquivalentTenMinuteWindows() {
  assertEquals(35,Build.VERSION.SDK_INT)
  val args=InstrumentationRegistry.getArguments()
  val duration=args.getString("hybridWindowMs")?.toLong()?:600000L
  require(duration in 600000L..900000L)
  val code=args.getString("geofencePairingCode")?:error("hybrid_measurement_bootstrap_missing")
  var credential:String?=null;var nodeId:String?=null
  val previousSaver=helper.shell("settings get global low_power").trim()
  val previousSticky=helper.shell("settings get global low_power_sticky").trim()
  val rules=mutableMapOf<String,Long>()
  try {
   val attempt=UUID.randomUUID().toString()
   val receipt=helper.request("/pair",body=JSONObject().put("attemptId",attempt).put("code",code).put("recoveryKey",Base64.encodeToString(ByteArray(32).also{SecureRandom().nextBytes(it)},Base64.NO_WRAP or Base64.URL_SAFE or Base64.NO_PADDING)).put("name","Hybrid efficiency fixture").put("platform","android").put("appVersion","0.0.0-framework-fixture").put("protocolVersion",1))
   credential=receipt.getString("credential")
   val node=helper.request("/pair/finalize",body=JSONObject().put("attemptId",attempt).put("credential",credential));nodeId=node.getString("id")
   helper.announce(credential);helper.vault(node,credential)
   helper.request("/location/settings",credential,JSONObject().put("nodeId",nodeId).put("sourceNodeId",nodeId).put("mode","dynamic"),"PUT")
   helper.shell("pm grant com.aegis.node android.permission.ACCESS_COARSE_LOCATION")
   helper.shell("pm grant com.aegis.node android.permission.ACCESS_FINE_LOCATION")
   helper.shell("pm grant com.aegis.node android.permission.ACCESS_BACKGROUND_LOCATION")
   saver(false)
   val notifications=context.getSystemService(NotificationManager::class.java)
   fun mark(phase:String,event:String) {
    val nonce=UUID.randomUUID().toString()
    val marker=File(context.cacheDir,"aegis-idle-snapshot-$nonce.ack");marker.writeText("pending")
    try {
     android.util.Log.i("AegisHybridMeasure",JSONObject().put("phase",phase).put("event",event).put("elapsedRealtimeMs",SystemClock.elapsedRealtime()).put("snapshotNonce",nonce).put("session",session("snapshot")).toString())
     await("Host captures CPU and network boundary",15000){!marker.exists()}
    } finally {marker.delete()}
   }
   fun prepare():Triple<String,String,String> {
    for((id,revision) in rules)helper.request("/geofences/$id/actions",credential,JSONObject().put("action","cancel").put("expectedRevision",revision))
    rules.clear()
    session("stop");await("Previous monitoring service stopped"){!session("snapshot").optBoolean("running")}
    val place=helper.request("/places",credential,JSONObject().put("name","Hybrid efficiency synthetic home").put("type","property").put("aliases",JSONArray()).put("circles",JSONArray().put(JSONObject().put("id",UUID.randomUUID().toString()).put("latitude",0).put("longitude",0).put("radiusMeters",50))).put("monitoringEnvelope",JSONObject().put("enabled",true).put("paddingMeters",150).put("confirmed",true).put("expectedRevision",0)))
    val placeId=place.getString("id")
    val rule=helper.request("/geofences",credential,JSONObject().put("text","Hybrid efficiency fixture").put("placeIds",JSONArray().put(placeId)).put("observerNodeId",nodeId).put("transition","enter").put("repeat",true).put("timeZoneId","UTC"))
    val ruleId=rule.getString("id");rules[ruleId]=rule.getLong("revision")
    val manifest=helper.request("/geofences/manifest",credential)
    check(preferences.edit().putString("manifest",manifest.toString()).commit())
    runtime.getMethod("restore",Context::class.java).invoke(null,context)
    await("Real GeofencingClient installs both approved regions",60000) {
     preferences.getString("installedRevision",null)==manifest.getString("revision")&&!preferences.getBoolean("needsInstall",true)
    }
    val config=JSONObject(preferences.getString("installedConfiguration",null)?:error("hybrid_install_missing"))
    val rows=config.getJSONArray("regions")
    val operational=(0 until rows.length()).map{rows.getJSONObject(it)}.first{it.optString("placeId")==placeId&&it.optString("kind")=="operational"}.getString("requestId")
    val outside=System.currentTimeMillis()-60000
    check(preferences.edit().putString("state:$placeId",JSONObject().put("confirmed","outside").put("candidate","unknown").put("candidateAt",0).put("lastCapturedAt",outside).toString()).putLong("confirmedAt:$placeId",outside).putLong("notBefore:$placeId",outside-1000).putLong("ruleRevision:$ruleId",rule.getLong("revision")).putBoolean("armed:$ruleId",true).remove("hybridFix:$placeId").remove("reliable:$placeId").remove("diagnostic:$placeId").commit())
    session("reset");session("configure",placeId,"outside")
    return Triple(placeId,ruleId,operational)
   }
   runtime.getMethod("deactivate",Context::class.java).invoke(null,context)
   session("reset")
   mark("idle_no_geofences","start");Thread.sleep(duration);mark("idle_no_geofences","end")
   assertEquals(0,session("snapshot").optInt("serviceStarts"))
   for(phase in listOf("armed_stationary","approach","session_active","session_ended","background_saver")) {
    val (placeId,_,operational)=prepare()
    saver(phase=="background_saver")
    if(phase in setOf("session_active","background_saver"))session("configure",placeId,"real")
    if(phase=="session_ended") {
     helper.callback(1,listOf(operational),longitude=0.0012)
     await("Session starts before the ended control"){session("snapshot").optBoolean("running")}
     session("expire",placeId)
     await("Expired service and notification removed"){!session("snapshot").optBoolean("running")&&notifications.activeNotifications.none{it.id==7351}}
    }
    mark(phase,"start")
    val start=SystemClock.elapsedRealtime()
    if(phase in setOf("approach","session_active","background_saver")) {
     mark(phase,"active_start")
     helper.callback(1,listOf(operational),longitude=0.0012)
     await("Operational ENTER starts the bounded foreground service"){session("snapshot").optBoolean("running")}
     if(phase=="approach") {
      Thread.sleep(15000)
      session("configure",placeId,"inside")
      val regions=JSONObject(preferences.getString("installedConfiguration",null)!!).getJSONArray("regions")
      val inner=(0 until regions.length()).map{regions.getJSONObject(it)}.first{it.optString("placeId")==placeId&&it.optString("kind","real")=="real"}.getString("requestId")
      helper.callback(1,listOf(inner),longitude=0.0)
     }
     await("Session ends automatically within its hard budget",285000){!session("snapshot").optBoolean("running")}
     assertTrue("Temporary notification removed",notifications.activeNotifications.none{it.id==7351})
     mark(phase,"active_end")
    }
    val remaining=duration-(SystemClock.elapsedRealtime()-start)
    if(remaining>0)Thread.sleep(remaining)
    mark(phase,"end")
    val snapshot=session("snapshot")
    assertFalse("No tracking after the budget",snapshot.optBoolean("running"))
    val sessions=snapshot.optJSONArray("sessions")?:JSONArray()
    for(i in 0 until sessions.length()) {
     val row=sessions.getJSONObject(i)
     assertTrue("Acquisition budget",row.optInt("attempts")<=9)
     assertTrue("Persisted session deadline",row.getLong("deadline")-row.getLong("startedAt")<=270000)
    }
    if(phase=="armed_stationary")assertEquals("Stationary registration starts no monitoring",0,snapshot.optInt("serviceStarts"))
   }
  }finally {
   runCatching{session("stop")}
   runCatching{runtime.getMethod("deactivate",Context::class.java).invoke(null,context)}
   if(credential!=null&&nodeId!=null)runCatching{helper.request("/$nodeId/revoke",credential,JSONObject())}
   helper.shell("cmd power set-mode 0");helper.shell("cmd battery reset")
   for((key,value)in listOf("low_power" to previousSaver,"low_power_sticky" to previousSticky))if(value in setOf("0","1"))helper.shell("settings put global $key $value")else helper.shell("settings delete global $key")
  }
 }
}
