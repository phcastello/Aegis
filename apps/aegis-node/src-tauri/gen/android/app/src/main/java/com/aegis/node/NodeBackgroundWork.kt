package com.aegis.node

import android.content.Context
import androidx.work.*
import org.json.JSONObject
import java.security.MessageDigest
import java.util.concurrent.TimeUnit

/** Event driven registration, durable action delivery, and bounded lease reconciliation. */
object NodeBackgroundWork {
  fun enqueue(context: Context) {
    val work = OneTimeWorkRequestBuilder<NodeSyncWorker>()
      .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
      .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 30, TimeUnit.SECONDS).build()
    WorkManager.getInstance(context).enqueueUniqueWork("aegis-native-sync", ExistingWorkPolicy.APPEND_OR_REPLACE, work)
  }
  fun scheduleRenewal(context: Context) {
    val work = PeriodicWorkRequestBuilder<NodeSyncWorker>(7, TimeUnit.DAYS)
      .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()).build()
    WorkManager.getInstance(context).enqueueUniquePeriodicWork("aegis-native-renewal", ExistingPeriodicWorkPolicy.KEEP, work)
  }
  fun fingerprint(token: String): String = MessageDigest.getInstance("SHA-256").digest(token.toByteArray()).joinToString("") { "%02x".format(it) }
}
class NodeSyncWorker(context: Context, params: WorkerParameters) : Worker(context, params) {
  override fun doWork(): Result = try {
    val prefs = NodeNativeNotifications.prefs(applicationContext)
    val session = NodeNativeSession.read(applicationContext)
    if (session == null) { NodeGeofenceRuntime.deactivate(applicationContext); Result.success() }
    else {
      var retry=false
      // Independent channels: a location/provider error cannot strand temporal actions.
      val token = prefs.getString("token", null)
      val stamp = System.currentTimeMillis()
      try {
        if (token != null && (prefs.getString("registeredTokenHash", null) != NodeBackgroundWork.fingerprint(token) || prefs.getString("registeredNodeId", null) != session.nodeId || stamp - prefs.getLong("registeredAt", 0) >= TimeUnit.DAYS.toMillis(14))) {
          NodeNativeSession.call(applicationContext, "PUT", "/api/nodes/me/push", JSONObject().put("token", token))
          check(prefs.edit().putString("registeredTokenHash", NodeBackgroundWork.fingerprint(token)).putString("registeredNodeId", session.nodeId).putLong("registeredAt", stamp).commit())
        }
      }catch(error:NodeNativeSession.HttpFailure){if(error.status in setOf(401,403,404))throw error else retry=true}catch(_:Exception){retry=true}
      try{NodeLocation.flush(applicationContext)}catch(_:Exception){retry=true}
      try{NodeGeofences.flushEvents(applicationContext)}catch(_:Exception){retry=true}
      try{NodeReminderActions.flush(applicationContext)}catch(_:Exception){retry=true}
      try{NodeReminderActions.reconcilePresentations(applicationContext)}catch(_:Exception){retry=true}
      try{NodeGeofences.reconcile(applicationContext)}catch(error:NodeNativeSession.HttpFailure){if(error.status in setOf(401,403,404))throw error else retry=true}catch(_:Exception){retry=true}
      if(retry)Result.retry()else Result.success()
    }
  } catch (error: NodeNativeSession.HttpFailure) {
    if (error.status == 401 || error.status == 403 || error.status == 404) {
      NodeGeofenceRuntime.deactivate(applicationContext)
      if(error.status!=403)NodeNativeNotifications.prefs(applicationContext).edit().remove("nodeId").commit()
      Result.success()
    } else Result.retry()
  } catch (_: Exception) { Result.retry() }
}
