package com.aegis.node.fixture

import android.content.Context
import android.os.SystemClock
import com.aegis.node.BuildConfig
import com.aegis.node.NodeGeofences
import com.aegis.node.NodeNativeSession
import com.aegis.node.NodeReminderActions
import org.json.JSONObject

/** Independent instrumentation seam; unreachable in ordinary distribution builds. */
internal object NodeTransientIdentityFixture {
 fun exercise(context:Context,kind:String):JSONObject {
  check(BuildConfig.DEBUG || BuildConfig.AEGIS_FRAMEWORK_FIXTURE)
  require(kind in setOf("actions","occurrences"))
  var result="pending"
  val consumed=NodeNativeSession.withUnavailableCallIdentityFixture {
   try {
    if(kind=="actions")NodeReminderActions.flush(context,foreground=true)
    else NodeGeofences.flushEvents(context,maxEvents=1,deadlineElapsed=SystemClock.elapsedRealtime()+20000,connectTimeoutMillis=3000,readTimeoutMillis=5000)
   }catch(_:NodeNativeSession.IdentityUnavailable) {
    result="credential_unavailable"
   }
  }
  return JSONObject().put("fixture","aegis_transient_identity_outbox_fixture_v1").put("consumed",consumed).put("result",result)
 }
}
