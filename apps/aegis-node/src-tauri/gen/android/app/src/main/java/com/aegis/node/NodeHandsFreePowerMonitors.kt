package com.aegis.node

import android.annotation.TargetApi
import android.content.Context
import android.os.Build
import android.os.OutcomeReceiver
import android.os.PowerMonitor
import android.os.PowerMonitorReadings
import android.os.SystemClock
import android.os.health.SystemHealthManager
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.Executor
import java.util.concurrent.ExecutorService
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit

internal enum class HandsFreePowerMonitorKind(val wireName:String) { MEASUREMENT("measurement"),CONSUMER("consumer") }
internal data class HandsFreePowerMonitorReading(val name:String,val kind:HandsFreePowerMonitorKind,val energyUws:Long,val timestampElapsedMs:Long)
internal sealed class HandsFreePowerMonitorResult {
    data class Readings(val readings:List<HandsFreePowerMonitorReading>,val totalMonitorCount:Int=readings.size):HandsFreePowerMonitorResult()
    object Error:HandsFreePowerMonitorResult()
}
internal fun interface HandsFreePowerMonitorSource {
    fun request(executor:Executor,callback:(HandsFreePowerMonitorResult)->Unit)
}

/** Optional whole-device readings. No polling timer, alarm, microphone or wakelock.
 * Request and callback work share one expiring worker. Timeouts are observed only
 * when the existing diagnostic caller requests/checks state, never by waking it.
 */
internal class HandsFreePowerMonitors internal constructor(
    private val sdkInt:Int,
    private val source:HandsFreePowerMonitorSource?,
    private val elapsed:()->Long,
    private val wall:()->Long,
    private val executor:ExecutorService
):AutoCloseable {
    constructor(context:Context):this(Build.VERSION.SDK_INT,
        if(Build.VERSION.SDK_INT>=35)HandsFreePowerMonitorApi35(context.applicationContext) else null,
        {SystemClock.elapsedRealtime()},{System.currentTimeMillis()},newWorker())

    companion object {
        const val MIN_INTERVAL_MS=60_000L
        const val REQUEST_TIMEOUT_MS=10_000L
        const val STALE_AFTER_MS=120_000L
        const val MAX_MONITORS=64
        const val MAX_NAME_LENGTH=128
        private fun newWorker():ExecutorService=ThreadPoolExecutor(0,1,30,TimeUnit.SECONDS,
            LinkedBlockingQueue<Runnable>(4),{task->Thread(task,"AegisPowerMonitors").apply{isDaemon=true}})
    }
    private data class Sample(val name:String,val kind:HandsFreePowerMonitorKind,val energy:Long?,val timestamp:Long?)
    private val lock=Any()
    private var generation=0L
    private var closed=false
    private var pending=false
    private var status=if(sdkInt<35)"unsupported_api" else "not_requested"
    private var error:String?=null
    private var lastRequestElapsed:Long?=null
    private var lastRequestWall:Long?=null
    private var lastObservedElapsed:Long?=null
    private var lastObservedWall:Long?=null
    private var sampledElapsed:Long?=null
    private var sampledWall:Long?=null
    private var trigger="observation"
    private var samples=emptyList<Sample>()
    private var totalMonitorCount=0

    private fun expireLocked(now:Long) {
        val started=lastRequestElapsed?:return
        if(pending && (now<started || now-started>=REQUEST_TIMEOUT_MS)) {
            pending=false;generation++;status="timeout";error="request_timeout"
            lastObservedElapsed=now;lastObservedWall=wall()
        }
    }
    private fun active(token:Long):Boolean=synchronized(lock){!closed && pending && generation==token}

    /** force marks a manual request; it never bypasses the one-per-minute limit. */
    fun request(force:Boolean=false) {
        val token=synchronized(lock) {
            val now=elapsed();expireLocked(now)
            if(closed || sdkInt<35 || pending)return
            val previous=lastRequestElapsed
            if(previous!=null && (now<previous || now-previous<MIN_INTERVAL_MS))return
            generation++;pending=true;status="pending";error=null
            lastRequestElapsed=now;lastRequestWall=wall();trigger=if(force)"manual" else "observation"
            generation
        }
        try {
            executor.execute {
                if(!active(token))return@execute
                try {
                    val callbackExecutor=Executor { task->
                        if(active(token))try { executor.execute { if(active(token))task.run() } }
                        catch(_:Exception){complete(token,HandsFreePowerMonitorResult.Error)}
                    }
                    val availableSource=source
                    if(availableSource==null)complete(token,HandsFreePowerMonitorResult.Error)
                    else availableSource.request(callbackExecutor){result->complete(token,result)}
                } catch(_:Exception) { complete(token,HandsFreePowerMonitorResult.Error) }
            }
        } catch(_:Exception) { complete(token,HandsFreePowerMonitorResult.Error) }
    }

    private fun complete(token:Long,result:HandsFreePowerMonitorResult)=synchronized(lock) {
        val now=elapsed();expireLocked(now)
        if(closed || !pending || token!=generation)return@synchronized
        pending=false;lastObservedElapsed=now;lastObservedWall=wall()
        when(result) {
            HandsFreePowerMonitorResult.Error->{status="error";error="readings_unavailable"}
            is HandsFreePowerMonitorResult.Readings->{
                totalMonitorCount=maxOf(result.totalMonitorCount,result.readings.size).coerceAtLeast(0)
                samples=result.readings.take(MAX_MONITORS).map { reading->
                    val time=reading.timestampElapsedMs.takeIf { it>=0 && it<=now }
                    // Negative energy includes ENERGY_UNAVAILABLE (-1). It is not zero use.
                    val energy=reading.energyUws.takeIf { it>=0 && time!=null }
                    Sample(reading.name.take(MAX_NAME_LENGTH),reading.kind,energy,time)
                }
                sampledElapsed=now;sampledWall=lastObservedWall
                status=if(samples.any{it.energy!=null})"available" else "unavailable"
                error=when { samples.isEmpty()->"no_power_monitors";status=="unavailable"->"readings_unavailable";else->null }
            }
        }
    }

    fun state():JSONObject=synchronized(lock) {
        val now=elapsed();expireLocked(now)
        val sampled=sampledElapsed
        val hasFreshReading=samples.any{it.energy!=null && it.timestamp!=null && now>=it.timestamp && now-it.timestamp<STALE_AFTER_MS}
        val stale=status!="available" || sampled==null || now<sampled || now-sampled>=STALE_AFTER_MS || !hasFreshReading
        val monitors=JSONArray()
        for(sample in samples) {
            val row=JSONObject().put("name",sample.name).put("type",sample.kind.wireName)
                .put("available",sample.energy!=null)
            sample.energy?.let{row.put("energyUws",it)}
            sample.timestamp?.let { row.put("timestampElapsedMs",it)
                row.put("stale",stale || now<it || now-it>=STALE_AFTER_MS) }
            if(sample.timestamp==null)row.put("stale",true)
            monitors.put(row)
        }
        JSONObject().put("schema",1).put("status",status).put("supportedApi",sdkInt>=35)
            .put("sdkInt",sdkInt).put("minimumApi",35).put("scope","whole_device")
            .put("attribution","not_per_app_or_function").put("energyUnit","uWs")
            .put("platformMayCacheOrAddNoise",true).put("pending",pending).put("stale",stale)
            .put("minimumIntervalMs",MIN_INTERVAL_MS).put("timeoutMs",REQUEST_TIMEOUT_MS)
            .put("staleAfterMs",STALE_AFTER_MS).put("checkedAtElapsedMs",now)
            .put("requestTrigger",trigger).put("totalMonitorCount",totalMonitorCount)
            .put("truncated",totalMonitorCount>samples.size).put("monitors",monitors).also { state->
                error?.let{state.put("error",it)}
                lastRequestElapsed?.let{state.put("requestedAtElapsedMs",it)}
                lastRequestWall?.let{state.put("requestedAtUnixMs",it)}
                lastObservedElapsed?.let{state.put("observedAtElapsedMs",it)}
                lastObservedWall?.let{state.put("observedAtUnixMs",it)}
                sampledElapsed?.let{state.put("sampledAtElapsedMs",it)}
                sampledWall?.let{state.put("sampledAtUnixMs",it)}
            }
    }
    override fun close() {
        synchronized(lock){if(closed)return;closed=true;generation++;pending=false;status="closed";error=null}
        executor.shutdownNow()
    }
}

