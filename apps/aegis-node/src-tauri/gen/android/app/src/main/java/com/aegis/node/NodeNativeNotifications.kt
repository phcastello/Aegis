package com.aegis.node

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import com.google.firebase.FirebaseApp
import com.google.firebase.messaging.FirebaseMessaging
import java.time.Instant
import java.util.UUID
import org.json.JSONObject

/** One native renderer for both live JNI and the background Firebase service. */
object NodeNativeNotifications {
  const val CHANNEL = "aegis_node_notifications"
  private val tokenRequested=java.util.concurrent.atomic.AtomicBoolean(false)
  fun prefs(context: Context) = context.getSharedPreferences("aegis-node-push-v1", Context.MODE_PRIVATE)
  fun granted(context: Context): Boolean {
    if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return false
    if (!NotificationManagerCompat.from(context).areNotificationsEnabled()) return false
    return Build.VERSION.SDK_INT < 26 || context.getSystemService(NotificationManager::class.java).getNotificationChannel(CHANNEL)?.importance != NotificationManager.IMPORTANCE_NONE
  }
  fun ensureChannel(context: Context) {
    if (Build.VERSION.SDK_INT >= 26) {
      val manager = context.getSystemService(NotificationManager::class.java)
      if (manager.getNotificationChannel(CHANNEL) == null) {
        manager.createNotificationChannel(NotificationChannel(CHANNEL, "Aegis", NotificationManager.IMPORTANCE_DEFAULT))
      }
      check(manager.getNotificationChannel(CHANNEL) != null)
    }
  }
  fun initialize(context: Context) {
    try { notificationPhase("channel", NotificationDiagnostic.CHANNEL) { ensureChannel(context) } }
    catch (_: NotificationPhaseFailure) { /* show() retries and reports the bounded code. */ }
    if (FirebaseApp.initializeApp(context) != null && tokenRequested.compareAndSet(false,true)) FirebaseMessaging.getInstance().token.addOnSuccessListener { token ->
      // Recover the SDK's durable current token once per process, including a previous interrupted update.
      if (prefs(context).getString("token", null) != token) { if(prefs(context).edit().putString("token", token).commit())NodeBackgroundWork.enqueue(context) else tokenRequested.set(false) }
    }.addOnFailureListener { tokenRequested.set(false) }
  }
  // Private JNI metadata is separate from the strict external command contract.
  fun fromLiveCommand(context:Context,payload:String):NodeNotificationResult=try {
    val wrapper=notificationPhase("payload_parse",NotificationDiagnostic.PAYLOAD_PARSE) {
      require(payload.toByteArray(Charsets.UTF_8).size<=4352)
      JSONObject(payload).also {
        require(it.keys().asSequence().toSet()==setOf("command","validatedNowEpochMillis"))
        require(it.get("validatedNowEpochMillis") is Int || it.get("validatedNowEpochMillis") is Long)
      }
    }
    val clock=notificationPhase("payload_parse",NotificationDiagnostic.PAYLOAD_PARSE) {NodeNotificationClock.authenticated(wrapper.getLong("validatedNowEpochMillis"))}
    fromCommand(context,wrapper.getJSONObject("command").toString(),clock=clock)
  }catch(error:NotificationPhaseFailure){NodeNotificationResult("failed",error.diagnostic)}
   catch(error:Exception){notificationLog("payload_parse",error);NodeNotificationResult("failed",NotificationDiagnostic.PAYLOAD_PARSE)}
  fun fromCommand(context: Context, payload: String, expectedScope:String?=null,clock:NodeNotificationClock=NodeNotificationClock.wall): NodeNotificationResult = try {
    val command = notificationPhase("payload_parse", NotificationDiagnostic.PAYLOAD_PARSE) {
      require(payload.toByteArray(Charsets.UTF_8).size <= 4096)
      val value = JSONObject(payload)
      require(value.keys().asSequence().toSet() == setOf("commandId", "capability", "capabilityVersion", "expiresAt", "input"))
      value
    }
    val capability=command.getString("capability")
    if(command.getInt("capabilityVersion")!=1) NodeNotificationResult("unsupported")
    else if(capability=="reminder.withdraw") {
      val input=command.getJSONObject("input")
      require(input.keys().asSequence().toSet()==setOf("reminderId","generation"))
      val commandId=command.getString("commandId");require(UUID.fromString(commandId).toString()==commandId && commandId!="00000000-0000-0000-0000-000000000000")
      val expiry=NodeNotificationPayload.expiryEpochSecond(command.getString("expiresAt"));val now=clock.nowEpochSecond()
      require(expiry>now && expiry<=now+300)
      require(input.get("generation") is Int || input.get("generation") is Long)
      NodeReminderActions.withdrawRemote(context,input.getString("reminderId"),input.getLong("generation"))
      NodeNotificationResult("success")
    } else if(capability=="geofence.monitor" || capability=="location.current") {
      NodeDomainCommands.accept(context,command,expectedScope,clock)
    } else if(capability!="notification.show" && capability!="reminder.notify") NodeNotificationResult("unsupported")
    else {
      val input=command.getJSONObject("input")
      val reminder=if(capability=="reminder.notify") NodeReminderActions.read(input,command.getString("commandId")) else null
      if(reminder==null) require(input.keys().asSequence().toSet()==setOf("title","body"))
      show(context,command.getString("commandId"),command.getString("expiresAt"),input.getString("title"),input.getString("body"),reminder,clock)
    }
  } catch (error: NotificationPhaseFailure) { NodeNotificationResult("failed", error.diagnostic) }
    catch (error: Exception) { notificationLog("payload_parse", error); NodeNotificationResult("failed", NotificationDiagnostic.PAYLOAD_PARSE) }

