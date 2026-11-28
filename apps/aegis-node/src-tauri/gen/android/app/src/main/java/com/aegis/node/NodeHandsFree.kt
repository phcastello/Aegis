package com.aegis.node

import android.Manifest
import android.app.KeyguardManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.AudioRecordingConfiguration
import android.media.MediaRecorder
import android.os.Build
import android.os.Debug
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.os.SystemClock
import android.service.voice.VoiceInteractionService
import org.json.JSONObject
import java.io.InputStream
import java.util.UUID
import java.util.concurrent.CancellationException
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledThreadPoolExecutor
import java.util.concurrent.TimeUnit

/** Process singleton owns capture independently of Activity/WebView visibility. */
object NodeHandsFree {
    private var runtime:HandsFreeRuntime?=null
    @Synchronized internal fun runtime(context:Context):HandsFreeRuntime = runtime?:HandsFreeRuntime(context.applicationContext).also{runtime=it}
    fun state(context:Context)=runtime(context).state().toString()
    fun configure(context:Context,payload:String):String {
        val args=JSONObject(payload);return runtime(context).configure(args.getBoolean("enabled"),args.optBoolean("diagnostic",false)).toString()
    }
    fun manualAcquire(context:Context,payload:String):String {
        val args=JSONObject(payload);val lease=args.getString("leaseId");require(UUID.fromString(lease).toString()==lease)
        return JSONObject().put("acquired",runtime(context).manualAcquire(lease,args.optString("kind","capture"))).toString()
    }
    fun manualRelease(context:Context,payload:String):String {runtime(context).manualRelease(JSONObject(payload).getString("leaseId"));return state(context)}
    fun cancel(context:Context):String {runtime(context).cancelSession();return state(context)}
    fun configureCollection(context:Context,payload:String):String {
        val args=JSONObject(payload);val runtime=runtime(context)
        runtime.interruptBatteryStudy("collection_changed")
        if (args.has("syncEnabled")) runtime.dataset.configure(args.getBoolean("syncEnabled"))
        else runtime.collection.configure(args.getBoolean("enabled"),args.getInt("limitMiB"))
        return runtime.state().toString()
    }
    fun deleteCollection(context:Context):String {
        val runtime=runtime(context);runtime.interruptBatteryStudy("collection_changed");runtime.collection.deleteAll();return runtime.state().toString()
    }
    fun batteryStart(context:Context,payload:String):String = runtime(context).startBatteryStudy(JSONObject(payload).getString("mode")).toString()
    fun batteryFinish(context:Context):String = runtime(context).finishBatteryStudy().toString()
    fun batteryAutoStart(context:Context,payload:String):String = runtime(context).startBatteryAutomation(JSONObject(payload).optBoolean("validation",false)).toString()
    fun batteryAutoCancel(context:Context):String = runtime(context).finishBatteryStudy().toString()
    internal fun batteryAlarm(context:Context,token:HandsFreeBatteryAutomation.Token,leaseDeadlineMs:Long,onComplete:()->Unit) {
        try{runtime(context).queueBatteryTransition(token,leaseDeadlineMs,onComplete)}catch(_:Throwable){onComplete()}
    }
    internal fun externalReferenceAllowed(context: Context): Boolean = runtime(context).externalReferenceAllowed()
    internal fun permission(context:Context)=context.checkSelfPermission(Manifest.permission.RECORD_AUDIO)==PackageManager.PERMISSION_GRANTED
    internal fun selected(context:Context)=VoiceInteractionService.isActiveService(context,ComponentName(context,NodeVoiceInteractionService::class.java))
}

internal class HandsFreeRuntime(private val context:Context) {
    private class ResearchReply(
        val jobId: String,
        val voiceSessionId: String,
        val conversationId: String,
        val identity: String,
        val expiresAt: Long,
        var ready: Boolean = false
    )