/** All API35 types stay in this class; API24-34 callers never initialize it. */
@TargetApi(35)
private class HandsFreePowerMonitorApi35(private val context:Context):HandsFreePowerMonitorSource {
    override fun request(executor:Executor,callback:(HandsFreePowerMonitorResult)->Unit) {
        val manager=context.getSystemService(SystemHealthManager::class.java)
        if(manager==null){callback(HandsFreePowerMonitorResult.Error);return}
        manager.getSupportedPowerMonitors(executor) { available->
            try {
                val chosen=available.take(HandsFreePowerMonitors.MAX_MONITORS)
                if(chosen.isEmpty())callback(HandsFreePowerMonitorResult.Readings(emptyList(),0))
                else manager.getPowerMonitorReadings(chosen,executor,
                    object:OutcomeReceiver<PowerMonitorReadings,RuntimeException> {
                        override fun onResult(result:PowerMonitorReadings) {
                            try {
                                val readings=chosen.mapNotNull { monitor->
                                    val kind=when(monitor.type) {
                                        PowerMonitor.POWER_MONITOR_TYPE_MEASUREMENT->HandsFreePowerMonitorKind.MEASUREMENT
                                        PowerMonitor.POWER_MONITOR_TYPE_CONSUMER->HandsFreePowerMonitorKind.CONSUMER
                                        else->null
                                    }?:return@mapNotNull null
                                    HandsFreePowerMonitorReading(monitor.name,kind,result.getConsumedEnergy(monitor),result.getTimestampMillis(monitor))
                                }
                                callback(HandsFreePowerMonitorResult.Readings(readings,available.size))
                            }catch(_:Exception){callback(HandsFreePowerMonitorResult.Error)}
                        }
                        override fun onError(error:RuntimeException){callback(HandsFreePowerMonitorResult.Error)}
                    })
            }catch(_:Exception){callback(HandsFreePowerMonitorResult.Error)}
        }
    }
}
