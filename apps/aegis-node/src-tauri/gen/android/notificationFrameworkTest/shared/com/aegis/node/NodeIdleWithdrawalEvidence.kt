package com.aegis.node

import android.app.NotificationManager
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.os.SystemClock
import org.json.JSONObject
import org.junit.Assert.*
import java.util.UUID

/** Candidate-only preparation precedes the existing four measured idle intervals. */
internal fun prepareIdleWithdrawalReceipt(context:Context,nodeId:String,invoke:(String,JSONObject)->JSONObject):JSONObject {
 val id=UUID.randomUUID().toString()
 val manager=context.getSystemService(NotificationManager::class.java)
 val posted=fixtureJson("reminder-command",JSONObject().put("nodeId",nodeId).put("reminderId",id).put("commandId",UUID.randomUUID().toString()))
 assertEquals("success",posted.getString("status"));awaitFrameworkReminder(manager,id,1)
 invoke("node_reminders",JSONObject().put("operation","action").put("id",id).put("payload",JSONObject().put("actionId",UUID.randomUUID().toString()).put("expectedRevision",1).put("generation",1).put("action","acknowledge")))
 val actionDeadline=SystemClock.elapsedRealtime()+30000
 fun applied():Boolean=fixtureJson("reminder-actions").getJSONArray("items").let{rows->(0 until rows.length()).map{rows.getJSONObject(it)}.any{it.getString("reminderId")==id&&it.getBoolean("applied")}}
 while(!applied()&&SystemClock.elapsedRealtime()<actionDeadline)SystemClock.sleep(50)
 assertTrue(applied())
 val requestsBefore=fixtureJson("idle").optLong("withdrawal_receipt_requests",0)
 nativeFcm(context,nodeId,"reminder.withdraw",JSONObject().put("reminderId",id).put("generation",1))
 val deadline=SystemClock.elapsedRealtime()+30000
 fun received():Boolean {
  val pending=SQLiteDatabase.openDatabase(context.getDatabasePath("aegis-native-reminders-v1.db").path,null,SQLiteDatabase.OPEN_READONLY).use{db->db.rawQuery("SELECT COUNT(*) FROM withdrawal_receipts WHERE reminderId=?",arrayOf(id)).use{it.moveToFirst();it.getInt(0)}}
  val rows=fixtureJson("reminder-recipients").getJSONArray("items")
  val server=(0 until rows.length()).map{rows.getJSONObject(it)}.single{it.getString("reminderId")==id&&it.getString("nodeId")==nodeId}
  return pending==0&&!server.isNull("withdrawnAt")&&fixtureJson("idle").optLong("withdrawal_receipt_requests",0)>requestsBefore
 }
 while(!received()&&SystemClock.elapsedRealtime()<deadline)SystemClock.sleep(50)
 assertTrue(received());awaitFrameworkReminderWithdrawal(manager,id,1)
 val row=fixtureJson("reminder-recipients").getJSONArray("items").let{rows->(0 until rows.length()).map{rows.getJSONObject(it)}.single{it.getString("reminderId")==id&&it.getString("nodeId")==nodeId}}
 val pump=fixtureJson("withdrawal-pump",JSONObject().put("enabled",true))
 assertTrue(pump.getBoolean("enabled"));assertEquals(5,pump.getInt("pollSeconds"));assertEquals("simulated_fcm",pump.getString("transport"))
 return JSONObject().put("reminderId",id).put("generation",1).put("withdrawnAt",row.getString("withdrawnAt")).put("providerFcmSimulated",true).put("serverReconcilerEnabled",true)
}
