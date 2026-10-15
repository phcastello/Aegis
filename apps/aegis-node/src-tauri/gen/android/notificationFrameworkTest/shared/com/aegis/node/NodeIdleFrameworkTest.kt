package com.aegis.node

import android.os.Build
import android.os.SystemClock
import android.view.View
import android.view.ViewGroup
import android.webkit.WebView
import androidx.lifecycle.Lifecycle
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/** Same external instrumentation drives accepted and candidate APKs; no domain fixture hooks. */
@RunWith(AndroidJUnit4::class)
class NodeIdleFrameworkTest {
 private val instrumentation=InstrumentationRegistry.getInstrumentation()
 private fun webview(view:View):WebView? {if(view is WebView)return view;if(view is ViewGroup)for(i in 0 until view.childCount)webview(view.getChildAt(i))?.let{return it};return null}
 private fun evaluate(view:WebView,script:String):String {
  val latch=CountDownLatch(1);var result="null"
  instrumentation.runOnMainSync{view.evaluateJavascript(script){result=it;latch.countDown()}}
  check(latch.await(10,TimeUnit.SECONDS)){"idle_webview_callback_timeout"};return result
 }
 private fun invoke(view:WebView,name:String,args:JSONObject=JSONObject()):JSONObject {
  evaluate(view,"window.__aegisIdleReply=null;window.__TAURI_INTERNALS__.invoke(${JSONObject.quote(name)},$args).then(value=>window.__aegisIdleReply={ok:true,value}).catch(()=>window.__aegisIdleReply={ok:false});null")
  val end=SystemClock.elapsedRealtime()+30000
  while(SystemClock.elapsedRealtime()<end) {val raw=evaluate(view,"window.__aegisIdleReply");if(raw!="null"){val result=JSONObject(raw);check(result.getBoolean("ok")){"idle_native_invocation_failed"};return result.optJSONObject("value")?:JSONObject()};SystemClock.sleep(100)}
  error("idle_native_invocation_timeout")
 }
 @Test fun equivalentTenMinuteIdleWindows() {
  val args=InstrumentationRegistry.getArguments();val mode=args.getString("idleMode")?:error("idle_mode_missing")
  require(mode in setOf("before","after"))
  val duration=args.getString("phaseDurationMs")?.toLong()?:150000L;require(duration>=150000L && duration<=600000L)
  val code=if(mode=="before")args.getString("pairingCode")?:error("idle_bootstrap_missing") else null
  val expected=if(mode=="after")args.getString("expectedNodeId")?:error("idle_expected_identity_missing") else null
  ActivityScenario.launch(notificationActivityClass()).let {scenario->
   var target:WebView?=null;val end=SystemClock.elapsedRealtime()+30000
   while(target==null && SystemClock.elapsedRealtime()<end){scenario.onActivity{target=webview(it.window.decorView)};if(target==null)SystemClock.sleep(100)}
   val view=target?:error("idle_webview_missing")
   while(evaluate(view,"Boolean(window.__TAURI_INTERNALS__&&window.__TAURI_INTERNALS__.invoke)")!="true"&&SystemClock.elapsedRealtime()<end)SystemClock.sleep(100)
   assertEquals("http://127.0.0.1:18104",invoke(view,"runtime_info").getString("backendUrl"))
   val paired=if(mode=="before")invoke(view,"node_pair",JSONObject().put("name","Android idle $mode").put("code",code)) else invoke(view,"node_status")
   assertEquals("paired",paired.getString("state"))
   val nodeId=paired.getJSONObject("node").getString("id")
   if(mode=="after")assertEquals("The upgrade must preserve the installation NodeId and native vault",expected,nodeId)
   val online=SystemClock.elapsedRealtime()+30000
   while(invoke(view,"node_transport_status").optString("transportState")!="online"&&SystemClock.elapsedRealtime()<online)SystemClock.sleep(100)
   assertEquals("online",invoke(view,"node_transport_status").getString("transportState"))
   val configured=invoke(view,"node_notification_settings").optBoolean("pushConfigured")
   fun closePanels() {
    repeat(3){evaluate(view,"(()=>{const b=document.querySelector('[aria-label=\"Fechar configurações\"]')||[...document.querySelectorAll('button')].find(b=>b.textContent.trim()==='Fechar'&&b.getClientRects().length);b?.click();return Boolean(b)})()");SystemClock.sleep(100)}
   }
   fun phase(name:String) {
    // All setup and DOM inspection precede the measured interval. No JS polling while idle.
    val ui=if(name=="background")JSONObject().put("visible",false) else JSONObject(evaluate(view,"(()=>{const a=document.getAnimations();return {visible:document.visibilityState==='visible',runningAnimations:a.filter(x=>x.playState==='running').length,visibleRunningAnimations:a.filter(x=>x.playState==='running'&&x.effect?.target?.getClientRects().length).length,domElements:document.getElementsByTagName('*').length}})()"))
    fun mark(event:String){android.util.Log.i("AegisIdleTest",JSONObject().put("event",event).put("mode",mode).put("nodeId",nodeId).put("phase",name).put("elapsedRealtimeMs",SystemClock.elapsedRealtime()).put("durationMs",duration).put("pushConfigured",configured).put("api",Build.VERSION.SDK_INT).put("hardware",Build.HARDWARE).put("model",Build.MODEL).put("ui",ui).toString())}
    mark("start");Thread.sleep(duration);mark("end")
   }
   closePanels();SystemClock.sleep(1000);phase("foreground_closed")
   val opened=evaluate(view,"(()=>{const b=document.querySelector('[aria-label=\"Configurações da Aegis\"]')||document.querySelector('[aria-label=\"Dispositivos / Nodes\"]');b?.click();return Boolean(b)})()")
   assertEquals("true",opened);SystemClock.sleep(1000);phase("devices_visible")
   closePanels()
   val settings=evaluate(view,"(()=>{const s=document.querySelector('[aria-label=\"Configurações da Aegis\"]');if(s){s.click();return true}const a=document.querySelector('[aria-label=\"Sobre a Aegis\"]');a?.click();return Boolean(a)})()")
   assertEquals("true",settings);SystemClock.sleep(200)
   evaluate(view,"(()=>{[...document.querySelectorAll('.settings-navigation button')].find(b=>b.textContent.trim()==='Sobre')?.click();return true})()")
   SystemClock.sleep(200)
   evaluate(view,"(()=>{const s=[...document.querySelectorAll('summary')].find(s=>s.textContent.includes('Diagnóstico técnico'));if(s&&!s.parentElement.open)s.click();return true})()")
   SystemClock.sleep(1000);phase("diagnostics_visible")
   closePanels();scenario.moveToState(Lifecycle.State.CREATED);SystemClock.sleep(1000);phase("background")
   scenario.moveToState(Lifecycle.State.RESUMED)
   if(mode=="after")invoke(view,"node_revoke",JSONObject().put("id",nodeId))
  }
 }
}
