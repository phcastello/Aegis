package com.aegis.node
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
class NodeMessagingService:FirebaseMessagingService() {
 override fun onNewToken(token:String) { NodeNativeNotifications.prefs(this).edit().putString("token",token).apply() }
 override fun onMessageReceived(message:RemoteMessage) {
  val d=message.data
  if(d["type"]!="notification.show" || d["version"]!="1" || d.size!=7 || d["nodeId"]!=NodeNativeNotifications.prefs(this).getString("nodeId",null)) return
  NodeNativeNotifications.initialize(this)
  NodeNativeNotifications.show(this,d["commandId"]?:return,d["expiresAt"]?:return,d["title"]?:return,d["body"]?:return)
 }
}
