package com.aegis.node

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.media.AudioDeviceCallback
import android.media.AudioDeviceInfo
import android.media.AudioManager
import android.media.AudioRecordingConfiguration
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.service.voice.VoiceInteractionService
import android.service.voice.VoiceInteractionSession
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat

/** System-selected assistant, never started as an arbitrary background microphone service. */
class NodeVoiceInteractionService:VoiceInteractionService() {
    private var tornDown=false
    private var ready=false
    private var registered=false
    private var foreground=false
    private val main=Handler(Looper.getMainLooper())
    private val runtime by lazy{NodeHandsFree.runtime(this)}
    private val receiver=object:BroadcastReceiver(){override fun onReceive(context:Context,intent:Intent){
        if(intent.action==ACTION_STOP){runtime.configure(false,false);return}
        snapshot(intent)
        if(intent.action=="android.media.action.MICROPHONE_MUTE_CHANGED")runtime.recoverFromSystem()
        else if(intent.action!="android.intent.action.BATTERY_CHANGED")runtime.reconcile()
    }}
    private var externalRecording:Boolean?=null
    private val recovery=Runnable{if(!tornDown)runtime.reconcile()}
    private val recording=object:AudioManager.AudioRecordingCallback(){override fun onRecordingConfigChanged(configs:MutableList<AudioRecordingConfiguration>){
        // Keep only an in-memory availability boolean. Filter recently owned AudioRecord IDs
        // so our own release cannot replenish the failure budget and cause a retry loop.
        val external=configs.any{!runtime.isOwnAudioSession(it.clientAudioSessionId)}
        val externalEnded=externalRecording==true && !external
        externalRecording=external
        if(!tornDown){
            main.removeCallbacks(recovery)
            if(externalEnded)runtime.recoverFromSystem() else main.postDelayed(recovery,2000)
        }
    }}
    private val devices=object:AudioDeviceCallback(){
        override fun onAudioDevicesAdded(addedDevices:Array<out AudioDeviceInfo>){changed()}
        override fun onAudioDevicesRemoved(removedDevices:Array<out AudioDeviceInfo>){changed()}
        private fun changed(){runtime.metrics.event("audio_route_changed");if(!tornDown)runtime.recoverFromSystem()}
    }
    private var modeListener:Any?=null
    override fun onReady() {
        super.onReady();if(tornDown || registered)return
        ready=true;registered=true
        // The assistant has no need for content or screenshots from the foreground application.
        setDisabledShowContext(VoiceInteractionSession.SHOW_WITH_ASSIST or VoiceInteractionSession.SHOW_WITH_SCREENSHOT)
        ContextCompat.registerReceiver(this,receiver,IntentFilter().apply{
            addAction(ACTION_STOP);addAction(Intent.ACTION_SCREEN_ON);addAction(Intent.ACTION_SCREEN_OFF);addAction(Intent.ACTION_USER_PRESENT)
            addAction(Intent.ACTION_BATTERY_CHANGED);addAction("android.media.action.MICROPHONE_MUTE_CHANGED")
        },ContextCompat.RECEIVER_NOT_EXPORTED)
        val manager=getSystemService(AudioManager::class.java)
        manager.registerAudioDeviceCallback(devices,main);manager.registerAudioRecordingCallback(recording,main)
        if(Build.VERSION.SDK_INT>=31){val listener=AudioManager.OnModeChangedListener{runtime.metrics.event("audio_mode_changed");runtime.recoverFromSystem()};modeListener=listener;manager.addOnModeChangedListener(mainExecutor,listener)}
        snapshot(null);runtime.attach(this)
    }
    private fun snapshot(intent:Intent?) {
        val power=getSystemService(android.os.PowerManager::class.java)
        val keyguard=getSystemService(android.app.KeyguardManager::class.java)
        runtime.screenLocked=keyguard.isKeyguardLocked;runtime.screenInteractive=power.isInteractive
        val battery=if(intent?.action==Intent.ACTION_BATTERY_CHANGED)intent else registerReceiver(null,IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        val level=battery?.getIntExtra("level",-1)?:-1;val scale=battery?.getIntExtra("scale",-1)?:-1
        val percent=if(level>=0 && scale>0)level*100/scale else null
        val status=battery?.getIntExtra("status",-1)
        runtime.metrics.device(runtime.screenLocked,runtime.screenInteractive,percent,status?.let{it==android.os.BatteryManager.BATTERY_STATUS_CHARGING || it==android.os.BatteryManager.BATTERY_STATUS_FULL})
    }
    internal fun ensureForeground():Boolean {
        if(tornDown || !ready)return false
        return try {
            val manager=getSystemService(NotificationManager::class.java)
            if(Build.VERSION.SDK_INT>=26)manager.createNotificationChannel(NotificationChannel(CHANNEL,"Hands-free Aegis",NotificationManager.IMPORTANCE_LOW).apply{setShowBadge(false)})
            val notification=notification("Preparando escuta local.")
            if(Build.VERSION.SDK_INT>=29)startForeground(NOTIFICATION,notification,ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE) else startForeground(NOTIFICATION,notification)
            foreground=true;true
        }catch(_:Exception){runtime.metrics.event("foreground_start_denied");false}
    }
    private fun notification(text:String):android.app.Notification {
        val open=PendingIntent.getActivity(this,580,Intent(this,MainActivity::class.java),PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val stop=PendingIntent.getBroadcast(this,581,Intent(ACTION_STOP).setPackage(packageName),PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        return NotificationCompat.Builder(this,CHANNEL).setContentTitle("Aegis hands-free").setContentText(text).setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .setContentIntent(open).setOngoing(true).setSilent(true).setPriority(NotificationCompat.PRIORITY_LOW).addAction(0,"Desligar",stop).build()
    }
    internal fun updateNotification(text:String){if(foreground)getSystemService(NotificationManager::class.java).notify(NOTIFICATION,notification(text))}
    internal fun stopHandsFreeForeground(){if(foreground){foreground=false;stopForeground(STOP_FOREGROUND_REMOVE)}}
    internal fun showHandsFreeSession(token:String){if(ready && !tornDown)showSession(Bundle().apply{putBoolean("activation",true);putString("sessionId",token)},0)}
    override fun onLaunchVoiceAssistFromKeyguard(){if(ready && !tornDown)showSession(Bundle(),0)}
    override fun onShutdown(){tearDown();super.onShutdown()}
    override fun onDestroy(){tearDown();super.onDestroy()}
    private fun tearDown() {
        if(tornDown)return;tornDown=true;ready=false
        if(registered){registered=false;try{unregisterReceiver(receiver)}catch(_:IllegalArgumentException){}
            val manager=getSystemService(AudioManager::class.java);manager.unregisterAudioDeviceCallback(devices);manager.unregisterAudioRecordingCallback(recording)
            if(Build.VERSION.SDK_INT>=31)modeListener?.let{manager.removeOnModeChangedListener(it as AudioManager.OnModeChangedListener)}
        }
        main.removeCallbacks(recovery);runtime.detach(this);stopHandsFreeForeground()
    }
    companion object {private const val CHANNEL="aegis-hands-free";private const val NOTIFICATION=580;private const val ACTION_STOP="com.aegis.node.HANDS_FREE_STOP"}
}
