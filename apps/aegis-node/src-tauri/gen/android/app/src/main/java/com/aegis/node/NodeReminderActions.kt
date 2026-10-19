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
import androidx.work.*
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID
import java.util.concurrent.TimeUnit

/** Native-only occurrence context, notification generations, and bounded durable outbox. */
internal object NodeNotificationGate { val lock=Any() }
object NodeReminderActions {
  private val receiptFlushing=java.util.concurrent.atomic.AtomicBoolean(false)
  private val actionFlushing=java.util.concurrent.atomic.AtomicBoolean(false)
  // SQLiteOpenHelper acquired AutoCloseable after older supported Android releases.
  // Declare Closeable here so Kotlin use() is valid on API24+ and after R8.
  class Store(private val context: Context) : SQLiteOpenHelper(context, "aegis-native-reminders-v1.db", null, 1), java.io.Closeable {
    private val existingDatabase=context.getDatabasePath("aegis-native-reminders-v1.db").exists()
    override fun onCreate(db: SQLiteDatabase) {
      db.execSQL("CREATE TABLE presentations (id TEXT PRIMARY KEY, generation INTEGER NOT NULL, revision INTEGER NOT NULL, token TEXT, expires INTEGER NOT NULL, commandId TEXT NOT NULL,ruleId TEXT,ruleRevision INTEGER)")
      db.execSQL("CREATE TABLE withdrawals (id TEXT PRIMARY KEY,generation INTEGER NOT NULL,expires INTEGER NOT NULL)")
      db.execSQL("CREATE TABLE actions (actionId TEXT PRIMARY KEY, reminderId TEXT NOT NULL, revision INTEGER NOT NULL, generation INTEGER NOT NULL, action TEXT NOT NULL, token TEXT, created INTEGER NOT NULL)")
    }
    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) { error("Unsupported native reminder schema") }
    override fun onOpen(db:SQLiteDatabase) { synchronized(NodeNotificationGate.lock) {
      super.onOpen(db)
      val columns=db.rawQuery("PRAGMA table_info(actions)",null).use{rows->buildSet<String>{while(rows.moveToNext())add(rows.getString(1))}}
      if(existingDatabase && "attempts" !in columns) {
        val file=context.getDatabasePath("aegis-native-reminders-v1.db")
        val backup=java.io.File(file.parentFile,file.name+".pre-action-diagnostics.bak")
        if(!backup.exists()) {
          db.rawQuery("PRAGMA wal_checkpoint(FULL)",null).use{while(it.moveToNext()){} }
          db.beginTransaction();try{file.copyTo(backup,overwrite=false);db.setTransactionSuccessful()}finally{db.endTransaction()}
        }
      }
      db.execSQL("CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL)")
      // Additive and idempotent for installations already using schema version 1.
      db.execSQL("CREATE TABLE IF NOT EXISTS withdrawal_receipts(reminderId TEXT NOT NULL,generation INTEGER NOT NULL,created INTEGER NOT NULL,PRIMARY KEY(reminderId,generation))")
      for((name,definition)in listOf("attempts" to "INTEGER NOT NULL DEFAULT 0","lastAttempt" to "INTEGER","lastResult" to "TEXT NOT NULL DEFAULT 'queued'","nextAttempt" to "INTEGER NOT NULL DEFAULT 0"))if(name !in columns)db.execSQL("ALTER TABLE actions ADD COLUMN $name $definition")
      // Terminal results leave the send queue, retaining bounded traceability without a token.
      db.execSQL("CREATE TABLE IF NOT EXISTS action_history(actionId TEXT PRIMARY KEY,reminderId TEXT NOT NULL,revision INTEGER NOT NULL,generation INTEGER NOT NULL,action TEXT NOT NULL,created INTEGER NOT NULL,attempts INTEGER NOT NULL,lastAttempt INTEGER,lastResult TEXT NOT NULL,outcome TEXT NOT NULL,completed INTEGER NOT NULL)")
      db.delete("action_history","completed<?",arrayOf((System.currentTimeMillis()-TimeUnit.DAYS.toMillis(30)).toString()))
      db.execSQL("DELETE FROM action_history WHERE actionId NOT IN (SELECT actionId FROM action_history ORDER BY completed DESC LIMIT 100)")
    } }
  }
  fun bindIdentity(context:Context,id:String) { synchronized(NodeNotificationGate.lock) {
    if(!NodeNativeSession.bindingAllowed(context,id))return
    Store(context).use{store->val db=store.writableDatabase
      val previous=db.rawQuery("SELECT value FROM metadata WHERE key='scopeId'",null).use{if(it.moveToFirst())it.getString(0)else null}
      if(previous!=id) {
        val known=mutableListOf<Pair<String,Long>>()
        db.rawQuery("SELECT id,generation FROM presentations",null).use{while(it.moveToNext())known.add(it.getString(0) to it.getLong(1))}
        val manager=context.getSystemService(android.app.NotificationManager::class.java)
        val active=try{manager.activeNotifications.mapNotNull{notification->notification.tag?.let{it to notification.id}}}catch(error:Exception){notificationLog("scope_list",error);emptyList()}
        // notify() accepts IPC before its record appears in activeNotifications.
        // Under the same gate as show(), cancel every durable accepted generation.
        for((tag,notificationId)in identityReminderTags(known,active))try{manager.cancel(tag,notificationId)}catch(error:Exception){notificationLog("scope_withdraw",error)}
        try{manager.cancel("aegis-action-pending",709)}catch(error:Exception){notificationLog("scope_withdraw",error)}
        db.beginTransaction();try{db.delete("actions",null,null);db.delete("action_history",null,null);db.delete("presentations",null,null);db.delete("withdrawals",null,null);db.delete("withdrawal_receipts",null,null);db.execSQL("INSERT OR REPLACE INTO metadata(key,value) VALUES('scopeId',?)",arrayOf(id));db.setTransactionSuccessful()}finally{db.endTransaction()}
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
        if(it.moveToFirst()) {
          if(it.getLong(0) > value.generation || it.getLong(0) == value.generation && it.getLong(1) > value.revision)return false
          supersededReminderTag(value.id,it.getLong(0),value.generation)?.let { (tag,notificationId)->
            // Retire the accepted old generation even while its post is still in
            // the OS queue. On failure keep its row and report failure for retry.
            notificationPhase("generation_withdraw",NotificationDiagnostic.POST){NotificationManagerCompat.from(context).cancel(tag,notificationId)}
          }
        }
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
    NodeBackgroundWork.foregroundActions(context)
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
  /** A remote withdrawal receipt is separate from a local OK/snooze presentation change. */
  fun withdrawRemote(context:Context,id:String,generation:Long,expectedScope:String?=null) { synchronized(NodeNotificationGate.lock) {
    require(generation>=1)
    val scope=NodeNativeSession.read(context)?.nodeId
    if(expectedScope!=null && scope!=expectedScope)return
    // An unbound renderer has no authenticated Node to attribute a receipt to.
    if(scope==null){withdraw(context,id,generation);return}
    Store(context).use{store->val db=store.writableDatabase
      val bound=db.rawQuery("SELECT value FROM metadata WHERE key='scopeId'",null).use{if(it.moveToFirst())it.getString(0)else null}
      check(bound==scope)
      db.delete("withdrawal_receipts","created<?",arrayOf((System.currentTimeMillis()-NodeWithdrawalReceiptPolicy.TTL_MILLIS).toString()))
      val exists=db.rawQuery("SELECT 1 FROM withdrawal_receipts WHERE reminderId=? AND generation=?",arrayOf(id,generation.toString())).use{it.moveToFirst()}
      val count=db.rawQuery("SELECT COUNT(*) FROM withdrawal_receipts",null).use{it.moveToFirst();it.getInt(0)}
      check(exists || count<NodeWithdrawalReceiptPolicy.MAX_ITEMS)
    }
    // cancel() and the durable high-water run under the rendering gate. A crash
    // before receipt persistence is safe: replay repeats the idempotent cancel.
    withdraw(context,id,generation)
    val inserted=Store(context).use{store->
      val db=store.writableDatabase
      val exists=db.rawQuery("SELECT 1 FROM withdrawal_receipts WHERE reminderId=? AND generation=?",arrayOf(id,generation.toString())).use{it.moveToFirst()}
      db.execSQL("INSERT OR IGNORE INTO withdrawal_receipts(reminderId,generation,created) VALUES(?,?,?)",arrayOf<Any>(id,generation,System.currentTimeMillis()))
      !exists
    }
    scheduleWithdrawalReceipts(context,append=inserted)
  } }
  fun scheduleWithdrawalReceipts(context:Context,append:Boolean=false) {
    if(!hasWithdrawalReceipts(context))return
    val work=OneTimeWorkRequestBuilder<NodeWithdrawalReceiptWorker>().setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()).setBackoffCriteria(BackoffPolicy.EXPONENTIAL,30,TimeUnit.SECONDS).build()
    WorkManager.getInstance(context).enqueueUniqueWork("aegis-withdrawal-receipts",if(append)ExistingWorkPolicy.APPEND_OR_REPLACE else ExistingWorkPolicy.KEEP,work)
  }
  fun hasWithdrawalReceipts(context:Context):Boolean = synchronized(NodeNotificationGate.lock) {
    val scope=NodeNativeSession.read(context)?.nodeId ?: return false
    Store(context).use{store->val db=store.writableDatabase
      val bound=db.rawQuery("SELECT value FROM metadata WHERE key='scopeId'",null).use{if(it.moveToFirst())it.getString(0)else null}
      if(bound!=scope)return false
      db.delete("withdrawal_receipts","created<?",arrayOf((System.currentTimeMillis()-NodeWithdrawalReceiptPolicy.TTL_MILLIS).toString()))
      db.rawQuery("SELECT 1 FROM withdrawal_receipts LIMIT 1",null).use{it.moveToFirst()}
    }
  }
  fun flushWithdrawalReceipts(context:Context):Boolean {
    if(!receiptFlushing.compareAndSet(false,true))return hasWithdrawalReceipts(context)
    try {
    val scope=NodeNativeSession.read(context)?.nodeId ?: return false
    val requested=synchronized(NodeNotificationGate.lock) {
      if(!hasWithdrawalReceipts(context))return false
      Store(context).use{store->store.readableDatabase.rawQuery("SELECT reminderId,generation FROM withdrawal_receipts ORDER BY created LIMIT 100",null).use{rows->
        val result=linkedSetOf<Pair<String,Long>>();while(rows.moveToNext())result.add(rows.getString(0) to rows.getLong(1));result
      }}
    }
    val items=JSONArray(requested.map{(id,generation)->JSONObject().put("reminderId",id).put("generation",generation)})
    val response=NodeNativeSession.call(context,"POST","/api/nodes/reminders/withdrawals/receipts",JSONObject().put("items",items),expectedScope=scope)
    val received=response.getJSONArray("received").objects().map{it.getString("reminderId") to it.getLong("generation")}
    synchronized(NodeNotificationGate.lock) {
      if(NodeNativeSession.read(context)?.nodeId!=scope)return false
      Store(context).use{store->val db=store.writableDatabase
        val bound=db.rawQuery("SELECT value FROM metadata WHERE key='scopeId'",null).use{if(it.moveToFirst())it.getString(0)else null}
        if(bound!=scope)return false
        db.beginTransaction();try {
          for((id,generation)in NodeWithdrawalReceiptPolicy.received(requested,received))db.delete("withdrawal_receipts","reminderId=? AND generation=?",arrayOf(id,generation.toString()))
          db.setTransactionSuccessful()
        }finally{db.endTransaction()}
      }
      return hasWithdrawalReceipts(context)
    }
    }finally{receiptFlushing.set(false)}
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
        db.rawQuery("SELECT actionId FROM actions WHERE reminderId=? AND revision=? AND generation=?",arrayOf(id,revision.toString(),generation.toString())).use{
          if(it.moveToFirst())return JSONObject().put("pending",true).put("applied",false)
        }
        db.rawQuery("SELECT COUNT(*) FROM actions",null).use{it.moveToFirst();require(it.getInt(0)<100)}
        db.insertOrThrow("actions",null,ContentValues().apply{put("actionId",actionId);put("reminderId",id);put("revision",revision);put("generation",generation);put("action",action);putNull("token");put("created",System.currentTimeMillis())})
        db.setTransactionSuccessful()
      }finally{db.endTransaction()}}
      withdraw(context,id,generation);pendingNotice(context,action);NodeBackgroundWork.enqueue(context);NodeBackgroundWork.foregroundActions(context)
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
  fun discardAction(context:Context,id:String) {synchronized(NodeNotificationGate.lock){Store(context).use{store->
    val db=store.writableDatabase
    val ids=db.rawQuery("SELECT actionId FROM actions WHERE reminderId=?",arrayOf(id)).use{rows->buildList<String>{while(rows.moveToNext())add(rows.getString(0))}}
    for(actionId in ids)finishAction(db,actionId,"rejected_superseded","failed")
  }}}
  fun reconcilePresentations(context:Context) {
    val scope=NodeNativeSession.read(context)?.nodeId ?: return
    val pendingEvents=NodeGeofences.pendingOccurrences(context)
    val items=mutableListOf<JSONObject>()
    Store(context).use{store->store.readableDatabase.rawQuery("SELECT id,revision,generation FROM presentations WHERE expires>?",arrayOf(System.currentTimeMillis().toString())).use{
      while(it.moveToNext())if(it.getString(0) !in pendingEvents)items.add(JSONObject().put("reminderId",it.getString(0)).put("revision",it.getLong(1)).put("generation",it.getLong(2)))
    }}
    for(batch in items.chunked(100)) {
      val response=NodeNativeSession.call(context,"POST","/api/nodes/reminders/reconcile",JSONObject().put("items",org.json.JSONArray(batch)),expectedScope=scope)
      for(item in response.getJSONArray("withdraw").objects())withdrawRemote(context,item.getString("reminderId"),item.getLong("generation"),scope)
    }
  }
  /** Eligible tokenless actions may depend on an occurrence still in the geo outbox. */
  fun pendingGeographicDependencies(context:Context):Set<String> {
    val occurrences=NodeGeofences.pendingOccurrences(context)
    if(occurrences.isEmpty())return emptySet()
    return Store(context).use{store->store.readableDatabase.rawQuery("SELECT reminderId FROM actions WHERE token IS NULL AND (lastAttempt IS NULL OR lastAttempt<=?) ORDER BY created LIMIT 100",arrayOf((System.currentTimeMillis()-30000).toString())).use{rows->
      buildSet<String>{while(rows.moveToNext()){val id=rows.getString(0);if(id in occurrences)add(id)}}
    }}
  }
  /** Shared foreground/worker drain: one HTTP owner, four sends and at most 28 s. */
  fun flush(context: Context,foreground:Boolean=false,deadlineElapsed:Long=android.os.SystemClock.elapsedRealtime()+20000):Boolean {
    if(!actionFlushing.compareAndSet(false,true))return pending(context)>0
    val deadline=deadlineElapsed
    try {
      val scope=NodeNativeSession.read(context)?.nodeId ?: return pending(context)>0
      synchronized(NodeNotificationGate.lock){if(!applyForCurrentIdentity({NodeNativeSession.bindingAllowed(context,scope)}){bindIdentity(context,scope)})return false}
      val pending=Store(context).use{store->store.readableDatabase.rawQuery("SELECT actionId,reminderId,revision,generation,action,token,attempts FROM actions WHERE " + (if(foreground) "(lastAttempt IS NULL OR lastAttempt<=?)" else "nextAttempt<=?") + " ORDER BY created LIMIT 100",arrayOf((System.currentTimeMillis()-(if(foreground)30000 else 0)).toString())).use{rows->
        buildList<Pair<String,JSONObject>>{while(rows.moveToNext())add(rows.getString(1) to JSONObject().put("actionId",rows.getString(0)).put("expectedRevision",rows.getLong(2)).put("generation",rows.getLong(3)).put("action",rows.getString(4)).put("snoozeMinutes",10).apply{if(!rows.isNull(5))put("token",rows.getString(5))})}
      }}
      val unsynchronizedGeo=NodeGeofences.pendingOccurrences(context)
      var requests=0
      for((id,body)in pending) {
        if(requests>=4 || android.os.SystemClock.elapsedRealtime()>=deadline || NodeNativeSession.read(context)?.nodeId!=scope)break
        val actionId=body.getString("actionId")
        if(!body.has("token") && id in unsynchronizedGeo) {
          Store(context).use{it.writableDatabase.update("actions",ContentValues().apply{put("lastResult","awaiting_occurrence_sync");put("nextAttempt",System.currentTimeMillis()+30000)},"actionId=?",arrayOf(actionId))};continue
        }
        val attempt=synchronized(NodeNotificationGate.lock){
          if(NodeNativeSession.read(context)?.nodeId!=scope)return false
          Store(context).use{store->val db=store.writableDatabase
            val previous=db.rawQuery("SELECT attempts FROM actions WHERE actionId=?",arrayOf(actionId)).use{if(it.moveToFirst())it.getInt(0)else -1}
            if(previous<0)0 else (previous+1).also{count->db.update("actions",ContentValues().apply{put("attempts",count);put("lastAttempt",System.currentTimeMillis());put("lastResult","sending");put("nextAttempt",System.currentTimeMillis()+NodeReminderActionPolicy.delayMillis(count))},"actionId=?",arrayOf(actionId))}
          }
        }
        if(attempt==0)continue
        requests++
        try {
          val tokenOnly=body.has("token")
          val path=if(tokenOnly) "/api/reminder-actions/$id" else "/api/nodes/reminders/$id/actions"
          val response=NodeNativeSession.call(context,"POST",path,body,tokenOnly,expectedScope=scope,connectTimeoutMillis=3000,readTimeoutMillis=5000)
          require(response.has("applied") && response.has("conflict"))
          withdrawScoped(context,id,body.getLong("generation"),scope)
          synchronized(NodeNotificationGate.lock){if(NodeNativeSession.read(context)?.nodeId==scope)Store(context).use{finishAction(it.writableDatabase,actionId,if(response.optBoolean("applied"))"accepted" else "rejected_conflict",if(response.optBoolean("applied"))"resolved" else "failed")}}
        }catch(error:Exception) {
          val result=when(error){is NodeNativeSession.HttpFailure->NodeReminderActionPolicy.httpResult(error.status);is org.json.JSONException,is IllegalArgumentException->"invalid_response";else->"network_unavailable"}
          synchronized(NodeNotificationGate.lock){if(NodeNativeSession.read(context)?.nodeId==scope)Store(context).use{store->
            if(NodeReminderActionPolicy.permanent(result))finishAction(store.writableDatabase,actionId,result,"failed")
            else store.writableDatabase.update("actions",ContentValues().apply{put("lastResult",result)},"actionId=?",arrayOf(actionId))
          }}
        }
      }
      if(pending(context)==0)NotificationManagerCompat.from(context).cancel("aegis-action-pending",709)
      return pending(context)>0
    }finally{actionFlushing.set(false)}
  }
  private fun finishAction(db:SQLiteDatabase,actionId:String,result:String,outcome:String) {
    db.beginTransaction();try {
      db.execSQL("INSERT OR REPLACE INTO action_history(actionId,reminderId,revision,generation,action,created,attempts,lastAttempt,lastResult,outcome,completed) SELECT actionId,reminderId,revision,generation,action,created,attempts,lastAttempt,?,?,? FROM actions WHERE actionId=?",arrayOf<Any>(result,outcome,System.currentTimeMillis(),actionId))
      db.delete("actions","actionId=?",arrayOf(actionId));db.setTransactionSuccessful()
    }finally{db.endTransaction()}
  }
  /** Safe public metadata only: never IDs, token, text, or coordinates. */
  fun diagnostics(context:Context):JSONObject {
    val now=System.currentTimeMillis();val rows=JSONArray();var pending=0;var failed=0;var temporary=false
    val scheduled=NodeNativeNotifications.prefs(context).getBoolean("actionsRetryScheduled",false)
    Store(context).use{store->val db=store.readableDatabase
      db.rawQuery("SELECT action,created,attempts,lastAttempt,lastResult,nextAttempt FROM actions ORDER BY created LIMIT 100",null).use{cursor->while(cursor.moveToNext()){
        pending++;val result=cursor.getString(4);if(result in setOf("network_unavailable","temporary_http","credential_unavailable","invalid_response"))temporary=true
        rows.put(JSONObject().put("type",cursor.getString(0)).put("ageSeconds",maxOf(0,(now-cursor.getLong(1))/1000)).put("attempts",cursor.getInt(2)).put("lastAttemptAt",if(cursor.isNull(3))JSONObject.NULL else cursor.getLong(3)).put("lastResult",result).put("state","pending").put("retryScheduled",scheduled).put("nextAttemptAt",cursor.getLong(5)))
      }}
      db.rawQuery("SELECT action,created,attempts,lastAttempt,lastResult FROM action_history WHERE outcome='failed' ORDER BY completed DESC LIMIT 100",null).use{cursor->while(cursor.moveToNext()){
        failed++;if(rows.length()<100)rows.put(JSONObject().put("type",cursor.getString(0)).put("ageSeconds",maxOf(0,(now-cursor.getLong(1))/1000)).put("attempts",cursor.getInt(2)).put("lastAttemptAt",if(cursor.isNull(3))JSONObject.NULL else cursor.getLong(3)).put("lastResult",cursor.getString(4)).put("state","failed").put("retryScheduled",false))
      }}
    }
    return JSONObject().put("pendingActions",pending).put("failedActions",failed).put("actionDiagnostics",rows).put("actionSyncState",when{actionFlushing.get()->"syncing";temporary->"temporarily_unavailable";pending>0->"pending";failed>0->"permanent_failure";else->"idle"})
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

/** FCM receivers only persist receipts; authenticated delivery runs outside the receiver. */
class NodeWithdrawalReceiptWorker(context:Context,params:WorkerParameters):Worker(context,params) {
  override fun doWork():Result {
    val pending=try{NodeReminderActions.flushWithdrawalReceipts(applicationContext)}catch(_:Exception){NodeReminderActions.hasWithdrawalReceipts(applicationContext)}
    return if(NodeWithdrawalReceiptPolicy.retry(runAttemptCount,pending))Result.retry()else Result.success()
  }
}
