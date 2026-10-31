package com.aegis.node

import android.Manifest
import android.app.Activity
import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageManager
import android.os.SystemClock
import android.service.voice.VoiceInteractionService
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

/** Actual bundled WebView -> Tauri ACL -> Rust -> JNI -> Android state, including R8 APKs. */
@RunWith(AndroidJUnit4::class)
class NodeHandsFreeStateIpcTest {
    private val instrumentation=InstrumentationRegistry.getInstrumentation()
    private val context=instrumentation.targetContext
    private fun find(view:View):WebView? {
        if(view is WebView)return view
        if(view is ViewGroup)for(i in 0 until view.childCount)find(view.getChildAt(i))?.let{return it}
        return null
    }
    private fun evaluate(view:WebView,script:String):String {
        val done=CountDownLatch(1);var result="null"
        instrumentation.runOnMainSync{view.evaluateJavascript(script){result=it;done.countDown()}}
        check(done.await(10,TimeUnit.SECONDS)){"hands_free_webview_callback_timeout"}
        return result
    }
    private fun state(view:WebView):JSONObject {
        // Call only the distributed API. Do not replace invoke, permission checks or native state.
        evaluate(view,"window.__aegisHandsFreeStateTest=null;window.__TAURI_INTERNALS__.invoke('node_hands_free',{operation:'state'}).then(function(value){window.__aegisHandsFreeStateTest={ok:true,value:value};}).catch(function(){window.__aegisHandsFreeStateTest={ok:false};});null")
        val until=SystemClock.elapsedRealtime()+15000
        while(SystemClock.elapsedRealtime()<until) {
            val raw=evaluate(view,"window.__aegisHandsFreeStateTest")
            if(raw!="null") {
                val response=JSONObject(raw)
                assertTrue("hands_free_real_ipc_must_be_authorized",response.getBoolean("ok"))
                return response.getJSONObject("value")
            }
            SystemClock.sleep(50)
        }
        error("hands_free_real_ipc_timeout")
    }
    private fun waitFor(view:WebView,script:String,phase:String) {
        val deadline=SystemClock.elapsedRealtime()+15000
        while(evaluate(view,script)!="true") {
            check(SystemClock.elapsedRealtime()<deadline){phase};SystemClock.sleep(50)
        }
    }
    @Test fun initialSetupStateIsReachableWithoutModelMicrophoneRoleOrBackend() {
        // Host runner restricts this to a disposable emulator and revokes microphone before launch.
        assertFalse("fixture_requires_no_model",java.io.File(context.filesDir,"hands-free/aegis.int8.tflite").exists())
        assertFalse("fixture_requires_disabled_hands_free",context.getSharedPreferences("aegis-hands-free-v1",0).getBoolean("enabled",false))
        assertEquals(PackageManager.PERMISSION_DENIED,context.checkSelfPermission(Manifest.permission.RECORD_AUDIO))
        assertFalse(VoiceInteractionService.isActiveService(context,ComponentName(context,"com.aegis.node.NodeVoiceInteractionService")))
        val scenario=ActivityScenario.launch<Activity>(Intent().setClassName(context.packageName,"com.aegis.node.MainActivity").addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        var webview:WebView?=null
        val deadline=SystemClock.elapsedRealtime()+30000
        while(webview==null && SystemClock.elapsedRealtime()<deadline) {
            scenario.onActivity{activity->webview=find(activity.window.decorView)}
            if(webview==null)SystemClock.sleep(100)
        }
        val view=checkNotNull(webview){"hands_free_real_webview_missing"}
        while(evaluate(view,"Boolean(window.__TAURI_INTERNALS__ && window.__TAURI_INTERNALS__.invoke)")!="true") {
            check(SystemClock.elapsedRealtime()<deadline){"hands_free_real_ipc_bootstrap_timeout"};SystemClock.sleep(100)
        }
        repeat(2) {
            val result=state(view)
            for(key in listOf("enabled","assistantSelected","permissionGranted","modelReady","captureConfirmed"))
                assertFalse("initial_setup_flag_$key",result.getBoolean(key))
            assertTrue(result.getString("status").isNotBlank())
            assertTrue(result.getString("message").isNotBlank())
            assertEquals("aegis-mww-fleurs-dev-2026-10-31",result.getString("modelVersion"))
        }
        waitFor(view,"Boolean(document.querySelector('button[aria-label=\"Abrir histórico\"]') || document.querySelector('.global-settings-trigger'))","hands_free_real_ui_bootstrap_timeout")
        evaluate(view,"document.querySelector('button[aria-label=\"Abrir histórico\"]')?.click();null")
        waitFor(view,"Boolean(document.querySelector('.global-settings-trigger'))","hands_free_real_settings_trigger_timeout")
        evaluate(view,"document.querySelector('.global-settings-trigger').click();null")
        waitFor(view,"Boolean(document.querySelector('.settings-dialog[open]'))","hands_free_real_settings_dialog_timeout")
        evaluate(view,"Array.from(document.querySelectorAll('.settings-navigation button')).find(function(b){return b.textContent.trim()==='Áudio e voz';}).click();null")
        waitFor(view,"Boolean(document.querySelector('.hands-free-settings input[type=checkbox]'))","hands_free_real_settings_state_timeout")
        val controls=JSONObject(evaluate(view,"(function(){var e=document.querySelector('.hands-free-settings');return {labels:Array.from(e.querySelectorAll('button')).filter(function(b){return !b.disabled;}).map(function(b){return b.textContent.trim();}),checked:e.querySelector('input[type=checkbox]').checked,error:Boolean(e.querySelector('[role=alert]')),loading:e.textContent.includes('Consultando escuta…')};})()"))
        val labels=controls.getJSONArray("labels")
        for(label in listOf("Importar modelo privado","Permitir microfone","Selecionar assistente no Android","Atualizar estado"))
            assertTrue("initial_setup_control_missing",(0 until labels.length()).any{labels.getString(it)==label})
        assertFalse(controls.getBoolean("checked"));assertFalse(controls.getBoolean("error"));assertFalse(controls.getBoolean("loading"))
        assertEquals(PackageManager.PERMISSION_DENIED,context.checkSelfPermission(Manifest.permission.RECORD_AUDIO))
        assertFalse(context.getSharedPreferences("aegis-hands-free-v1",0).getBoolean("enabled",false))
        android.util.Log.i("AegisNodeNativeTest","phase=hands_free_real_ipc_initial_state errorType=Checkpoint")
        // Tao exits when its final Activity is destroyed. The host force-stops after results,
        // with waitForActivitiesToComplete=false, as in the existing live WebView tests.
    }
}
