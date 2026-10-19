package com.aegis.node

import android.Manifest
import android.app.NotificationManager
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.os.Build
import android.os.SystemClock
import android.provider.Settings
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

/** Separate instrumentation invocations around an actual host-initiated emulator reboot.
 * No restore method is invoked by verify: BOOT_COMPLETED and its WorkManager job must run.
 * A controlled GMS failure remains a failure result, never a fabricated registration. */
@RunWith(AndroidJUnit4::class)
class NodeHybridBootRecoveryFrameworkTest {
 private val instrumentation=InstrumentationRegistry.getInstrumentation()
 private val context=instrumentation.targetContext
 private val helper=NodeGeofenceReceiverFrameworkTest()
 private val runtime=Class.forName("com.aegis.node.NodeGeofenceRuntime",true,context.classLoader)
 private val preferences=context.getSharedPreferences("aegis-geofences-v1",Context.MODE_PRIVATE)
 private val vaultAlias="keyring-aegis-node-identity-v1"
 private val vaultEntry="installation\uFEFF@\uFEFFcom.aegis.node.identity.v1"
 private val marker=File(context.cacheDir,"aegis-hybrid-boot-fixture.json")
 private val preferenceNames=listOf(vaultAlias,"aegis-node-push-v1","aegis-geofences-v1","aegis-hybrid-sessions-v1")
 private fun bootCount()=Settings.Global.getInt(context.contentResolver,Settings.Global.BOOT_COUNT,-1)
 private fun session()=JSONObject(runtime.getMethod("hybridSessionFixture",Context::class.java,String::class.java,String::class.java,String::class.java).invoke(null,context,"snapshot","","real") as String)
 private fun await(reason:String,condition:()->Boolean) {
  val end=SystemClock.elapsedRealtime()+100000
  while(!condition() && SystemClock.elapsedRealtime()<end)SystemClock.sleep(100)
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
 @Test fun prepareBeforeBoot() {
  val code=InstrumentationRegistry.getArguments().getString("bootPairingCode")?:error("boot_fixture_bootstrap_missing")
  val keyStore=KeyStore.getInstance("AndroidKeyStore").apply{load(null)}
  val saved=JSONObject().put("originalPreferences",snapshotPreferences()).put("originalKeyPresent",keyStore.containsAlias(vaultAlias)).put("bootCount",bootCount())
  marker.writeText(saved.toString())
  instrumentation.uiAutomation.grantRuntimePermission(context.packageName,Manifest.permission.ACCESS_COARSE_LOCATION)
  instrumentation.uiAutomation.grantRuntimePermission(context.packageName,Manifest.permission.ACCESS_FINE_LOCATION)
  if(Build.VERSION.SDK_INT>=29)instrumentation.uiAutomation.grantRuntimePermission(context.packageName,Manifest.permission.ACCESS_BACKGROUND_LOCATION)
  val attempt=UUID.randomUUID().toString()
  val pair=helper.request("/pair",body=JSONObject().put("attemptId",attempt).put("code",code).put("recoveryKey",Base64.encodeToString(ByteArray(32).also{SecureRandom().nextBytes(it)},Base64.NO_WRAP or Base64.URL_SAFE or Base64.NO_PADDING)).put("name","Reboot recovery fixture").put("platform","android").put("appVersion","0.0.0-framework-fixture").put("protocolVersion",1))
  val credential=pair.getString("credential")
  val node=helper.request("/pair/finalize",body=JSONObject().put("attemptId",attempt).put("credential",credential));val nodeId=node.getString("id")
  helper.announce(credential);helper.vault(node,credential)
  helper.request("/location/settings",credential,JSONObject().put("nodeId",nodeId).put("sourceNodeId",nodeId).put("mode","dynamic"),"PUT")
  val place=helper.request("/places",credential,JSONObject().put("name","Reboot synthetic home").put("type","property").put("aliases",JSONArray()).put("circles",JSONArray().put(JSONObject().put("id",UUID.randomUUID().toString()).put("latitude",0).put("longitude",0).put("radiusMeters",50))).put("monitoringEnvelope",JSONObject().put("enabled",true).put("paddingMeters",150).put("confirmed",true).put("expectedRevision",0)))
  val placeId=place.getString("id")
  val rule=helper.request("/geofences",credential,JSONObject().put("text","Reboot recovery fixture").put("placeIds",JSONArray().put(placeId)).put("observerNodeId",nodeId).put("transition","enter").put("repeat",true).put("timeZoneId","UTC"))
  val ruleId=rule.getString("id");val manifest=helper.request("/geofences/manifest",credential)
  check(preferences.edit().putString("manifest",manifest.toString()).commit())
  // Initial real GeofencingClient registration and its bounded initial FLP attempt.
  runtime.getMethod("restore",Context::class.java).invoke(null,context)
  val now=System.currentTimeMillis()
  check(preferences.edit().putString("state:$placeId",JSONObject().put("confirmed","outside").put("candidate","unknown").put("candidateAt",0).put("lastCapturedAt",now-60000).toString()).putLong("confirmedAt:$placeId",now-60000).putBoolean("armed:$ruleId",true).putLong("ruleRevision:$ruleId",rule.getLong("revision")).commit())
  val sessions=context.getSharedPreferences("aegis-hybrid-sessions-v1",Context.MODE_PRIVATE)
  val expired=JSONObject().put("id",UUID.randomUUID().toString()).put("placeId",placeId).put("scope",nodeId).put("configurationId","expired-before-boot").put("processInstance","completed-fixture-session").put("trigger","operational_enter").put("status","monitoring_session_expired").put("startedAt",now-240000).put("deadline",now-60000).put("endedAt",now-60000).put("attempts",6).put("lastAttemptAt",now-90000).put("acquired",0).put("unavailable",6)
  check(sessions.edit().putString("session:$placeId",expired.toString()).putString("starts:$placeId",JSONArray().put(now-240000).toString()).commit())
  assertFalse(session().getBoolean("running"))
  saved.put("nodeId",nodeId).put("ruleId",ruleId).put("placeId",placeId).put("place",place.toString()).put("manifestRevision",manifest.getString("revision")).put("ruleRevision",rule.getLong("revision")).put("occurrences",occurrenceCount()).put("serviceStarts",session().getInt("serviceStarts")).put("expiredSession",expired.toString()).put("activeVault",context.getSharedPreferences(vaultAlias,Context.MODE_PRIVATE).getString(vaultEntry,null))
  marker.writeText(saved.toString())
 }
 @Test fun verifyAfterBoot() {
  assertTrue("Host preserves fixture state across an actual reboot",marker.isFile)
  val saved=JSONObject(marker.readText())
  try {
   val currentBoot=bootCount()
   assertTrue("Android boot count proves a real reboot",currentBoot>saved.getInt("bootCount"))
   assertTrue("Node identity is preserved",saved.getString("nodeId")==context.getSharedPreferences("aegis-node-push-v1",Context.MODE_PRIVATE).getString("nodeId",null))
   assertTrue("Encrypted credential record is preserved",saved.getString("activeVault")==context.getSharedPreferences(vaultAlias,Context.MODE_PRIVATE).getString(vaultEntry,null))
   await("The actual boot receiver and its native restore worker finish") {preferences.getInt("lastRestoreCompletedBootCount",-1)==currentBoot}
   val status=preferences.getString("status",null)
   if(!preferences.getBoolean("needsInstall",true)) {
    assertEquals(saved.getString("manifestRevision"),preferences.getString("installedRevision",null))
    val config=JSONObject(preferences.getString("installedConfiguration",null)?:error("boot_installed_configuration_missing"))
    val regions=config.getJSONArray("regions")
    assertEquals(setOf("real","operational"),(0 until regions.length()).map{regions.getJSONObject(it).optString("kind","real")}.toSet())
   } else {
    assertEquals("recoverable_error",status)
    assertTrue(preferences.getString("error",null) in setOf("location_provider_unavailable","location_permission_required","precise_location_required","background_location_required","location_services_disabled","native_registration_failed"))
   }
   val credential=fixtureCredential()
   val backendPlaces=helper.request("/places",credential).getJSONArray("items")
   val place=(0 until backendPlaces.length()).map{backendPlaces.getJSONObject(it)}.single{it.getString("id")==saved.getString("placeId")}
   val before=JSONObject(saved.getString("place"))
   assertEquals(before.getLong("revision"),place.getLong("revision"));assertEquals(before.getJSONArray("circles").toString(),place.getJSONArray("circles").toString());assertEquals(before.getJSONObject("monitoringEnvelope").toString(),place.getJSONObject("monitoringEnvelope").toString())
   val backendRules=helper.request("/geofences",credential).getJSONArray("items")
   val rule=(0 until backendRules.length()).map{backendRules.getJSONObject(it)}.single{it.getString("id")==saved.getString("ruleId")}
   assertEquals(saved.getLong("ruleRevision"),rule.getLong("revision"));assertEquals("active",rule.getString("status"))
   val state=session();assertFalse(state.getBoolean("running"));assertEquals(saved.getInt("serviceStarts"),state.getInt("serviceStarts"))
   val expired=context.getSharedPreferences("aegis-hybrid-sessions-v1",Context.MODE_PRIVATE).getString("session:${saved.getString("placeId")}",null)
   assertEquals(saved.getString("expiredSession"),expired)
   assertEquals(saved.getInt("occurrences"),occurrenceCount())
   assertTrue(context.getSystemService(NotificationManager::class.java).activeNotifications.none{it.id==7351})
   android.util.Log.i("AegisNodeNativeTest","phase=hybrid_reboot_preserves_identity_no_transition errorType=Checkpoint")
  }finally {
   runCatching{helper.request("/${saved.getString("nodeId")}/revoke",fixtureCredential(),JSONObject())}
   runCatching{runtime.getMethod("deactivate",Context::class.java).invoke(null,context)}
   restorePreferences(saved.getJSONObject("originalPreferences"))
   val keyStore=KeyStore.getInstance("AndroidKeyStore").apply{load(null)}
   if(!saved.getBoolean("originalKeyPresent") && keyStore.containsAlias(vaultAlias))keyStore.deleteEntry(vaultAlias)
   assertEquals(saved.getBoolean("originalKeyPresent"),keyStore.containsAlias(vaultAlias));marker.delete()
  }
 }
}
