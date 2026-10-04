package com.aegis.node

import android.app.NotificationManager
import android.os.SystemClock
import android.view.View
import android.view.ViewGroup
import android.webkit.WebView
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/** Native HttpApi -> real isolated controller -> live Rust WebSocket -> JNI -> real framework. */
@RunWith(AndroidJUnit4::class)
class NodeNotificationLiveTest {
  private val instrumentation = InstrumentationRegistry.getInstrumentation()
  private fun findWebView(view: View): WebView? {
    if (view is WebView) return view
    if (view is ViewGroup) for (i in 0 until view.childCount) findWebView(view.getChildAt(i))?.let { return it }
    return null
  }
  private fun evaluate(webview: WebView, js: String): String {
    val done = CountDownLatch(1)
    var output = "null"
    instrumentation.runOnMainSync { webview.evaluateJavascript(js) { output = it; done.countDown() } }
    check(done.await(10, TimeUnit.SECONDS)) { "webview_callback_timeout" }
    return output
  }
  private fun invoke(webview: WebView, name: String, args: JSONObject = JSONObject()): JSONObject {
    evaluate(webview, "window.__aegisNotificationTest=null;window.__TAURI_INTERNALS__.invoke(${JSONObject.quote(name)},$args).then(value=>{window.__aegisNotificationTest={ok:true,value}}).catch(()=>{window.__aegisNotificationTest={ok:false}});null")
    val end = SystemClock.elapsedRealtime() + 20000
    while (SystemClock.elapsedRealtime() < end) {
      val raw = evaluate(webview, "window.__aegisNotificationTest")
      if (raw != "null") {
        val result = JSONObject(raw)
        check(result.getBoolean("ok")) { "native_invocation_failed" }
        return result.getJSONObject("value")
      }
      SystemClock.sleep(100)
    }
    error("native_invocation_timeout")
  }
  @Test fun liveSelfNotificationPostsThroughProductionJni() {
    val code = InstrumentationRegistry.getArguments().getString("pairingCode") ?: error("fixture_bootstrap_missing")
    ActivityScenario.launch(MainActivity::class.java).use { scenario ->
      lateinit var webview: WebView
      scenario.onActivity { activity ->
        webview = findWebView(activity.window.decorView) ?: error("webview_missing")
        // Verify exact fixed method descriptors in the actual installed/minified activity.
        for (method in listOf("showNodeNotification", "nodePushState", "bindNodePush")) {
          assertEquals(String::class.java, activity.javaClass.getMethod(method, String::class.java).returnType)
        }
        assertTrue(NodeNativeNotifications.granted(activity))
      }
      val deadline = SystemClock.elapsedRealtime() + 30000
      while (evaluate(webview, "Boolean(window.__TAURI_INTERNALS__?.invoke)") != "true" && SystemClock.elapsedRealtime() < deadline) SystemClock.sleep(100)
      // Fail before pairing if this APK points anywhere except the isolated fixture.
      assertEquals("http://127.0.0.1:18104", invoke(webview, "runtime_info").getString("backendUrl"))
      val identity = invoke(webview, "node_pair", JSONObject().put("name", "Android instrumentation").put("code", code))
      assertEquals("paired", identity.getString("state"))
      val id = identity.getJSONObject("node").getString("id")
      val onlineEnd = SystemClock.elapsedRealtime() + 30000
      while (invoke(webview, "node_transport_status").getString("transportState") != "online" && SystemClock.elapsedRealtime() < onlineEnd) SystemClock.sleep(100)
      assertEquals("online", invoke(webview, "node_transport_status").getString("transportState"))
      val context = instrumentation.targetContext
      val manager = context.getSystemService(NotificationManager::class.java)
      val result = invoke(webview, "node_test_notification", JSONObject().put("id", id))
      assertEquals("live_websocket", result.getString("transport"))
      val commandId = result.getString("commandId")
      try {
        val diagnostic = result.optString("diagnosticCode").takeIf { it.matches(Regex("android_[a-z_]{1,40}")) } ?: "none"
        assertEquals("diagnostic=$diagnostic", "success", result.getString("status"))
        val activeEnd = SystemClock.elapsedRealtime() + 5000
        while (manager.activeNotifications.none { it.tag == commandId && it.id == 1 } && SystemClock.elapsedRealtime() < activeEnd) SystemClock.sleep(50)
        assertTrue(manager.activeNotifications.any { it.tag == commandId && it.id == 1 })
      } finally {
        manager.cancel(commandId, 1)
        invoke(webview, "node_revoke", JSONObject().put("id", id))
      }
    }
  }
}
