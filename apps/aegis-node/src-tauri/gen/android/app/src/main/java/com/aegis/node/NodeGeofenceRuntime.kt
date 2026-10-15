package com.aegis.node

import android.content.Context
import androidx.annotation.Keep

/** Stable native domain boundary shared by OS receivers and bounded confirmation work. */
@Keep
object NodeGeofenceRuntime {
 @JvmStatic fun observe(context:Context,latitude:Double,longitude:Double,accuracyMeters:Double,capturedAt:Long,allowConfirmation:Boolean) {
  require(latitude.isFinite() && latitude in -90.0..90.0 && longitude.isFinite() && longitude in -180.0..180.0)
  NodeGeofences.process(context,NodeGeoEngine.Observation(latitude,longitude,accuracyMeters,capturedAt),allowConfirmation)
 }
 @JvmStatic fun bindIdentity(context:Context,id:String) {NodeGeofences.bindIdentity(context,id);NodeReminderActions.bindIdentity(context,id)}
 @JvmStatic fun permissions(context:Context):String = NodeLocation.permissions(context).toString()
 @JvmStatic fun deactivate(context:Context) = NodeGeofences.clear(context)
 @JvmStatic fun restore(context:Context) = NodeGeofences.restore(context)
}
