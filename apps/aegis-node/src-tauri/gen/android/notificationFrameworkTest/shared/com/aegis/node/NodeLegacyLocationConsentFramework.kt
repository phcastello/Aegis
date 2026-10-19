package com.aegis.node

import android.accessibilityservice.AccessibilityServiceInfo
import android.content.Context
import android.location.LocationManager
import android.os.Build
import android.os.SystemClock
import android.view.accessibility.AccessibilityNodeInfo
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONObject
import org.junit.Assert.*

/** Resolve actual Google settings; never inject/replace a location provider. */
internal fun prepareLegacyLocationConsent(context:Context,shell:(String)->String) {
 if(Build.VERSION.SDK_INT>=28)return
 val instrumentation=InstrumentationRegistry.getInstrumentation()
 val automation=instrumentation.uiAutomation
 val runtime=Class.forName("com.aegis.node.NodeGeofenceRuntime",true,context.classLoader)
 val method=runtime.getMethod("legacyLocationSettingsFixture",Context::class.java,Boolean::class.javaPrimitiveType)
 fun settings(resolve:Boolean)=JSONObject(method.invoke(null,context,resolve) as String)
 val manager=context.getSystemService(LocationManager::class.java)
 fun masks():String {
  val allowed=shell("settings get secure location_providers_allowed").trim().split(',').toSet()
  return "gpsAllowed=${"gps" in allowed} networkAllowed=${"network" in allowed} gpsEnabled=${manager.isProviderEnabled(LocationManager.GPS_PROVIDER)} networkEnabled=${manager.isProviderEnabled(LocationManager.NETWORK_PROVIDER)} saver=${context.getSystemService(android.os.PowerManager::class.java).isPowerSaveMode}"
 }
 val serviceInfo=automation.serviceInfo
 val oldFlags=serviceInfo.flags
 automation.serviceInfo=serviceInfo.apply{flags=flags or AccessibilityServiceInfo.FLAG_REPORT_VIEW_IDS}
 try {
  shell("input keyevent KEYCODE_WAKEUP")
  shell("wm dismiss-keyguard")
  var result=settings(true)
  if(result.optString("status")=="resolution_opened") {
   android.util.Log.i("AegisNodeNativeTest","phase=hybrid_legacy_nlp_resolution_opened errorType=Checkpoint")
   val end=SystemClock.elapsedRealtime()+15000
   var accepted=false
   while(!accepted && SystemClock.elapsedRealtime()<end) {
    val root=automation.rootInActiveWindow
    try {
     if(root?.packageName?.toString() in setOf("com.google.android.gms","com.android.settings")) {
      val buttons=root?.findAccessibilityNodeInfosByViewId("android:id/button1")?:emptyList()
      try {for(button in buttons)if(button.isVisibleToUser && button.isEnabled && button.isClickable) {
       accepted=button.performAction(AccessibilityNodeInfo.ACTION_CLICK);if(accepted)break
      }}finally{buttons.forEach{it.recycle()}}
     }
    }finally{root?.recycle()}
    if(!accepted)SystemClock.sleep(100)
   }
   assertTrue("The returned Google settings resolution must expose its real positive control",accepted)
   android.util.Log.i("AegisNodeNativeTest","phase=hybrid_legacy_nlp_consent_accepted errorType=Checkpoint")
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
 }finally{automation.serviceInfo=serviceInfo.apply{flags=oldFlags}}
}
