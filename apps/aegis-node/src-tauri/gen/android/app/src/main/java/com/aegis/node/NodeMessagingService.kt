package com.aegis.node
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
class NodeMessagingService:FirebaseMessagingService() {
 override fun onNewToken(token:String) { NodeNativeNotifications.prefs(this).edit().putString("token",token).apply() }
 override fun onMessageReceived(message:RemoteMessage) {
  val payload=NodeNotificationPayload.fromData(message.data,NodeNativeNotifications.prefs(this).getString("nodeId",null)) ?: return
  NodeNativeNotifications.initialize(this)
  NodeNativeNotifications.show(this,payload.commandId,payload.expiresAt,payload.title,payload.body)
 }
}
