package com.aegis.node

import android.content.Context
import androidx.work.*
import org.json.JSONObject
import java.security.MessageDigest
import java.util.concurrent.TimeUnit

/** Event driven registration, durable action delivery, and bounded lease reconciliation. */
object NodeBackgroundWork {
  @Volatile var actionsChanged:(()->Unit)?=null
  private val foregroundRunning=java.util.concurrent.atomic.AtomicBoolean(false)
  private val foregroundExecutor=java.util.concurrent.Executors.newSingleThreadExecutor()
  /** Runs on opening/resuming or usable network return; independent of WorkManager cadence. */
  fun foregroundActions(context:Context) {
    val app=context.applicationContext
    if(NodeReminderActions.pending(app)==0 || NodeNativeSession.read(app)==null)return
    val manager=app.getSystemService(android.net.ConnectivityManager::class.java)
    val network=manager.activeNetwork ?: return
    val capabilities=manager.getNetworkCapabilities(network) ?: return
    if(!capabilities.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET))return
    if(!foregroundRunning.compareAndSet(false,true))return
    foregroundExecutor.execute {try {
      val deadline=android.os.SystemClock.elapsedRealtime()+20000
      val dependencies=NodeReminderActions.pendingGeographicDependencies(app)
      // Only eligible action dependencies consume this foreground budget. A failed
      // occurrence upload never prevents independent temporal actions from trying.
      if(dependencies.isNotEmpty())try {
        NodeGeofences.flushEvents(app,maxEvents=2,deadlineElapsed=deadline,occurrenceIds=dependencies,connectTimeoutMillis=3000,readTimeoutMillis=5000)
      }catch(_:Exception){}
      if(NodeReminderActions.flush(app,foreground=true,deadlineElapsed=deadline))enqueue(app)
    }catch(_:Exception){enqueue(app)}finally{foregroundRunning.set(false);actionsChanged?.invoke()}}
  }
  /** Independent release harness can defer the worker while proving the foreground path. */
  fun deferFixture(context:Context,defer:Boolean):String {
    check(BuildConfig.DEBUG || BuildConfig.AEGIS_FRAMEWORK_FIXTURE)
    val manager=WorkManager.getInstance(context)
    // Include daily geofence and weekly renewal instances of the same worker;
    // deferring only the one-shot name would not prove the foreground dependency path.
    manager.cancelAllWorkByTag(NodeSyncWorker::class.java.name).result.get(5,TimeUnit.SECONDS)
    if(defer) {
      val delayed=OneTimeWorkRequestBuilder<NodeSyncWorker>().setInitialDelay(1,TimeUnit.DAYS).build()
      // A foreground retry may enqueue between cancellation and this insertion.
      // Replace it; later production KEEP requests preserve the fixture delay.
      manager.enqueueUniqueWork("aegis-native-sync",ExistingWorkPolicy.REPLACE,delayed).result.get(5,TimeUnit.SECONDS)
    }else {enqueue(context);scheduleRenewal(context)}
    val deferred=defer && manager.getWorkInfosForUniqueWork("aegis-native-sync").get(5,TimeUnit.SECONDS).any{!it.state.isFinished && it.nextScheduleTimeMillis>System.currentTimeMillis()+60000}
    return JSONObject().put("workerDeferred",deferred).toString()
  }
  fun enqueue(context: Context) {
    val work = OneTimeWorkRequestBuilder<NodeSyncWorker>()
      .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
      .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 30, TimeUnit.SECONDS).build()
    NodeNativeNotifications.prefs(context).edit().putBoolean("actionsRetryScheduled",true).commit()
    WorkManager.getInstance(context).enqueueUniqueWork("aegis-native-sync", ExistingWorkPolicy.KEEP, work)
  }
  fun scheduleRenewal(context: Context) {
    val work = PeriodicWorkRequestBuilder<NodeSyncWorker>(7, TimeUnit.DAYS)
      .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()).build()
    WorkManager.getInstance(context).enqueueUniquePeriodicWork("aegis-native-renewal", ExistingPeriodicWorkPolicy.KEEP, work)
  }
  fun fingerprint(token: String): String = MessageDigest.getInstance("SHA-256").digest(token.toByteArray()).joinToString("") { "%02x".format(it) }
}
class NodeSyncWorker(context: Context, params: WorkerParameters) : Worker(context, params) {
  override fun doWork(): Result {
   NodeNativeNotifications.prefs(applicationContext).edit().putBoolean("actionsRetryScheduled",false).commit()
   val session = NodeNativeSession.read(applicationContext)
   return try {
    val prefs = NodeNativeNotifications.prefs(applicationContext)
    if (session == null) {
      // A boot-time vault/key outage is not an authenticated unbind or revocation.
      val retained=prefs.getString("nodeId",null)!=null || applicationContext.getSharedPreferences("aegis-geofences-v1",Context.MODE_PRIVATE).getString("manifest",null)!=null
      if(retained)retryBounded() else Result.success()
    }
    else {
      var retry=false
      // A foreground/native reconciliation event can resume pending receipts
      // immediately; remote failures use their own bounded retry budget.
      try{if(NodeReminderActions.flushWithdrawalReceipts(applicationContext))NodeReminderActions.scheduleWithdrawalReceipts(applicationContext)}catch(_:Exception){NodeReminderActions.scheduleWithdrawalReceipts(applicationContext)}
      // Send temporal OK/snooze before independent push registration or location work.
      try{if(NodeReminderActions.flush(applicationContext))retry=true}catch(_:Exception){retry=true}
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
      try{if(NodeReminderActions.flush(applicationContext))retry=true}catch(_:Exception){retry=true}
      try{NodeReminderActions.reconcilePresentations(applicationContext)}catch(_:Exception){retry=true}
      try{NodeGeofences.reconcile(applicationContext)}catch(error:NodeNativeSession.HttpFailure){if(error.status in setOf(401,403,404))throw error else retry=true}catch(_:Exception){retry=true}
      if(retry && runAttemptCount<8) {
        prefs.edit().putBoolean("actionsRetryScheduled",true).commit();Result.retry()
      }else Result.success()
    }
  } catch (error: NodeNativeSession.HttpFailure) {
    if (error.status == 401 || error.status == 403 || error.status == 404) {
      if(session!=null) {
        NodeGeofences.clearForScope(applicationContext,session.nodeId,session.origin)
        synchronized(NodeNotificationGate.lock) {
          if(NodeNativeSession.scopeIsCurrent(applicationContext,session.nodeId,session.origin)&&error.status!=403) {
            NodeNativeNotifications.prefs(applicationContext).edit().remove("nodeId").commit()
            WorkManager.getInstance(applicationContext).cancelUniqueWork("aegis-native-renewal")
          }
        }
      }
      Result.success()
    } else retryBounded()
  } catch (_: Exception) { retryBounded() }
   finally {NodeBackgroundWork.actionsChanged?.invoke()}
  }
  private fun retryBounded():Result {
    val retry=runAttemptCount<8
    NodeNativeNotifications.prefs(applicationContext).edit().putBoolean("actionsRetryScheduled",retry).commit()
    return if(retry)Result.retry()else Result.success()
  }
}
