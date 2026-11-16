package com.aegis.node

import android.app.KeyguardManager
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager
import android.os.Build
import android.os.PowerManager
import android.os.Process
import android.os.SystemClock
import android.os.health.SystemHealthManager
import android.os.health.UidHealthStats
import org.json.JSONObject
import java.time.Instant
import java.util.UUID

/** Reads existing platform counters only. No receiver, thread, timer or power ownership. */
internal class HandsFreeBatteryProbe(private val context:Context) {
    companion object {
        private val processId=UUID.randomUUID().toString()
        internal fun intProperty(value:Int):Long?=value.takeUnless{it==Int.MIN_VALUE}?.toLong()
        internal fun longProperty(value:Long):Long?=value.takeUnless{it==Long.MIN_VALUE}
    }
    fun snapshot():JSONObject {
        fun <T> safe(read:()->T):T?=try{read()}catch(_:Exception){null}
        val elapsed=safe{SystemClock.elapsedRealtime()}?.takeIf{it>=0}
        val uptime=safe{SystemClock.uptimeMillis()}?.takeIf{it>=0}
        val processCpu=safe{Process.getElapsedCpuTime()}?.takeIf{it>=0}
        val wall=safe{System.currentTimeMillis()}
        val power=safe{context.getSystemService(PowerManager::class.java)}
        val battery=safe{context.getSystemService(BatteryManager::class.java)}
        val intent=safe{context.registerReceiver(null,IntentFilter(Intent.ACTION_BATTERY_CHANGED))}
        fun extra(key:String):Int?=intent?.takeIf{it.hasExtra(key)}?.getIntExtra(key,Int.MIN_VALUE)?.takeUnless{it==Int.MIN_VALUE}
        fun property(key:Int):Long?=battery?.let{safe{intProperty(it.getIntProperty(key))}}
        val level=extra(BatteryManager.EXTRA_LEVEL)
        val scale=extra(BatteryManager.EXTRA_SCALE)
        val percent=if(level!=null && level>=0 && scale!=null && scale>0)level*100.0/scale else property(BatteryManager.BATTERY_PROPERTY_CAPACITY)?.toDouble()
        val health=JSONObject().put("status","unavailable").put("scope","own_uid_os_accounting")
            .put("userCpuMs",JSONObject.NULL).put("systemCpuMs",JSONObject.NULL).put("audioTimerMs",JSONObject.NULL)
        safe {
            val stats=context.getSystemService(SystemHealthManager::class.java).takeMyUidSnapshot()
            fun measurement(key:Int)=if(stats.hasMeasurement(key))stats.getMeasurement(key).takeIf{it>=0} else null
            val user=measurement(UidHealthStats.MEASUREMENT_USER_CPU_TIME_MS)
            val system=measurement(UidHealthStats.MEASUREMENT_SYSTEM_CPU_TIME_MS)
            // Android's AUDIO timer is UID accounting, not measured microphone energy.
            val audio=if(stats.hasTimer(UidHealthStats.TIMER_AUDIO))stats.getTimerTime(UidHealthStats.TIMER_AUDIO).takeIf{it>=0} else null
            health.put("status","ok").put("userCpuMs",user?:JSONObject.NULL)
                .put("systemCpuMs",system?:JSONObject.NULL).put("audioTimerMs",audio?:JSONObject.NULL)
        }
        return JSONObject().put("at",wall?.let{safe{Instant.ofEpochMilli(it).toString()}}?:JSONObject.NULL)
            .put("elapsedMs",elapsed?:JSONObject.NULL)
            .put("uptimeMs",uptime?:JSONObject.NULL)
            .put("processId",processId)
            .put("processCpuMs",processCpu?:JSONObject.NULL)
            .put("batteryPercent",percent?.takeIf{it.isFinite() && it in 0.0..100.0}?:JSONObject.NULL)
            .put("chargeMicroAh",property(BatteryManager.BATTERY_PROPERTY_CHARGE_COUNTER)?.takeIf{it>=0}?:JSONObject.NULL)
            .put("energyNanoWh",battery?.let{safe{longProperty(it.getLongProperty(BatteryManager.BATTERY_PROPERTY_ENERGY_COUNTER))}}?.takeIf{it>=0}?:JSONObject.NULL)
            .put("currentMicroA",property(BatteryManager.BATTERY_PROPERTY_CURRENT_NOW)?:JSONObject.NULL)
            .put("currentAverageMicroA",property(BatteryManager.BATTERY_PROPERTY_CURRENT_AVERAGE)?:JSONObject.NULL)
            .put("charging",battery?.let{safe{it.isCharging}}?:JSONObject.NULL)
            .put("powerSaveMode",power?.let{safe{it.isPowerSaveMode}}?:JSONObject.NULL)
            .put("deviceIdleMode",power?.let{safe{it.isDeviceIdleMode}}?:JSONObject.NULL)
            .put("thermalStatus",if(Build.VERSION.SDK_INT>=29)power?.let{safe{it.currentThermalStatus}}?:JSONObject.NULL else JSONObject.NULL)
            .put("sdkInt",Build.VERSION.SDK_INT).put("manufacturer",Build.MANUFACTURER?:JSONObject.NULL)
            .put("model",Build.MODEL?:JSONObject.NULL).put("androidVersion",Build.VERSION.RELEASE?:JSONObject.NULL)
            .put("androidBuildFingerprint",Build.FINGERPRINT?:JSONObject.NULL)
            .put("plugged",extra(BatteryManager.EXTRA_PLUGGED)?:JSONObject.NULL)
            .put("batteryStatus",extra(BatteryManager.EXTRA_STATUS)?:JSONObject.NULL)
            .put("temperatureDeciC",extra(BatteryManager.EXTRA_TEMPERATURE)?:JSONObject.NULL)
            .put("voltageMv",extra(BatteryManager.EXTRA_VOLTAGE)?.takeIf{it>0}?:JSONObject.NULL)
            .put("screenInteractive",power?.let{safe{it.isInteractive}}?:JSONObject.NULL)
            .put("locked",safe{context.getSystemService(KeyguardManager::class.java).isKeyguardLocked}?:JSONObject.NULL)
            .put("uidHealth",health)
    }
}
