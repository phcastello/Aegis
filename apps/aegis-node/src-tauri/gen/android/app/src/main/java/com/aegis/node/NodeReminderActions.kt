package com.aegis.node

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import org.json.JSONObject
import java.util.UUID
import java.util.concurrent.TimeUnit

/** Native-only occurrence context, notification generations, and bounded durable outbox. */
internal object NodeNotificationGate { val lock=Any() }
object NodeReminderActions {
  // SQLiteOpenHelper acquired AutoCloseable after older supported Android releases.
  // Declare Closeable here so Kotlin use() is valid on API24+ and after R8.
  class Store(context: Context) : SQLiteOpenHelper(context, "aegis-native-reminders-v1.db", null, 1), java.io.Closeable {
    override fun onCreate(db: SQLiteDatabase) {
      db.execSQL("CREATE TABLE presentations (id TEXT PRIMARY KEY, generation INTEGER NOT NULL, revision INTEGER NOT NULL, token TEXT, expires INTEGER NOT NULL, commandId TEXT NOT NULL,ruleId TEXT,ruleRevision INTEGER)")
      db.execSQL("CREATE TABLE withdrawals (id TEXT PRIMARY KEY,generation INTEGER NOT NULL,expires INTEGER NOT NULL)")
      db.execSQL("CREATE TABLE actions (actionId TEXT PRIMARY KEY, reminderId TEXT NOT NULL, revision INTEGER NOT NULL, generation INTEGER NOT NULL, action TEXT NOT NULL, token TEXT, created INTEGER NOT NULL)")
    }
    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) { error("Unsupported native reminder schema") }
    override fun onOpen(db:SQLiteDatabase) {super.onOpen(db);db.execSQL("CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL)") }
  }
  fun bindIdentity(context:Context,id:String) { synchronized(NodeNotificationGate.lock) {
    Store(context).use{store->val db=store.writableDatabase
      val previous=db.rawQuery("SELECT value FROM metadata WHERE key='scopeId'",null).use{if(it.moveToFirst())it.getString(0)else null}
      if(previous!=id) {
        db.beginTransaction();try{db.delete("actions",null,null);db.delete("presentations",null,null);db.delete("withdrawals",null,null);db.execSQL("INSERT OR REPLACE INTO metadata(key,value) VALUES('scopeId',?)",arrayOf(id));db.setTransactionSuccessful()}finally{db.endTransaction()}
        val manager=context.getSystemService(android.app.NotificationManager::class.java)
        for(notification in manager.activeNotifications)if(notification.tag?.startsWith("reminder:")==true)manager.cancel(notification.tag,notification.id)
        manager.cancel("aegis-action-pending",709)
      }
    }
  } }
  data class Presentation(val id: String, val revision: Long, val generation: Long, val token: String?, val commandId: String,val ruleId:String?=null,val ruleRevision:Long?=null,val observerNodeId:String?=null)
  fun read(input: JSONObject, commandId: String): Presentation {
    require(input.keys().asSequence().toSet() == setOf("title", "body", "reminderId", "revision", "generation", "interactionToken"))
    val id = input.getString("reminderId"); require(UUID.fromString(id).toString() == id && id!="00000000-0000-0000-0000-000000000000")
    require((input.get("revision") is Int || input.get("revision") is Long) && (input.get("generation") is Int || input.get("generation") is Long))
    val revision = input.getLong("revision"); val generation = input.getLong("generation")
    val token = input.getString("interactionToken")
    require(revision >= 1 && generation >= 0 && token.length in 20..2048 && !token.any { it.isISOControl() })
    return Presentation(id, revision, generation, token, commandId)
  }
  fun remember(context: Context, value: Presentation): Boolean { synchronized(NodeNotificationGate.lock) {
    Store(context).use { store ->
      val db=store.writableDatabase
      db.delete("withdrawals","expires < ?",arrayOf(System.currentTimeMillis().toString()))
      db.rawQuery("SELECT generation FROM withdrawals WHERE id=?",arrayOf(value.id)).use { if(it.moveToFirst() && it.getLong(0)>=value.generation) return false }
      db.rawQuery("SELECT generation, revision FROM presentations WHERE id=?", arrayOf(value.id)).use {
        if(it.moveToFirst() && (it.getLong(0) > value.generation || it.getLong(0) == value.generation && it.getLong(1) > value.revision)) return false
      }
      val row=ContentValues().apply {
        put("id",value.id); put("generation",value.generation); put("revision",value.revision)
        put("token",value.token); put("commandId",value.commandId); put("ruleId",value.ruleId); if(value.ruleRevision!=null)put("ruleRevision",value.ruleRevision); put("expires",System.currentTimeMillis()+TimeUnit.DAYS.toMillis(7))
      }
      check(db.insertWithOnConflict("presentations",null,row,SQLiteDatabase.CONFLICT_REPLACE)>=0)
      db.delete("presentations","expires < ?",arrayOf(System.currentTimeMillis().toString()))
      return true
    }
  }
  }
  fun addButtons(context: Context, builder: NotificationCompat.Builder, presentation: Presentation) {
    val scope=NodeNativeSession.read(context)?.nodeId
    for ((action,label) in listOf("acknowledge" to "OK", "snooze" to "Adiar 10 min")) {
      val intent = Intent(context, NodeReminderActionReceiver::class.java)
        .setAction("com.aegis.node.reminder.${scope?:"unbound"}.$action.${presentation.id}.${presentation.generation}")
        .putExtra("scopeNodeId",scope)
        .putExtra("reminderId",presentation.id).putExtra("revision",presentation.revision)
        .putExtra("generation",presentation.generation).putExtra("action",action)
      val pending=PendingIntent.getBroadcast(context,0,intent,PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
      builder.addAction(0,label,pending)
    }
  }
  fun enqueue(context: Context, intent: Intent): Boolean { synchronized(NodeNotificationGate.lock) {
    val capturedScope=intent.getStringExtra("scopeNodeId")
    if(capturedScope!=NodeNativeSession.read(context)?.nodeId)return false
    val id=intent.getStringExtra("reminderId") ?: return false
    val revision=intent.getLongExtra("revision",-1); val generation=intent.getLongExtra("generation",-1)
    val action=intent.getStringExtra("action") ?: return false
    if(action !in setOf("acknowledge","snooze")) return false
    Store(context).use { store ->
      val db=store.writableDatabase
      db.beginTransaction()
      try {
        val currentScope=db.rawQuery("SELECT value FROM metadata WHERE key='scopeId'",null).use{if(it.moveToFirst())it.getString(0)else null}
        if(currentScope!=capturedScope)return false
        db.delete("actions","created < ?",arrayOf((System.currentTimeMillis()-TimeUnit.DAYS.toMillis(7)).toString()))
        val row=db.rawQuery("SELECT revision,generation,token,expires FROM presentations WHERE id=?",arrayOf(id)).use {
          if(!it.moveToFirst() || it.getLong(0)!=revision || it.getLong(1)!=generation || it.getLong(3)<System.currentTimeMillis()) return false
          if(it.isNull(2)) null else it.getString(2)
        }
        db.rawQuery("SELECT COUNT(*) FROM actions",null).use { it.moveToFirst(); if(it.getInt(0)>=100) return false }
        // First local action on the displayed revision wins as well; no second offline overwrite.
        db.rawQuery("SELECT actionId FROM actions WHERE reminderId=? AND revision=? AND generation=?",arrayOf(id,revision.toString(),generation.toString())).use { if(it.moveToFirst()) return true }
        val values=ContentValues().apply {
          put("actionId",UUID.randomUUID().toString()); put("reminderId",id); put("revision",revision); put("generation",generation)
          put("action",action); put("token",row); put("created",System.currentTimeMillis())
        }
        check(db.insertOrThrow("actions",null,values)>0)
        db.setTransactionSuccessful()
      } finally { db.endTransaction() }
    }
    withdraw(context,id,generation)
    // It is a pending synchronization, never presented as server acknowledgement or punctual offline snooze.
    pendingNotice(context,action)
    NodeBackgroundWork.enqueue(context)
    return true
  }
  }
  fun withdraw(context: Context, id: String, generation: Long) { synchronized(NodeNotificationGate.lock) {
    require(UUID.fromString(id).toString()==id && id!="00000000-0000-0000-0000-000000000000" && generation>=0)
    Store(context).use { store ->
      val previous=store.readableDatabase.rawQuery("SELECT generation FROM withdrawals WHERE id=?",arrayOf(id)).use { if(it.moveToFirst()) it.getLong(0) else -1L }
      check(store.writableDatabase.insertWithOnConflict("withdrawals",null,ContentValues().apply{put("id",id);put("generation",maxOf(previous,generation));put("expires",System.currentTimeMillis()+TimeUnit.DAYS.toMillis(7))},SQLiteDatabase.CONFLICT_REPLACE)>=0)
      NotificationManagerCompat.from(context).cancel("reminder:$id:$generation",1)
      store.readableDatabase.rawQuery("SELECT generation FROM presentations WHERE id=?",arrayOf(id)).use {
        if(it.moveToFirst() && it.getLong(0)<=generation) {
          NotificationManagerCompat.from(context).cancel("reminder:$id:${it.getLong(0)}",1)
          store.writableDatabase.delete("presentations","id=? AND generation<=?",arrayOf(id,generation.toString()))
        }
      }
    }
  }
  }
  fun enqueueUi(context:Context,id:String,body:JSONObject,expectedScope:String):JSONObject {
    synchronized(NodeNotificationGate.lock) {
      require(NodeNativeSession.read(context)?.nodeId==expectedScope)
      bindIdentity(context,expectedScope)
      require(UUID.fromString(id).toString()==id)
      val actionId=body.getString("actionId");require(UUID.fromString(actionId).toString()==actionId)
      val revision=body.getLong("expectedRevision");val generation=body.getLong("generation");val action=body.getString("action")
      require(revision>0&&generation>=0&&action in setOf("acknowledge","snooze")&&body.optInt("snoozeMinutes",10)==10)
      Store(context).use{store->val db=store.writableDatabase;db.beginTransaction();try {
        db.delete("actions","created<?",arrayOf((System.currentTimeMillis()-TimeUnit.DAYS.toMillis(7)).toString()))
        db.rawQuery("SELECT actionId FROM actions WHERE reminderId=? AND revision=? AND generation=?",arrayOf(id,revision.toString(),generation.toString())).use{
          if(it.moveToFirst())return JSONObject().put("pending",true).put("applied",false)
        }
        db.rawQuery("SELECT COUNT(*) FROM actions",null).use{it.moveToFirst();require(it.getInt(0)<100)}
        db.insertOrThrow("actions",null,ContentValues().apply{put("actionId",actionId);put("reminderId",id);put("revision",revision);put("generation",generation);put("action",action);putNull("token");put("created",System.currentTimeMillis())})
        db.setTransactionSuccessful()
      }finally{db.endTransaction()}}
      withdraw(context,id,generation);pendingNotice(context,action);NodeBackgroundWork.enqueue(context)
      return JSONObject().put("pending",true).put("applied",false)
    }
  }
  fun reconcileRules(context:Context,rules:List<JSONObject>) {
    val current=rules.filter{it.optString("status")=="active"}.associate{it.getString("id") to it.getLong("revision")}
    val pendingEvents=NodeGeofences.pendingOccurrences(context)
    val obsolete=mutableListOf<Pair<String,Long>>()
    Store(context).use{store->store.readableDatabase.rawQuery("SELECT id,generation,ruleId,ruleRevision FROM presentations WHERE ruleId IS NOT NULL",null).use{
      while(it.moveToNext())if(it.getString(0) in pendingEvents && current[it.getString(2)]!=it.getLong(3))obsolete.add(it.getString(0) to it.getLong(1))
    }}
    for((id,generation)in obsolete){withdraw(context,id,generation);discardAction(context,id)}
  }
  fun withdrawScoped(context:Context,id:String,generation:Long,scope:String,discard:Boolean=false) { synchronized(NodeNotificationGate.lock) {
    if(NodeNativeSession.read(context)?.nodeId!=scope)return
    withdraw(context,id,generation)
    if(discard)discardAction(context,id)
  } }
  fun discardAction(context:Context,id:String) {Store(context).use{it.writableDatabase.delete("actions","reminderId=?",arrayOf(id))}}
  fun reconcilePresentations(context:Context) {
    val scope=NodeNativeSession.read(context)?.nodeId ?: return
    val pendingEvents=NodeGeofences.pendingOccurrences(context)
    val items=mutableListOf<JSONObject>()
    Store(context).use{store->store.readableDatabase.rawQuery("SELECT id,revision,generation FROM presentations WHERE expires>?",arrayOf(System.currentTimeMillis().toString())).use{
      while(it.moveToNext())if(it.getString(0) !in pendingEvents)items.add(JSONObject().put("reminderId",it.getString(0)).put("revision",it.getLong(1)).put("generation",it.getLong(2)))
    }}
    for(batch in items.chunked(100)) {
      val response=NodeNativeSession.call(context,"POST","/api/nodes/reminders/reconcile",JSONObject().put("items",org.json.JSONArray(batch)),expectedScope=scope)
      for(item in response.getJSONArray("withdraw").objects())withdrawScoped(context,item.getString("reminderId"),item.getLong("generation"),scope)
    }
  }
  fun flush(context: Context) {
    val scope=NodeNativeSession.read(context)?.nodeId ?: return
    bindIdentity(context,scope)
    Store(context).use { store ->
      val db=store.writableDatabase
      db.delete("actions","created < ?",arrayOf((System.currentTimeMillis()-TimeUnit.DAYS.toMillis(7)).toString()))
      val pending=mutableListOf<Pair<String,JSONObject>>()
      db.rawQuery("SELECT actionId,reminderId,revision,generation,action,token FROM actions ORDER BY created LIMIT 100",null).use {
        while(it.moveToNext()) pending.add(it.getString(1) to JSONObject().put("actionId",it.getString(0)).put("expectedRevision",it.getLong(2)).put("generation",it.getLong(3)).put("action",it.getString(4)).put("snoozeMinutes",10).apply {if(!it.isNull(5)) put("token",it.getString(5))})
      }
      val unsynchronizedGeo=NodeGeofences.pendingOccurrences(context)
      for((id,body) in pending) {
        if(!body.has("token") && id in unsynchronizedGeo)continue
        try {
          val tokenOnly=body.has("token")
          val path=if(tokenOnly) "/api/reminder-actions/$id" else "/api/nodes/reminders/$id/actions"
          val response=NodeNativeSession.call(context,"POST",path,body,tokenOnly,expectedScope=scope)
          // A snooze response already names the next generation. Withdraw only the presentation acted on.
          withdrawScoped(context,id,body.getLong("generation"),scope)
          db.delete("actions","actionId=?",arrayOf(body.getString("actionId")))
        } catch(error:NodeNativeSession.HttpFailure) {
          if(error.status in setOf(400,401,403,404,409,410)) db.delete("actions","actionId=?",arrayOf(body.getString("actionId"))) else throw error
        }
      }
      db.rawQuery("SELECT COUNT(*) FROM actions",null).use{it.moveToFirst();if(it.getInt(0)==0)NotificationManagerCompat.from(context).cancel("aegis-action-pending",709)}
    }
  }
  private fun pendingNotice(context:Context,action:String) {
    if(!NodeNativeNotifications.granted(context))return
    val click=PendingIntent.getActivity(context,709,Intent(context,MainActivity::class.java),PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
    val message=if(action=="snooze") "Adiamento salvo no aparelho; o novo horário será confirmado ao sincronizar." else "Reconhecimento salvo no aparelho; aguardando sincronização."
    NotificationManagerCompat.from(context).notify("aegis-action-pending",709,NotificationCompat.Builder(context,NodeNativeNotifications.CHANNEL)
      .setSmallIcon(R.drawable.ic_node_notification).setContentTitle("Aegis · ação pendente").setContentText(message).setStyle(NotificationCompat.BigTextStyle().bigText(message))
      .setSilent(true).setContentIntent(click).setAutoCancel(true).build())
  }
  fun pending(context: Context): Int = Store(context).use { store -> store.readableDatabase.rawQuery("SELECT COUNT(*) FROM actions",null).use { it.moveToFirst();it.getInt(0) } }
}
class NodeReminderActionReceiver : BroadcastReceiver() {
  override fun onReceive(context: Context, intent: Intent) { try { NodeReminderActions.enqueue(context,intent) } catch(error:Exception) { notificationLog("action_persist",error) } }
}
