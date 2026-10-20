package com.aegis.node

import android.app.Activity
import android.content.Context
import android.os.Looper
import com.google.android.gms.common.api.ApiException
import com.google.android.gms.common.api.ResolvableApiException
import com.google.android.gms.location.LocationRequest
import com.google.android.gms.location.LocationServices
import com.google.android.gms.location.LocationSettingsRequest
import com.google.android.gms.location.Priority
import com.google.android.gms.tasks.Tasks
import org.json.JSONObject
import java.util.concurrent.ExecutionException
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

/** Removed by R8 from distribution: reachable only beyond the Runtime fixture gate. */
internal object NodeLegacyLocationSettingsFixture {
 private val pendingResolution=AtomicReference<ResolvableApiException?>()
 fun check(context:Context,resolve:Boolean):JSONObject {
  check(BuildConfig.DEBUG || BuildConfig.AEGIS_FRAMEWORK_FIXTURE)
  check(Looper.myLooper()!=Looper.getMainLooper())
  val result=JSONObject().put("fixture","AEGIS_LEGACY_NLP_FIXTURE_V1")
  val client=LocationServices.getSettingsClient(context)
  try{result.put("googleAccuracy",Tasks.await(client.isGoogleLocationAccuracyEnabled(),5,TimeUnit.SECONDS))}catch(_:Exception){}
  // Both requests are settings requirements only: neither requests GPS updates.
  val request=LocationSettingsRequest.Builder()
   .addLocationRequest(LocationRequest.Builder(Priority.PRIORITY_HIGH_ACCURACY,10000).build())
   .addLocationRequest(LocationRequest.Builder(Priority.PRIORITY_BALANCED_POWER_ACCURACY,10000).build())
   .setAlwaysShow(true).build()
  return try {
   val response=Tasks.await(client.checkLocationSettings(request),10,TimeUnit.SECONDS)
   response.locationSettingsStates?.let{result.put("networkPresent",it.isNetworkLocationPresent).put("networkUsable",it.isNetworkLocationUsable).put("gpsUsable",it.isGpsUsable)}
   result.put("status","ready")
  }catch(error:ExecutionException) {
   val cause=error.cause
   if(cause is ResolvableApiException) {
    if(resolve){pendingResolution.set(cause);result.put("status","resolution_prepared")}
    else result.put("status","resolution_required")
   } else result.put("status","settings_unavailable").apply{if(cause is ApiException)put("apiStatus",cause.statusCode)}
  }catch(_:InterruptedException){Thread.currentThread().interrupt();result.put("status","interrupted")}
   catch(_:Exception){result.put("status","settings_unavailable")}
 }
 fun resolve(activity:Activity?,operation:String):JSONObject {
  check(BuildConfig.DEBUG || BuildConfig.AEGIS_FRAMEWORK_FIXTURE)
  if(operation=="clear"){pendingResolution.set(null);return JSONObject().put("status","cleared")}
  require(operation=="open")
  check(Looper.myLooper()==Looper.getMainLooper())
  val pending=pendingResolution.getAndSet(null)?:return JSONObject().put("status","resolution_unavailable")
  if(activity==null || activity.isFinishing || activity.isDestroyed)return JSONObject().put("status","activity_unavailable")
  return try {
   // Use the documented Activity/token/result path, rather than sending an
   // activity PendingIntent from an application Context on legacy Android.
   pending.startResolutionForResult(activity,7326)
   JSONObject().put("status","resolution_opened")
  }catch(_:Exception){JSONObject().put("status","resolution_unavailable")}
 }
}
