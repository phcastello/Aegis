package com.aegis.node
import java.util.UUID
/** Fixed FCM schema, never a generic data-payload executor. No Android/WebView dependency. */
data class NodeNotificationPayload(val commandId:String,val expiresAt:String,val title:String,val body:String) {
 companion object {
  private val keys=setOf("nodeId","type","version","commandId","expiresAt","title","body")
  fun validText(title:String,body:String):Boolean = title.isNotBlank() && title.length<=120 && body.length<=2000 && (title+body).toByteArray(Charsets.UTF_8).size<=2800 && !title.any {it.isISOControl()} && !body.any {it.isISOControl() && it!='\n' && it!='\t'}
  fun fromData(data:Map<String,String>,boundNodeId:String?):NodeNotificationPayload? {
   if(boundNodeId==null || data.keys!=keys || data["nodeId"]!=boundNodeId || data["type"]!="notification.show" || data["version"]!="1") return null
   val id=data.getValue("commandId")
   try {if(UUID.fromString(id).toString()!=id || UUID.fromString(id).mostSignificantBits==0L && UUID.fromString(id).leastSignificantBits==0L) return null} catch(_:Exception){return null}
   if(!validText(data.getValue("title"),data.getValue("body"))) return null
   return NodeNotificationPayload(id,data.getValue("expiresAt"),data.getValue("title"),data.getValue("body"))
  }
 }
}
