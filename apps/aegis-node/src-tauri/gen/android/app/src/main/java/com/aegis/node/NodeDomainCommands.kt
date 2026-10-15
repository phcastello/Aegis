package com.aegis.node

import android.content.Context
import androidx.work.*
import org.json.JSONObject
import java.time.Instant
import java.util.UUID

object NodeDomainCommands {
 fun accept(context: Context,command: JSONObject): NodeNotificationResult {
  require(UUID.fromString(command.getString("commandId")).toString()==command.getString("commandId"))
  val now=Instant.now().epochSecond;val expiry=NodeNotificationPayload.expiryEpochSecond(command.getString("expiresAt"))
  if(expiry<=now) return NodeNotificationResult("expired")
  require(expiry<=now+300)
  val capability=command.getString("capability");val input=command.getJSONObject("input")
  when(capability) {
   "geofence.monitor" -> { require(input.toString().length<64 && input.keys().asSequence().toSet()==setOf("operation") && input.getString("operation")=="sync");NodeBackgroundWork.enqueue(context) }
   "location.current" -> {
    require(input.keys().asSequence().toSet()==setOf("operation","requestId") && input.getString("operation")=="refresh")
    UUID.fromString(input.getString("requestId"))
    val work=OneTimeWorkRequestBuilder<NodeLocationWorker>().setInputData(workDataOf("requestId" to input.getString("requestId"))).build()
    WorkManager.getInstance(context).enqueueUniqueWork("aegis-location-${input.getString("requestId")}",ExistingWorkPolicy.KEEP,work)
   }
   else -> return NodeNotificationResult("unsupported")
  }
  return NodeNotificationResult("success")
 }
}
class NodeLocationWorker(context: Context,params: WorkerParameters):Worker(context,params) {
 override fun doWork():Result = try { NodeLocation.current(applicationContext,inputData.getString("requestId"));Result.success() }catch(_:Exception){Result.failure()}
}
