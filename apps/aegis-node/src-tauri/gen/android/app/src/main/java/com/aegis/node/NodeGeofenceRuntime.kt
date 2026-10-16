package com.aegis.node

import android.content.Context
import android.location.Location
import android.os.SystemClock
import org.json.JSONObject
import com.google.android.gms.tasks.CancellationTokenSource
import java.util.concurrent.TimeoutException
import androidx.annotation.Keep

/** Stable native domain boundary shared by OS receivers and bounded confirmation work. */
@Keep
object NodeGeofenceRuntime {
 @JvmStatic fun observe(context:Context,latitude:Double,longitude:Double,accuracyMeters:Double,capturedAt:Long,allowConfirmation:Boolean) {
  require(latitude.isFinite() && latitude in -90.0..90.0 && longitude.isFinite() && longitude in -180.0..180.0)
  NodeGeofences.process(context,NodeGeoEngine.Observation(latitude,longitude,accuracyMeters,capturedAt),allowConfirmation)
 }
 @JvmStatic fun bindIdentity(context:Context,id:String) {NodeGeofences.bindIdentity(context,id);NodeReminderActions.bindIdentity(context,id)}
 @JvmStatic fun diagnostics(context:Context):String = NodeGeofences.diagnostics(context).toString()
 @JvmStatic fun verifyPresence(context:Context):String = NodeGeofences.verifyPresence(context).toString()
 /** Instrumentation-only seam: production builds cannot inject observations. */
 @JvmStatic fun locationAdapterFixture(context:Context,scenario:String,accuracyMeters:Double,ageMillis:Long):String {
  check(BuildConfig.DEBUG || BuildConfig.AEGIS_FRAMEWORK_FIXTURE)
  val requestInfo=JSONObject()
  var cancellation:CancellationTokenSource?=null
  val result=NodeGeofences.verifyPresence(context,allowConfirmation=false) {request,cancel,timeout->
   cancellation=cancel
   requestInfo.put("priority",request.priority).put("maxUpdateAgeMillis",request.maxUpdateAgeMillis).put("durationMillis",request.durationMillis).put("timeoutMillis",timeout)
   when(scenario) {
    "unavailable" -> null
    "timeout" -> throw TimeoutException()
    else -> Location("fused").apply{latitude=0.0;longitude=0.0;accuracy=accuracyMeters.toFloat();time=System.currentTimeMillis()-ageMillis;elapsedRealtimeNanos=SystemClock.elapsedRealtimeNanos()-ageMillis*1000000}
   }
  }
  return result.put("fixtureRequest",requestInfo.put("cancelled",cancellation?.token?.isCancellationRequested?:false)).toString()
 }
 @JvmStatic fun permissions(context:Context):String = NodeLocation.permissions(context).toString()
 @JvmStatic fun deactivate(context:Context) = NodeGeofences.clear(context)
 @JvmStatic fun restore(context:Context) = NodeGeofences.restore(context)
}
