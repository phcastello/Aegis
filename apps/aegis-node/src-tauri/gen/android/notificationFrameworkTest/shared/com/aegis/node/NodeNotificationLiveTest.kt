package com.aegis.node

import android.Manifest
import android.os.Build
import android.app.NotificationManager
import android.os.SystemClock
import android.view.View
import android.view.ViewGroup
import android.view.MotionEvent
import android.view.KeyEvent
import android.view.WindowInsets
import android.view.inputmethod.InputMethodManager
import android.content.Context
import android.webkit.WebView
import androidx.test.core.app.ActivityScenario
import androidx.lifecycle.Lifecycle
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
  private fun checkpoint(phase: String) = android.util.Log.i("AegisNodeNativeTest", "phase=$phase errorType=Checkpoint")
  internal fun findWebView(view: View): WebView? {
    if (view is WebView) return view
    if (view is ViewGroup) for (i in 0 until view.childCount) findWebView(view.getChildAt(i))?.let { return it }
    return null
  }
  internal fun evaluate(webview: WebView, js: String): String {
    val done = CountDownLatch(1)
    var output = "null"
    instrumentation.runOnMainSync { webview.evaluateJavascript(js) { output = it; done.countDown() } }
    check(done.await(10, TimeUnit.SECONDS)) { "webview_callback_timeout" }
    return output
  }
  internal fun invoke(webview: WebView, name: String, args: JSONObject = JSONObject(), timeout:Long=20000): JSONObject {
    evaluate(webview, "window.__aegisNotificationTest=null;window.__TAURI_INTERNALS__.invoke(${JSONObject.quote(name)},$args).then(value=>{window.__aegisNotificationTest={ok:true,value}}).catch(()=>{window.__aegisNotificationTest={ok:false}});null")
    val end = SystemClock.elapsedRealtime() + timeout
    while (SystemClock.elapsedRealtime() < end) {
      val raw = evaluate(webview, "window.__aegisNotificationTest")
      if (raw != "null") {
        val result = JSONObject(raw)
        check(result.getBoolean("ok")) { "native_invocation_failed" }
        return when(val value=result.get("value")){is JSONObject->value;is org.json.JSONArray->JSONObject().put("items",value);is Boolean->JSONObject().put("value",value);else->JSONObject()}
      }
      SystemClock.sleep(100)
    }
    error("native_invocation_timeout")
  }
  /** Actual Activity, WebView focus, native dialog and lifecycle; runs on debug and R8 APKs. */
  private fun exerciseReadVisibility(webview:WebView,scenario:ActivityScenario<out android.app.Activity>) {
    fun awaitVisibility(expected:Boolean) {
      val deadline=SystemClock.elapsedRealtime()+10000
      var actual=!expected
      while(actual!=expected && SystemClock.elapsedRealtime()<deadline) {
        actual=invoke(webview,"node_read_visibility").getBoolean("value")
        if(actual!=expected)SystemClock.sleep(50)
      }
      assertEquals("Read visibility follows the Activity window, not WebView input focus",expected,actual)
    }
    awaitVisibility(true)
    invoke(webview,"node_hide_keyboard")
    var focusTarget:View?=null
    try {
      scenario.onActivity { activity ->
        // Keep native focus outside the WebView deterministically; a bare
        // clearFocus may let Android immediately choose its default view again.
        val target=View(activity).apply{isFocusableInTouchMode=true}
        activity.findViewById<ViewGroup>(android.R.id.content).addView(target,ViewGroup.LayoutParams(1,1))
        focusTarget=target
        assertTrue(target.requestFocus())
        webview.clearFocus()
        assertFalse("The native input view is deliberately unfocused",webview.hasFocus())
      }
      awaitVisibility(true)
    } finally {
      scenario.onActivity { focusTarget?.let{(it.parent as? ViewGroup)?.removeView(it)} }
    }
    var covering:android.app.Dialog?=null
    try {
      scenario.onActivity { activity -> covering=android.app.Dialog(activity).apply{setContentView(android.widget.TextView(activity).apply{text="Read visibility fixture"});show()} }
      awaitVisibility(false)
    } finally {
      scenario.onActivity { covering?.dismiss() }
    }
    awaitVisibility(true)
    scenario.moveToState(Lifecycle.State.CREATED)
    var background="true"
    scenario.onActivity { activity -> background=activity.javaClass.getMethod("readVisibility",String::class.java).invoke(activity,"{}") as String }
    assertEquals("A stopped Activity never acknowledges reading","false",background)
    scenario.moveToState(Lifecycle.State.RESUMED)
    awaitVisibility(true)
    checkpoint("read_visibility_native_focus_dialog_background_resume")
  }
  /** Actual target WebView, Android touch input, IME and native back dispatcher. */
  private fun exerciseReminderEditing(webview:WebView,scenario:ActivityScenario<out android.app.Activity>,ruleId:String) {
    fun awaitUi(script:String,message:String,timeoutMillis:Long=15000) {
      val deadline=SystemClock.elapsedRealtime()+timeoutMillis
      while(evaluate(webview,script)!="true" && SystemClock.elapsedRealtime()<deadline)SystemClock.sleep(50)
      val actual=evaluate(webview,script)
      if(actual!="true") {
        val detail=evaluate(webview,"(()=>{const h=document.querySelector('#reminder-editor-title'),c=document.querySelector('.settings-content'),button=[...document.querySelectorAll('.geofence-rule button')].find(b=>b.textContent.trim()==='Editar regra');const rect=e=>{if(!e)return null;const r=e.getBoundingClientRect();return {top:r.top,bottom:r.bottom}};return {formExists:Boolean(document.querySelector('.reminder-editor')),activeId:document.activeElement?.id??null,activeTag:document.activeElement?.tagName??null,heading:rect(h),content:rect(c),editDisabled:button?.disabled??null}})()")
        assertEquals("$message; safeUi=$detail","true",actual)
      }
    }
    fun tap(expression:String) {
      evaluate(webview,"(()=>{const element=$expression;if(!element)throw Error('ui_control_missing');element.scrollIntoView({block:'center',behavior:'instant'});window.__aegisAndroidUiPaintReady=false;requestAnimationFrame(()=>requestAnimationFrame(()=>window.__aegisAndroidUiPaintReady=true));return true})()")
      // DOM geometry changes synchronously, but native input must wait for the
      // WebView compositor to display the scrolled frame at those coordinates.
      awaitUi("window.__aegisAndroidUiPaintReady===true","Scrolled WebView frame is ready for native touch")
      instrumentation.waitForIdleSync()
      val bounds=JSONObject(evaluate(webview,"(()=>{const element=$expression;if(!element||element.disabled)throw Error('ui_control_not_ready');const r=element.getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+r.height/2,scale:window.devicePixelRatio}})()"))
      val offset=IntArray(2);instrumentation.runOnMainSync{webview.getLocationOnScreen(offset)}
      val x=offset[0]+bounds.getDouble("x").toFloat()*bounds.getDouble("scale").toFloat()
      val y=offset[1]+bounds.getDouble("y").toFloat()*bounds.getDouble("scale").toFloat()
      val at=SystemClock.uptimeMillis()
      val down=MotionEvent.obtain(at,at,MotionEvent.ACTION_DOWN,x,y,0)
      val up=MotionEvent.obtain(at,at+30,MotionEvent.ACTION_UP,x,y,0)
      try{instrumentation.sendPointerSync(down);instrumentation.sendPointerSync(up)}finally{down.recycle();up.recycle()}
      instrumentation.waitForIdleSync()
    }
    fun imeVisible():Boolean {
      var visible=false
      scenario.onActivity{activity->if(Build.VERSION.SDK_INT>=30)visible=activity.window.decorView.rootWindowInsets?.isVisible(WindowInsets.Type.ime())==true}
      return visible
    }
    fun awaitIme(expected:Boolean) {
      val deadline=SystemClock.elapsedRealtime()+5000
      while(imeVisible()!=expected && SystemClock.elapsedRealtime()<deadline)SystemClock.sleep(50)
      assertEquals("Android IME visibility",expected,imeVisible())
    }
    fun shell(command:String)=instrumentation.uiAutomation.executeShellCommand(command).use{descriptor->android.os.ParcelFileDescriptor.AutoCloseInputStream(descriptor).bufferedReader().use{it.readText()}}
    val hardwareKeyboardIme=shell("settings get secure show_ime_with_hard_keyboard").trim()
    try {
    // CI emulators expose a hardware keyboard; explicitly permit the software
    // keyboard for this assertion and restore the prior secure setting below.
    shell("settings put secure show_ime_with_hard_keyboard 1")
    val article="document.getElementById('geofence-$ruleId')"
    val edit="[...($article?.querySelectorAll('button')??[])].find(button=>button.textContent.trim()==='Editar regra')"
    // The settings control lives in the normal sidebar, which is absent while
    // mobile history is collapsed. Exercise the actual product entry path.
    if(evaluate(webview,"Boolean(document.querySelector('[aria-label=\"Abrir histórico\"]')?.getClientRects().length)")=="true")tap("document.querySelector('[aria-label=\"Abrir histórico\"]')")
    awaitUi("Boolean(document.querySelector('[aria-label=\"Configurações da Aegis\"]')?.getClientRects().length)","Settings control is available in the opened sidebar")
    tap("document.querySelector('[aria-label=\"Configurações da Aegis\"]')")
    awaitUi("Boolean(document.querySelector('.settings-dialog[open]'))","Settings dialog opened")
    evaluate(webview,"[...document.querySelectorAll('.settings-navigation button')].find(button=>button.textContent.trim()==='Lembretes')?.click();null")
    awaitUi("Boolean($article)","Authenticated geographic rule is visible in Android WebView")
    // Rows render before asynchronous location/diagnostic loading finishes.
    // The user's Edit control is intentionally disabled until that load ends.
    awaitUi("Boolean($edit)&&!($edit).disabled","Edit control is enabled after native diagnostics load",90000)
    // A disposable DOM fixture supplies scroll distance without adding personal
    // records or changing the existing authenticated rule and its revision.
    evaluate(webview,"(()=>{const row=$article;const spacer=document.createElement('div');spacer.dataset.androidUiFixture='scroll';spacer.style.minHeight=(window.innerHeight*3)+'px';row.before(spacer);($edit).scrollIntoView({block:'center'});return true})()")
    awaitUi("document.querySelector('.settings-content').scrollTop>0","Rule starts in a scrolled list")
    val originalScroll=evaluate(webview,"document.querySelector('.settings-content').scrollTop").toDouble()
    tap(edit)
    awaitUi("Boolean(document.querySelector('.reminder-editor'))","Enabled Edit touch opens the editor")
    awaitUi("document.activeElement===document.querySelector('#reminder-editor-title')","Opened editor heading receives focus")
    awaitUi("(()=>{const heading=document.querySelector('#reminder-editor-title'),content=document.querySelector('.settings-content');if(!heading||!content)return false;const h=heading.getBoundingClientRect(),c=content.getBoundingClientRect();return h.top>=c.top&&h.bottom<=c.bottom})()","Opened editor heading is within the scrolled viewport")
    // The production list uses v-show, so this fixture and its scroll distance
    // must remain connected while the editor is visible.
    awaitUi("document.querySelector('[data-android-ui-fixture=\"scroll\"]')?.isConnected===true","Scrolled list fixture survives editor opening")
    instrumentation.sendKeyDownUpSync(KeyEvent.KEYCODE_BACK)
    awaitUi("!document.querySelector('.reminder-editor')&&Boolean(document.querySelector('.settings-dialog[open]'))&&!document.querySelector('.settings-confirm')","Unchanged native back closes only the editor without a discard warning")
    assertTrue("Scrolled list position restored",kotlin.math.abs(evaluate(webview,"document.querySelector('.settings-content').scrollTop").toDouble()-originalScroll)<3.0)
    awaitUi("document.activeElement===($edit)","Focus returns to the edited row")
    tap(edit)
    awaitUi("Boolean(document.querySelector('.reminder-editor'))","Editor reopened")
    tap("document.querySelector('.reminder-editor textarea')")
    instrumentation.runOnMainSync{webview.requestFocus();(webview.context.getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager).showSoftInput(webview,InputMethodManager.SHOW_IMPLICIT)}
    awaitIme(true)
    instrumentation.sendStringSync(" changed")
    awaitUi("document.querySelector('.reminder-editor textarea')?.value.includes('changed')===true","Android key input changes the reminder")
    instrumentation.sendKeyDownUpSync(KeyEvent.KEYCODE_BACK)
    awaitIme(false)
    awaitUi("Boolean(document.querySelector('.reminder-editor'))&&!document.querySelector('.editor-discard')","First back dismisses the keyboard and preserves the editor")
    instrumentation.sendKeyDownUpSync(KeyEvent.KEYCODE_BACK)
    awaitUi("Boolean(document.querySelector('.editor-discard'))","Dirty native back requires explicit discard")
    tap("document.querySelector('[data-continue-editing]')")
    awaitUi("!document.querySelector('.editor-discard')&&document.querySelector('.reminder-editor textarea')?.value.includes('changed')===true","Continue editing preserves the unsaved text")
    tap("[...document.querySelectorAll('.reminder-editor button')].find(button=>button.textContent.trim()==='Cancelar')")
    awaitUi("Boolean(document.querySelector('.editor-discard'))","Cancel also protects actual unsaved changes")
    tap("document.querySelector('.editor-discard button.settings-danger')")
    awaitUi("!document.querySelector('.reminder-editor')&&document.activeElement===($edit)","Explicit discard returns focus to the original row")
    evaluate(webview,"document.querySelector('[data-android-ui-fixture=\"scroll\"]')?.remove();null")
    tap("document.querySelector('[aria-label=\"Fechar configurações\"]')")
    awaitUi("!document.querySelector('.settings-dialog[open]')","Settings close without a stale dirty warning")
    checkpoint("reminder_webview_scroll_edit_keyboard_native_back")
    } finally {
      if(hardwareKeyboardIme in setOf("0","1"))shell("settings put secure show_ime_with_hard_keyboard $hardwareKeyboardIme")
      else shell("settings delete secure show_ime_with_hard_keyboard")
    }
  }
  @Test fun liveSelfNotificationPostsThroughProductionJni() {
    val code = InstrumentationRegistry.getArguments().getString("pairingCode") ?: error("fixture_bootstrap_missing")
    val replacementCode = InstrumentationRegistry.getArguments().getString("replacementCode") ?: error("fixture_replacement_missing")
    checkpoint("live_activity_launch")
    // Destroying Tauri's last Activity exits the native process. Keep it alive until
    // AndroidJUnitRunner reports results; the emulator harness force-stops it afterwards.
    ActivityScenario.launch(notificationActivityClass()).let { scenario ->
      checkpoint("live_activity_ready")
      var candidate: WebView? = null
      scenario.onActivity { activity ->
        // Verify exact fixed method descriptors in the actual installed/minified activity.
        for (method in listOf("showNodeNotification", "showNodeNotificationLive", "nodePushState", "bindNodePush","settingsOpen","hideKeyboard","readVisibility","nodeActionState","consumeReminderOpen")) {
          assertEquals(String::class.java, activity.javaClass.getMethod(method, String::class.java).returnType)
        }
        assertEquals(Build.VERSION.SDK_INT < 33, frameworkNotificationPermission(activity))
      }
      // Tauri creates its WebView asynchronously after the Activity reaches RESUMED.
      val viewEnd = SystemClock.elapsedRealtime() + 30000
      while (candidate == null && SystemClock.elapsedRealtime() < viewEnd) {
        scenario.onActivity { activity -> candidate = findWebView(activity.window.decorView) }
        if (candidate == null) SystemClock.sleep(100)
      }
      val webview = candidate ?: error("webview_missing")
      checkpoint("live_webview_ready")
      val deadline = SystemClock.elapsedRealtime() + 30000
      while (evaluate(webview, "Boolean(window.__TAURI_INTERNALS__ && window.__TAURI_INTERNALS__.invoke)") != "true" && SystemClock.elapsedRealtime() < deadline) SystemClock.sleep(100)
      // Fail before pairing if this APK points anywhere except the isolated fixture.
      val runtime=invoke(webview,"runtime_info")
      assertEquals("http://127.0.0.1:18104",runtime.getString("backendUrl"))
      checkpoint("live_runtime_ready")
      exerciseReadVisibility(webview,scenario)
      val permissions=invoke(webview,"node_location",JSONObject().put("operation","permissions"))
      assertTrue(permissions.getBoolean("supported"))
      assertFalse(permissions.getBoolean("foreground"))
      assertFalse(permissions.getBoolean("precise"))
      val current=invoke(webview,"node_location",JSONObject().put("operation","current"))
      assertTrue(current.getString("status") in setOf("permission_denied","provider_unavailable"))
      assertFalse(current.has("latitude"));assertFalse(current.has("longitude"))
      checkpoint("location_bridge_framework")
      val identity = invoke(webview, "node_pair", JSONObject().put("name", "Android instrumentation").put("code", code))
      assertEquals("paired", identity.getString("state"))
      checkpoint("live_paired")
      val id = identity.getJSONObject("node").getString("id")
      val onlineEnd = SystemClock.elapsedRealtime() + 30000
      while (invoke(webview, "node_transport_status").getString("transportState") != "online" && SystemClock.elapsedRealtime() < onlineEnd) SystemClock.sleep(100)
      assertEquals("online", invoke(webview, "node_transport_status").getString("transportState"))
      checkpoint("live_online")
      val context = instrumentation.targetContext
      val manager = context.getSystemService(NotificationManager::class.java)
      if (Build.VERSION.SDK_INT >= 33) {
        val denied = invoke(webview, "node_test_notification", JSONObject().put("id", id))
        assertEquals("live_websocket", denied.getString("transport"))
        assertEquals("permission_denied", denied.getString("status"))
        assertEquals("android_permission_denied", denied.getString("diagnosticCode"))
        assertTrue(manager.activeNotifications.none { it.tag == denied.getString("commandId") })
        checkpoint("live_permission_denied")
        grantFrameworkPermission(context, Manifest.permission.POST_NOTIFICATIONS)
      }
      assertTrue(frameworkNotificationPermission(context))
      checkpoint("live_permission_granted")
      val result = invoke(webview, "node_test_notification", JSONObject().put("id", id))
      assertEquals("live_websocket", result.getString("transport"))
      val commandId = result.getString("commandId")
      try {
        val diagnostic = result.optString("diagnosticCode").takeIf { it.matches(Regex("android_[a-z_]{1,40}")) } ?: "none"
        assertEquals("diagnostic=$diagnostic", "success", result.getString("status"))
        val activeEnd = SystemClock.elapsedRealtime() + 5000
        while (manager.activeNotifications.none { it.tag == commandId && it.id == 1 } && SystemClock.elapsedRealtime() < activeEnd) SystemClock.sleep(50)
        assertTrue(manager.activeNotifications.any { it.tag == commandId && it.id == 1 })
        checkpoint("live_notification_active")
        withReenabledIdentityFramework(context,id,replacementCode,runtime.getString("version"),{name,args,timeout->invoke(webview,name,args,timeout)}) { nextCode,peerId,peerCredential->
          exerciseGeofenceLiveFramework(context,id,nextCode,peerId,peerCredential,setBackground={background->
            val state=if(background)Lifecycle.State.CREATED else Lifecycle.State.RESUMED
            scenario.moveToState(state);assertEquals(state,scenario.state)
          },exerciseUi={ruleId->exerciseReminderEditing(webview,scenario,ruleId)}){name,args,timeout->invoke(webview,name,args,timeout)}
        }
      } finally {
        checkpoint("live_cleanup_started")
        manager.cancel(commandId, 1)
        checkpoint("live_notification_cancelled")
        checkpoint("live_node_revoke_started")
        val currentIdentity=invoke(webview,"node_status")
        if(currentIdentity.getString("state")=="paired")invoke(webview, "node_revoke", JSONObject().put("id", currentIdentity.getJSONObject("node").getString("id")))
        checkpoint("live_node_revoked")
      }
    }
    checkpoint("live_cleanup_completed")
  }
}
