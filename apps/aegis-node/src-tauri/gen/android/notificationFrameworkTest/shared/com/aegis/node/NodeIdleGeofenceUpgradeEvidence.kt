package com.aegis.node

import android.Manifest
import android.app.NotificationManager
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.os.Build
import android.os.SystemClock
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import java.util.UUID

/** Target-independent instrumentation uses only the fixed .17/.18 WebView IPC.
 * The before fixture is prepared AFTER its idle windows; the upgraded fixture is
 * removed BEFORE the candidate windows, keeping both measured workloads quiet. */
private const val UPGRADE_PREFS="aegis-idle-geofence-upgrade-v1"
private fun upgradeRows(value:JSONObject)=value.optJSONArray("items")?:error("upgrade_list_missing")
private fun upgradeDatabaseCounts(context:Context):JSONObject {
 val file=context.getDatabasePath("aegis-geofence-events-v1.db")
 if(!file.exists())return JSONObject().put("events",0).put("transitionKeys",0)
 return SQLiteDatabase.openDatabase(file.path,null,SQLiteDatabase.OPEN_READONLY).use{db->
  fun count(table:String)=db.rawQuery("SELECT COUNT(*) FROM $table",null).use{it.moveToFirst();it.getInt(0)}
  JSONObject().put("events",count("events")).put("transitionKeys",count("transition_keys"))
 }
}
private fun upgradeDomain(invoke:(String,JSONObject,Long)->JSONObject,name:String,operation:String,payload:JSONObject?=null,id:String?=null):JSONObject =
 invoke(name,JSONObject().put("operation",operation).apply{payload?.let{put("payload",it)};id?.let{put("id",it)}},90000)

internal fun prepareIdleGeofenceUpgrade(context:Context,nodeId:String,invoke:(String,JSONObject,Long)->JSONObject):JSONObject {
 val automation=InstrumentationRegistry.getInstrumentation().uiAutomation
 automation.grantRuntimePermission(context.packageName,Manifest.permission.ACCESS_COARSE_LOCATION)
 automation.grantRuntimePermission(context.packageName,Manifest.permission.ACCESS_FINE_LOCATION)
 if(Build.VERSION.SDK_INT>=29)automation.grantRuntimePermission(context.packageName,Manifest.permission.ACCESS_BACKGROUND_LOCATION)
 upgradeDomain(invoke,"node_location","configureNode",JSONObject().put("nodeId",nodeId).put("sourceNodeId",nodeId).put("mode","dynamic"))
 val circle=JSONObject().put("id",UUID.randomUUID().toString()).put("latitude",0).put("longitude",0).put("radiusMeters",50)
 val place=upgradeDomain(invoke,"node_places","create",JSONObject().put("name","APK upgrade synthetic 50m home").put("type","property").put("aliases",JSONArray()).put("circles",JSONArray().put(circle)))
 val rule=upgradeDomain(invoke,"node_geofences","create",JSONObject().put("text","APK upgrade synthetic arrival").put("placeIds",JSONArray().put(place.getString("id"))).put("observerNodeId",nodeId).put("transition","enter").put("repeat",true).put("timeZoneId","UTC"))
 val sync=upgradeDomain(invoke,"node_geofences","sync")
 assertTrue("The accepted APK must register the real Google region",sync.optString("status") in setOf("installed","waiting_initial","active"))
 val native=context.getSharedPreferences("aegis-geofences-v1",Context.MODE_PRIVATE)
 assertFalse("Real region installation is complete",native.getBoolean("needsInstall",true));assertTrue(native.getBoolean("hasRegions",false))
 // An unavailable fix remains unknown and unarmed; no synthetic position is seeded.
 if(native.getString("state:${place.getString("id")}",null)?.let{JSONObject(it).optString("confirmed","unknown")} !in setOf("inside","outside"))upgradeDomain(invoke,"node_geofences","verifyPresence")
 val baseline=native.getString("state:${place.getString("id")}",null)?.let{JSONObject(it).optString("confirmed","unknown")}?:"unknown"
 val armed=native.getBoolean("armed:${rule.getString("id")}",false)
 assertEquals("Arrival arming follows the actual real-area baseline",baseline=="outside",armed)
 val counts=upgradeDatabaseCounts(context)
 assertEquals("Preparation cannot invent an occurrence",0,counts.getInt("events"));assertEquals(0,counts.getInt("transitionKeys"))
 val reminders=upgradeDomain(invoke,"node_reminders","list").getJSONArray("reminders").length()
 assertEquals("No backend occurrence exists before the APK upgrade",0,reminders)
 val marker=JSONObject().put("nodeId",nodeId).put("placeId",place.getString("id")).put("placeRevision",place.getLong("revision")).put("circle",place.getJSONArray("circles").getJSONObject(0)).put("ruleId",rule.getString("id")).put("ruleRevision",rule.getLong("revision")).put("baseline",baseline).put("armed",armed).put("counts",counts).put("backendReminders",reminders)
 check(context.getSharedPreferences(UPGRADE_PREFS,Context.MODE_PRIVATE).edit().putString("expected",marker.toString()).commit())
 return JSONObject().put("nativeRegistered",true).put("realRadiusMeters",50).put("baseline",baseline).put("armed",armed).put("noOccurrence",true)
}

