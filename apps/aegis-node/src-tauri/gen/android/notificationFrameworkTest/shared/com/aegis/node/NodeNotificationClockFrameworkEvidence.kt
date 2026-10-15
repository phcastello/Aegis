package com.aegis.node

import android.app.NotificationManager
import android.content.Context
import android.os.SystemClock
import org.json.JSONObject
import org.junit.Assert.*
import java.time.Instant
import java.util.UUID

/** The fixed JNI bridge keeps a 300s lifetime even when the device wall clock differs. */
internal fun exerciseNotificationLiveClockFramework(context:Context,raw:(String)->JSONObject,live:(String)->JSONObject) {
 val manager=context.getSystemService(NotificationManager::class.java)
 val ids=mutableListOf<String>()
 fun command(now:Long,ttl:Long):JSONObject=JSONObject()
  .put("commandId",UUID.randomUUID().toString().also{ids.add(it)})
  .put("capability","notification.show").put("capabilityVersion",1)
  .put("expiresAt",Instant.ofEpochSecond(now+ttl).toString())
  .put("input",JSONObject().put("title","Aegis framework test").put("body","Fixture"))
 fun wrapper(command:JSONObject,now:Long)=JSONObject().put("command",command).put("validatedNowEpochMillis",now*1000).toString()
 try {
  for(drift in listOf(1L,30L)) {
   val now=Instant.now().epochSecond+drift
   val command=command(now,300)
   val payload=wrapper(command,now)
   val result=live(payload)
   assertEquals("authenticated clock drift=$drift diagnostic=${result.optString("diagnosticCode")}","success",result.getString("status"))
   val id=command.getString("commandId")
   val deadline=SystemClock.elapsedRealtime()+5000
   while(manager.activeNotifications.none{it.tag==id&&it.id==1}&&SystemClock.elapsedRealtime()<deadline)SystemClock.sleep(50)
   assertTrue(manager.activeNotifications.any{it.tag==id&&it.id==1})
   assertEquals("duplicate",live(payload).getString("status"))
   // This metadata is not an extension of the external five-field command.
   assertEquals("failed",raw(payload).getString("status"))
   manager.cancel(id,1);awaitFrameworkNotificationWithdrawal(manager,id)
  }
  val now=Instant.now().epochSecond+30
  val beyond=command(now,301)
  val rejected=live(wrapper(beyond,now))
  assertEquals("failed",rejected.getString("status"))
  assertEquals("android_invalid_notification_payload",rejected.getString("diagnosticCode"))
  val expired=command(now,0)
  assertEquals("expired",live(wrapper(expired,now)).getString("status"))
  val unknownField=command(now,60).put("validatedNowEpochMillis",now*1000)
  assertEquals("failed",live(wrapper(unknownField,now)).getString("status"))
  val invalidClock=JSONObject().put("command",command(now,60)).put("validatedNowEpochMillis",(now*1000).toString())
  assertEquals("failed",live(invalidClock.toString()).getString("status"))
  assertTrue(manager.activeNotifications.none{it.tag==beyond.getString("commandId")||it.tag==expired.getString("commandId")})
  android.util.Log.i("AegisNodeNativeTest","phase=notification_live_authenticated_clock errorType=Checkpoint")
 }finally{ids.forEach{manager.cancel(it,1)}}
}
