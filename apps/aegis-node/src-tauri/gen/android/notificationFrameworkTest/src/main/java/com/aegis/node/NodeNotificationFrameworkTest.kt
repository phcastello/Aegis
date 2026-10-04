package com.aegis.node

import android.app.Activity
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.content.ContextWrapper
import android.os.SystemClock
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone
import java.util.UUID

private fun renderNotification(payload: String, base: Context): JSONObject {
  val instrumentation = InstrumentationRegistry.getInstrumentation()
  lateinit var activity: Activity
  instrumentation.runOnMainSync {
    // Real target Context; the local renderer needs no Activity UI/lifecycle.
    // No production test seam or keep rule is added to the optimized APK.
    activity = notificationActivityClass().getDeclaredConstructor().newInstance()
    ContextWrapper::class.java.getDeclaredMethod("attachBaseContext", Context::class.java).apply {
      isAccessible = true
    }.invoke(activity, base)
  }
  return JSONObject(activity.javaClass.getMethod("showNodeNotification", String::class.java).invoke(activity, payload) as String)
}

/** Targets the actual minified APK through its fixed native entry point, without app dependencies. */
@RunWith(AndroidJUnit4::class)
class NodeNotificationFrameworkTest {
  private val instrumentation = InstrumentationRegistry.getInstrumentation()
  private val context = instrumentation.targetContext
  private val manager = context.getSystemService(NotificationManager::class.java)
  private val ids = mutableListOf<String>()
  private fun id() = UUID.randomUUID().toString().also { ids.add(it) }
  private fun command(id: String, expiry: String) = JSONObject()
    .put("commandId", id).put("capability", "notification.show").put("capabilityVersion", 1)
    .put("expiresAt", expiry).put("input", JSONObject().put("title", "Aegis framework test").put("body", "Fixture"))
    .toString()
  private fun expiry(): String = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'0000+00:00'", Locale.ROOT).apply {
    timeZone = TimeZone.getTimeZone("UTC")
  }.format(Date(System.currentTimeMillis() + 60000))
  private fun render(payload: String, base: Context = context) = renderNotification(payload, base)
  private fun active(id: String) {
    val end = SystemClock.elapsedRealtime() + 5000
    while (manager.activeNotifications.none { it.tag == id && it.id == 1 } && SystemClock.elapsedRealtime() < end) SystemClock.sleep(50)
    val notification = manager.activeNotifications.single { it.tag == id && it.id == 1 }.notification
    assertNotNull(notification.smallIcon.loadDrawable(context))
    assertNotNull(notification.contentIntent)
    assertEquals(NODE_NOTIFICATION_CHANNEL, notification.channelId)
  }
  @After fun cleanup() {
    ids.forEach { manager.cancel(it, 1) }
    context.getSharedPreferences("aegis-node-push-v1", Context.MODE_PRIVATE).edit().remove("recent").commit()
  }
  @Test fun validBackendCommandPostsActiveNotification() {
    assertTrue(frameworkNotificationPermission(context))
    manager.createNotificationChannel(NotificationChannel(NODE_NOTIFICATION_CHANNEL, "Aegis", NotificationManager.IMPORTANCE_DEFAULT))
    val id = id(); val payload = command(id, expiry()); val result = render(payload)
    assertEquals("diagnostic=${result.optString("diagnosticCode")}", "success", result.getString("status"))
    assertFalse(result.has("diagnosticCode")); active(id)
    assertEquals("duplicate", render(payload).getString("status"))
  }
  @Test fun missingChannelIsRecreatedBeforePosting() {
    manager.deleteNotificationChannel(NODE_NOTIFICATION_CHANNEL)
    assertNull(manager.getNotificationChannel(NODE_NOTIFICATION_CHANNEL))
    val id = id()
    assertEquals("success", render(command(id, expiry())).getString("status"))
    assertNotNull(manager.getNotificationChannel(NODE_NOTIFICATION_CHANNEL)); active(id)
  }
  @Test fun invalidExpiryHasClosedDiagnosticAndPostsNothing() {
    val id = id(); val result = render(command(id, "invalid-expiry"))
    assertEquals("failed", result.getString("status"))
    assertEquals("android_expiry_parse_failed", result.getString("diagnosticCode"))
    assertTrue(manager.activeNotifications.none { it.tag == id })
  }
  @Test fun pendingIntentFrameworkFailureHasSpecificDiagnostic() {
    val invalidContext = object : ContextWrapper(context) {
      override fun getPackageName(): String = "com.aegis.fixture.missing"
    }
    val id = id(); val result = render(command(id, expiry()), invalidContext)
    assertEquals("failed", result.getString("status"))
    assertEquals("android_pending_intent_failed", result.getString("diagnosticCode"))
    assertTrue(manager.activeNotifications.none { it.tag == id })
  }
  @Test fun invalidPayloadHasSafeDiagnostic() {
    val result = render("{invalid}")
    assertEquals("failed", result.getString("status"))
    assertEquals("android_payload_parse_failed", result.getString("diagnosticCode"))
    assertEquals(setOf("status", "diagnosticCode"), result.keys().asSequence().toSet())
  }
}

@RunWith(AndroidJUnit4::class)
class NodeNotificationPermissionDeniedTest {
  @Test fun deniedPermissionReturnsPermissionDeniedWithoutPosting() {
    val context = InstrumentationRegistry.getInstrumentation().targetContext
    assertFalse(frameworkNotificationPermission(context))
    val id = UUID.randomUUID().toString()
    val expiry = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'0000+00:00'", Locale.ROOT).apply { timeZone = TimeZone.getTimeZone("UTC") }.format(Date(System.currentTimeMillis()+60000))
    val payload = JSONObject().put("commandId",id).put("capability","notification.show").put("capabilityVersion",1).put("expiresAt",expiry).put("input",JSONObject().put("title","Aegis framework test").put("body","Fixture")).toString()
    val manager = context.getSystemService(NotificationManager::class.java)
    try {
      val result = renderNotification(payload, context)
      assertEquals("permission_denied", result.getString("status"))
      assertEquals("android_permission_denied", result.getString("diagnosticCode"))
      assertTrue(manager.activeNotifications.none { it.tag == id })
    } finally { manager.cancel(id, 1) }
  }
}
