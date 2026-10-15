package com.aegis.node

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.location.Location
import android.location.LocationManager
import android.os.Build
import android.os.SystemClock
import androidx.core.content.ContextCompat
import com.google.android.gms.location.*
import com.google.android.gms.tasks.CancellationTokenSource
import com.google.android.gms.tasks.Tasks
import org.json.JSONObject
import java.time.Instant
import java.util.concurrent.TimeUnit

object NodeLocation {
  fun permissions(context: Context): JSONObject {
    val fine=ContextCompat.checkSelfPermission(context,Manifest.permission.ACCESS_FINE_LOCATION)==PackageManager.PERMISSION_GRANTED
    val coarse=ContextCompat.checkSelfPermission(context,Manifest.permission.ACCESS_COARSE_LOCATION)==PackageManager.PERMISSION_GRANTED
    val background=Build.VERSION.SDK_INT<29 || ContextCompat.checkSelfPermission(context,Manifest.permission.ACCESS_BACKGROUND_LOCATION)==PackageManager.PERMISSION_GRANTED
    val manager=context.getSystemService(LocationManager::class.java)
    val enabled=if(Build.VERSION.SDK_INT>=28) manager.isLocationEnabled else manager.isProviderEnabled(LocationManager.GPS_PROVIDER)||manager.isProviderEnabled(LocationManager.NETWORK_PROVIDER)
    val provider=com.google.android.gms.common.GoogleApiAvailability.getInstance().isGooglePlayServicesAvailable(context)==com.google.android.gms.common.ConnectionResult.SUCCESS
    return JSONObject().put("supported",true).put("providerAvailable",provider).put("granted",fine||coarse).put("foreground",fine||coarse).put("precise",fine).put("background",background).put("servicesEnabled",enabled).put("pendingActions",NodeReminderActions.pending(context))
  }
  fun current(context: Context,requestId: String?=null,upload: Boolean=true,fresh: Boolean=false,durationMillis:Long=15000): JSONObject {
    val state=permissions(context)
    if(!state.getBoolean("providerAvailable")) return state.put("status","provider_unavailable")
    if(!state.getBoolean("granted")) return state.put("status","permission_denied")
    if(!state.getBoolean("servicesEnabled")) return state.put("status","location_disabled")
    val cancel=CancellationTokenSource()
    try {
      val duration=durationMillis.coerceIn(1,15000)
      val request=CurrentLocationRequest.Builder().setPriority(Priority.PRIORITY_BALANCED_POWER_ACCURACY).setMaxUpdateAgeMillis(if(fresh) 0 else 120000).setDurationMillis(duration).build()
      val result=Tasks.await(LocationServices.getFusedLocationProviderClient(context).getCurrentLocation(request,cancel.token),duration+1000,TimeUnit.MILLISECONDS)
        ?: return state.put("status","unavailable")
      val age=TimeUnit.NANOSECONDS.toMillis((SystemClock.elapsedRealtimeNanos()-result.elapsedRealtimeNanos).coerceAtLeast(0))
      if(age>120000) return state.put("status","stale")
      val value=observation(result).apply { if(requestId!=null) put("requestId",requestId) }
      val prefs=NodeNativeNotifications.prefs(context)
      check(prefs.edit().putString("lastObservation",value.toString()).putLong("observationMonotonic",result.elapsedRealtimeNanos).putLong("observationReceivedAt",System.currentTimeMillis()).putBoolean("observationPending",upload).commit())
      if(upload) try { flush(context) } catch(_:Exception) { value.put("syncPending",true);NodeBackgroundWork.enqueue(context) }
      return value.put("status","available").put("ageSeconds",age/1000)
    } finally { cancel.cancel() }
  }
  fun queueTransition(context:Context,observation:NodeGeoEngine.Observation) {
    val value=JSONObject().put("latitude",observation.latitude).put("longitude",observation.longitude).put("accuracyMeters",observation.accuracyMeters).put("capturedAt",Instant.ofEpochMilli(observation.capturedAt).toString()).put("source","fused")
    val prefs=NodeNativeNotifications.prefs(context)
    val existing=prefs.getString("lastObservation",null)?.let{JSONObject(it)}
    if(existing==null || engine(existing).capturedAt<observation.capturedAt) check(prefs.edit().putString("lastObservation",value.toString()).putBoolean("observationPending",true).commit())
    NodeBackgroundWork.enqueue(context)
  }
  fun flush(context:Context) {
    val prefs=NodeNativeNotifications.prefs(context)
    if(!prefs.getBoolean("observationPending",false))return
    val original=prefs.getString("lastObservation",null) ?: return
    try{NodeNativeSession.call(context,"POST","/api/nodes/location/observations",JSONObject(original))}
    catch(error:NodeNativeSession.HttpFailure){if(error.status !in setOf(400,403,409))throw error}
    if(prefs.getString("lastObservation",null)==original)prefs.edit().putBoolean("observationPending",false).commit()
  }
  fun observation(location: Location): JSONObject = JSONObject().put("latitude",location.latitude).put("longitude",location.longitude)
    .put("accuracyMeters",location.accuracy.toDouble()).put("capturedAt",Instant.ofEpochMilli(location.time).toString()).put("source","fused")
  fun engine(value: JSONObject): NodeGeoEngine.Observation = NodeGeoEngine.Observation(value.getDouble("latitude"),value.getDouble("longitude"),value.getDouble("accuracyMeters"),java.time.OffsetDateTime.parse(value.getString("capturedAt")).toInstant().toEpochMilli())
}