    private val prefs=context.getSharedPreferences("aegis-hands-free-v1",Context.MODE_PRIVATE)
    internal val metrics=HandsFreeMetrics(context)
    internal val collection=HandsFreeActivationCollection(context)
    internal val dataset = NodeWakeDataset.get(context)
    private var heldDatasetCommand: Pair<String, String>? = null
    private val batteryDiagnostics=HandsFreeBatteryDiagnostics(context)
    private val powerMonitors=HandsFreePowerMonitors(context)
    private val batteryAutomation=HandsFreeBatteryAutomation(context)
    private val batteryAlarm=HandsFreeBatteryAlarm(context)
    private val batteryControl=Any()
    private val batteryTransitionPending=java.util.concurrent.atomic.AtomicBoolean(false)
    @Volatile private var batteryChanging=false
    @Volatile private var batteryStopRequested=false
    @Volatile private var batteryAbortPending=false
    @Volatile private var batteryCaptureStartup:HandsFreeCaptureStartup?=null
    @Volatile private var batterySuspended=prefs.getBoolean("batteryStudySuspended",false)
    @Volatile private var batteryDeadlineMs=0L
    @Volatile private var batteryNextObservationMs=0L
    private val batteryObservationPending=java.util.concurrent.atomic.AtomicBoolean(false)
    private val batteryStudyGeneration=java.util.concurrent.atomic.AtomicInteger(0)
    private val main=Handler(Looper.getMainLooper())
    private val cleanup=Executors.newSingleThreadExecutor{r->Thread(r,"AegisHandsFreeCancel").apply{isDaemon=true}}
    private val researchReplies = LinkedHashMap<String, ResearchReply>()
    private val acceptedResearchJobs = LinkedHashSet<String>()
    private val researchWatcher = ScheduledThreadPoolExecutor(1) { task ->
        Thread(task, "AegisHandsFreeResearch").apply { isDaemon = true }
    }.apply {
        setKeepAliveTime(30, TimeUnit.SECONDS)
        allowCoreThreadTimeOut(true)
    }
    private val lock=Any()
    @Volatile private var service:NodeVoiceInteractionService?=null
    @Volatile private var worker:Thread?=null
    @Volatile private var recorder:AudioRecord?=null
    private val epochCounter=java.util.concurrent.atomic.AtomicInteger(0)
    private val epoch:Int get()=epochCounter.get()
    @Volatile private var manualLease:String?=null
    @Volatile private var manualAudioKind:String?=null
    internal fun currentManualPlaybackLease():ManualPlaybackLease?=synchronized(lock) {
        val id=manualLease
        if(id==null || manualAudioKind!="playback")null else ManualPlaybackLease(id,
            {token->synchronized(lock){manualLease==token && manualAudioKind=="playback"}},
            {token->manualRelease(token)})
    }
    private var leaseTimeout:Runnable?=null
    @Volatile private var status="disabled"
    @Volatile private var message="Hands-free desligado."
    @Volatile private var enabled=prefs.getBoolean("enabled",false)
    @Volatile private var diagnostic=prefs.getBoolean("diagnostic",false)
    @Volatile private var modelReady=false
    @Volatile private var installedModel:HandsFreeModelIdentity?=null
    @Volatile private var modelChanging=false
    @Volatile private var captureConfirmed=false
    @Volatile private var lastAudioReadMs=0L
    @Volatile private var pendingActivation=false
    @Volatile private var sessionCancelled=false
    @Volatile private var activeEvent:String?=null
    @Volatile private var transcriptionStatus="not_started"
    @Volatile private var endReason:String?=null
    @Volatile private var initialInteractive=true
    @Volatile private var initialLocked=false
    @Volatile private var pendingActivationAt=0L
    @Volatile private var cancelledAt=0L
    @Volatile private var recordingGroupId:String?=null
    private val diagnosticCancelEpoch=java.util.concurrent.atomic.AtomicInteger(0)
    private val electionEpoch=java.util.concurrent.atomic.AtomicInteger(0)
    @Volatile internal var screenLocked=false
    @Volatile internal var screenInteractive=true
    private var continuationDeadline=0L
    private val ownAudioSessions=java.util.LinkedHashSet<Int>()
    internal fun isOwnAudioSession(id:Int):Boolean=NodeExternalPlaybackReference.ownsAudioSession(id) || synchronized(lock){ownAudioSessions.contains(id)}
    private var failures=0
    @Volatile private var retryNotBefore=0L
    @Volatile private var retriesExhausted=false
    private var conversationId:String?=null
    private val conversationContinuity=HandsFreeConversationContinuity{SystemClock.elapsedRealtime()}
    private var continuityActivation:HandsFreeConversationContinuity.Activation?=null
    @Volatile private var sessionId:String?=null
    internal fun activeSessionToken():String?=sessionId
    internal fun activationNeedsWake(token:String)=sessionId==token && !sessionCancelled && !initialInteractive
    private var sessionPower:PowerManager.WakeLock?=null
    private var sessionPowerToken:String?=null
    private var sessionPowerAt=0L
    private var sessionPowerIdentity:String?=null
    private fun holdSessionPower(token:String,renew:Boolean=false) {
        if(sessionPowerToken==token && !renew)return
        releaseSessionPower(sessionPowerToken)
        try {
            val wake=context.getSystemService(PowerManager::class.java).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK,"Aegis:hands-free-session")
            wake.acquire(600000);sessionPower=wake;sessionPowerToken=token;sessionPowerAt=SystemClock.elapsedRealtime();sessionPowerIdentity=metrics.identityToken()
        }catch(_:Exception){metrics.add("session_power_failures")}
    }
    private fun releaseSessionPower(token:String?)=synchronized(lock) {
        if(token==null || sessionPowerToken!=token)return@synchronized
        val wake=sessionPower;sessionPower=null;sessionPowerToken=null
        try{if(wake?.isHeld==true)wake.release()}catch(_:RuntimeException){metrics.add("session_power_failures")}
        finally{metrics.addForIdentity("sessionWakeLockMs",minOf(600000,SystemClock.elapsedRealtime()-sessionPowerAt),sessionPowerIdentity)}
    }
    @Volatile private var backend:NodeHandsFreeBackend?=null
    @Volatile private var responseCapture: HandsFreeBargeInCapture? = null
    private var capturedTurnLatency:HandsFreeTurnLatency?=null
    private var capturedSpeechFloor: Double? = null
    private fun newBackend(generation:Int,session:String):NodeHandsFreeBackend {
        val identity=metrics.identityToken()
        val sessionIdentity=continuityActivation?.identity
        lateinit var owner: NodeHandsFreeBackend
        owner = NodeHandsFreeBackend(readSession={
            // Pin the authority as well as the conversation at activation. A vault change
            // between wake acceptance and beginCapture cannot send an old chat to a new Node.
            NodeNativeSession.read(context)?.takeIf{sessionIdentity!=null && NodeNativeSession.fingerprint(it)==sessionIdentity}
        },onStage={stage,duration->
            if(stage in setOf("stt","chat","tts_fetch","tts_first_audio","tts_total","tts_playback"))metrics.timing(stage,duration,identity)
            if(stage in setOf("transcribed","empty","ignored","failed"))metrics.addForIdentity("commands_${stage}",1,identity)
            if(stage in setOf("transcription_started","transcription_final","empty","tts_first_audio")) synchronized(lock) {
                if(ownsTurn(generation,session,owner)) when(stage) {
                    "transcription_started"->transcriptionStatus="in_progress"
                    "transcription_final"->transcriptionStatus="final"
                    "empty"->transcriptionStatus="empty"
                    "tts_first_audio"->setState("speaking","Aegis respondendo…")
                }
            }
        }, onResearchDelegated = ::watchResearchReply)
        return owner
    }
    private fun watchResearchReply(scope:NodeNativeSession.Session, jobId:String, voiceSessionId:String, destination:String) {
        val reply = ResearchReply(jobId, voiceSessionId, destination, NodeNativeSession.fingerprint(scope),
            SystemClock.elapsedRealtime() + 600_000)
        synchronized(lock) {
            if (!enabled || diagnostic || researchReplies.size >= 8 || !acceptedResearchJobs.add(jobId)) return
            while (acceptedResearchJobs.size > 64) acceptedResearchJobs.remove(acceptedResearchJobs.first())
            researchReplies[jobId] = reply
            researchWatcher.execute { refreshResearchReply(reply) }
        }
    }
    private fun refreshResearchReply(reply:ResearchReply) {
        fun current() = synchronized(lock) {
            enabled && !diagnostic && researchReplies[reply.jobId] === reply &&
                SystemClock.elapsedRealtime() < reply.expiresAt
        }
        if (!current() || currentIdentity() != reply.identity) {
            synchronized(lock) { researchReplies.remove(reply.jobId, reply) }
            return
        }
        val source = NodeHandsFreeBackend(readSession = {
            NodeNativeSession.read(context)?.takeIf { NodeNativeSession.fingerprint(it) == reply.identity && current() }
        })
        val state = try { source.researchJobState(reply.jobId) }
        catch (error:NodeNativeSession.HttpFailure) {
            if (error.status in setOf(400, 401, 403, 404)) "unavailable" else "queued"
        } catch (_:Exception) { "queued" }
        synchronized(lock) {
            if (!current()) researchReplies.remove(reply.jobId, reply)
            else when (state) {
                "completed", "failed" -> reply.ready = true
                "queued", "running" -> researchWatcher.schedule({ refreshResearchReply(reply) }, 3, TimeUnit.SECONDS)
                else -> researchReplies.remove(reply.jobId, reply)
            }
        }
    }
    private fun beginResearchReply(generation:Int):ResearchReply? = synchronized(lock) {
        if (researchReplies.isEmpty() || !current(generation) || !enabled || diagnostic ||
            pendingActivation || sessionId != null || batteryBlocksNormal())
            return null
        val now = SystemClock.elapsedRealtime()
        // The polling deadline bounds unreachable jobs. A completed result can
        // still be waiting for other speech to finish on this device.
        researchReplies.entries.removeAll { !it.value.ready && it.value.expiresAt <= now }
        val reply = researchReplies.values.firstOrNull { it.ready } ?: return null
        researchReplies.remove(reply.jobId)
        if (currentIdentity() != reply.identity) return null
        val token = UUID.randomUUID().toString()
        continuityActivation = conversationContinuity.activate(reply.identity, now)
        conversationId = reply.conversationId
        sessionId = token
        sessionCancelled = false
        endReason = null
        holdSessionPower(token)
        reply
    }
    private fun cancelBackend(){val active=synchronized(lock){val value=backend;backend=null;value};if(active!=null){active.requestCancellation();cleanup.execute{active.cancel()}}}
    private fun ownsSession(generation:Int,session:String) =
        current(generation) && !sessionCancelled && sessionId==session
    private fun ownsTurn(generation:Int,session:String,owner:NodeHandsFreeBackend) =
        ownsSession(generation,session) && backend===owner
    internal fun closeResponseCapture() = synchronized(lock) { responseCapture?.close(); Unit }

    internal fun bargeInPanelAvailability(token: String): HandsFreeBargeInPanelAvailability = synchronized(lock) {
        if (sessionId != token || sessionCancelled || status !in setOf("speaking","processing")) {
            HandsFreeBargeInPanelAvailability.UNAVAILABLE
        } else {
            responseCapture?.panelAvailability() ?: HandsFreeBargeInPanelAvailability.UNAVAILABLE
        }
    }

    private val sessionMediaFocus=HandsFreeSessionMediaFocus(context,
        onLoss={token->cancelSession(token,"audio_focus_lost")},
        observe={outcome->metrics.event("media_focus",JSONObject().put("outcome",outcome))})
    private val mediaFocusRelease=HandsFreeMediaFocusRelease(sessionMediaFocus::release,sessionMediaFocus::releaseAll) { expire ->
        main.postDelayed({expire()},2000)
    }
    private val playback=HandsFreePlayback(context)
    private val activationCue=HandsFreeActivationCue{metrics.add("beep_failures")}
    // Retain the registration for this process singleton's lifetime; no identity is persisted.
    private val identityObserver=NodeNativeSession.observeIdentity(context){identityChanged()}
    private fun currentIdentity()=NodeNativeSession.read(context)?.let{NodeNativeSession.fingerprint(it)}
    private fun identityChanged()=synchronized(lock) {
        electionEpoch.incrementAndGet()
        val identity=currentIdentity()
        researchReplies.entries.removeAll { it.value.identity != identity }
        val active=continuityActivation
        conversationContinuity.identityChanged(identity)
        if(active!=null && active.identity!=identity) {
            continuityActivation=null;pendingActivation=false;sessionCancelled=true
            closeResponseCapture();playback.cancel();activationCue.cancel();cancelBackend();mediaFocusRelease.releaseWhenSafe(sessionId);closePanel(sessionId,true)
            metrics.event("session_identity_changed")
        }
    }
    private fun endConversationSession(token:String?,close:Boolean=true,releaseMediaFocus:Boolean=true)=synchronized(lock) {
        if(sessionId!=token)return@synchronized
        conversationContinuity.end(continuityActivation);continuityActivation=null
        conversationId=null;sessionId=null;backend=null
        if(releaseMediaFocus)sessionMediaFocus.release(token)
        if(close)closePanel(token)
    }
    init {
        modelReady=try{HandsFreeModel.readPackage(context)?.use { model->
            installedModel=model.identity;true
        }?:false}catch(_:Throwable){false}
        metrics.restoreRequested(enabled,diagnostic)
    }
    internal fun attach(value:NodeVoiceInteractionService) {service=value;metrics.event("assistant_ready");recoverFromSystem();observeBattery(force=true)}
    internal fun detach(value:NodeVoiceInteractionService) {
        if(service!==value)return
        interruptBatteryStudy("assistant_unavailable")
        service=null;NodeExternalPlaybackReference.stop(context);stop("assistant_unavailable","Selecione a Aegis como assistente e retome o hands-free.");metrics.event("assistant_shutdown")
    }
    private fun recordIdentity()=synchronized(lock) {
        val role=NodeHandsFree.selected(context);val permission=NodeHandsFree.permission(context)
        val info=context.packageManager.getPackageInfo(context.packageName,0)
        val model=installedModel
        metrics.identity(info.versionName?:"unknown",if(Build.VERSION.SDK_INT>=28)info.longVersionCode.toString() else @Suppress("DEPRECATION") info.versionCode.toString(),modelReady,role,permission,
            modelVersion=model?.version,modelSha256=model?.sha256)
    }
    fun state():JSONObject {
        // Never hold the audio/control lock while a platform snapshot is in flight.
        val batteryView=batteryState()
        val mediaReference=NodeExternalPlaybackReference.state()
        val wakeDataset=dataset.snapshot()
        return synchronized(lock) {
        val role=NodeHandsFree.selected(context);val permission=NodeHandsFree.permission(context)
        val info=context.packageManager.getPackageInfo(context.packageName,0)
        val model=installedModel
        metrics.identity(info.versionName?:"unknown",if(Build.VERSION.SDK_INT>=28)info.longVersionCode.toString() else @Suppress("DEPRECATION") info.versionCode.toString(),modelReady,role,permission,
            modelVersion=model?.version,modelSha256=model?.sha256)
        JSONObject().put("enabled",enabled).put("diagnostic",diagnostic).put("assistantSelected",role).put("permissionGranted",permission)
            .put("modelReady",modelReady).put("status",status).put("message",message).put("captureConfirmed",captureConfirmed && SystemClock.elapsedRealtime()-lastAudioReadMs<=1500 && status in setOf("listening","capturing","continuation"))
            .put("modelName",model?.name?:JSONObject.NULL).put("modelVersion",model?.version?:JSONObject.NULL)
            .put("modelSha256",model?.sha256?:JSONObject.NULL)
            .put("collection",collection.state())
            .put("wakeSync",wakeDataset)
            .put("batteryDiagnostics",batteryView)
            .put("mediaReference",mediaReference)
        }
    }
    fun configure(value:Boolean,diagnosticValue:Boolean):JSONObject {
        if (!value || diagnosticValue) NodeExternalPlaybackReference.stop(context)
        interruptBatteryStudy("configuration_changed")
        val changed=enabled!=value || diagnostic!=diagnosticValue
        if(!changed){if(value)recoverFromSystem();return state()}
        enabled=value;diagnostic=diagnosticValue;check(prefs.edit().putBoolean("enabled",value).putBoolean("diagnostic",diagnosticValue).commit())
        metrics.configure(value,diagnosticValue);failures=0;retryNotBefore=0;retriesExhausted=false
        if(!value)stop("disabled","Hands-free desligado.") else {stop("starting","Preparando escuta local.");reconcile()}
        return state()
    }
    internal fun importModel(input:InputStream) {
        check(!batteryBlocksNormal()){"battery_study_active"}
        var paused=false
        try {
            val identity=HandsFreeModel.import(context,input) {
                // No activation can be accepted after this boundary. Wait for the old
                // detector and its final metrics before attributing anything to new bytes.
                val previous=synchronized(lock) {
                    check(!modelChanging && manualLease==null && !pendingActivation && sessionId==null &&
                        status !in setOf("capturing","processing","speaking","continuation")){"model_import_busy"}
                    modelChanging=true;paused=true
                    NodeExternalPlaybackReference.stop(context)
                    stop("starting","Preparando troca do modelo local.");worker
                }
                if(previous!=null && previous!==Thread.currentThread())previous.join(1500)
                check(previous?.isAlive!=true){"model_import_busy"}
            }
            synchronized(lock) {
                installedModel=identity;modelReady=true
                recordIdentity();metrics.event("model_imported",HandsFreeModel.metadata(identity))
                failures=0;retryNotBefore=0;retriesExhausted=false
            }
        }finally {
            if(paused){synchronized(lock){modelChanging=false};reconcile()}
        }
    }
    internal fun importFailed(){metrics.event("model_import_failed");if(!modelReady)setState("model_invalid","Modelo ausente, corrompido ou incompatível. Importe o arquivo privado selecionado na PoC.")}
    internal fun recoverFromSystem(){failures=0;retryNotBefore=0;retriesExhausted=false;reconcile()}
    internal fun reconcile() {
        if(modelChanging || batteryChanging)return
        recordIdentity()
        if(!wantsCapture()) {
            if(batteryBlocksNormal())stop("battery_paused",when {
                batteryAutomation.isWaiting()->"Comparação pronta. Apague a tela para iniciar."
                batteryDiagnostics.activeMode()=="off"->"Medição: escuta desligada."
                else->"Medição interrompida. Encerre o teste nas configurações para retomar."
            })
            else stop("disabled","Hands-free desligado.")
            return
        }
        if(!NodeHandsFree.selected(context)){NodeExternalPlaybackReference.stop(context);stop("assistant_unavailable","Selecione a Aegis como assistente padrão.");return}
        if(!NodeHandsFree.permission(context)){NodeExternalPlaybackReference.stop(context);stop("permission_missing","Permita o microfone para habilitar a escuta.");return}
        if(!modelReady && batteryDiagnostics.activeMode()!="capture_only"){stop("model_missing","Importe o modelo privado Aegis selecionado na PoC.");return}
        if(manualLease!=null){setState("manual_audio","Áudio em uso pelo chat.");return}
        val host=service?:run{setState("assistant_unavailable","Aguardando o serviço de assistente do Android. Se necessário, selecione novamente a Aegis.");return}
        synchronized(lock) {
            if(modelChanging || batteryChanging || worker?.isAlive==true || retriesExhausted || SystemClock.elapsedRealtime()<retryNotBefore)return
            if(!host.ensureForeground()){setState("service_unavailable","O Android não permitiu iniciar a escuta. Abra a Aegis e retome o hands-free.");return}
            val generation=epochCounter.incrementAndGet()
            worker=Thread({run(generation)},"AegisHandsFreeAudio").apply{isDaemon=true;start()}
        }
    }
    internal fun externalReferenceAllowed(): Boolean = enabled && !diagnostic && modelReady &&
        !modelChanging && !batteryChanging && !batteryBlocksNormal() && manualLease == null && service != null &&
        NodeHandsFree.permission(context) && NodeHandsFree.selected(context) &&
        !context.getSystemService(AudioManager::class.java).isMicrophoneMute

    private fun batteryBlocksNormal()=batterySuspended || batteryDiagnostics.blocksNormal() || batteryAutomation.blocksNormal()
    private fun batteryState():JSONObject = batteryDiagnostics.state().also {
        if((batterySuspended || batteryAutomation.blocksNormal()) && it.isNull("active"))it.put("requiresResume",true)
        it.put("powerMonitors",powerMonitors.state()).put("automation",batteryAutomation.state().takeUnless{a->a.optString("status")=="idle"}?:JSONObject.NULL)
    }
    private fun wantsCapture():Boolean {
        if(batteryStopRequested)return false
        if(batteryCaptureStartup!=null)return true
        return when(batteryDiagnostics.activeMode()) {
            "capture_only","detection"->true
            "off"->false
            else->enabled && !batteryBlocksNormal()
        }
    }
    private fun current(generation:Int):Boolean {
        if(epoch!=generation || !wantsCapture() || modelChanging || service==null || manualLease!=null)return false
        val startup=batteryCaptureStartup
        return if(startup?.audioEpoch==generation)startup.failure()==null else !batteryChanging
    }
    private fun setState(value:String,text:String) {
        status=value;message=text;metrics.state(value)
        val generation=epoch
        main.post{if(epoch==generation && status==value && message==text){NodeVoiceInteractionSession.current?.refresh(value,text);service?.updateNotification(text)}}
    }
    private fun stop(value:String,text:String) {
        val stoppedEpoch=synchronized(lock) {
            val captureActive=status=="capturing"
            batteryCaptureStartup?.cancel("capture_stopped")
            batteryCaptureStartup=null
            epochCounter.incrementAndGet();captureConfirmed=false;pendingActivation=false;cancelledAt=HandsFreeTurnLatency.now();sessionCancelled=true;playback.cancel();activationCue.cancel()
            closeResponseCapture()
            if (!enabled || diagnostic || value == "assistant_unavailable" || value == "permission_missing")
                researchReplies.clear()
            endReason=value;if(!captureActive)finishEvent("interrupted",value)
            releaseSessionPower(sessionPowerToken)
            try{recorder?.stop()}catch(_:Exception){}
            cancelBackend()
            mediaFocusRelease.releaseWhenSafe(sessionId)
            conversationContinuity.invalidate();continuityActivation=null
            conversationId=null;sessionId=null;setState(value,text)
            epoch
        }
        main.post{if(epoch==stoppedEpoch){service?.stopHandsFreeForeground();NodeVoiceInteractionSession.current?.finishQuietly()}}
    }
    fun manualAcquire(lease:String,kind:String):Boolean {
        interruptBatteryStudy("manual_audio")
        synchronized(lock) {
            if(modelChanging || manualLease!=null || pendingActivation || sessionId!=null || status in setOf("capturing","processing","speaking","continuation"))return false
            manualLease=lease;manualAudioKind=kind;metrics.event("manual_audio_acquired",JSONObject().put("kind",if(kind=="playback")"playback" else "capture"))
            epochCounter.incrementAndGet();try{recorder?.stop()}catch(_:Exception){}
        }
        NodeExternalPlaybackReference.stop(context)
        val active=worker
        if(active!=null && active!==Thread.currentThread())active.join(1500)
        if(active?.isAlive==true){manualLease=null;manualAudioKind=null;metrics.event("manual_audio_acquire_timeout");return false}
        synchronized(lock) {
            if(manualLease!=lease)return false
            leaseTimeout?.let{main.removeCallbacks(it)}
            val timeout=Runnable{manualRelease(lease)};leaseTimeout=timeout
            main.postDelayed(timeout,if(kind=="playback")600000 else 300000)
        }
        setState("manual_audio","Áudio em uso pelo chat.")
        return true
    }
    fun manualRelease(lease:String) {
        synchronized(lock){if(manualLease!=lease)return;manualLease=null;manualAudioKind=null;leaseTimeout?.let{main.removeCallbacks(it)};leaseTimeout=null;metrics.event("manual_audio_released")}
        reconcile()
    }
    internal fun releaseActivityAudio(){manualLease?.let{manualRelease(it)}}
    fun cancelSession(expectedSession:String?=null,reason:String="explicit_cancel") {
        val panelCancellation=if(expectedSession==null)NodeVoiceInteractionSession.current?.cancellationSnapshot() else null
        synchronized(lock) {
            if(expectedSession!=null && expectedSession!=sessionId)return
            electionEpoch.incrementAndGet()
            if(sessionId==null && !pendingActivation){
                if(diagnostic){cancelledAt=HandsFreeTurnLatency.now();diagnosticCancelEpoch.incrementAndGet()}
                // A terminal error may outlive its turn. Dismiss only the presentation
                // captured by this request, while no new activation has taken ownership.
                if(panelCancellation!=null)main.post{synchronized(lock){if(sessionId==null && !pendingActivation)panelCancellation.run()}}
                return
            }
            val captureActive=status=="capturing"
            pendingActivation=false;cancelledAt=HandsFreeTurnLatency.now();sessionCancelled=true;endReason=reason;playback.cancel();activationCue.cancel();cancelBackend();metrics.add("sessions_cancelled")
            closeResponseCapture()
            // Capture owns its last in-flight read and FIFO finalization. A pending manual
            // activation has no capture yet and must persist its empty WAVs immediately.
            if(!captureActive)finishEvent(if(reason=="explicit_cancel")"cancelled" else "interrupted",reason)
            conversationContinuity.end(continuityActivation);continuityActivation=null
            val token=sessionId;sessionId=null;conversationId=null;mediaFocusRelease.releaseWhenSafe(token);closePanel(token,true)
        }
    }
    internal fun activateFromAssistant():String? = synchronized(lock) {
        if(batteryBlocksNormal()){interruptBatteryStudy("manual_activation");return null}
        if(!enabled || modelChanging || manualLease!=null || service==null)return null
        if(pendingActivation)return sessionId
        if(status=="listening") {
            val activatedAt=SystemClock.elapsedRealtime()
            val token=UUID.randomUUID().toString();sessionId=token;sessionCancelled=false;endReason=null
            if(!diagnostic) {
                continuityActivation=conversationContinuity.activate(currentIdentity(),activatedAt)
                conversationId=continuityActivation?.conversationId
            }
            pendingActivationAt=HandsFreeTurnLatency.now();snapshotActivationScreen()
            transcriptionStatus="not_started"
            activeEvent=collection.begin(activationMetadata("manual",null,null,null,token),ShortArray(0))
            service?.acceptedHandsFreeActivation(token)
            holdSessionPower(token)
            pendingActivation=true;return token
        }
        if(!enabled)setState("disabled","Habilite o hands-free nas configurações da Aegis.")
        return null
    }
    /** Explicit study controls never overwrite the owner's normal listening preferences. */
    internal fun startBatteryStudy(mode:String):JSONObject = synchronized(batteryControl) {
        if(batteryAutomation.isLive())return@synchronized state()
        require(mode in setOf("off","capture_only","detection")){"battery_mode"}
        var alreadyActive=false
        val previous=synchronized(lock) {
            if(batteryDiagnostics.activeMode()!=null){alreadyActive=true;return@synchronized null}
            check(!batteryBlocksNormal()){"battery_resume_required"}
            check(!batteryChanging && !modelChanging && manualLease==null && !pendingActivation && sessionId==null &&
                status !in setOf("capturing","processing","speaking","continuation")){"battery_audio_busy"}
            check(NodeHandsFree.selected(context) && service!=null){"battery_assistant_required"}
            if(mode!="off")check(NodeHandsFree.permission(context)){"battery_permission_required"}
            if(mode=="detection")check(modelReady){"battery_model_required"}
            // This second durable gate precedes the study's own storage boundary.
            // A failure in that storage cannot restore an old enabled=true preference.
            check(prefs.edit().putBoolean("batteryStudySuspended",true).commit()){"battery_storage"}
            batterySuspended=true;batteryChanging=true;batteryStopRequested=false;batteryAbortPending=false
            NodeExternalPlaybackReference.stop(context)
            batteryStudyGeneration.incrementAndGet()
            try {
                val info=context.packageManager.getPackageInfo(context.packageName,0)
                batteryDiagnostics.start(mode,HandsFreeModel.metadata(installedModel)
                    .put("appVersion",info.versionName?:"unknown").put("build",if(Build.VERSION.SDK_INT>=28)info.longVersionCode else @Suppress("DEPRECATION") info.versionCode.toLong())
                    .put("requestedEnabled",enabled).put("requestedDiagnostic",diagnostic)
                    .put("audioSource","MIC").put("sampleRate",16000).put("readSamples",3200)
                    .put("commandsSuppressed",true).put("audioCollectionSuppressed",true))
                batteryDeadlineMs=SystemClock.elapsedRealtime()+60*60000L
                batteryNextObservationMs=SystemClock.elapsedRealtime()+60000L
                stop("battery_preparing","Preparando comparação de bateria.")
                worker
            } catch(error:Throwable){
                stop("battery_interrupted","Não foi possível iniciar a medição. Encerre o teste nas configurações para retomar.")
                batteryChanging=false;throw error
            }
        }
        if(alreadyActive)return@synchronized state()
        try {
            if(previous!=null && previous!==Thread.currentThread())previous.join(1500)
            if(previous?.isAlive==true)batteryDiagnostics.interrupt("capture_shutdown_timeout")
            else if(NodeExternalPlaybackReference.recording())batteryDiagnostics.interrupt("media_reference_shutdown_timeout")
            metrics.event("battery_study_started",JSONObject().put("mode",mode));powerMonitors.request()
        }finally{batteryChanging=false}
        reconcile();state()
    }
    internal fun finishBatteryStudy():JSONObject {
        // Wake a coordinator waiting for the first PCM before taking its control lock.
        batteryCaptureStartup?.cancel("automation_cancelled")
        return synchronized(batteryControl) {
            if(!batteryBlocksNormal() || batteryChanging)return@synchronized state()
            batteryChanging=true;batteryStopRequested=true;batteryAbortPending=true;batteryStudyGeneration.incrementAndGet()
            val alarmToken=batteryAutomation.token()
            if(alarmToken!=null)cancelBatteryAlarm(alarmToken)
            try {
                stop("battery_finishing","Encerrando comparação de bateria.")
                val previous=worker
                if(previous!=null && previous!==Thread.currentThread())previous.join(1500)
                if(previous?.isAlive==true) {
                    batteryAutomation.interrupt("capture_shutdown_timeout")
                    batteryDiagnostics.interrupt("capture_shutdown_timeout")
                } else {
                    if(batteryAutomation.isLive())batteryDiagnostics.interrupt("automation_cancelled")
                    batteryDiagnostics.finish()
                    check(prefs.edit().putBoolean("batteryStudySuspended",false).commit()){"battery_storage"}
                    // The sequence gate is the last durable gate removed. A failure here
                    // leaves it recoverable/interrupted even if the preference write succeeded.
                    batteryAutomation.cancel()
                    batterySuspended=false;batteryStopRequested=false;batteryAbortPending=false;batteryDeadlineMs=0L;powerMonitors.request()
                }
                metrics.event("battery_study_finished")
            }finally{batteryChanging=false}
            reconcile();state()
        }
    }
    /** Fail closed immediately; finalize slow platform observations off the audio/main thread. */
    internal fun interruptBatteryStudy(reason:String) {
        if(batteryDiagnostics.activeMode()==null && !batteryAutomation.isLive())return
        val generation=synchronized(lock) {
            batteryStopRequested=true;batteryAbortPending=true;batteryDeadlineMs=0L
            val value=batteryStudyGeneration.incrementAndGet()
            batteryCaptureStartup?.fail(reason)
            stop("battery_interrupted","Medição interrompida. Encerre o teste nas configurações para retomar.")
            value
        }
        cleanup.execute{synchronized(batteryControl){
            if(batteryStudyGeneration.get()!=generation)return@synchronized
            batteryAutomation.token()?.let{cancelBatteryAlarm(it)}
            if(batteryAutomation.isLive())batteryAutomation.interrupt(reason)
            batteryDiagnostics.interrupt(reason)
        }}
    }
    private fun cancelBatteryAlarm(token:HandsFreeBatteryAutomation.Token) {
        // The persisted sequence token remains authoritative even if Android refuses cancellation.
        try{batteryAlarm.cancel(token)}catch(_:RuntimeException){metrics.add("battery_alarm_cancel_failures")}
    }
    private fun batteryIdentity():JSONObject {
        val info=context.packageManager.getPackageInfo(context.packageName,0)
        return HandsFreeModel.metadata(installedModel)
            .put("appVersion",info.versionName?:"unknown").put("build",if(Build.VERSION.SDK_INT>=28)info.longVersionCode else @Suppress("DEPRECATION") info.versionCode.toLong())
            .put("requestedEnabled",enabled).put("requestedDiagnostic",diagnostic)
            .put("audioSource","MIC").put("sampleRate",16000).put("readSamples",3200)
            .put("commandsSuppressed",true).put("audioCollectionSuppressed",true)
    }
    private fun batteryAutomationPrerequisites() {
        check(NodeHandsFree.selected(context) && service!=null && service===NodeVoiceInteractionService.current){"battery_assistant_required"}
        check(NodeHandsFree.permission(context)){"battery_permission_required"}
        check(modelReady){"battery_model_required"}
        check(!modelChanging && manualLease==null && !pendingActivation && sessionId==null &&
            status !in setOf("capturing","processing","speaking","continuation")){"battery_audio_busy"}
        check(!microphoneBlocked()){"battery_microphone_unavailable"}
        val battery=context.registerReceiver(null,android.content.IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        check(battery!=null && battery.getIntExtra(android.os.BatteryManager.EXTRA_PLUGGED,-1)==0 &&
            battery.getIntExtra(android.os.BatteryManager.EXTRA_STATUS,-1) !in setOf(android.os.BatteryManager.BATTERY_STATUS_CHARGING,android.os.BatteryManager.BATTERY_STATUS_FULL)){"battery_unplug_required"}
    }
    internal fun startBatteryAutomation(validation:Boolean):JSONObject = synchronized(batteryControl) {
        if(batteryAutomation.isLive())return@synchronized state()
        check(!batteryBlocksNormal() && !batteryChanging){"battery_resume_required"}
        batteryAutomationPrerequisites()
        check(prefs.edit().putBoolean("batteryStudySuspended",true).commit()){"battery_storage"}
        batterySuspended=true;batteryChanging=true;batteryStopRequested=true;batteryAbortPending=false
        NodeExternalPlaybackReference.stop(context)
        batteryStudyGeneration.incrementAndGet()
        try {
            batteryAutomation.arm(validation)
            stop("battery_waiting_screen_off","Comparação pronta. Apague a tela para iniciar.")
            val previous=worker
            if(previous!=null && previous!==Thread.currentThread())previous.join(1500)
            check(previous?.isAlive!=true){"capture_shutdown_timeout"}
            check(!NodeExternalPlaybackReference.recording()){"media_reference_shutdown_timeout"}
        }catch(error:Throwable){
            stop("battery_interrupted","Não foi possível preparar a comparação. Encerre o teste para retomar.")
            batteryAutomation.interrupt("preparation_failed")
            throw error
        }finally{batteryChanging=false}
        if(!context.getSystemService(PowerManager::class.java).isInteractive)requestBatteryTransition(null)
        state()
    }
    /** The receiver owns a bounded wake lock. No periodic wakeup is added during a phase. */
    internal fun queueBatteryTransition(token:HandsFreeBatteryAutomation.Token?,leaseDeadlineMs:Long,onComplete:()->Unit) {
        val generation=batteryStudyGeneration.get()
        cleanup.execute{try{synchronized(batteryControl){
            if(!batteryAbortPending && batteryStudyGeneration.get()==generation)advanceBatteryAutomation(token,leaseDeadlineMs)
        }}catch(_:Throwable){interruptBatteryStudy("transition_failed")}finally{onComplete()}}
    }
    private fun requestBatteryTransition(token:HandsFreeBatteryAutomation.Token?) {
        if(!batteryTransitionPending.compareAndSet(false,true))return
        var wake:PowerManager.WakeLock?=null
        try {
            wake=context.getSystemService(PowerManager::class.java).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK,"Aegis:battery-transition")
            wake.setReferenceCounted(false)
            val leaseDeadlineMs=SystemClock.elapsedRealtime()+NodeHandsFreeBatteryAlarmReceiver.TRANSITION_TIMEOUT_MS
            wake.acquire(NodeHandsFreeBatteryAlarmReceiver.TRANSITION_TIMEOUT_MS)
            val held=wake
            queueBatteryTransition(token,leaseDeadlineMs){
                try{if(held.isHeld)held.release()}catch(_:RuntimeException){}
                batteryTransitionPending.set(false)
            }
        }catch(_:Throwable){
            try{if(wake?.isHeld==true)wake.release()}catch(_:RuntimeException){}
            batteryTransitionPending.set(false);interruptBatteryStudy("transition_unavailable")
        }
    }
    private fun advanceBatteryAutomation(token:HandsFreeBatteryAutomation.Token?,leaseDeadlineMs:Long) {
        val generation=batteryStudyGeneration.get()
        val step=if(token==null) {
            if(context.getSystemService(PowerManager::class.java).isInteractive)return
            batteryAutomation.claimScreenOff()
        } else batteryAutomation.claimDue(token)
        if(step==null)return
        // Finalize persistence before the receiver's bounded lease expires.
        val startupDeadlineMs=leaseDeadlineMs-1000L
        var startup:HandsFreeCaptureStartup?=null
        batteryChanging=true;batteryStopRequested=true;batteryDeadlineMs=0L
        try {
            check(SystemClock.elapsedRealtime()<startupDeadlineMs){"startup_timeout"}
            if(token!=null)cancelBatteryAlarm(token)
            stop("battery_transition","Mudando a etapa da comparação de bateria.")
            val previous=worker
            if(previous!=null && previous!==Thread.currentThread())previous.join(1500)
            check(previous?.isAlive!=true){"capture_shutdown_timeout"}
            check(batteryStudyGeneration.get()==generation){"conditions_changed"}
            if(batteryDiagnostics.activeMode()!=null)batteryDiagnostics.finishPhase()
            if(step.mode==null) {
                batteryDiagnostics.finish()
                synchronized(lock) {
                    check(!batteryAbortPending && batteryStudyGeneration.get()==generation){"conditions_changed"}
                    check(prefs.edit().putBoolean("batteryStudySuspended",false).commit()){"battery_storage"}
                    batteryAutomation.complete(step)
                    batterySuspended=false;batteryStopRequested=false
                }
            } else {
                batteryAutomationPrerequisites()
                check(batteryStudyGeneration.get()==generation){"conditions_changed"}
                val mode=checkNotNull(step.mode)
                if(mode!="off") {
                    val pending=synchronized(lock) {
                        check(!batteryAbortPending && batteryStudyGeneration.get()==generation){"conditions_changed"}
                        check(SystemClock.elapsedRealtime()<startupDeadlineMs){"startup_timeout"}
                        check(service?.ensureForeground()==true){"foreground_start_denied"}
                        val audioEpoch=epochCounter.incrementAndGet()
                        HandsFreeCaptureStartup(mode,generation,audioEpoch,startupDeadlineMs).also { handoff->
                            batteryCaptureStartup=handoff
                            batteryStopRequested=false
                            setState("battery_preparing","Preparando microfone para a medição.")
                            worker=Thread({run(audioEpoch,handoff)},"AegisHandsFreeAudio").apply{isDaemon=true;start()}
                        }
                    }
                    startup=pending
                    check(pending.awaitReady()){pending.failure()?:"startup_timeout"}
                    val readyMs=checkNotNull(pending.readyWallMs())
                    batteryAutomation.recordCaptureReady(step,readyMs,pending.readyThreadCpuNanos())
                }
                check(!batteryAbortPending && batteryStudyGeneration.get()==generation){"conditions_changed"}
                check(SystemClock.elapsedRealtime()<startupDeadlineMs){"startup_timeout"}
                val basis=if(startup==null)"capture_disabled" else "first_positive_pcm"
                val preparationMs=startup?.readyWallMs()?.minus(step.claimedAtElapsedMs)
                batteryDiagnostics.start(mode,batteryIdentity(),step.sequenceId,step.validation,preparationMs,basis)
                val runId=checkNotNull(batteryDiagnostics.activeRunId())
                val measuredAt=checkNotNull(batteryDiagnostics.activeStartElapsedMs())
                val next=batteryAutomation.running(step,runId,measuredAt,"pending",preparationMs,basis)
                val precision=batteryAlarm.schedule(next)
                batteryAutomation.setAlarmPrecision(next,precision)
                synchronized(lock) {
                    check(!batteryAbortPending && batteryStudyGeneration.get()==generation){"conditions_changed"}
                    check(SystemClock.elapsedRealtime()<startupDeadlineMs){"startup_timeout"}
                    startup?.let {
                        check(it.markMeasuring(runId)){it.failure()?:"startup_timeout"}
                        setState("listening",if(mode=="capture_only")"Medição: somente microfone. Aegis não responderá."
                            else "Medição: detecção local sem comandos ou gravações.")
                    }
                    batteryStopRequested=false
                }
                batteryNextObservationMs=measuredAt+60000L
                powerMonitors.request()
            }
        }catch(error:Throwable){
            val issue=startup?.failure() ?: when(error.message) {
                "startup_timeout"->"startup_timeout"
                "foreground_start_denied"->"foreground_start_denied"
                else->"transition_failed"
            }
            batteryStopRequested=true;batteryAbortPending=true
            stop("battery_interrupted","Comparação interrompida. Encerre o teste nas configurações para retomar.")
            batteryAutomation.token()?.let{cancelBatteryAlarm(it)}
            try{batteryAutomation.interrupt(issue)}catch(_:Exception){metrics.add("battery_storage_failures")}
            try{batteryDiagnostics.interrupt(issue)}catch(_:Exception){metrics.add("battery_storage_failures")}
        }finally{batteryChanging=false}
        reconcile()
    }
    internal fun observeBattery(force:Boolean=false) {
        if(batteryAutomation.isWaiting()) {
            if(!context.getSystemService(PowerManager::class.java).isInteractive)requestBatteryTransition(null)
            return
        }
        val token=batteryAutomation.token()
        if(token!=null && SystemClock.elapsedRealtime()>=token.deadlineElapsedMs)requestBatteryTransition(token)
        if(batteryDiagnostics.activeMode()==null)return
        val now=SystemClock.elapsedRealtime()
        val deadline=batteryDeadlineMs
        if(deadline>0 && now>=deadline){
            val generation=batteryStudyGeneration.get()
            main.post{if(batteryStudyGeneration.get()==generation && batteryDeadlineMs==deadline)interruptBatteryStudy("time_limit")}
            return
        }
        if(!force && now<batteryNextObservationMs)return
        if(!batteryObservationPending.compareAndSet(false,true))return
        batteryNextObservationMs=now+60000L
        cleanup.execute{try{batteryDiagnostics.observe(force);powerMonitors.request()}finally{batteryObservationPending.set(false)}}
    }
    internal fun exportMetrics():String = JSONObject(metrics.export())
        .put("batteryDiagnostics",batteryDiagnostics.export().put("suspensionPersisted",batterySuspended)
            .put("powerMonitors",powerMonitors.state()).put("automation",batteryAutomation.export().takeUnless{a->a.optString("status")=="idle"}?:JSONObject.NULL)).toString()

    private fun showSession() {
        val token=sessionId?:return
        val generation=epoch
        main.post{if(epoch==generation && manualLease==null && sessionId==token && !sessionCancelled)try{service?.showHandsFreeSession(token,!initialInteractive)}catch(_:Exception){metrics.add("presentation_failures")}}
    }
    private fun snapshotActivationScreen() {
        screenInteractive=context.getSystemService(PowerManager::class.java).isInteractive
        screenLocked=context.getSystemService(KeyguardManager::class.java).isKeyguardLocked
        initialInteractive=screenInteractive;initialLocked=screenLocked
    }
    private fun activationMetadata(origin:String,position:Long?,offset:Int?,scores:FloatArray?,token:String):JSONObject {
        val info=context.packageManager.getPackageInfo(context.packageName,0)
        fun score(index:Int):Any=scores?.getOrNull(index)?.takeIf{it.isFinite()}?:JSONObject.NULL
        return HandsFreeModel.metadata(installedModel).put("activatedAt",java.time.OffsetDateTime.now().toString())
            .put("appVersion",info.versionName?:JSONObject.NULL)
            .put("build",if(Build.VERSION.SDK_INT>=28)info.longVersionCode else @Suppress("DEPRECATION") info.versionCode.toLong())
            .put("detectorConfig",HandsFreeMetrics.detectorConfiguration())
            .put("instantaneousScore",score(0)).put("decisionScore",score(1))
            .put("detectorSample",position?:JSONObject.NULL).put("activationOffsetInBlock",offset?:JSONObject.NULL)
            .put("positionResolutionSamples",if(position!=null)160 else JSONObject.NULL)
            .put("origin",origin).put("diagnosticMode",diagnostic)
            .put("initialScreenInteractive",initialInteractive).put("initialDeviceLocked",initialLocked)
            .put("sessionId",token).put("groupId",if(diagnostic)recordingGroupId?:token else token).put("humanLabel",JSONObject.NULL)
    }
    private fun finishEvent(finalState:String,reason:String?) {
        heldDatasetCommand?.let { (epoch, event) ->
            val disposition = when {
                finalState == "ignored" -> "ignored"
                finalState == "played" -> "accepted"
                reason == "no_speech" -> "no_speech"
                finalState == "cancelled" -> "cancelled"
                else -> "unknown"
            }
            dataset.finishCommand(epoch, event, disposition)
        }
        heldDatasetCommand = null
        val id=activeEvent;activeEvent=null
        val transcription=when {
            transcriptionStatus=="in_progress" && finalState=="failed"->"failed"
            transcriptionStatus in setOf("requested","in_progress")->if(finalState=="cancelled")"cancelled" else "aborted_before_final"
            else->transcriptionStatus
        }
        collection.finish(id,finalState,reason,transcription)
    }
    private fun closePanel(token:String?,immediate:Boolean=false,failed:Boolean=false){if(token!=null){releaseSessionPower(token);main.post{NodeVoiceInteractionSession.current?.finishIfSession(token,immediate,failed)}}}
    private fun beep():HandsFreeCueCompletion {
        val identity=metrics.identityToken()
        return activationCue.play{delay->metrics.timing("activation_cue_enqueue_to_play",delay,identity)}
    }

    private class MicrophoneUnavailable:Exception()
    private class WakeVerificationUnavailable(cause:Throwable):Exception(cause)
    private fun microphoneBlocked(allowCommunication: Boolean = false):Boolean {
        val manager=context.getSystemService(AudioManager::class.java)
        val mode = manager.mode
        if((mode!=AudioManager.MODE_NORMAL && !(allowCommunication && mode==AudioManager.MODE_IN_COMMUNICATION)) ||
            manager.isMicrophoneMute)return true
        // Public snapshot prevents starting over an already recording application. Keep no
        // other-app metadata: only the boolean, and our own bounded recent session IDs.
        if(manager.activeRecordingConfigurations.any{!isOwnAudioSession(it.clientAudioSessionId)})return true
        return false
    }
    @Suppress("MissingPermission")
    private fun openRecorder():AudioRecord {
        if(microphoneBlocked())throw MicrophoneUnavailable()
        val min=AudioRecord.getMinBufferSize(16000,AudioFormat.CHANNEL_IN_MONO,AudioFormat.ENCODING_PCM_16BIT)
        check(min>0){"unsupported_sample_rate"}
        // MIC is deliberately not privacy-sensitive; no VOICE_COMMUNICATION source to take priority.
        return AudioRecord(MediaRecorder.AudioSource.MIC,16000,AudioFormat.CHANNEL_IN_MONO,AudioFormat.ENCODING_PCM_16BIT,maxOf(min,12800)).also{
            if(it.state!=AudioRecord.STATE_INITIALIZED){it.release();throw MicrophoneUnavailable()}
            registerOwnedAudioSession(it.audioSessionId)
        }
    }
    private fun registerOwnedAudioSession(audioSessionId: Int) = synchronized(lock) {
        ownAudioSessions.add(audioSessionId)
        while (ownAudioSessions.size > 8) ownAudioSessions.remove(ownAudioSessions.first())
    }

    private val bargeInDetectorModel by lazy {
        context.assets.open(HandsFreeDoubleTalkDetector.ASSET).use { HandsFreeDoubleTalkDetector.Model.load(it) }
    }

    private class ResponseBargeIn(val voiceSessionId:String,val researchJobId:String?,val sustainedSpeechFloor:Double?) {
        val echo = HandsFreeBargeInEcho()
        var model: HandsFreeDoubleTalkDetector.Model? = null
        @Volatile var native: HandsFreeConversationAudio? = null
        @Volatile var nativeFailed = false
        @Volatile var playing = false
        @Volatile var processing = false
        @Volatile var processingStartup:CountDownLatch?=null
        @Volatile var processingReleaseFailed=false
        @Volatile var destination: String? = null
        @Volatile var listener: HandsFreeBargeInCapture? = null
        @Volatile var provisional: CountDownLatch? = null
        @Volatile var replacement: BargeInCommand? = null
    }

    private fun resolveVoiceInterruption(
        response: ResponseBargeIn, generation: Int, session: String, owner: NodeHandsFreeBackend,
        sink: HandsFreePcmSink, listener: HandsFreeBargeInCapture,
        observedHead: Long, clock: HandsFreePlaybackClock?
    ): Boolean {
        // The recorder's current candidate may be empty or unrelated. Keep the predecessor
        // alive until the owner actually asks to replace it or stop its reading.
        val ready = CountDownLatch(1)
        val playback: HandsFreePlaybackInterruption?
        synchronized(lock) {
            if (!ownsTurn(generation,session,owner) || responseCapture!==listener ||
                response.provisional!=null || !sink.pausePlayback()) return false
            response.provisional=ready
            playback=owner.playbackInterruption(clock?.invoke() ?: observedHead)
            transcriptionStatus="not_started"
            setState("capturing",HANDSFREE_BARGE_IN_LISTENING)
        }
        try {
            Thread({
                var captured: BargeInCommand? = null
                var replacement: PreparedVoiceInterruption? = null
                var decision = VoiceInterruptionDisposition.RESUME
                var rearm = false
                try {
                    captured=listener.awaitCommand()
                    if (captured!=null && ownsTurn(generation,session,owner)) {
                        val result = owner.classifyInterruption(captured.wav) {
                            // Keep the original request while its first STT resolves. The
                            // classifier sees the new utterance and the prior text separately.
                            val awaitOriginal=playback==null && response.researchJobId==null
                            if (awaitOriginal && !owner.awaitOriginalTranscription() || !ownsTurn(generation,session,owner)) null
                            else VoiceInterruptionContext(response.voiceSessionId,
                                response.destination?:playback?.conversationId?:conversationId,
                                if (awaitOriginal) owner.generationInterruption() else null,playback,response.researchJobId)
                        }
                        decision=result.disposition
                        replacement=result.replacement
                    }
                } catch (_: Exception) {
                    // A failed provisional decision has no authority to change the current turn.
                } finally {
                    listener.close()
                    val released = listener.awaitClosed(1500)
                    try {
                        synchronized(lock) {
                            if (response.provisional===ready && ownsTurn(generation,session,owner)) {
                                if (released) {
                                    if (responseCapture===listener) responseCapture=null
                                    if (response.listener===listener) response.listener=null
                                }
                                when (decision) {
                                    VoiceInterruptionDisposition.REPLACE -> {
                                        val prepared=checkNotNull(replacement)
                                        response.replacement=checkNotNull(captured).copy(preparedInterruption=prepared)
                                        captured=null
                                        conversationId=prepared.context.conversationId
                                        backend=null
                                        owner.requestCancellation()
                                        finishEvent("interrupted","barge_in_replaced")
                                    }
                                    VoiceInterruptionDisposition.STOP -> {
                                        owner.stopReading()
                                        setState("processing","Concluindo resposta…")
                                    }
                                    VoiceInterruptionDisposition.RESUME -> {
                                        sink.resumePlayback()
                                        transcriptionStatus="final"
                                        if (response.playing) setState("speaking","Aegis respondendo…")
                                        else setState("processing","Processando pedido…")
                                        rearm=released
                                    }
                                }
                            }
                        }
                    } finally {
                        captured?.wav?.fill(0)
                        synchronized(lock) { if(response.provisional===ready)response.provisional=null }
                        ready.countDown()
                    }
                    metrics.event("barge_in_disposition",JSONObject().put("outcome",decision.name.lowercase()))
                    if (rearm) {
                        if (clock==null) startProcessingCapture(response,generation,session,owner,sink,rearming=true)
                        else startResponseCapture(response,generation,session,owner,sink,clock,rearming=true)
                    }
                }
            },"AegisBargeInDecision").apply{isDaemon=true;start()}
        } catch (_: Exception) {
            synchronized(lock) {
                if(response.provisional===ready) { response.provisional=null;sink.resumePlayback() }
                ready.countDown()
            }
            return false
        }
        return true
    }

    /** Open the normal microphone on a separate worker while STT/model/TTS preparation
     * run. Its local speech gate is retired before playback can start. */
    private fun startProcessingCapture(response:ResponseBargeIn,generation:Int,session:String,
                                       owner:NodeHandsFreeBackend,sink:HandsFreePcmSink,rearming:Boolean=false) {
        val startup=CountDownLatch(1)
        synchronized(lock) {
            if(!ownsTurn(generation,session,owner) || responseCapture!=null || diagnostic ||
                rearming && !response.processing)return
            response.processing=true
            response.processingStartup=startup
        }
        try {
            Thread({
                try {
                    if(!response.processing || !ownsTurn(generation,session,owner))return@Thread
                    val audio=try { openRecorder() } catch(_:Exception) {
                        metrics.event("barge_in_processing_capture",JSONObject().put("outcome","recorder_open_failed"))
                        return@Thread
                    }
                    lateinit var listener:HandsFreeBargeInCapture
                    listener=try{HandsFreeBargeInCapture(audio,
                        stillCurrent={ownsSession(generation,session) && responseCapture===listener},
                        inputUsable={!microphoneBlocked()},
                        sustainedSpeechFloor=response.sustainedSpeechFloor,
                        onSpeech={
                            val eligible=synchronized(lock) {
                                val activation=continuityActivation
                                response.processing && !response.playing && responseCapture===listener &&
                                    ownsTurn(generation,session,owner) && owner.processingInProgress() && activation!=null &&
                                    activation.identity==currentIdentity() && !microphoneBlocked() &&
                                    resolveVoiceInterruption(response,generation,session,owner,sink,listener,0,null)
                            }
                            if(eligible) {
                                metrics.add("barge_in_interruptions")
                                metrics.add("barge_in_processing_interruptions")
                            }
                            eligible
                        },
                        onCommandFrame={frame->
                            if(ownsSession(generation,session))
                                HandsFreePanelSignal.capture(session,frame,frame.size,SystemClock.elapsedRealtime())
                        },
                        onAvailabilityChanged={main.post{NodeVoiceInteractionSession.current?.refreshBargeInControls(session)}},
                        onProbeResult={outcome,floor,peak->
                            metrics.event("barge_in_detection",JSONObject().put("outcome",outcome)
                                .put("backgroundRms",floor).put("confirmationPeakRms",peak).put("path","processing"))
                        },
                        onCaptureSummary={summary->
                            metrics.event("barge_in_processing_capture",JSONObject()
                                .put("outcome",summary.outcome.name.lowercase()).put("accepted",summary.accepted)
                                .put("voiceReadyFrames",summary.voiceReadyFrames).put("independentFrames",summary.independentFrames)
                                .put("levelRejectedFrames",summary.levelRejectedFrames).put("commandFrames",summary.commandFrames)
                                .put("mechanicalVetoFrames",summary.mechanicalVetoFrames))
                        },
                        onMediaFilterClosed={referenceSamples,filteredReads->
                            metrics.add("external_media_barge_reference_samples",referenceSamples)
                            metrics.add("external_media_barge_filtered_reads",filteredReads)
                        })}catch(error:Exception) {
                        try {audio.release()}catch(_:Exception){response.processingReleaseFailed=true}
                        throw error
                    }
                    val attached=synchronized(lock) {
                        if(!response.processing || !ownsTurn(generation,session,owner) || responseCapture!=null)false
                        else {responseCapture=listener;response.listener=listener;true}
                    }
                    if(attached)listener.startAsync()
                    else {
                        // Playback or cancellation won before the recorder could be attached.
                        listener.close()
                        if(!listener.awaitClosed(1500))response.processingReleaseFailed=true
                    }
                } catch(_:Exception) {
                    metrics.event("barge_in_processing_capture",JSONObject().put("outcome","capture_start_failed"))
                } finally {startup.countDown()}
            },"AegisProcessingMic").apply{isDaemon=true;start()}
        } catch(_:Exception) {
            synchronized(lock){response.processing=false}
            startup.countDown()
        }
    }

    /** Serialize the microphone route change with speech acceptance. If the
     * listener accepted first, its command keeps the microphone and playback
     * must never start. If playback won, the listener can no longer accept. */
    private fun retireProcessingCapture(response:ResponseBargeIn,generation:Int,session:String,owner:NodeHandsFreeBackend) {
        var startup: CountDownLatch?
        while (true) {
            val candidate=synchronized(lock) {
                if(!ownsTurn(generation,session,owner))throw CancellationException()
                val pending=response.provisional
                if(pending==null)response.processing=false
                startup=response.processingStartup
                pending
            }
            if(candidate==null)break
            candidate.await()
        }
        check(startup?.await(1500,TimeUnit.MILLISECONDS)!=false){"processing_recorder_start_timeout"}
        check(!response.processingReleaseFailed){"processing_recorder_close_failed"}
        val listener=synchronized(lock) {
            if(!ownsTurn(generation,session,owner))throw CancellationException()
            response.listener
        }
        if(listener!=null) {
            listener.close()
            check(listener.awaitClosed(1500)){"processing_recorder_close_timeout"}
            synchronized(lock) {
                if(responseCapture===listener)responseCapture=null
                if(response.listener===listener)response.listener=null
            }
        }
    }

    private fun startResponseCapture(
        response: ResponseBargeIn,
        generation: Int,
        session: String,
        owner: NodeHandsFreeBackend,
        sink: HandsFreePcmSink,
        playedFrames: HandsFreePlaybackClock,
        rearming: Boolean = false
    ) {
        if (!ownsTurn(generation,session,owner) || response.destination==null || diagnostic ||
            (rearming && !response.playing)) return
        val model = response.model ?: return
        val native = response.native
        fun nativeUsable(): Boolean {
            val reason = native?.unavailableReason() ?: return true
            response.nativeFailed = true
            metrics.event("barge_in_audio_path", JSONObject().put("outcome",reason))
            return false
        }
        if (!nativeUsable()) return
        val audio = try { native?.record ?: openRecorder() } catch (_: Exception) {
            metrics.event("barge_in_capture", JSONObject().put("outcome", "recorder_open_failed"))
            return
        }
        val processor = HandsFreeDuplexSpeech(response.echo, HandsFreeDoubleTalkDetector(model), native != null)
        if (!processor.available) {
            processor.close()
            if (native != null) native.closeRecorder() else audio.release()
            metrics.event("barge_in_audio_path", JSONObject().put("outcome","software_aec_unavailable"))
            return
        }
        val audioManager = context.getSystemService(AudioManager::class.java)
        fun outputVolume(): Int = native?.mediaVolume() ?:
            if (audioManager.isStreamMute(AudioManager.STREAM_MUSIC)) 0
            else audioManager.getStreamVolume(AudioManager.STREAM_MUSIC)
        val audioPath = if (native != null) "native_aec" else "software_aec3"
        metrics.event("barge_in_audio_path", JSONObject().put("outcome","active").put("path",audioPath))
        lateinit var listener: HandsFreeBargeInCapture
        var readyReported = false
        fun acceptInterruption(): Boolean {
            val accepted = synchronized(lock) {
                val destination = response.destination
                val activation = continuityActivation
                val head = if (destination == null || activation == null || !response.playing ||
                    !ownsTurn(generation,session,owner) || responseCapture !== listener ||
                    activation.identity != currentIdentity() || !nativeUsable() ||
                    microphoneBlocked(native?.isOwnedMode() == true)) null else playedFrames()
                head != null && resolveVoiceInterruption(response,generation,session,owner,sink,listener,
                    head,playedFrames)
            }
            if (accepted) {
                metrics.add("barge_in_interruptions")
            }
            return accepted
        }
        listener = HandsFreeBargeInCapture(
            audio, processor, playedFrames,
            mediaVolume = ::outputVolume,
            sustainedSpeechFloor = response.sustainedSpeechFloor,
            inputUsable = ::nativeUsable,
            // A paused response owns the native recorder/AEC until it resolves.
            // A false candidate can rearm the same routing lease.
            releaseAudio = { if (native == null) audio.release() },
            onInputFailure = { if (native != null) response.nativeFailed = true },
            onMediaFilterClosed = { referenceSamples, filteredReads ->
                metrics.add("external_media_barge_reference_samples", referenceSamples)
                metrics.add("external_media_barge_filtered_reads", filteredReads)
            },
            stillCurrent = { ownsSession(generation,session) && responseCapture===listener },
            onSpeech = ::acceptInterruption,
            onProbeResult = { outcome, floorRms, peakRms ->
                metrics.event("barge_in_detection", JSONObject()
                    .put("outcome", outcome)
                    .put("backgroundRms", floorRms)
                    .put("confirmationPeakRms", peakRms)
                    .put("mediaVolume", outputVolume())
                    .put("path", audioPath))
            },
            onCaptureSummary = { summary ->
                metrics.event("barge_in_capture", JSONObject()
                    .put("outcome", summary.outcome.name.lowercase())
                    .put("path",audioPath)
                    .put("accepted", summary.accepted)
                    .put("timestampFrames", summary.timestampFrames)
                    .put("playbackHeadFrames", summary.playbackHeadFrames)
                    .put("timingResets", summary.timingResets)
                    .put("referenceUnavailableFrames", summary.referenceUnavailableFrames)
                    .put("voiceReadyFrames", summary.voiceReadyFrames)
                    .put("independentFrames", summary.independentFrames)
                    .put("levelRejectedFrames", summary.levelRejectedFrames)
                    .put("mechanicalVetoFrames", summary.mechanicalVetoFrames)
                    .put("detectorPeakPercent", (processor.peakSpeechScore * 100).toInt())
                    .put("nativeQualityFrames", processor.qualifiedNativeFrames)
                    .put("commandFrames", summary.commandFrames))
            },
            onAvailabilityChanged = {
                if (!readyReported && listener.panelAvailability() == HandsFreeBargeInPanelAvailability.VOICE &&
                    ownsTurn(generation,session,owner)) {
                    readyReported = true
                    metrics.event("barge_in_ready", JSONObject()
                        .put("mediaVolume", outputVolume())
                        .put("path",audioPath))
                }
                main.post {
                    NodeVoiceInteractionSession.current?.refreshBargeInControls(session)
                }
            },
            onCommandFrame = { frame ->
                if (ownsSession(generation,session)) {
                    HandsFreePanelSignal.capture(session,frame,frame.size,SystemClock.elapsedRealtime())
                }
            }
        )
        synchronized(lock) {
            if (!ownsTurn(generation,session,owner) || responseCapture!=null || diagnostic ||
                (rearming && !response.playing) ||
                microphoneBlocked(native?.isOwnedMode() == true)) {
                listener.close()
                return
            }
            responseCapture = listener
            response.listener = listener
        }
        if (!listener.start() && native != null) response.nativeFailed = true
    }

    private fun run(generation:Int,startup:HandsFreeCaptureStartup?=null) {
        var nextCommand: BargeInCommand? = null
        var detector=0L
        var modelPackage:HandsFreeModelPackage?=null
        var wakeVerifier:HandsFreeWakeVerifier?=null
        val batteryMode=startup?.mode?:batteryDiagnostics.activeMode()
        val startupCpuAt=if(startup!=null)Debug.threadCpuTimeNanos() else -1L
        var startupSignaled=false
        var measurementProfileStarted=startup==null
        // Drop preparation blocks. As soon as the coordinator publishes the run,
        // the existing capture loop handles every following block as before.
        fun measureBlock(audio:AudioRecord):Boolean {
            if(startup==null || startup.shouldMeasure())return true
            if(!startupSignaled) {
                // The callback may not have been posted by Android yet.
                if(Build.VERSION.SDK_INT>=29 && audio.activeRecordingConfiguration?.isClientSilenced==true)
                    throw MicrophoneUnavailable()
                startupSignaled=true
                val cpu=Debug.threadCpuTimeNanos()
                startup.signalReady(if(startupCpuAt>=0 && cpu>=startupCpuAt)cpu-startupCpuAt else -1L)
            }
            return false
        }
        var profileAt=SystemClock.elapsedRealtime()
        var profileWall=SystemClock.elapsedRealtimeNanos()
        var profileCpu=Debug.threadCpuTimeNanos()
        fun profileWorker(force:Boolean=false) {
            if(batteryMode==null)return
            val now=SystemClock.elapsedRealtime()
            if(!measurementProfileStarted) {
                if(startup?.measurementRunId()==null)return
                measurementProfileStarted=true
                profileAt=now
                profileWall=SystemClock.elapsedRealtimeNanos()
                profileCpu=Debug.threadCpuTimeNanos()
                return
            }
            if(!force && now-profileAt<5000)return
            val wall=SystemClock.elapsedRealtimeNanos();val cpu=Debug.threadCpuTimeNanos()
            batteryDiagnostics.component("capture_thread",0,wall-profileWall,if(profileCpu>=0 && cpu>=profileCpu)cpu-profileCpu else -1L)
            profileAt=now;profileWall=wall;profileCpu=cpu
            if(!force)observeBattery()
        }
        try {
            if(!current(generation))return
            if(batteryMode!="capture_only") {
            val loadedPackage=HandsFreeModel.readPackage(context)?:error("model_missing")
            modelPackage=loadedPackage
            detector=MicroWakeWordNative.create(loadedPackage.wakeModel);check(detector!=0L){"model_runtime_incompatible"}
            wakeVerifier=loadedPackage.verifier?.let {
                try { HandsFreeWakeVerifier(it.melModel,it.embeddingModel,it.keywordModel,it.seedEmbedding,it.threshold) }
                catch(error:Exception) { throw WakeVerificationUnavailable(error) }
            }
            val loaded=loadedPackage.identity
            synchronized(lock){if(!current(generation))return;installedModel=loaded;recordIdentity()}
            }
            var continuation=false
            var nativeRejectedSession: String? = null
            while(current(generation)) {
                val interrupted = nextCommand
                nextCommand = null
                var researchReply: ResearchReply? = null
                val command=if(batteryMode=="capture_only")captureOnly(generation,startup,::measureBlock){profileWorker()}
                    else interrupted?.wav ?: capture(generation,detector,continuation,wakeVerifier,batteryMode=="detection",startup,::measureBlock,
                        takeResearchReply = {
                            researchReply = beginResearchReply(generation)
                            researchReply != null
                        }) {profileWorker()}
                val research = researchReply
                if(command==null)break
                if(!current(generation)){command.fill(0);break}
                if(command.isEmpty() && research==null){synchronized(lock){finishEvent(if(sessionCancelled)"cancelled" else "empty",endReason?:if(continuation)"continuation_timeout" else "no_speech")};continuation=false;endConversationSession(sessionId);if(!MicroWakeWordNative.reset(detector))error("detector_reset");continue}
                if(sessionCancelled){command.fill(0);synchronized(lock){finishEvent("cancelled",endReason?:"operation_cancelled")};continuation=false;endConversationSession(sessionId);if(!MicroWakeWordNative.reset(detector))error("detector_reset");continue}
                val session=sessionId?:UUID.randomUUID().toString().also{sessionId=it}
                val turnIdentity=metrics.identityToken()
                val trace=interrupted?.trace ?: capturedTurnLatency
                val prepared = interrupted?.preparedInterruption
                val response = if (batteryMode==null && !diagnostic) ResponseBargeIn(
                    research?.voiceSessionId ?: prepared?.context?.voiceSessionId ?: session,research?.jobId,
                    if(research==null)capturedSpeechFloor else null) else null
                var turnOutcome="failed"
                try {
                    val active=synchronized(lock){
                        check(current(generation) && !sessionCancelled)
                        setState("processing","Processando pedido…")
                        backend?:newBackend(generation,session).also{backend=it}
                    }
                    var playbackCompletedAt:Long?=null
                    lateinit var sink: HandsFreePcmSink
                    sink=playback.openStream(
                        stillCurrent = { ownsTurn(generation,session,active) },
                        onPlaybackEvent = { event ->
                            trace?.mark(event)
                            when(event) {
                                "player_play_called" -> response?.playing = true
                                "player_drained" -> {
                                    playbackCompletedAt=SystemClock.elapsedRealtime()
                                    if (response!=null) synchronized(lock) {
                                        response.playing = false
                                        if (response.provisional==null) response.listener?.close()
                                    }
                                }
                            }
                        },
                        echo = response?.echo,
                        prepareCommunication = {
                            if(response!=null)retireProcessingCapture(response,generation,session,active)
                            if (response == null || !ownsTurn(generation,session,active) || response.destination == null) false
                            else {
                                response.model = try { bargeInDetectorModel } catch (_: Exception) {
                                    metrics.event("barge_in_audio_path", JSONObject().put("outcome","detector_unavailable"))
                                    null
                                }
                                if (response.model == null || nativeRejectedSession == session) false
                                else {
                                    val preparing = synchronized(lock) {
                                        if (!ownsTurn(generation,session,active)) false
                                        else { mediaFocusRelease.routePreparing(session); true }
                                    }
                                    if (!preparing) false else try {
                                        response.native = HandsFreeConversationAudio.tryOpen(context, ::microphoneBlocked,
                                            ::registerOwnedAudioSession) { reason ->
                                            response.nativeFailed = true
                                            metrics.event("barge_in_audio_path", JSONObject().put("outcome",reason))
                                        }
                                        response.native != null
                                    } finally {
                                        if (response.native == null) mediaFocusRelease.routeClosed(session)
                                    }
                                }
                            }
                        },
                        onCommunicationFailed = {
                            response?.let {
                                it.nativeFailed = true
                                try { it.native?.close() }
                                finally { it.native = null; mediaFocusRelease.routeClosed(session) }
                                metrics.event("barge_in_audio_path", JSONObject().put("outcome","native_aec_playback_failed"))
                            }
                        },
                        onPlaybackReady = { head ->
                            if (response!=null) startResponseCapture(response,generation,session,active,sink,head)
                        },
                        spectrumSession = if (response == null) null else session,
                        sessionFocus = if (response == null) null else {
                            {
                                synchronized(lock) {
                                    if (!ownsTurn(generation,session,active)) throw CancellationException()
                                    when (sessionMediaFocus.acquire(session,retryUnavailable=true)) {
                                        HandsFreeSessionFocusResult.ACQUIRED -> true
                                        HandsFreeSessionFocusResult.UNAVAILABLE -> false
                                        HandsFreeSessionFocusResult.LOST -> throw CancellationException()
                                    }
                                }
                            }
                        },
                        maximumFrames = if (research == null) 24000L * 180 else Int.MAX_VALUE.toLong()
                    )
                    val startProcessing: () -> Unit = {
                        if (response != null) startProcessingCapture(response,generation,session,active,sink)
                    }
                    val turn = if (research != null) {
                        response?.destination = research.conversationId
                        val played=active.speakResearch(research.jobId,research.voiceSessionId,sink,onProcessing = startProcessing)
                        HandsFreeTurn(research.conversationId,played,false,readingStopped=!played)
                    } else active.execute(command,session,conversationId,sink,trace,incremental=true,
                        onProcessing=startProcessing,
                        preparedInterruption=prepared) { destination ->
                        response?.destination = destination
                    }
                    response?.provisional?.await()
                    synchronized(lock) {
                        if(!ownsTurn(generation,session,active))throw CancellationException()
                        conversationId=turn.conversationId
                        // Commit only a fully successful response, anchored at the actual drain,
                        // before AudioTrack.close or bookkeeping can move the deadline.
                        if(turn.playedAudio) {
                            val activation=continuityActivation?:throw java.util.concurrent.CancellationException()
                            if(!conversationContinuity.playbackCompleted(activation,checkNotNull(turn.conversationId),currentIdentity(),checkNotNull(playbackCompletedAt)))
                                throw NodeNativeSession.IdentityUnavailable()
                        }
                        continuation=turn.continueListening && turn.playedAudio
                        continuationDeadline=(playbackCompletedAt?:SystemClock.elapsedRealtime())+2000
                        turnOutcome=when {
                            turn.readingStopped -> "stopped"
                            turn.ignored -> "ignored"
                            turn.playedAudio -> "played"
                            else -> "empty"
                        }
                        heldDatasetCommand?.let { (epoch, event) ->
                            val disposition = when { turn.ignored -> "ignored"; turn.playedAudio -> "accepted"; else -> "unknown" }
                            dataset.finishCommand(epoch, event, disposition)
                            heldDatasetCommand = null
                        }
                        if(!continuation){
                            val reason=when {
                                turn.readingStopped -> "barge_in_stop"
                                turn.ignored -> "not_addressed"
                                turn.playedAudio -> "session_completed"
                                else -> "empty_command"
                            }
                            finishEvent(turnOutcome,reason)
                            endConversationSession(session,releaseMediaFocus=false)
                        }
                    }
                }catch(error:Exception){
                    continuation=false
                    val replacement = response?.replacement
                    if (replacement!=null) {
                        turnOutcome = "interrupted"
                        synchronized(lock) {
                            if(ownsSession(generation,session)) {
                                nextCommand=replacement
                            } else {
                                replacement.wav.fill(0)
                                endConversationSession(session,releaseMediaFocus=false)
                            }
                        }
                    } else {
                        val cancelled=!current(generation) || sessionCancelled || error is CancellationException
                        turnOutcome=if(cancelled)"cancelled" else "failed"
                        synchronized(lock){finishEvent(turnOutcome,endReason?:if(cancelled)"operation_cancelled" else if(transcriptionStatus=="in_progress")"transcription_failed" else "session_processing_failed")}
                        metrics.addForIdentity(if(cancelled)"turns_cancelled" else "sessions_failed",1,turnIdentity);cancelBackend();endConversationSession(session,false,releaseMediaFocus=false)
                        if(current(generation) && !cancelled)setState("session_failed","Não foi possível concluir o pedido. Diga Aegis para tentar novamente.")
                        closePanel(session,failed=!cancelled)
                    }
                }finally {
                    command.fill(0)
                    response?.playing = false
                    try {
                        if(response!=null) {
                            synchronized(lock){response.processing=false}
                            check(response.processingStartup?.await(1500,TimeUnit.MILLISECONDS)!=false){"processing_recorder_start_timeout"}
                            check(!response.processingReleaseFailed){"processing_recorder_close_failed"}
                        }
                        response?.listener?.let { listener ->
                            listener.close()
                            check(listener.awaitClosed(1500)) { "barge_in_recorder_close_timeout" }
                            synchronized(lock) {
                                if (responseCapture === listener) responseCapture = null
                            }
                        }
                    } finally {
                        try { response?.native?.close() }
                        finally {
                            try { response?.echo?.clear() }
                            finally {
                                // Let media resume only after the response has released its communication route.
                                mediaFocusRelease.routeClosed(session)
                                synchronized(lock) { if (sessionId != session || sessionCancelled) sessionMediaFocus.release(session) }
                            }
                        }
                        if (response?.nativeFailed == true) nativeRejectedSession = session
                    }
                    trace?.let{it.mark("turn_ended");metrics.turnLatency(it,turnOutcome,turnIdentity)}
                    capturedTurnLatency=null
                }
                if(!MicroWakeWordNative.reset(detector))error("detector_reset")
            }
        }catch(error:Throwable) {
            if (error is MicrophoneUnavailable) NodeExternalPlaybackReference.stop(context)
            if(batteryMode!=null && current(generation)) {
                metrics.add("battery_study_capture_failures")
                val issue=if(error is MicrophoneUnavailable)"microphone_unavailable" else "capture_failure"
                startup?.fail(issue)
                interruptBatteryStudy(issue)
            } else {
            if(current(generation)) {
                synchronized(lock){finishEvent("failed",if(error is MicrophoneUnavailable)"microphone_unavailable" else if(detector==0L)"model_runtime_unavailable" else "capture_or_detector_failure")}
                metrics.add("capture_failures");main.post{if(epoch==generation)service?.stopHandsFreeForeground()}
                if(detector==0L || error is WakeVerificationUnavailable){modelReady=false;setState("model_incompatible","Falha de integridade ou compatibilidade do detector. Importe novamente o modelo privado.")}
                else {setState("microphone_unavailable","Escuta indisponível: confira o microfone, chamadas, permissões e o assistente padrão.")
                    if(++failures<=3) {
                        val delay=longArrayOf(5000,30000,120000)[failures-1];retryNotBefore=SystemClock.elapsedRealtime()+delay
                        main.postDelayed({if(epoch==generation && status=="microphone_unavailable")reconcile()},delay)
                    }else retriesExhausted=true}

            }
            }
        }finally {
            nextCommand?.wav?.fill(0)
            synchronized(lock) {
                closeResponseCapture()
                responseCapture = null
            }
            profileWorker(force=true)
            if(startup!=null && startup.measurementRunId()==null)startup.fail("capture_ended_before_ready")
            recorder=null
            val token=synchronized(lock){val token=sessionId;finishEvent(if(sessionCancelled)"cancelled" else "interrupted",endReason?:"audio_worker_ended");cancelBackend();endConversationSession(token,false);token}
            closePanel(token)
            try{wakeVerifier?.close()}catch(_:Exception){metrics.add("wake_verifier_close_failures")}
            modelPackage?.close()
            if(detector!=0L)MicroWakeWordNative.destroy(detector)
            synchronized(lock){if(worker===Thread.currentThread())worker=null}
            metrics.persist()
            // A configuration change may arrive during the previous capture thread's teardown.
            if(epoch!=generation && wantsCapture() && !batteryChanging && manualLease==null && service!=null)main.post{reconcile()}
        }
    }

    /** Same source, format and reads, with no detector/runtime/activation work. */
    @Suppress("MissingPermission")
    private fun captureOnly(generation:Int,startup:HandsFreeCaptureStartup?,measureBlock:(AudioRecord)->Boolean,onHeartbeat:()->Unit):ByteArray? {
        val audio=openRecorder();recorder=audio
        val buffer=ShortArray(3200)
        val silenced=java.util.concurrent.atomic.AtomicBoolean(false)
        var callback:AudioManager.AudioRecordingCallback?=null
        var registered=false
        var receivedAt=SystemClock.elapsedRealtime();var checkedAt=receivedAt
        try {
            if(Build.VERSION.SDK_INT>=29) {
                callback=object:AudioManager.AudioRecordingCallback(){override fun onRecordingConfigChanged(configs:MutableList<AudioRecordingConfiguration>){silenced.set(configs.any{it.isClientSilenced})}}
                audio.registerAudioRecordingCallback(context.mainExecutor,callback);registered=true
            }
            synchronized(lock) {
                if(!current(generation))return null
                check(NodeHandsFree.permission(context) && NodeHandsFree.selected(context))
                if(microphoneBlocked())throw MicrophoneUnavailable()
                audio.startRecording();check(audio.recordingState==AudioRecord.RECORDSTATE_RECORDING)
                if(startup==null)setState("listening","Medição: somente microfone. Aegis não responderá.")
            }
            while(current(generation)) {
                val count=audio.read(buffer,0,buffer.size,AudioRecord.READ_BLOCKING)
                if(!current(generation))return null
                val now=SystemClock.elapsedRealtime()
                if(count<=0 || silenced.get())throw MicrophoneUnavailable()
                if(!measureBlock(audio)){receivedAt=now;continue}
                captureConfirmed=true;lastAudioReadMs=now
                if(now-checkedAt>=5000) {
                    checkedAt=now
                    if(!NodeHandsFree.permission(context) || !NodeHandsFree.selected(context) || microphoneBlocked())throw MicrophoneUnavailable()
                }
                val duration=count*1000L/16000
                metrics.capture(duration,minOf(duration,(now-receivedAt).coerceAtLeast(0)),false,screenLocked,screenInteractive)
                batteryDiagnostics.capture(count,false)
                if(now-receivedAt>duration+1000)metrics.add("captureUnknownGapMs",now-receivedAt-duration)
                receivedAt=now;metrics.maybeTick();onHeartbeat()
            }
            return null
        }finally {
            captureConfirmed=false;recorder=null
            if(Build.VERSION.SDK_INT>=29 && registered && callback!=null)try{audio.unregisterAudioRecordingCallback(callback)}catch(_:Exception){}
            try{audio.stop()}catch(_:Exception){};audio.release();buffer.fill(0)
        }
    }

    private fun capture(generation:Int,detector:Long,continuation:Boolean,wakeVerifier:HandsFreeWakeVerifier?,batteryStudy:Boolean=false,startup:HandsFreeCaptureStartup?=null,
                        measureBlock:(AudioRecord)->Boolean={true},takeResearchReply:()->Boolean={false},onHeartbeat:()->Unit={}):ByteArray? {
        capturedTurnLatency=null
        if(!continuation)capturedSpeechFloor=null
        val audio=openRecorder();recorder=audio
        val buffer=ShortArray(3200) // One 200ms microphone read, shared by all local consumers.
        val mediaFilter = if (!batteryStudy && !diagnostic) HandsFreeMicrophoneMediaFilter(audio) { referenceSamples, filteredReads ->
            metrics.add("external_media_reference_samples", referenceSamples)
            metrics.add("external_media_filtered_reads", filteredReads)
        } else null
        val electWake = !continuation && !batteryStudy && !diagnostic
        val candidateWorker = wakeVerifier!=null || electWake
        val ring=HandsFreeRing(if(candidateWorker)HandsFreeWakeCandidate.RING_CAPACITY else 40000)
        var wakeCandidate:HandsFreeWakeCandidate?=null
        var pendingVerification:HandsFreePendingWakeVerification?=null
        var verificationStartedAt=0L
        var candidateObservedAt=0L
        var candidateDecisionEstimatedAt=0L
        var candidateDeadline=0L
        var candidateEpoch=0
        val verificationExecutor=if(candidateWorker)java.util.concurrent.ThreadPoolExecutor(
            0,1,30,java.util.concurrent.TimeUnit.SECONDS,java.util.concurrent.LinkedBlockingQueue<Runnable>(1)
        ) else null
        val command=ShortArray(320000) // At most 20s, exclusively at/after activation.
        var detectorSamples=0L;var streamSamples=0L
        val recordingGroup=UUID.randomUUID().toString()
        recordingGroupId=recordingGroup
        var commandStarted=0L;var captured=false
        var realtime:HandsFreeRealtimeTranscription?=null
        var capturedSuccessfully=false
        var trace:HandsFreeTurnLatency?=null
        var classifier:NativeHandsFreeSpeechClassifier?=null
        var commandCapture:HandsFreeCommandCapture?=null
        var cueCompletion:HandsFreeCueCompletion?=null
        var lastVadSample:Int?=null
        var lastVadFrameAtMs:Long?=null
        var captureEvent:String?=null
        var contextMarginRemaining=0
        // Diagnostic mode retains the detector's continuous stream and never starts STT.
        // Its post window uses the existing 3s no-speech bound; overlapping events share reads.
        data class DiagnosticCapture(val id:String?,var remaining:Int,var margin:Int)
        val diagnosticCaptures=mutableListOf<DiagnosticCapture>()
        val datasetClips = mutableListOf<WakeDatasetClip>()
        val datasetWindow = WakeDatasetConsentWindow()
        fun closePendingVerification() {
            val pending = pendingVerification ?: return
            pending.close()
            pendingVerification = null
            val episodeId = pending.election?.episodeId ?: return
            datasetClips.find { it.decision == pending.candidate.decisionSample }?.groupWithEpisode(episodeId)
        }
        var diagnosticCancelGeneration=diagnosticCancelEpoch.get()
        var captureExit="capture_interrupted"
        var receivedAt=SystemClock.elapsedRealtime();var checkedAt=receivedAt
        val detectorProfiler=HandsFreeDetectorProfileSampler(HandsFreeMetrics.DETECTOR_PROFILE_INTERVAL_MS)
        var recordingCallback:AudioManager.AudioRecordingCallback?=null
        val silenced=java.util.concurrent.atomic.AtomicBoolean(false)
        var recordingCallbackRegistered=false
        fun retainCancellationPrefix(count:Int,readReturnedAtMs:Long) {
            if(!captured || captureEvent==null || count<=0)return
            val n=handsFreeSamplesBeforeCancellation(count,readReturnedAtMs,cancelledAt)
            val state=checkNotNull(commandCapture);val before=state.length
            state.append(buffer,n)
            collection.appendPost(captureEvent,buffer,0,state.length-before)
            collection.appendContext(captureEvent,buffer,0,minOf(n,contextMarginRemaining))
        }
        try {
            val speechClassifier=NativeHandsFreeSpeechClassifier();classifier=speechClassifier
            val continuationGate=HandsFreeContinuationGate(speechClassifier)
            if(Build.VERSION.SDK_INT>=29) {
                recordingCallback=object:AudioManager.AudioRecordingCallback(){override fun onRecordingConfigChanged(configs:MutableList<AudioRecordingConfiguration>){silenced.set(configs.any{it.isClientSilenced});if(silenced.get())metrics.event("microphone_silenced")}}
                audio.registerAudioRecordingCallback(context.mainExecutor,recordingCallback);recordingCallbackRegistered=true
            }
            synchronized(lock) {
                if(!current(generation))return null
                if(continuation && sessionCancelled)return ByteArray(0)
                if(continuation)sessionId?.let{holdSessionPower(it,renew=true)}
                check(NodeHandsFree.permission(context) && NodeHandsFree.selected(context))
                if(microphoneBlocked())throw MicrophoneUnavailable()
                audio.startRecording();check(audio.recordingState==AudioRecord.RECORDSTATE_RECORDING)
                if(startup==null)setState(if(continuation)"continuation" else "listening",if(continuation)"Pode continuar…" else if(batteryStudy)"Medição: detecção local sem comandos ou gravações." else if(diagnostic)"Diagnóstico: detecção local sem comandos." else "Aguardando Aegis.")
                if(continuation)showSession()
            }
            while(current(generation)) {
                // The detector normally reads at 200ms for battery life. A candidate
                // awaiting lookahead or the coordinator may be recognized earlier at
                // 40ms cadence (80ms with active external reference), without changing
                // either the audio or the winner rule. Idle never probes that reference here.
                val waitingForElection=electWake && !pendingActivation && (wakeCandidate!=null || pendingVerification!=null)
                val readSize=handsFreePendingWakeReadSamples(buffer.size,waitingForElection,waitingForElection && NodeExternalPlaybackReference.active())
                val consentBeforeRead = dataset.epoch
                val count=audio.read(buffer,0,readSize,AudioRecord.READ_BLOCKING)
                val readReturnedAtMs=HandsFreeTurnLatency.now()
                if(!current(generation)){retainCancellationPrefix(count,readReturnedAtMs);return null}
                val now=SystemClock.elapsedRealtime()
                if(count<=0 || silenced.get())throw MicrophoneUnavailable()
                mediaFilter?.advance(count)
                val blockConsent = datasetWindow.received(consentBeforeRead, dataset.epoch, count)
                mediaFilter?.filter(buffer, count)
                if(captured || continuation)HandsFreePanelSignal.capture(sessionId,buffer,count,now)
                if(startup!=null && !measureBlock(audio)){receivedAt=now;continue}
                captureConfirmed=true;lastAudioReadMs=now
                if(now-checkedAt>=5000) {
                    checkedAt=now
                    if(!NodeHandsFree.permission(context) || !NodeHandsFree.selected(context) || microphoneBlocked())throw MicrophoneUnavailable()
                }
                val duration=count*1000L/16000
                metrics.capture(duration,minOf(duration,(now-receivedAt).coerceAtLeast(0)),!captured && !continuation,screenLocked,screenInteractive)
                if(now-receivedAt>duration+1000)metrics.add("captureUnknownGapMs",now-receivedAt-duration)
                receivedAt=now;metrics.maybeTick()
                if(batteryStudy){batteryDiagnostics.capture(count,true);onHeartbeat()}
                if(sessionCancelled && (captured || continuation)) {
                    retainCancellationPrefix(count,readReturnedAtMs)
                    return ByteArray(0)
                }
                val clips = datasetClips.iterator()
                while (clips.hasNext()) {
                    val clip = clips.next()
                    if (clip.epoch != dataset.epoch) { clip.clear(); clips.remove(); continue }
                    clip.append(buffer, 0, count)
                    // The PCM can be complete before a coordinator reply is observed.
                    val waitingForClaim = clip.decision == wakeCandidate?.decisionSample ||
                        clip.decision == pendingVerification?.candidate?.decisionSample
                    if (clip.complete && !waitingForClaim) { dataset.persist(clip); clips.remove() }
                }
                val pendingDiagnostics=diagnosticCaptures.iterator()
                val cancellationGeneration=diagnosticCancelEpoch.get()
                val diagnosticCancelled=diagnosticCancelGeneration!=cancellationGeneration
                diagnosticCancelGeneration=cancellationGeneration
                if(diagnosticCancelled) {
                    closePendingVerification();wakeCandidate=null
                }
                while(pendingDiagnostics.hasNext()) {
                    val item=pendingDiagnostics.next()
                    val n=minOf(if(diagnosticCancelled)handsFreeSamplesBeforeCancellation(count,readReturnedAtMs,cancelledAt) else count,item.remaining)
                    collection.appendPost(item.id,buffer,0,n);item.remaining-=n
                    val margin=minOf(n,item.margin)
                    collection.appendContext(item.id,buffer,0,margin);item.margin-=margin
                    if(item.remaining==0 || diagnosticCancelled) {
                        collection.finishCapture(item.id)
                        collection.finish(item.id,if(diagnosticCancelled)"cancelled" else "diagnostic_complete",if(diagnosticCancelled)"explicit_cancel" else "diagnostic_window_elapsed","not_started")
                        pendingDiagnostics.remove()
                    }
                }
                if(captured) {
                    val commandState=checkNotNull(commandCapture)
                    val before=commandState.length
                    val cueReady=cueCompletion?.completedBy(readReturnedAtMs)==true
                    if(cueReady) {
                        cueCompletion=null
                        trace?.markAt("activation_cue_observed_completed",readReturnedAtMs)
                    }
                    val ended=commandState.append(buffer,count,
                        activationCueCompleted=cueReady,readReturnedAtMs=readReturnedAtMs)
                    val accepted=commandState.length-before
                    // Persistence sees the identical bounded PCM even when VAD rejects it.
                    collection.appendPost(captureEvent,buffer,0,accepted)
                    val margin=minOf(accepted,contextMarginRemaining)
                    collection.appendContext(captureEvent,buffer,0,margin);contextMarginRemaining-=margin
                    realtime?.append(buffer,accepted)
                    val last=commandState.lastActiveEndSample
                    if(last!=null && last!=lastVadSample) {
                        lastVadSample=last
                        lastVadFrameAtMs=readReturnedAtMs-(commandState.length-last)*1000L/16000
                    }
                    if(ended) {
                        collection.finishCapture(captureEvent)
                        val endpointAt=HandsFreeTurnLatency.now()
                        metrics.add("commandCaptureMs",now-commandStarted)
                        if(!commandState.hasUsefulAudio){captureExit="no_speech";metrics.add("commands_no_speech");return ByteArray(0)}
                        val activeTrace=checkNotNull(trace)
                        lastVadFrameAtMs?.let{activeTrace.markAt("last_vad_active_frame_estimated",it)}
                        activeTrace.markAt("endpoint_completed",endpointAt)
                        realtime?.commit()
                        activeTrace.mark("wav_prepare_started")
                        val wav=handsFreeWav(command,commandState.transcriptionLength,commandState.transcriptionStart)
                        capturedSpeechFloor=commandState.sustainedSpeechFloor ?: capturedSpeechFloor
                        activeTrace.mark("wav_ready");capturedTurnLatency=activeTrace;capturedSuccessfully=true
                        return wav
                    }
                    continue
                }
                // Keep draining AudioRecord while the candidate verifier runs elsewhere.
                // The pending owner retains all command PCM, even after the ring advances.
                pendingVerification?.append(buffer,count)
                streamSamples+=count
                ring.append(buffer,count)
                var event=if(continuation) {
                    val beforeDeadline=(count-((now-continuationDeadline).coerceAtLeast(0)*16)).coerceIn(0,count.toLong()).toInt()
                    when(continuationGate.process(buffer,count,beforeDeadline,now>=continuationDeadline)) {
                        HandsFreeContinuationGate.Decision.STARTED->1L
                        HandsFreeContinuationGate.Decision.WAIT->-1L
                        HandsFreeContinuationGate.Decision.TIMED_OUT->{captureExit="continuation_timeout";metrics.add("continuation_no_speech");return ByteArray(0)}
                    }
                } else {
                    // Sample one unchanged block every5s; this is capture-thread work,
                    // not device energy or CPU attributed to the platform audio service.
                    val profile=detectorProfiler.sampleDue(now)
                    val studyProfile=batteryStudy
                    val wallStart=if(profile || studyProfile)SystemClock.elapsedRealtimeNanos() else 0L
                    val cpuStart=if(profile || studyProfile)Debug.threadCpuTimeNanos() else 0L
                    val traced=studyProfile && Build.VERSION.SDK_INT>=29 && android.os.Trace.isEnabled()
                    if(traced)android.os.Trace.beginSection("AegisWakeDetector")
                    val result=try{MicroWakeWordNative.process(detector,buffer,count)}finally{if(traced)android.os.Trace.endSection()}
                    if(profile || studyProfile) {
                        val cpuEnd=Debug.threadCpuTimeNanos()
                        val cpuNanos=if(cpuStart>=0 && cpuEnd>=cpuStart)cpuEnd-cpuStart else -1L
                        val wallNanos=SystemClock.elapsedRealtimeNanos()-wallStart
                        if(profile)metrics.detectorProfile(count,wallNanos,cpuNanos)
                        if(studyProfile)batteryDiagnostics.component("detector",count,wallNanos,cpuNanos)
                    }
                    detectorSamples+=count;result
                }
                if(event == -2L)error("detector_inference")
                // This read may contain a decision preceding an explicit diagnostic cancellation.
                if(diagnostic && diagnosticCancelled)event=-1L
                // Capture the detector observation before verification/election can discard it.
                val consent = blockConsent?.takeIf { it == dataset.epoch }
                if (consent != null && event >= 0 && !batteryStudy && !continuation && !pendingActivation && datasetClips.size < 8) {
                    val boundary = HandsFreeActivationBoundary(detectorSamples - count, count, event)
                    val meta = activationMetadata(if (diagnostic) "diagnostic" else "wake_word", event,
                        boundary.offset, MicroWakeWordNative.activationScores(detector), recordingGroup)
                    val observed = java.time.OffsetDateTime.now().minusNanos(boundary.postCount.toLong() * 1000000000 / 16000)
                    meta.put("activatedAt", observed.toString()).put("groupId", recordingGroup)
                    val clip = WakeDatasetClip(consent, event, datasetWindow.context(ring, boundary), meta)
                    clip.append(buffer, boundary.offset, boundary.postCount)
                    datasetClips.add(clip)
                }
                var verifiedCandidate:HandsFreeWakeCandidate?=null
                var verifiedPending:HandsFreePendingWakeVerification?=null
                var verifiedOutcome:HandsFreePendingWakeVerification.Outcome?=null
                var grantObservedAt=0L
                var verifiedCommandLength=0
                if(candidateWorker && !continuation && !pendingActivation) {
                    if(event>=0 && wakeCandidate==null && pendingVerification==null) {
                        wakeCandidate=HandsFreeWakeCandidate(event,(event-(detectorSamples-count)).toInt(),MicroWakeWordNative.activationScores(detector))
                        candidateObservedAt=SystemClock.elapsedRealtime()
                        // Sample boundary mapped from read return; not a physical wake timestamp.
                        candidateDecisionEstimatedAt=now-(streamSamples-event).coerceAtLeast(0)/16
                        candidateDeadline=now + HandsFreeWakeElection.MAX_AGE_MS - (streamSamples-event).coerceAtLeast(0)/16
                        candidateEpoch=electionEpoch.get()
                        if(wakeVerifier!=null)metrics.add("wake_verifier_candidates")
                    }
                    event=-1L
                    val candidate=wakeCandidate
                    if(candidate!=null && (wakeVerifier==null || candidate.ready(streamSamples))) {
                        val expectedEpoch=candidateEpoch
                        val election=if(electWake)HandsFreeWakeElection({NodeNativeSession.read(context)},
                            {SystemClock.elapsedRealtime()},candidateDeadline,
                            {current(generation) && electionEpoch.get()==expectedEpoch && !pendingActivation},wakeVerifier!=null) else null
                        verificationStartedAt=SystemClock.elapsedRealtime()
                        pendingVerification=HandsFreePendingWakeVerification(candidate,ring,streamSamples,
                            checkNotNull(verificationExecutor),verificationStartedAt,{SystemClock.elapsedRealtime()},
                            {window,cancelled->
                                val wall=if(batteryStudy)SystemClock.elapsedRealtimeNanos() else 0L
                                val cpu=if(batteryStudy)Debug.threadCpuTimeNanos() else 0L
                                val traced=batteryStudy && Build.VERSION.SDK_INT>=29 && android.os.Trace.isEnabled()
                                if(traced)android.os.Trace.beginSection("AegisWakeVerifier")
                                try{wakeVerifier?.score(window.samples,window.samples.size,window.candidateSample){cancelled() || !current(generation)} ?: 1f}
                                finally{
                                    if(traced)android.os.Trace.endSection()
                                    if(batteryStudy) {
                                    val end=Debug.threadCpuTimeNanos()
                                    batteryDiagnostics.component("verifier",window.samples.size,SystemClock.elapsedRealtimeNanos()-wall,if(cpu>=0 && end>=cpu)end-cpu else -1L)
                                }}
                            },threshold=wakeVerifier?.threshold ?: 1f,election=election,withLookahead=wakeVerifier!=null)
                        wakeCandidate=null
                    }
                    val pending=pendingVerification
                    if(pending!=null) {
                        val outcome=pending.poll(SystemClock.elapsedRealtime())
                        if(outcome.status!=HandsFreePendingWakeVerification.Status.WAITING) {
                            datasetClips.find { it.decision == pending.candidate.decisionSample }?.let { clip ->
                                clip.verifierScore(outcome.score.takeIf { wakeVerifier != null })
                                clip.groupWithEpisode(pending.election?.episodeId)
                                clip.disposition = when (outcome.status) {
                                    HandsFreePendingWakeVerification.Status.SUPPRESSED -> "election_lost"
                                    HandsFreePendingWakeVerification.Status.REJECTED -> "verifier_rejected"
                                    HandsFreePendingWakeVerification.Status.ACCEPTED -> "candidate"
                                    else -> "unavailable"
                                }
                            }
                            val terminalObservedAt=SystemClock.elapsedRealtime()
                            metrics.timing(if(electWake)"wake_verification_and_election_wait" else "wake_verifier_wait",terminalObservedAt-verificationStartedAt,metrics.identityToken())
                            when(outcome.status) {
                                HandsFreePendingWakeVerification.Status.ACCEPTED -> {
                                    grantObservedAt=terminalObservedAt;verifiedOutcome=outcome
                                    pending.candidate.verifierScore=outcome.score.takeIf{wakeVerifier!=null}
                                    verifiedCandidate=pending.candidate;verifiedPending=pending
                                    verifiedCommandLength=pending.copyCommandTo(command)
                                    event=pending.candidate.decisionSample
                                    if(wakeVerifier!=null)metrics.add("wake_verifier_accepted")
                                    if(electWake)metrics.add("wake_election_granted")
                                }
                                HandsFreePendingWakeVerification.Status.SUPPRESSED, HandsFreePendingWakeVerification.Status.COORDINATOR_UNAVAILABLE -> {
                                    metrics.add(if(outcome.status==HandsFreePendingWakeVerification.Status.SUPPRESSED)"wake_election_suppressed" else "wake_election_unavailable")
                                    pending.close();pendingVerification=null
                                }
                                HandsFreePendingWakeVerification.Status.FAILED ->
                                    throw WakeVerificationUnavailable(IllegalStateException("verifier_failed"))
                                else -> {
                                    metrics.add("wake_verifier_${outcome.status.name.lowercase()}")
                                    pending.close();pendingVerification=null
                                }
                            }
                        }
                    }
                } else if(pendingVerification!=null || wakeCandidate!=null) {
                    closePendingVerification();wakeCandidate=null
                }
                if(batteryStudy) {
                    // Suppress all activation side effects during the controlled comparison.
                    verifiedPending?.close();if(verifiedPending!=null)pendingVerification=null
                    if(event>=0)metrics.add("battery_study_confirmed_candidates")
                    command.fill(0,0,verifiedCommandLength)
                    continue
                }
                // Transfer ownership at an idle block boundary, after the wake detector has
                // advanced. A pending wake candidate or continuation always keeps capture.
                if (event < 0 && !captured && !continuation && !diagnostic && !pendingActivation &&
                    wakeCandidate == null && pendingVerification == null && takeResearchReply()) {
                    captureExit = "research_ready"
                    return ByteArray(0)
                }
                if(event>=0 || pendingActivation) {
                    synchronized(lock) {
                        if(!current(generation))return null
                        if(continuation && sessionCancelled)return ByteArray(0)
                        val assistantActivation=pendingActivation;pendingActivation=false
                        if(electWake && !assistantActivation && verifiedPending?.election?.canActivate()!=true) {
                            datasetClips.find { it.decision == event }?.disposition = "unavailable"
                            verifiedPending?.close();pendingVerification=null
                            command.fill(0,0,verifiedCommandLength)
                            continue
                        }
                        if(!continuation)metrics.add("activations")
                        if (!diagnostic && !continuation && !assistantActivation)
                            datasetClips.find { it.decision == event }?.disposition = "activated"
                        if(!continuation && !assistantActivation && !diagnostic) {
                            val activatedAt=SystemClock.elapsedRealtime()
                            continuityActivation=conversationContinuity.activate(verifiedPending?.election?.grantedIdentity() ?: currentIdentity(),activatedAt)
                            conversationId=continuityActivation?.conversationId
                        }
                        val acceptedCandidate=if(assistantActivation)null else verifiedCandidate
                        val boundary=if(!continuation && !assistantActivation && acceptedCandidate==null)
                            HandsFreeActivationBoundary(detectorSamples-count,count,event) else null
                        val offset=acceptedCandidate?.blockOffset ?: boundary?.offset ?: if(assistantActivation)
                            (count-(readReturnedAtMs-pendingActivationAt).coerceAtLeast(0)*16).coerceIn(0,count.toLong()).toInt() else 0
                        if(!continuation) {
                            if(!assistantActivation){sessionCancelled=false;endReason=null;snapshotActivationScreen();transcriptionStatus="not_started"}
                            val token=if(assistantActivation)checkNotNull(sessionId) else UUID.randomUUID().toString()
                            val contextPcm=verifiedPending?.takeUnless{assistantActivation}?.context() ?: boundary?.context(ring) ?: ShortArray(minOf(ring.size,32000+count-offset)).also{ring.copyRangeTo(it,ring.size-it.size,it.size)}
                            try {
                                if(assistantActivation)collection.setTriggerContext(activeEvent,contextPcm)
                                else {
                                    val metadata=activationMetadata(if(diagnostic)"diagnostic" else "wake_word",event,offset,MicroWakeWordNative.activationScores(detector),token)
                                    if(diagnostic)metadata.put("groupId",recordingGroup)
                                    // Map the sample boundary to capture read return; buffering remains an explicit uncertainty.
                                    val observedAt=java.time.OffsetDateTime.now()
                                    val inferenceDelay=(HandsFreeTurnLatency.now()-readReturnedAtMs).coerceAtLeast(0)*1_000_000L
                                    val postSamples=acceptedCandidate?.let{streamSamples-it.decisionSample} ?: (count-offset).toLong()
                                    metadata.put("activatedAt",observedAt.minusNanos(inferenceDelay+postSamples*1_000_000_000L/16000).toString())
                                    acceptedCandidate?.let {
                                        metadata.put("instantaneousScore",it.scores.getOrNull(0)).put("decisionScore",it.scores.getOrNull(1))
                                            .put("verifierScore",it.verifierScore).put("verifierLookaheadSamples",if(wakeVerifier!=null)HandsFreeWakeCandidate.LOOKAHEAD_SAMPLES else 0)
                                    }
                                    metadata.put("timestampBasis","sample_boundary_mapped_from_capture_read_return")
                                    val id=collection.begin(metadata,contextPcm)
                                    if(diagnostic)captureEvent=id else {sessionId=token;activeEvent=id;service?.acceptedHandsFreeActivation(token);holdSessionPower(token)}
                                }
                            }finally{contextPcm.fill(0)}
                            if(diagnostic) {
                                val id=if(assistantActivation)activeEvent.also{activeEvent=null;sessionId=null} else captureEvent
                                if(assistantActivation)closePanel(token)
                                val post=if(acceptedCandidate==null)buffer.copyOfRange(offset,count) else command.copyOf(verifiedCommandLength)
                                try { collection.appendPost(id,post,0,post.size) }finally{post.fill(0)}
                                val postCount=acceptedCandidate?.let{(streamSamples-it.decisionSample).toInt()} ?: count-offset
                                diagnosticCaptures.add(DiagnosticCapture(id,(HandsFreeVad.NO_SPEECH_SAMPLES-postCount).coerceAtLeast(0),(3200-postCount).coerceAtLeast(0)))
                                captureEvent=null
                                verifiedPending?.close();if(verifiedPending!=null)pendingVerification=null
                                beep();metrics.event("diagnostic_activation");continue
                            }
                            captureEvent=activeEvent;contextMarginRemaining=(3200-(acceptedCandidate?.let{(streamSamples-it.decisionSample).toInt()} ?: count-offset)).coerceAtLeast(0)
                        }
                        // A continuation uses only its qualified onset; a wake command starts exactly
                        // at the native decision boundary, including the suffix in this same read.
                        val length=if(continuation) {
                            val start=checkNotNull(continuationGate.confirmedStartSample).toLong()
                            val retainedStart=streamSamples-ring.size
                            val local=(start-retainedStart).coerceIn(0,ring.size.toLong()).toInt()
                            ring.copyRangeTo(command,local,ring.size-local)
                        } else if(acceptedCandidate!=null) {
                            verifiedCommandLength
                        } else {
                            buffer.copyInto(command,0,offset,count);count-offset
                        }
                        verifiedPending?.close();if(verifiedPending!=null)pendingVerification=null
                        wakeCandidate=null
                        captured=true;commandStarted=now
                        HandsFreePanelSignal.capture(sessionId,buffer,count,now)
                        if(!continuation)collection.appendPost(captureEvent,command,0,length)
                        speechClassifier.reset()
                        val speechFloor = if (continuation || assistantActivation) null else ring.sustainedSpeechFloor(length)
                        commandCapture=HandsFreeCommandCapture(command,length,speechClassifier,waitForActivationCue=!continuation,
                            sustainedSpeechFloor=speechFloor)
                        commandCapture?.lastActiveEndSample?.let{last->
                            lastVadSample=last;lastVadFrameAtMs=readReturnedAtMs-(length-last)*1000L/16000
                        }
                        val activeTrace=HandsFreeTurnLatency().also{it.mark("wake_accepted")};trace=activeTrace
                        val session=checkNotNull(sessionId)
                        if(!continuation && sessionMediaFocus.acquire(session)==HandsFreeSessionFocusResult.LOST) {
                            cancelSession(session,"audio_focus_lost")
                            return ByteArray(0)
                        }
                        metrics.add("sessions_started");setState("capturing","Escutando pedido…");showSession()
                        if(!continuation) {
                            val cueRequestedAt=SystemClock.elapsedRealtime()
                            cueCompletion=beep()
                            if(electWake && !assistantActivation)verifiedOutcome?.let { outcome ->
                                val identity=metrics.identityToken()
                                metrics.timing("wake_primary_read_observation_estimate",candidateObservedAt-candidateDecisionEstimatedAt,identity)
                                metrics.timing("wake_lookahead_wait",verificationStartedAt-candidateObservedAt,identity)
                                outcome.verifiedAtMs?.let{verifiedAt->
                                    metrics.timing("wake_local_verification_wait",verifiedAt-verificationStartedAt,identity)
                                    outcome.finishedAtMs?.let{finishedAt->
                                        metrics.timing("wake_coordinator_wait",finishedAt-verifiedAt,identity)
                                        metrics.timing("wake_grant_observation_wait",grantObservedAt-finishedAt,identity)
                                        verifiedPending?.election?.let { election ->
                                            election.coordinatorTiming?.let { timing ->
                                                metrics.timing("wake_server_election_wait",timing.serverWaitMs,identity)
                                                metrics.timing("wake_coordinator_transport_overhead",timing.transportOverheadMs,identity)
                                                timing.authenticationMs?.let{metrics.timing("wake_server_authentication",it,identity)}
                                            }
                                            election.transportTiming?.let { timing ->
                                                timing.dnsMs?.let{metrics.timing("wake_network_dns",it,identity)}
                                                timing.connectMs?.let{metrics.timing("wake_network_connect_including_tls",it,identity)}
                                                timing.tlsMs?.let{metrics.timing("wake_network_tls",it,identity)}
                                                timing.requestToHeadersMs?.let{metrics.timing("wake_network_request_to_headers",it,identity)}
                                                timing.responseBodyMs?.let{metrics.timing("wake_network_response_body",it,identity)}
                                                timing.reused?.let{metrics.add(if(it)"wake_network_connection_reused" else "wake_network_connection_new")}
                                            }
                                            metrics.timing("wake_identity_before_claim",election.identityPreparationMs,identity)
                                            metrics.timing("wake_identity_response_recheck",election.identityResponseRecheckMs,identity)
                                            metrics.timing("wake_identity_activation_recheck",election.identityActivationRecheckMs,identity)
                                        }
                                    }
                                }
                                metrics.timing("wake_grant_observed_to_cue_request",cueRequestedAt-grantObservedAt,identity)
                                metrics.timing("wake_decision_to_cue_request_estimate",cueRequestedAt-candidateDecisionEstimatedAt,identity)
                            }
                        }
                        val activeBackend=backend?:newBackend(generation,session).also{backend=it}
                        // Trim room speech around the command before batch STT.
                        // Realtime STT cannot retract transmitted PCM.
                        realtime=if(speechFloor!=null)null else try{activeBackend.beginCapture(activeTrace){partial->
                            // The capture closure and trace gate delayed callbacks even in a continuation
                            // sharing the same session. UI-only text never enters collection or chat.
                            if(current(generation) && !sessionCancelled && sessionId==session && trace===activeTrace && !capturedSuccessfully)
                                main.post{if(current(generation) && !sessionCancelled && sessionId==session && trace===activeTrace && !capturedSuccessfully)
                                    NodeVoiceInteractionSession.current?.showPartial(session,activeTrace.turnId,partial)}
                        }.also{transcriptionStatus="requested";it.append(command,length)}}catch(_:Exception){null}
                    }
                }
            }
            return null
        }catch(error:Throwable) {
            captureExit=if(error is MicrophoneUnavailable)"microphone_unavailable" else "capture_failure"
            throw error
        }finally {
            collection.finishCapture(captureEvent)
            closePendingVerification()
            synchronized(lock) {
                for (clip in datasetClips) {
                    val awaitCommand = clip.disposition == "activated" && capturedSuccessfully && !sessionCancelled
                    if (awaitCommand) heldDatasetCommand = clip.epoch to clip.id
                    else if (clip.disposition == "activated") clip.commandDisposition =
                        if (sessionCancelled) "cancelled" else if (captureExit == "no_speech") "no_speech" else "unknown"
                    dataset.persist(clip, hold = awaitCommand)
                }
            }
            for(item in diagnosticCaptures) {
                collection.finishCapture(item.id)
                collection.finish(item.id,"interrupted",endReason?:captureExit,"not_started")
            }
            if(!capturedSuccessfully && captureExit != "research_ready"){
                realtime?.close();backend?.abortCapture()
                synchronized(lock){finishEvent(if(sessionCancelled)if(endReason=="explicit_cancel")"cancelled" else "interrupted" else if(captureExit=="no_speech" || captureExit=="continuation_timeout")"empty" else "failed",endReason?:captureExit)}
            }
            verificationExecutor?.shutdownNow()
            trace=null
            captureConfirmed=false;recorder=null
            if(Build.VERSION.SDK_INT>=29 && recordingCallbackRegistered && recordingCallback!=null)try{audio.unregisterAudioRecordingCallback(recordingCallback)}catch(_:Exception){}
            mediaFilter?.close()
            try{audio.stop()}catch(_:Exception){};audio.release()
            classifier?.close()
            buffer.fill(0);command.fill(0);ring.clear()
            if(recordingGroupId==recordingGroup)recordingGroupId=null
            if(batteryStudy && verificationExecutor!=null) {
                // Future.cancel does not prove that the native callback/finally ended.
                // Release the microphone first, then keep this worker alive until every
                // verifier CPU update is complete. Explicit finish refuses a live worker.
                var interrupted=false
                while(true) {
                    try{if(verificationExecutor.awaitTermination(60,java.util.concurrent.TimeUnit.SECONDS))break}
                    catch(_:InterruptedException){interrupted=true}
                }
                if(interrupted)Thread.currentThread().interrupt()
            }
        }
    }
}
