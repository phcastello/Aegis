package com.aegis.node

import android.util.Log
import org.json.JSONObject

enum class NotificationDiagnostic(val code: String) {
  PAYLOAD_PARSE("android_payload_parse_failed"),
  EXPIRY_PARSE("android_expiry_parse_failed"),
  COMMAND_ID("android_invalid_command_id"),
  PAYLOAD_INVALID("android_invalid_notification_payload"),
  PERMISSION_DENIED("android_permission_denied"),
  PERMISSION_CHECK("android_permission_check_failed"),
  CHANNEL("android_channel_missing"),
  PENDING_INTENT("android_pending_intent_failed"),
  BUILD("android_notification_build_failed"),
  POST("android_notification_post_failed"),
  DEDUPE_READ("android_dedupe_read_failed"),
  DEDUPE_PERSIST("android_dedupe_persist_failed")
}

data class NodeNotificationResult(val status: String, val diagnostic: NotificationDiagnostic? = null) {
  fun json(): String = JSONObject().put("status", status).apply {
    diagnostic?.let { put("diagnosticCode", it.code) }
  }.toString()
}

internal class NotificationPhaseFailure(val diagnostic: NotificationDiagnostic) : RuntimeException()

internal fun notificationLog(phase: String, error: Throwable) {
  val name = error.javaClass.simpleName.takeIf {
    it.length in 1..64 && it.all { c -> c.isLetterOrDigit() && c.code < 128 || c == '_' }
  } ?: "NativeError"
  // No Throwable argument: Logcat must never contain messages, stacks or notification text.
  Log.e("AegisNodeNotification", "phase=$phase errorType=$name")
}

internal inline fun <T> notificationPhase(phase: String, diagnostic: NotificationDiagnostic, block: () -> T): T = try {
  block()
} catch (error: Exception) {
  notificationLog(phase, error)
  throw NotificationPhaseFailure(diagnostic)
} catch (error: LinkageError) {
  notificationLog(phase, error)
  throw NotificationPhaseFailure(diagnostic)
}