  fun show(context: Context, id: String, expires: String, title: String, body: String, reminder: NodeReminderActions.Presentation? = null,clock:NodeNotificationClock=NodeNotificationClock.wall): NodeNotificationResult { synchronized(NodeNotificationGate.lock) { return try {
    val now = notificationPhase("expiry_parse", NotificationDiagnostic.EXPIRY_PARSE) { Instant.now().epochSecond }
    val commandNow=notificationPhase("expiry_parse",NotificationDiagnostic.EXPIRY_PARSE){clock.nowEpochSecond()}
    val expiry = notificationPhase("expiry_parse", NotificationDiagnostic.EXPIRY_PARSE) { NodeNotificationPayload.expiryEpochSecond(expires) }
    if (expiry <= commandNow) NodeNotificationResult("expired")
    else if (expiry > commandNow + 300 || !NodeNotificationPayload.validText(title, body)) NodeNotificationResult("failed", NotificationDiagnostic.PAYLOAD_INVALID)
    else {
      notificationPhase("command_id", NotificationDiagnostic.COMMAND_ID) {
        require(UUID.fromString(id).toString() == id && id != "00000000-0000-0000-0000-000000000000")
      }
      if (!notificationPhase("permission_check", NotificationDiagnostic.PERMISSION_CHECK) { granted(context) }) NodeNotificationResult("permission_denied", NotificationDiagnostic.PERMISSION_DENIED)
      else {
        notificationPhase("channel", NotificationDiagnostic.CHANNEL) { ensureChannel(context) }
        val recent = notificationPhase("dedupe_read", NotificationDiagnostic.DEDUPE_READ) {
          prefs(context).getString("recent", "")!!.split(";").mapNotNull {
            val pair = it.split(",")
            if (pair.size == 2) pair[1].toLongOrNull()?.let { stamp -> pair[0] to stamp } else null
          }.filter { now - it.second < 600 }.takeLast(255)
        }
        if (recent.any { it.first == id }) NodeNotificationResult("duplicate")
        else {
          if(reminder?.ruleId!=null && reminder.observerNodeId!=NodeNativeSession.read(context)?.nodeId)return NodeNotificationResult("expired")
          if(reminder!=null && !NodeReminderActions.remember(context,reminder)) return NodeNotificationResult("expired")
          val click = notificationPhase("pending_intent", NotificationDiagnostic.PENDING_INTENT) {
            val intent=Intent(context,MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
            if(reminder!=null) intent.setAction("com.aegis.node.open.${reminder.id}.${reminder.generation}").putExtra("reminderId",reminder.id)
            PendingIntent.getActivity(context,0,intent,PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
          }
          val notification = notificationPhase("notification_build", NotificationDiagnostic.BUILD) {
            // Resolve the actual packaged drawable before passing its resource ID to the framework.
            check(ContextCompat.getDrawable(context, R.drawable.ic_node_notification) != null)
            val builder=NotificationCompat.Builder(context, CHANNEL).setSmallIcon(R.drawable.ic_node_notification)
              .setContentTitle(title).setContentText(body).setStyle(NotificationCompat.BigTextStyle().bigText(body))
              .setContentIntent(click).setAutoCancel(true).setOnlyAlertOnce(true)
            if(reminder!=null) NodeReminderActions.addButtons(context,builder,reminder)
            builder.build()
          }
          try {
            notificationPhase("notification_post", NotificationDiagnostic.POST) {
              try { NotificationManagerCompat.from(context).notify(if(reminder==null) id else "reminder:${reminder.id}:${reminder.generation}", 1, notification) }
              catch (error: SecurityException) {
                if (!granted(context)) { notificationLog("notification_post", error); return NodeNotificationResult("permission_denied", NotificationDiagnostic.PERMISSION_DENIED) }
                throw error
              }
            }
            notificationPhase("dedupe_persist", NotificationDiagnostic.DEDUPE_PERSIST) {
              check(prefs(context).edit().putString("recent", (recent + listOf(id to now)).joinToString(";") { "${it.first},${it.second}" }).commit())
            }
            NodeNotificationResult("success")
          } catch (error: NotificationPhaseFailure) { NodeNotificationResult("failed", error.diagnostic) }
        }
      }
    }
  } catch (error: NotificationPhaseFailure) { NodeNotificationResult("failed", error.diagnostic) }
  } }
}