internal fun verifyIdleGeofenceUpgradeAndCleanup(context:Context,nodeId:String,invoke:(String,JSONObject,Long)->JSONObject):JSONObject {
 val marker=JSONObject(context.getSharedPreferences(UPGRADE_PREFS,Context.MODE_PRIVATE).getString("expected",null)?:error("upgrade_marker_missing"))
 assertTrue("The real APK upgrade preserves the existing Node identity",marker.getString("nodeId")==nodeId)
 val places=upgradeRows(upgradeDomain(invoke,"node_places","list"))
 val place=(0 until places.length()).map{places.getJSONObject(it)}.single{it.getString("id")==marker.getString("placeId")}
 assertEquals("Place revision is preserved",marker.getLong("placeRevision"),place.getLong("revision"))
 val circle=place.getJSONArray("circles").getJSONObject(0);val expectedCircle=marker.getJSONObject("circle")
 assertEquals(1,place.getJSONArray("circles").length())
 assertTrue("The existing real circle ID is preserved",expectedCircle.getString("id")==circle.getString("id"))
 for(field in listOf("latitude","longitude","radiusMeters"))assertTrue("The real area is preserved",expectedCircle.getDouble(field)==circle.getDouble(field))
 assertEquals(50.0,circle.getDouble("radiusMeters"),0.0)
 val envelope=place.getJSONObject("monitoringEnvelope")
 assertFalse("Legacy places do not silently gain operational monitoring",envelope.getBoolean("enabled"))
 assertTrue(envelope.getBoolean("approvalRequired"));assertEquals(0,envelope.getJSONArray("circles").length())
 val rules=upgradeRows(upgradeDomain(invoke,"node_geofences","list"))
 val rule=(0 until rules.length()).map{rules.getJSONObject(it)}.single{it.getString("id")==marker.getString("ruleId")}
 assertEquals("Existing rule revision is preserved",marker.getLong("ruleRevision"),rule.getLong("revision"))
 assertEquals("active",rule.getString("status"));assertEquals("enter",rule.getString("transition"));assertTrue(rule.getBoolean("repeat"))
 assertTrue("The rule still references its real place",rule.getJSONArray("placeIds").getString(0)==place.getString("id"))
 upgradeDomain(invoke,"node_geofences","sync")
 val native=context.getSharedPreferences("aegis-geofences-v1",Context.MODE_PRIVATE)
 assertFalse(native.getBoolean("needsInstall",true));assertTrue(native.getBoolean("hasRegions",false))
 val configuration=JSONObject(native.getString("installedConfiguration",null)?:error("upgrade_registration_missing"))
 val regions=configuration.getJSONArray("regions")
 assertEquals(1,regions.length());assertEquals("real",regions.getJSONObject(0).optString("kind","real"))
 assertTrue("The installed real region retains its circle identity",regions.getJSONObject(0).getString("circleId")==circle.getString("id"))
 val counts=upgradeDatabaseCounts(context)
 for(field in listOf("events","transitionKeys"))assertEquals("SQLite occurrence/dedupe counts survive without a retroactive transition",marker.getJSONObject("counts").getInt(field),counts.getInt(field))
 assertEquals("Upgrade cannot create a backend reminder",marker.getInt("backendReminders"),upgradeDomain(invoke,"node_reminders","list").getJSONArray("reminders").length())
 val monitoring=context.getSharedPreferences("aegis-hybrid-sessions-v1",Context.MODE_PRIVATE)
 assertTrue("An unapproved legacy envelope starts no session",monitoring.all.filterKeys{it.startsWith("session:")}.values.none{it is String && JSONObject(it).optString("status")=="active"})
 assertTrue(context.getSystemService(NotificationManager::class.java).activeNotifications.none{it.id==7351})

 // Cleanup is part of setup, before any candidate measurement boundary.
 val cancelled=upgradeDomain(invoke,"node_geofences","cancel",JSONObject().put("expectedRevision",rule.getLong("revision")),rule.getString("id"))
 assertEquals("confirmed",cancelled.getJSONObject("localRemoval").getString("state"))
 upgradeDomain(invoke,"node_geofences","sync")
 assertFalse("No geofence is active during either set of idle measurements",native.getBoolean("hasRegions",true))
 upgradeDomain(invoke,"node_places","remove",JSONObject().put("expectedRevision",place.getLong("revision")),place.getString("id"))
 val remaining=upgradeRows(upgradeDomain(invoke,"node_places","list"))
 assertTrue("The disposable upgrade place is removed before measuring",(0 until remaining.length()).none{remaining.getJSONObject(it).getString("id")==place.getString("id")})
 upgradeDomain(invoke,"node_location","configureNode",JSONObject().put("nodeId",nodeId).put("sourceNodeId",nodeId).put("mode","none"))
 check(context.getSharedPreferences(UPGRADE_PREFS,Context.MODE_PRIVATE).edit().clear().commit())
 SystemClock.sleep(2000)
 return JSONObject().put("identityPreserved",true).put("realGeometryPreserved",true).put("revisionsPreserved",true).put("legacyEnvelopeUnapproved",true).put("noOccurrence",true).put("nativeRemovalConfirmed",true).put("placeRemoved",true).put("idleWorkloadRestored",true)
}
