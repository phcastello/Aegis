package com.aegis.node
import androidx.annotation.Keep
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
class NodeMessagingService:FirebaseMessagingService() {
 override fun onNewToken(token:String) {
   val prefs=NodeNativeNotifications.prefs(this)
   if(prefs.getString("token",null)!=token) { if(prefs.edit().putString("token",token).commit())NodeBackgroundWork.enqueue(this) else notificationLog("token_persist",IllegalStateException()) }
 }
 @Keep override fun onMessageReceived(message:RemoteMessage) {
  try {
   val data=message.data
   val bound=NodeNativeNotifications.prefs(this).getString("nodeId",null)
   if(bound==null || data["nodeId"]!=bound) return
   if(data["type"] in setOf("reminder.notify","reminder.withdraw","conversation.notify","domain.sync")) {
    val base=setOf("nodeId","type","version","commandId","expiresAt")
    val input=if(data["type"]=="domain.sync") {
     require(data.keys==base+setOf("capability","input")); org.json.JSONObject(data.getValue("input"))
    } else {
     require(data.keys==base+setOf("input")); org.json.JSONObject(data.getValue("input"))
    }
    require(data["version"]=="1")
    val command=org.json.JSONObject().put("commandId",data.getValue("commandId")).put("capability",if(data["type"]=="domain.sync") data.getValue("capability") else data.getValue("type"))
     .put("capabilityVersion",1).put("expiresAt",data.getValue("expiresAt")).put("input",input)
    if(data["type"]=="domain.sync")NodeNativeNotifications.fromCommand(this,command.toString(),expectedScope=bound)
    else synchronized(NodeNotificationGate.lock) {
     // Pairing may have changed authority after the envelope's initial check.
     // Keep this gate limited to notifications; geofences use apply -> notification.
     applyForCurrentIdentity({NodeNativeSession.read(this)?.nodeId==bound}) {
      NodeNativeNotifications.fromCommand(this,command.toString())
     }
    }
   } else {
    val payload=NodeNotificationPayload.fromData(data,bound) ?: return
    synchronized(NodeNotificationGate.lock) {
     applyForCurrentIdentity({NodeNativeSession.read(this)?.nodeId==bound}) {
      NodeNativeNotifications.initialize(this)
      NodeNativeNotifications.show(this,payload.commandId,payload.expiresAt,payload.title,payload.body)
     }
    }
   }
  } catch(error:Exception) { notificationLog("fcm_payload",error) }
 }
}
