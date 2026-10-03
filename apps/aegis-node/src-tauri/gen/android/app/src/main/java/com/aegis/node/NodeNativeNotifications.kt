package com.aegis.node
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import com.google.firebase.FirebaseApp
import com.google.firebase.messaging.FirebaseMessaging
import java.time.Instant
import java.util.UUID

object NodeNativeNotifications {
  private const val CHANNEL = "aegis_node_notifications"
  fun prefs(context:Context) = context.getSharedPreferences("aegis-node-push-v1",Context.MODE_PRIVATE)
  fun granted(context:Context):Boolean {
    if (!NotificationManagerCompat.from(context).areNotificationsEnabled()) return false
    return Build.VERSION.SDK_INT < 26 || context.getSystemService(NotificationManager::class.java).getNotificationChannel(CHANNEL)?.importance != NotificationManager.IMPORTANCE_NONE
  }
  fun initialize(context:Context) {
    if (Build.VERSION.SDK_INT >= 26) context.getSystemService(NotificationManager::class.java).createNotificationChannel(NotificationChannel(CHANNEL,"Aegis",NotificationManager.IMPORTANCE_DEFAULT))
    if (FirebaseApp.initializeApp(context) != null) FirebaseMessaging.getInstance().token.addOnSuccessListener { prefs(context).edit().putString("token",it).apply() }
  }
  @Synchronized fun show(context:Context,id:String,expires:String,title:String,body:String):String {
    val now=Instant.now().epochSecond
    val expiry=try {Instant.parse(expires).epochSecond} catch(_:Exception){return "failed"}
    if(expiry<=now) return "expired"
    if(expiry>now+300 || !NodeNotificationPayload.validText(title,body)) return "failed"
    try { if(UUID.fromString(id).toString()!=id || id=="00000000-0000-0000-0000-000000000000") return "failed" } catch(_:Exception){return "failed"}
    if(!granted(context)) return "permission_denied"
    val prefs=prefs(context)
    val recent=prefs.getString("recent","")!!.split(";").mapNotNull { val pair=it.split(",");if(pair.size==2) pair[1].toLongOrNull()?.let {stamp -> pair[0] to stamp} else null }.filter {now-it.second<600}.takeLast(255)
    if(recent.any {it.first==id}) return "duplicate"
    val click=PendingIntent.getActivity(context,0,Intent(context,MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP),PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
    val notification=NotificationCompat.Builder(context,CHANNEL).setSmallIcon(R.drawable.ic_node_notification).setContentTitle(title).setContentText(body).setStyle(NotificationCompat.BigTextStyle().bigText(body)).setContentIntent(click).setAutoCancel(true).build()
    return try { NotificationManagerCompat.from(context).notify(id,1,notification);prefs.edit().putString("recent",(recent+listOf(id to now)).joinToString(";"){"${it.first},${it.second}"}).commit();"success" } catch(_:SecurityException){"permission_denied"} catch(_:Exception){"failed"}
  }
}
