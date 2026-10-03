package com.aegis.node

import android.os.Bundle
import android.webkit.WebView
import androidx.activity.enableEdgeToEdge
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat

class MainActivity : TauriActivity() {
  override fun onCreate(savedInstanceState: Bundle?) {
    enableEdgeToEdge()
    super.onCreate(savedInstanceState)
    NodeNativeNotifications.initialize(this)
  }

  // Native Rust JNI only; deliberately not a JavascriptInterface or arbitrary command API.
  fun showNodeNotification(payload:String):String = try {
    val command=org.json.JSONObject(payload)
    if(command.getString("capability")!="notification.show" || command.getInt("capabilityVersion")!=1) "unsupported"
    else { val input=command.getJSONObject("input");NodeNativeNotifications.show(this,command.getString("commandId"),command.getString("expiresAt"),input.getString("title"),input.getString("body")) }
  } catch(_:Exception){"failed"}

  // Sensitive state is returned only to native Rust; these are not WebView interfaces.
  fun nodePushState(payload:String):String {
    NodeNativeNotifications.initialize(this)
    return org.json.JSONObject()
      .put("configured",com.google.firebase.FirebaseApp.getApps(this).isNotEmpty())
      .put("granted",NodeNativeNotifications.granted(this))
      .put("token",NodeNativeNotifications.prefs(this).getString("token",null)).toString()
  }

  fun bindNodePush(payload:String):String = try {
    val value=org.json.JSONObject(payload)
    val id=if(value.isNull("nodeId")) null else value.getString("nodeId")
    if(id!=null && java.util.UUID.fromString(id).toString()!=id) "failed"
    else if(NodeNativeNotifications.prefs(this).edit().putString("nodeId",id).commit()) "success" else "failed"
  } catch(_:Exception){"failed"}

  override fun onWebViewCreate(webView: WebView) {
    super.onWebViewCreate(webView)
    // Respect Android edge-to-edge system bars/cutouts and the soft keyboard.
    // Resize the native content viewport; shared Vue's visualViewport handling remains intact.
    val content = findViewById<android.view.View>(android.R.id.content)
    ViewCompat.setOnApplyWindowInsetsListener(content) { view, insets ->
      val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.displayCutout())
      val keyboard = insets.getInsets(WindowInsetsCompat.Type.ime())
      view.setPadding(bars.left, bars.top, bars.right, maxOf(bars.bottom, keyboard.bottom))
      insets
    }
    ViewCompat.requestApplyInsets(content)
  }
}
