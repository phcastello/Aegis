package com.aegis.node

import android.Manifest
import android.app.Activity
import android.app.NotificationManager
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.test.platform.app.InstrumentationRegistry

internal const val NODE_NOTIFICATION_CHANNEL = "aegis_node_notifications"
internal fun notificationActivityClass(): Class<out Activity> = Class.forName(
  "com.aegis.node.MainActivity", true, InstrumentationRegistry.getInstrumentation().targetContext.classLoader
).asSubclass(Activity::class.java)

// Verify the permission in the framework independently of the target's private implementation.
internal fun frameworkNotificationPermission(context: Context): Boolean =
  (Build.VERSION.SDK_INT < 33 || context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) &&
    context.getSystemService(NotificationManager::class.java).areNotificationsEnabled()

/** Exercises real notifications, PendingIntent receiver, and private SQLite after R8. */
internal fun exerciseReminderNotificationActions(render:(String)->org.json.JSONObject,context:Context) {
  val manager=context.getSystemService(NotificationManager::class.java)
  val id=java.util.UUID.randomUUID().toString()
  val withdrawn=java.util.UUID.randomUUID().toString()
  val expiry=java.text.SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'0000+00:00'",java.util.Locale.ROOT).apply{timeZone=java.util.TimeZone.getTimeZone("UTC")}.format(java.util.Date(System.currentTimeMillis()+60000))
  fun command(capability:String,input:org.json.JSONObject)=org.json.JSONObject().put("commandId",java.util.UUID.randomUUID().toString()).put("capability",capability).put("capabilityVersion",1).put("expiresAt",expiry).put("input",input).toString()
  fun notify(target:String,generation:Int)=command("reminder.notify",org.json.JSONObject().put("title","Aegis framework").put("body","Fixture").put("reminderId",target).put("revision",generation).put("generation",generation).put("interactionToken","isolated-framework-token-value"))
  fun withdraw(target:String,generation:Int)=command("reminder.withdraw",org.json.JSONObject().put("reminderId",target).put("generation",generation))
  try {
    val malformed=org.json.JSONObject(withdraw(withdrawn,1)).apply{getJSONObject("input").put("generation",-1)}
    org.junit.Assert.assertEquals("failed",render(malformed.toString()).getString("status"))
    malformed.getJSONObject("input").put("generation","1")
    org.junit.Assert.assertEquals("failed",render(malformed.toString()).getString("status"))
    org.junit.Assert.assertEquals("success",render(withdraw(withdrawn,1)).getString("status"))
    org.junit.Assert.assertEquals("expired",render(notify(withdrawn,1)).getString("status"))
    org.junit.Assert.assertTrue(manager.activeNotifications.none{it.tag=="reminder:$withdrawn:1"})
    org.junit.Assert.assertEquals("success",render(notify(id,1)).getString("status"))
    val first=awaitFrameworkReminder(manager,id,1).notification
    org.junit.Assert.assertEquals(listOf("OK","Adiar 10 min"),first.actions.map{it.title.toString()})
    org.junit.Assert.assertNotNull(first.contentIntent)
    org.junit.Assert.assertEquals("success",render(notify(id,2)).getString("status"))
    // Advancing the generation retires its predecessor before any delayed
    // withdrawal command arrives. The pure selector also covers an empty OS list.
    awaitFrameworkReminderWithdrawal(manager,id,1)
    org.junit.Assert.assertEquals("success",render(withdraw(id,1)).getString("status"))
    awaitFrameworkReminder(manager,id,2)
    awaitFrameworkReminderWithdrawal(manager,id,1)
    // A stale button cannot acknowledge the new generation. The receiver is native, no WebView.
    first.actions[0].actionIntent.send()
    android.os.SystemClock.sleep(200)
    val database=android.database.sqlite.SQLiteDatabase.openDatabase(context.getDatabasePath("aegis-native-reminders-v1.db").path,null,android.database.sqlite.SQLiteDatabase.OPEN_READONLY)
    database.use{db->db.rawQuery("SELECT COUNT(*) FROM actions WHERE reminderId=?",arrayOf(id)).use{it.moveToFirst();org.junit.Assert.assertEquals(0,it.getInt(0))}}
    // The same occurrence/revision/generation from another installation scope is invalid.
    context.sendBroadcast(android.content.Intent().setClassName(context.packageName,"com.aegis.node.NodeReminderActionReceiver")
      .putExtra("reminderId",id).putExtra("revision",2L).putExtra("generation",2L).putExtra("action","acknowledge").putExtra("scopeNodeId",java.util.UUID.randomUUID().toString()))
    android.os.SystemClock.sleep(200)
    android.database.sqlite.SQLiteDatabase.openDatabase(context.getDatabasePath("aegis-native-reminders-v1.db").path,null,android.database.sqlite.SQLiteDatabase.OPEN_READONLY).use{db->db.rawQuery("SELECT COUNT(*) FROM actions WHERE reminderId=?",arrayOf(id)).use{it.moveToFirst();org.junit.Assert.assertEquals(0,it.getInt(0))}}
    val current=awaitFrameworkReminder(manager,id,2).notification
    current.actions[1].actionIntent.send()
    val deadline=android.os.SystemClock.elapsedRealtime()+5000
    var count=0
    while(count==0&&android.os.SystemClock.elapsedRealtime()<deadline){
      android.os.SystemClock.sleep(50)
      android.database.sqlite.SQLiteDatabase.openDatabase(context.getDatabasePath("aegis-native-reminders-v1.db").path,null,android.database.sqlite.SQLiteDatabase.OPEN_READONLY).use{db->db.rawQuery("SELECT COUNT(*) FROM actions WHERE reminderId=? AND generation=2 AND action='snooze'",arrayOf(id)).use{it.moveToFirst();count=it.getInt(0)}}
    }
    org.junit.Assert.assertEquals(1,count)
    awaitFrameworkReminderWithdrawal(manager,id,2)
    org.junit.Assert.assertEquals("expired",render(notify(id,2)).getString("status"))
    org.junit.Assert.assertEquals("success",render(notify(id,3)).getString("status"))
  }finally{
    for(generation in 1..3) manager.cancel("reminder:$id:$generation",1)
    manager.cancel("reminder:$withdrawn:1",1)
    context.getDatabasePath("aegis-native-reminders-v1.db").takeIf{it.exists()}?.let{
      android.database.sqlite.SQLiteDatabase.openDatabase(it.path,null,android.database.sqlite.SQLiteDatabase.OPEN_READWRITE).use{db->
       for(table in listOf("presentations","withdrawals"))db.delete(table,"id IN (?,?)",arrayOf(id,withdrawn))
       db.delete("actions","reminderId IN (?,?)",arrayOf(id,withdrawn))
      }
    }
  }
}

/** NotificationManager accepts IPC before its asynchronous record/cancellation is visible. */
internal fun awaitFrameworkReminder(manager:NotificationManager,id:String,generation:Int):android.service.notification.StatusBarNotification {
 val deadline=android.os.SystemClock.elapsedRealtime()+5000
 while(manager.activeNotifications.none{it.tag=="reminder:$id:$generation"} && android.os.SystemClock.elapsedRealtime()<deadline)android.os.SystemClock.sleep(50)
 return manager.activeNotifications.single{it.tag=="reminder:$id:$generation"}
}
internal fun awaitFrameworkReminderWithdrawal(manager:NotificationManager,id:String,generation:Int) {
 awaitFrameworkNotificationWithdrawal(manager,"reminder:$id:$generation")
}

internal fun awaitFrameworkNotificationWithdrawal(manager:NotificationManager,tag:String) {
 val deadline=android.os.SystemClock.elapsedRealtime()+5000
 while(manager.activeNotifications.any{it.tag==tag} && android.os.SystemClock.elapsedRealtime()<deadline)android.os.SystemClock.sleep(50)
 org.junit.Assert.assertTrue("The previous native presentation must be withdrawn before reuse",manager.activeNotifications.none{it.tag==tag})
}
