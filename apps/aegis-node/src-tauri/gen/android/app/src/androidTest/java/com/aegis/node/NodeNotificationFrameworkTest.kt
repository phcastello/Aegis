package com.aegis.node

import android.app.NotificationManager
import android.content.ContextWrapper
import android.content.Intent
import android.os.SystemClock
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.time.Instant
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.UUID

/** Runs on the Android framework, including the minified release APK; no OS mocks. */
@RunWith(AndroidJUnit4::class)
class NodeNotificationFrameworkTest {
  private val context = InstrumentationRegistry.getInstrumentation().targetContext
  private val manager = context.getSystemService(NotificationManager::class.java)
  private val ids = mutableListOf<String>()
  private fun id() = UUID.randomUUID().toString().also { ids.add(it) }
  private fun command(id: String, expiry: String) = JSONObject()
    .put("commandId", id).put("capability", "notification.show").put("capabilityVersion", 1)
    .put("expiresAt", expiry).put("input", JSONObject().put("title", "Aegis framework test").put("body", "Fixture"))
    .toString()
  private fun expectActive(id: String) {
    val deadline = SystemClock.elapsedRealtime() + 5000
    while (manager.activeNotifications.none { it.tag == id && it.id == 1 } && SystemClock.elapsedRealtime() < deadline) SystemClock.sleep(50)
    val active = manager.activeNotifications.single { it.tag == id && it.id == 1 }
    assertNotNull(active.notification.smallIcon)
    assertNotNull(active.notification.smallIcon.loadDrawable(context))
    assertNotNull(active.notification.contentIntent)
    assertEquals(NodeNativeNotifications.CHANNEL, active.notification.channelId)
  }
  @After fun cleanup() { ids.forEach { manager.cancel(it, 1) }; NodeNativeNotifications.prefs(context).edit().remove("recent").commit() }
  @Test fun validBackendCommandPostsActiveNotification() {
    assertTrue(NodeNativeNotifications.granted(context))
    NodeNativeNotifications.ensureChannel(context)
    assertNotNull(manager.getNotificationChannel(NodeNativeNotifications.CHANNEL))
    val id = id()
    // System.Text.Json DateTimeOffset format from the backend: numeric UTC offset, 7 fractional digits.
    val expiry = DateTimeFormatter.ofPattern("uuuu-MM-dd'T'HH:mm:ss.SSSSSSSxxx").withZone(ZoneOffset.UTC).format(Instant.now().plusSeconds(60))
    val result = NodeNativeNotifications.fromCommand(context, command(id, expiry))
    assertEquals("diagnostic=${result.diagnostic?.code}", "success", result.status)
    assertNull(result.diagnostic)
    expectActive(id)
    assertEquals("duplicate", NodeNativeNotifications.fromCommand(context, command(id, expiry)).status)
  }
  @Test fun backgroundPayloadUsesSameRendererAndPostsActiveNotification() {
    assertTrue(NodeNativeNotifications.granted(context))
    val id = id()
    val expiry = DateTimeFormatter.ofPattern("uuuu-MM-dd'T'HH:mm:ss.SSSSSSSxxx").withZone(ZoneOffset.UTC).format(Instant.now().plusSeconds(60))
    val nodeId = UUID.randomUUID().toString()
    val payload = NodeNotificationPayload.fromData(mapOf("nodeId" to nodeId, "type" to "notification.show", "version" to "1", "commandId" to id, "expiresAt" to expiry, "title" to "Aegis framework test", "body" to "Fixture"), nodeId)!!
    val result = NodeNativeNotifications.show(context, payload.commandId, payload.expiresAt, payload.title, payload.body)
    assertEquals("diagnostic=${result.diagnostic?.code}", "success", result.status)
    expectActive(id)
  }
  @Test fun invalidExpiryHasClosedDiagnosticAndPostsNothing() {
    val id = id()
    val result = NodeNativeNotifications.fromCommand(context, command(id, "invalid-expiry"))
    assertEquals("failed", result.status)
    assertEquals("android_expiry_parse_failed", result.diagnostic?.code)
    assertTrue(manager.activeNotifications.none { it.tag == id })
  }
  @Test fun pendingIntentFrameworkFailureHasSpecificDiagnostic() {
    assertTrue(NodeNativeNotifications.granted(context))
    // Use the real PendingIntent API with a Context whose start identity cannot be resolved.
    val invalidContext = object : ContextWrapper(context) {
      override fun getPackageName(): String = "com.aegis.fixture.missing"
    }
    val id = id()
    val result = NodeNativeNotifications.show(invalidContext, id, Instant.now().plusSeconds(60).toString(), "Aegis framework test", "Fixture")
    assertEquals("failed", result.status)
    assertEquals("android_pending_intent_failed", result.diagnostic?.code)
    assertTrue(manager.activeNotifications.none { it.tag == id })
  }
  @Test fun missingChannelIsRecreatedBeforePosting() {
    manager.deleteNotificationChannel(NodeNativeNotifications.CHANNEL)
    assertNull(manager.getNotificationChannel(NodeNativeNotifications.CHANNEL))
    val id = id()
    val result = NodeNativeNotifications.show(context, id, Instant.now().plusSeconds(60).toString(), "Aegis framework test", "Fixture")
    assertEquals("success", result.status)
    assertNotNull(manager.getNotificationChannel(NodeNativeNotifications.CHANNEL))
    expectActive(id)
  }
  @Test fun invalidPayloadHasSafeDiagnostic() {
    val result = NodeNativeNotifications.fromCommand(context, "{invalid}")
    assertEquals("failed", result.status)
    assertEquals("android_payload_parse_failed", result.diagnostic?.code)
    assertEquals(setOf("status", "diagnosticCode"), JSONObject(result.json()).keys().asSequence().toSet())
  }
}

/** The host grants/revokes POST_NOTIFICATIONS before starting instrumentation (revocation kills processes). */
@RunWith(AndroidJUnit4::class)
class NodeNotificationPermissionDeniedTest {
  @Test fun deniedPermissionReturnsPermissionDeniedWithoutPosting() {
    val context = InstrumentationRegistry.getInstrumentation().targetContext
    val manager = context.getSystemService(NotificationManager::class.java)
    NodeNativeNotifications.ensureChannel(context)
    val id = UUID.randomUUID().toString()
    try {
      assertFalse(NodeNativeNotifications.granted(context))
      val result = NodeNativeNotifications.show(context, id, Instant.now().plusSeconds(60).toString(), "Aegis framework test", "Fixture")
      assertEquals("permission_denied", result.status)
      assertEquals("android_permission_denied", result.diagnostic?.code)
      assertTrue(manager.activeNotifications.none { it.tag == id })
    } finally { manager.cancel(id, 1) }
  }
}
