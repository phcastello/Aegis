package com.aegis.node

import android.accessibilityservice.AccessibilityServiceInfo
import android.app.Activity
import android.app.KeyguardManager
import android.content.Context
import android.content.Intent
import android.location.LocationManager
import android.os.Build
import android.os.SystemClock
import android.view.accessibility.AccessibilityNodeInfo
import androidx.test.core.app.ActivityScenario
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONObject
import org.junit.Assert.*
import java.util.Locale

// As in the live framework test, closing the last Tauri Activity exits its
// process. Keep the real Activity alive until AndroidJUnitRunner publishes its
// result; the disposable host then stops that process between test stages.
private var legacyResolutionScenario:ActivityScenario<out Activity>?=null

/** Resolve actual Google settings; never inject/replace a location provider. */
internal fun prepareLegacyLocationConsent(context:Context,shell:(String)->String) {
 if(Build.VERSION.SDK_INT>=28)return
 val instrumentation=InstrumentationRegistry.getInstrumentation()
 val automation=instrumentation.uiAutomation
 shell("input keyevent KEYCODE_WAKEUP")
 shell("wm dismiss-keyguard")
 shell("input keyevent 82")
 val runtime=Class.forName("com.aegis.node.NodeGeofenceRuntime",true,context.classLoader)
 val method=runtime.getMethod("legacyLocationSettingsFixture",Context::class.java,Boolean::class.javaPrimitiveType)
 val resolution=runtime.getMethod("legacyLocationResolutionFixture",Activity::class.java,String::class.java)
 val scenario=legacyResolutionScenario?:ActivityScenario.launch(notificationActivityClass()).also{legacyResolutionScenario=it}
 lateinit var activity:Activity
 scenario.onActivity{activity=it}
 fun settings(resolve:Boolean):JSONObject {
  var result=JSONObject(method.invoke(null,activity,resolve) as String)
  if(resolve && result.optString("status")=="resolution_prepared")instrumentation.runOnMainSync {
   result=JSONObject(resolution.invoke(null,activity,"open") as String)
  }
  return result
 }
 val manager=context.getSystemService(LocationManager::class.java)
 val home=context.packageManager.resolveActivity(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME),0)?.activityInfo?.packageName
 fun packageCode(value:String?)=when(value) {
  "com.google.android.gms"->"gms";"com.android.settings"->"settings";context.packageName->"target";"android"->"android"
  "com.android.systemui"->"keyguard"
  "com.android.packageinstaller","com.google.android.packageinstaller","com.android.permissioncontroller","com.google.android.permissioncontroller"->"permissioncontroller"
  "com.google.android.gsf","com.google.process.gapps"->"googleframework";"com.google.android.location"->"googlelocation"
  "com.android.launcher","com.android.launcher2","com.android.launcher3","com.google.android.apps.nexuslauncher"->"launcher"
  null->"none";else->if(home!=null && value==home)"launcher" else "other"
 }
 fun resumedCode():String=try {
  val value=Regex("(?:mResumedActivity|mFocusedActivity):\\s*ActivityRecord\\{[^\\n]*?\\su\\d+\\s+([A-Za-z0-9_.]+)/").find(shell("dumpsys activity activities"))?.groupValues?.get(1)
  packageCode(value)
 }catch(_:Exception){"unavailable"}
 fun masks():String {
  val allowed=shell("settings get secure location_providers_allowed").trim().split(',').toSet()
  val keyguard=context.getSystemService(KeyguardManager::class.java)
  val power=context.getSystemService(android.os.PowerManager::class.java)
  return "gpsAllowed=${"gps" in allowed} networkAllowed=${"network" in allowed} gpsEnabled=${manager.isProviderEnabled(LocationManager.GPS_PROVIDER)} networkEnabled=${manager.isProviderEnabled(LocationManager.NETWORK_PROVIDER)} saver=${power.isPowerSaveMode} interactive=${power.isInteractive} keyguardLocked=${keyguard.isKeyguardLocked} deviceLocked=${keyguard.isDeviceLocked}"
 }
 val serviceInfo=automation.serviceInfo
 val oldFlags=serviceInfo.flags
 automation.serviceInfo=serviceInfo.apply{flags=flags or AccessibilityServiceInfo.FLAG_REPORT_VIEW_IDS or AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS}
 try {
  var result=settings(true)
  if(result.optString("status")=="resolution_opened") {
   android.util.Log.i("AegisNodeNativeTest","phase=hybrid_legacy_nlp_resolution_opened errorType=Checkpoint")
   val end=SystemClock.elapsedRealtime()+30000
   var accepted=false
   var crashOverlayClosed=false
   var reopenResolution=false
   var reopenStatus="not_requested"
   val packages=linkedSetOf<String>()
   val viewIds=linkedSetOf<String>()
   var windowCount=0;var visitedCount=0;var positiveCount=0;var clickableCount=0
   val approvedPackages=setOf("com.google.android.gms","com.android.settings","android")
   val positiveLabels=setOf("AGREE","I AGREE","YES","TURN ON","OK","ACCEPT")
   fun selectPositive(root:AccessibilityNodeInfo):Boolean {
    packages.add(packageCode(root.packageName?.toString()))
    if(root.packageName?.toString() !in approvedPackages)return false
    // An unrelated legacy emulator crash dialog can cover the official Google
    // resolution. Close only this exact system control once; it is neither a
    // consent action nor evidence that any provider or registration succeeded.
    if(root.packageName?.toString()=="android" && !crashOverlayClosed) {
     val close=root.findAccessibilityNodeInfosByViewId("android:id/aerr_close")
     try {for(button in close)if(button.isVisibleToUser && button.isEnabled && button.isClickable && button.performAction(AccessibilityNodeInfo.ACTION_CLICK)) {
      crashOverlayClosed=true;reopenResolution=true
      if(viewIds.size<16)viewIds.add("android:id/aerr_close")
      android.util.Log.i("AegisNodeNativeTest","phase=hybrid_legacy_nlp_crash_overlay_closed errorType=Checkpoint")
      return false
     }}finally{close.forEach{it.recycle()}}
    }
    val buttons=root.findAccessibilityNodeInfosByViewId("android:id/button1")
    try {for(button in buttons)if(button.isVisibleToUser && button.isEnabled && button.isClickable &&
     (root.packageName?.toString()!="android" || button.text?.toString()?.trim()?.uppercase(Locale.ROOT) in positiveLabels)) {
     positiveCount++
     if(button.performAction(AccessibilityNodeInfo.ACTION_CLICK))return true
    }}finally{buttons.forEach{it.recycle()}}
    // The original Google resolution may use an application resource instead of
    // the framework AlertDialog button ID. Only its exact positive vocabulary
    // is accepted, and no label is retained or included in diagnostics.
    var visited=0
    fun clickAncestorButton(node:AccessibilityNodeInfo,depth:Int):Boolean {
     val parent=node.parent?:return false
     return try {
      if(parent.className?.toString()?.endsWith("Button")==true && parent.isVisibleToUser && parent.isEnabled && parent.isClickable)
       parent.performAction(AccessibilityNodeInfo.ACTION_CLICK)
      else depth>1 && clickAncestorButton(parent,depth-1)
     }finally{parent.recycle()}
    }
    fun visit(node:AccessibilityNodeInfo):Boolean {
     if(++visited>256)return false
     visitedCount++
     val resource=node.viewIdResourceName
     if(viewIds.size<16 && resource!=null && resource.matches(Regex("(?:android|com\\.google\\.android\\.gms|com\\.android\\.settings):id/[A-Za-z0-9_]{1,80}")))viewIds.add(resource)
     if(node.isVisibleToUser && node.isEnabled) {
      if(node.isClickable)clickableCount++
      if(node.text?.toString()?.trim()?.uppercase(Locale.ROOT) in positiveLabels) {
       positiveCount++
       if(node.isClickable && node.performAction(AccessibilityNodeInfo.ACTION_CLICK))return true
       if(!node.isClickable && clickAncestorButton(node,2))return true
      }
     }
     for(index in 0 until node.childCount) {
      val child=node.getChild(index)?:continue
      try{if(visit(child))return true}finally{child.recycle()}
     }
     return false
    }
    return visit(root)
   }
   while(!accepted && SystemClock.elapsedRealtime()<end) {
    visitedCount=0;positiveCount=0;clickableCount=0
    val windows=automation.windows
    windowCount=windows.size
    try {for(window in windows) {
     val root=window.root?:continue
     try{if(selectPositive(root)){accepted=true;break};if(reopenResolution)break}finally{root.recycle()}
    }}finally{windows.forEach{it.recycle()}}
    if(!accepted && !reopenResolution) {
     val root=automation.rootInActiveWindow
     try{if(root!=null)accepted=selectPositive(root)}finally{root?.recycle()}
    }
    if(reopenResolution) {
     reopenResolution=false
     // A SettingsClient call has at most 5s + 10s of SDK waits. Reopen at most
     // once, only while it fits in the explicit thirty-second UI budget.
     if(end-SystemClock.elapsedRealtime()<15000)break
     result=settings(true)
     reopenStatus=result.optString("status").takeIf{it in setOf("ready","resolution_opened","resolution_required","resolution_unavailable","activity_unavailable","settings_unavailable","interrupted")}?:"unknown"
     if(result.optString("status")=="ready")break
    }
    if(!accepted)SystemClock.sleep(100)
   }
   if(!accepted)result=settings(false)
   val ui="packages=${packages.joinToString(",")} resumed=${resumedCode()} reopen=$reopenStatus windows=$windowCount visited=$visitedCount clickable=$clickableCount positive=$positiveCount viewIds=${viewIds.joinToString(",")}"
   assertTrue("The returned Google settings resolution must expose its real positive control or become ready after removing the crash overlay; $ui; ${masks()}; diagnostics=$result",accepted || (crashOverlayClosed && result.optString("status")=="ready"))
   if(accepted)android.util.Log.i("AegisNodeNativeTest","phase=hybrid_legacy_nlp_consent_accepted errorType=Checkpoint")
   // Requery the real client after the actual positive action; a click alone
   // never certifies that the framework settings or NLP are usable.
   val settled=SystemClock.elapsedRealtime()+15000
   do {
    result=settings(false)
    if(result.optString("status")=="ready")break
    SystemClock.sleep(250)
   }while(SystemClock.elapsedRealtime()<settled)
  }
  assertEquals("Google settings precondition; ${masks()}; diagnostics=$result","ready",result.optString("status"))
  assertTrue("Google reports a present and usable network provider; ${masks()}; diagnostics=$result",result.optBoolean("networkPresent") && result.optBoolean("networkUsable"))
  var stableSince=0L
  val end=SystemClock.elapsedRealtime()+15000
  while(SystemClock.elapsedRealtime()<end) {
   val now=SystemClock.elapsedRealtime()
   if(manager.isProviderEnabled(LocationManager.GPS_PROVIDER) && manager.isProviderEnabled(LocationManager.NETWORK_PROVIDER)) {
    if(stableSince==0L)stableSince=now
    if(now-stableSince>=3000)break
   }else stableSince=0
   SystemClock.sleep(100)
  }
  assertTrue("Actual framework providers remain enabled for three seconds; ${masks()}",stableSince>0 && SystemClock.elapsedRealtime()-stableSince>=3000)
  android.util.Log.i("AegisNodeNativeTest","phase=hybrid_legacy_nlp_settings_ready errorType=Checkpoint")
 }finally{
  resolution.invoke(null,null,"clear")
  automation.serviceInfo=serviceInfo.apply{flags=oldFlags}
 }
}
