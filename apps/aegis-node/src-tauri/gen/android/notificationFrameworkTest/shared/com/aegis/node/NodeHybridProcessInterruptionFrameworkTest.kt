package com.aegis.node

import android.Manifest
import android.app.NotificationManager
import android.content.Context
import android.content.pm.PackageManager
import android.database.sqlite.SQLiteDatabase
import android.os.Build
import android.os.SystemClock
import android.util.Base64
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.security.KeyStore
import java.security.SecureRandom
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec

/** Host runs prepare, force-stops the real target process, then runs verify.
 * A controlled FLP result is used only after actual Android permission denial.
 * No Activity/Rust startup or synthetic session restart hides process death. */
@RunWith(AndroidJUnit4::class)
class NodeHybridProcessInterruptionFrameworkTest {
 private val instrumentation=InstrumentationRegistry.getInstrumentation()
 private val context=instrumentation.targetContext
 private val helper=NodeGeofenceReceiverFrameworkTest()
 private val runtime=Class.forName("com.aegis.node.NodeGeofenceRuntime",true,context.classLoader)
 private val preferences=context.getSharedPreferences("aegis-geofences-v1",Context.MODE_PRIVATE)
 private val sessionPreferences=context.getSharedPreferences("aegis-hybrid-sessions-v1",Context.MODE_PRIVATE)
 private val vaultAlias="keyring-aegis-node-identity-v1"
 private val vaultEntry="installation\uFEFF@\uFEFFcom.aegis.node.identity.v1"
 private val marker=File(context.cacheDir,"aegis-hybrid-process-fixture.json")
 private val preferenceNames=listOf(vaultAlias,"aegis-node-push-v1","aegis-geofences-v1","aegis-hybrid-sessions-v1")
 private fun session(operation:String,place:String="",scenario:String="real")=JSONObject(runtime.getMethod("hybridSessionFixture",Context::class.java,String::class.java,String::class.java,String::class.java).invoke(null,context,operation,place,scenario) as String)
 private fun await(reason:String,condition:()->Boolean) {
  val end=SystemClock.elapsedRealtime()+20000
  while(!condition() && SystemClock.elapsedRealtime()<end)SystemClock.sleep(50)
  assertTrue(reason,condition())
 }
 private fun snapshotPreferences():JSONObject=JSONObject().apply {
  for(name in preferenceNames)put(name,JSONObject().apply{for((key,value)in context.getSharedPreferences(name,Context.MODE_PRIVATE).all)put(key,JSONObject().put("type",when(value){is Long->"long";is Int->"int";is Boolean->"boolean";is Float->"float";is Set<*>->"set";else->"string"}).put("value",if(value is Set<*>)JSONArray(value.toList())else value))})
 }
 private fun restorePreferences(saved:JSONObject) {
  for(name in preferenceNames) {
   val edit=context.getSharedPreferences(name,Context.MODE_PRIVATE).edit().clear();val rows=saved.getJSONObject(name)
   for(key in rows.keys()) {val row=rows.getJSONObject(key);when(row.getString("type")) {
    "long"->edit.putLong(key,row.getLong("value"));"int"->edit.putInt(key,row.getInt("value"));"boolean"->edit.putBoolean(key,row.getBoolean("value"));"float"->edit.putFloat(key,row.getDouble("value").toFloat());"set"->edit.putStringSet(key,row.getJSONArray("value").let{a->(0 until a.length()).map{a.getString(it)}.toSet()});else->edit.putString(key,row.getString("value"))
   }}
   check(edit.commit())
  }
 }
 private fun fixtureCredential():String {
  val bytes=Base64.decode(context.getSharedPreferences(vaultAlias,Context.MODE_PRIVATE).getString(vaultEntry,null),Base64.DEFAULT)
  val key=KeyStore.getInstance("AndroidKeyStore").apply{load(null)}.getKey(vaultAlias,null)
  val cipher=Cipher.getInstance("AES/GCM/NoPadding").apply{init(Cipher.DECRYPT_MODE,key,GCMParameterSpec(128,bytes.copyOfRange(1,13)))}
  return JSONObject(String(cipher.doFinal(bytes.copyOfRange(13,bytes.size)))).getString("credential")
 }
 private fun occurrenceCount():Int {
  val file=context.getDatabasePath("aegis-geofence-events-v1.db");if(!file.exists())return 0
  return SQLiteDatabase.openDatabase(file.path,null,SQLiteDatabase.OPEN_READONLY).use{db->db.rawQuery("SELECT COUNT(*) FROM events",null).use{it.moveToFirst();it.getInt(0)}}
 }
 @Test fun prepareSessionBeforeProcessDeath() {
  assertFalse("Host revokes permission before launching this test process",context.checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION)==PackageManager.PERMISSION_GRANTED)
  val code=InstrumentationRegistry.getArguments().getString("processPairingCode")?:error("process_fixture_bootstrap_missing")
  val original=snapshotPreferences();val keyStore=KeyStore.getInstance("AndroidKeyStore").apply{load(null)}
  val saved=JSONObject().put("originalPreferences",original).put("originalKeyPresent",keyStore.containsAlias(vaultAlias))
  marker.writeText(saved.toString())
  val attempt=UUID.randomUUID().toString()
  val pair=helper.request("/pair",body=JSONObject().put("attemptId",attempt).put("code",code).put("recoveryKey",Base64.encodeToString(ByteArray(32).also{SecureRandom().nextBytes(it)},Base64.NO_WRAP or Base64.URL_SAFE or Base64.NO_PADDING)).put("name","Process interruption fixture").put("platform","android").put("appVersion","0.0.0-framework-fixture").put("protocolVersion",1))
  val credential=pair.getString("credential")
  val node=helper.request("/pair/finalize",body=JSONObject().put("attemptId",attempt).put("credential",credential));val nodeId=node.getString("id")
  helper.announce(credential);helper.vault(node,credential)
  helper.request("/location/settings",credential,JSONObject().put("nodeId",nodeId).put("sourceNodeId",nodeId).put("mode","dynamic"),"PUT")
  val place=helper.request("/places",credential,JSONObject().put("name","Process interruption synthetic home").put("type","property").put("aliases",JSONArray()).put("circles",JSONArray().put(JSONObject().put("id",UUID.randomUUID().toString()).put("latitude",0).put("longitude",0).put("radiusMeters",50))).put("monitoringEnvelope",JSONObject().put("enabled",true).put("paddingMeters",150).put("confirmed",true).put("expectedRevision",0)))
  val placeId=place.getString("id")
  val rule=helper.request("/geofences",credential,JSONObject().put("text","Process interruption synthetic rule").put("placeIds",JSONArray().put(placeId)).put("observerNodeId",nodeId).put("transition","enter").put("repeat",true).put("timeZoneId","UTC"))
  val ruleId=rule.getString("id");val manifest=helper.request("/geofences/manifest",credential)
  // Pending worker reconciliation must not replace the controlled installed allowlist.
  fixtureJson("backend-offline",JSONObject().put("seconds",120))
  val now=System.currentTimeMillis();val generation=UUID.randomUUID().toString();val operational="$generation:operational"
  val configuration=JSONObject().put("id",generation).put("nodeId",nodeId).put("revision",manifest.getString("revision")).put("installedAt",now-120000).put("regions",JSONArray().put(JSONObject().put("requestId",operational).put("placeId",placeId).put("circleId",place.getJSONObject("monitoringEnvelope").getJSONArray("circles").getJSONObject(0).getString("id")).put("kind","operational")))
  check(preferences.edit().putString("manifest",manifest.toString()).putString("installedConfiguration",configuration.toString()).putString("installedRevision",manifest.getString("revision")).putBoolean("needsInstall",false).putBoolean("hasRegions",true).putLong("installedUntil",now+3600000).putLong("ruleRevision:$ruleId",rule.getLong("revision")).putBoolean("armed:$ruleId",true).putString("state:$placeId",JSONObject().put("confirmed","outside").put("candidate","unknown").put("candidateAt",0).put("lastCapturedAt",now-60000).toString()).putLong("notBefore:$placeId",now-120000).putLong("confirmedAt:$placeId",now-60000).commit())
  // Actual missing Android permissions reject the receiver's FGS preflight;
  // no service is requested and no injected start-denied adapter is involved.
  session("reset");session("configure",placeId,"real")
  helper.callback(1,listOf(operational),longitude=null)
  await("Missing Android permission closes the service with no invented fix") {
   val row=session("snapshot").getJSONArray("sessions").optJSONObject(0)
   row?.optString("status") in setOf("monitoring_start_denied","monitoring_location_blocked") && !session("snapshot").getBoolean("running")
  }
  assertEquals(0,session("snapshot").getJSONArray("sessions").getJSONObject(0).optInt("acquired"))
  assertEquals(0,session("snapshot").getInt("serviceStarts"));assertEquals(0,session("snapshot").getInt("servicePromotions"))
  android.util.Log.i("AegisNodeNativeTest","phase=hybrid_actual_permission_denial errorType=Checkpoint")
  grantFrameworkPermission(context,Manifest.permission.ACCESS_COARSE_LOCATION)
  grantFrameworkPermission(context,Manifest.permission.ACCESS_FINE_LOCATION)
  if(Build.VERSION.SDK_INT>=29)grantFrameworkPermission(context,Manifest.permission.ACCESS_BACKGROUND_LOCATION)
  session("reset");session("configure",placeId,"outside")
  check(preferences.edit().remove("nativeSeen").putBoolean("operationalInside:$placeId",false).putBoolean("operationalRegion:$operational",false).commit())
  helper.callback(1,listOf(operational),longitude=null)
  await("A real service is active before the host kills the target process") {session("snapshot").getBoolean("running") && session("snapshot").getJSONArray("sessions").getJSONObject(0).optInt("acquired")>=1}
  val snapshot=session("snapshot")
  saved.put("nodeId",nodeId).put("ruleId",ruleId).put("placeId",placeId).put("configuration",configuration.toString()).put("manifest",manifest.toString()).put("operational",operational).put("pid",android.os.Process.myPid()).put("serviceStarts",snapshot.getInt("serviceStarts")).put("attempts",snapshot.getJSONArray("sessions").getJSONObject(0).getInt("attempts")).put("occurrences",occurrenceCount()).put("activeVault",context.getSharedPreferences(vaultAlias,Context.MODE_PRIVATE).getString(vaultEntry,null))
  marker.writeText(saved.toString())
 }
 @Test fun verifyNoRestartAfterProcessDeath() {
  assertTrue("Host preserves the marker across process death",marker.isFile)
  val saved=JSONObject(marker.readText())
  try {
   assertNotEquals("Instrumentation runs in a new OS process",saved.getInt("pid"),android.os.Process.myPid())
   assertTrue("Node identity is preserved",saved.getString("nodeId")==context.getSharedPreferences("aegis-node-push-v1",Context.MODE_PRIVATE).getString("nodeId",null))
   assertTrue("Encrypted credential record is preserved",saved.getString("activeVault")==context.getSharedPreferences(vaultAlias,Context.MODE_PRIVATE).getString(vaultEntry,null))
   assertEquals(saved.getString("manifest"),preferences.getString("manifest",null));assertEquals(saved.getString("configuration"),preferences.getString("installedConfiguration",null))
   val state=session("snapshot")
   assertFalse(state.getBoolean("running"));assertEquals(saved.getInt("serviceStarts"),state.getInt("serviceStarts"))
   val row=state.getJSONArray("sessions").getJSONObject(0)
   assertEquals("monitoring_interrupted",row.getString("status"));assertEquals(saved.getInt("attempts"),row.getInt("attempts"))
   assertTrue(context.getSystemService(NotificationManager::class.java).activeNotifications.none{it.id==7351})
   helper.callback(1,listOf(saved.getString("operational")),longitude=null)
   SystemClock.sleep(2000)
   assertFalse(session("snapshot").getBoolean("running"));assertEquals(saved.getInt("serviceStarts"),session("snapshot").getInt("serviceStarts"));assertEquals(saved.getInt("occurrences"),occurrenceCount())
   android.util.Log.i("AegisNodeNativeTest","phase=hybrid_process_interruption_recovered errorType=Checkpoint")
  }finally {
   fixtureJson("backend-offline",JSONObject().put("seconds",0))
   runCatching{helper.request("/${saved.getString("nodeId")}/revoke",fixtureCredential(),JSONObject())}
   runCatching{runtime.getMethod("deactivate",Context::class.java).invoke(null,context)}
   restorePreferences(saved.getJSONObject("originalPreferences"))
   val keyStore=KeyStore.getInstance("AndroidKeyStore").apply{load(null)}
   if(!saved.getBoolean("originalKeyPresent") && keyStore.containsAlias(vaultAlias))keyStore.deleteEntry(vaultAlias)
   assertEquals(saved.getBoolean("originalKeyPresent"),keyStore.containsAlias(vaultAlias))
   marker.delete()
  }
 }
}
