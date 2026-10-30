package com.aegis.node

import android.os.Bundle
import android.webkit.WebView
import androidx.activity.enableEdgeToEdge
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat

class MainActivity : TauriActivity() {
  private var currentWebView:WebView?=null
  private var readResumed=false
  // A cleared WebView input focus does not mean the Activity is in background.
  // Query the current Activity/window state through the existing closed JNI bridge.
  fun readVisibility(payload:String):String = (readResumed && hasWindowFocus()).toString()
  private fun notifyReadVisibility() {
    currentWebView?.post { currentWebView?.evaluateJavascript("window.dispatchEvent(new Event('aegis-app-visibility'))",null) }
  }
  override fun onWindowFocusChanged(hasFocus:Boolean) {
    super.onWindowFocusChanged(hasFocus)
    notifyReadVisibility()
  }
  private val settingsBack=object:androidx.activity.OnBackPressedCallback(false) {
    override fun handleOnBackPressed() { currentWebView?.evaluateJavascript("window.dispatchEvent(new CustomEvent('aegis-back'))",null) }
  }
  fun settingsOpen(payload:String):String {
    val enabled=org.json.JSONObject(payload).getBoolean("open")
    runOnUiThread { settingsBack.isEnabled=enabled }
    return "success"
  }
  fun hideKeyboard(payload:String):String {
    runOnUiThread {
      val view=currentFocus ?: currentWebView
      view?.let {
        (getSystemService(android.content.Context.INPUT_METHOD_SERVICE) as android.view.inputmethod.InputMethodManager).hideSoftInputFromWindow(it.windowToken,0)
        it.clearFocus()
      }
      ViewCompat.getWindowInsetsController(window.decorView)?.hide(WindowInsetsCompat.Type.ime())
    }
    return "success"
  }
  override fun onNewIntent(intent:android.content.Intent) {
    super.onNewIntent(intent)
    openReminder(intent)
  }
  private fun openReminder(intent:android.content.Intent?) {
    intent?.getStringExtra("conversationId")?.let { id ->
      intent.removeExtra("conversationId")
      try {
        if(java.util.UUID.fromString(id).toString()==id) {
          NodeNativeNotifications.prefs(this).edit().putString("pendingConversationOpen",id).commit()
          currentWebView?.post { currentWebView?.evaluateJavascript("window.dispatchEvent(new CustomEvent('aegis-conversation-open',{detail:{conversationId:'$id'}}))",null) }
        }
      } catch(_:Exception) {}
    }
    val id=intent?.getStringExtra("reminderId") ?: return
    intent.removeExtra("reminderId")
    try { if(java.util.UUID.fromString(id).toString()!=id)return }catch(_:Exception){return}
    NodeNativeNotifications.prefs(this).edit().putString("pendingReminderOpen",id).commit()
    currentWebView?.post { currentWebView?.evaluateJavascript("window.dispatchEvent(new CustomEvent('aegis-reminder-open',{detail:{reminderId:'$id'}}))",null) }
  }
  override fun onCreate(savedInstanceState: Bundle?) {
    enableEdgeToEdge()
    super.onCreate(savedInstanceState)
    NodeNativeNotifications.initialize(this)
    onBackPressedDispatcher.addCallback(this,settingsBack)
    NodeUpdates.cleanup(this)
    openReminder(intent)
  }

  private var connectivityCallback:android.net.ConnectivityManager.NetworkCallback?=null
  override fun onResume() {
    super.onResume()
    readResumed=true
    notifyReadVisibility()
    NodeBackgroundWork.actionsChanged={currentWebView?.post{currentWebView?.evaluateJavascript("window.dispatchEvent(new CustomEvent('aegis-reminder-actions-changed'))",null)}}
    if(connectivityCallback==null) {
      val callback=object:android.net.ConnectivityManager.NetworkCallback() {
        override fun onCapabilitiesChanged(network:android.net.Network,capabilities:android.net.NetworkCapabilities) {
          if(capabilities.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET))NodeBackgroundWork.foregroundActions(this@MainActivity)
        }
      }
      try{getSystemService(android.net.ConnectivityManager::class.java).registerDefaultNetworkCallback(callback);connectivityCallback=callback}catch(_:Exception){}
    }
    NodeBackgroundWork.foregroundActions(this)
  }
  override fun onPause() {
    readResumed=false
    notifyReadVisibility()
    NodeBackgroundWork.actionsChanged=null
    connectivityCallback?.let{try{getSystemService(android.net.ConnectivityManager::class.java).unregisterNetworkCallback(it)}catch(_:Exception){}}
    connectivityCallback=null
    super.onPause()
  }

  // Native Rust JNI only; deliberately not a JavascriptInterface or arbitrary command API.
  fun consumeReminderOpen(payload:String):String {
    val prefs=NodeNativeNotifications.prefs(this)
    val id=prefs.getString("pendingReminderOpen",null)
    val conversationId=prefs.getString("pendingConversationOpen",null)
    prefs.edit().remove("pendingReminderOpen").remove("pendingConversationOpen").commit()
    return org.json.JSONObject().put("reminderId",id ?: org.json.JSONObject.NULL).put("conversationId",conversationId ?: org.json.JSONObject.NULL).toString()
  }
  fun nodeActionState(payload:String):String {
    return NodeReminderActions.diagnostics(this).toString()
  }
  fun showNodeNotification(payload: String): String = NodeNativeNotifications.fromCommand(this, payload).json()
  fun showNodeNotificationLive(payload:String):String=NodeNativeNotifications.fromLiveCommand(this,payload).json()

  // Sensitive state is returned only to native Rust; these are not WebView interfaces.
  fun nodePushState(payload:String):String {
    NodeNativeNotifications.initialize(this)
    return org.json.JSONObject()
      .put("configured",com.google.firebase.FirebaseApp.getApps(this).isNotEmpty())
      .put("granted",NodeNativeNotifications.granted(this))
      .put("token",NodeNativeNotifications.prefs(this).getString("token",null)).toString()
  }

  fun bindNodePush(payload:String):String {return try {
    val value=org.json.JSONObject(payload)
    val id=if(value.isNull("nodeId")) null else value.getString("nodeId")
    if(id!=null && java.util.UUID.fromString(id).toString()!=id) "failed"
    else {
      val prefs=NodeNativeNotifications.prefs(this)
      val origin=value.optString("origin")
      require(NodeNativeSession.validOrigin(origin,this))
      if(!NodeNativeSession.bindingAllowed(this,id,origin))return "failed"
      if(id!=null) { NodeGeofenceRuntime.bindIdentity(this,id) }
      synchronized(NodeNotificationGate.lock) {
        if(!applyForCurrentIdentity({NodeNativeSession.bindingAllowed(this,id,origin)}) {
          if(prefs.getString("nodeId",null)!=id || prefs.getString("origin",null)!=origin) check(prefs.edit().putString("nodeId",id).putString("origin",origin).commit())
        })return "failed"
      }
      if(id!=null) { NodeBackgroundWork.enqueue(this); NodeBackgroundWork.scheduleRenewal(this); NodeBackgroundWork.foregroundActions(this) } else NodeGeofences.background{NodeGeofences.clearIfUnbound(this,origin)}
      "success"
    }
  } catch(_:Exception){"failed"} }

  override fun onWebViewCreate(webView: WebView) {
    super.onWebViewCreate(webView)
    currentWebView=webView
    webView.postDelayed({openReminder(intent)},1000)
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
