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
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.os.SystemClock
import android.service.voice.VoiceInteractionService
import org.json.JSONObject
import java.util.UUID
import java.util.concurrent.Executors

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
        runtime.collection.configure(args.getBoolean("enabled"),args.getInt("limitMiB"))
        return runtime.state().toString()
    }
    fun deleteCollection(context:Context):String {
        val runtime=runtime(context);runtime.collection.deleteAll();return runtime.state().toString()
    }
    internal fun permission(context:Context)=context.checkSelfPermission(Manifest.permission.RECORD_AUDIO)==PackageManager.PERMISSION_GRANTED
    internal fun selected(context:Context)=VoiceInteractionService.isActiveService(context,ComponentName(context,NodeVoiceInteractionService::class.java))
}

internal class HandsFreeRuntime(private val context:Context) {
    private val prefs=context.getSharedPreferences("aegis-hands-free-v1",Context.MODE_PRIVATE)
    internal val metrics=HandsFreeMetrics(context)
    internal val collection=HandsFreeActivationCollection(context)
    private val main=Handler(Looper.getMainLooper())
    private val cleanup=Executors.newSingleThreadExecutor{r->Thread(r,"AegisHandsFreeCancel").apply{isDaemon=true}}
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
    @Volatile internal var screenLocked=false
    @Volatile internal var screenInteractive=true
    private var continuationDeadline=0L
    private val ownAudioSessions=java.util.LinkedHashSet<Int>()
    internal fun isOwnAudioSession(id:Int):Boolean=synchronized(lock){ownAudioSessions.contains(id)}
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
    private var capturedTurnLatency:HandsFreeTurnLatency?=null
    private fun newBackend(generation:Int,session:String):NodeHandsFreeBackend {
        val identity=metrics.identityToken()
        val sessionIdentity=continuityActivation?.identity
        return NodeHandsFreeBackend(readSession={
            // Pin the authority as well as the conversation at activation. A vault change
            // between wake acceptance and beginCapture cannot send an old chat to a new Node.
            NodeNativeSession.read(context)?.takeIf{sessionIdentity!=null && NodeNativeSession.fingerprint(it)==sessionIdentity}
        },onStage={stage,duration->
            if(stage in setOf("stt","chat","tts_fetch","tts_first_audio","tts_total","tts_playback"))metrics.timing(stage,duration,identity)
            if(stage in setOf("transcribed","empty","failed"))metrics.addForIdentity("commands_${stage}",1,identity)
            if(current(generation) && sessionId==session)when(stage) {
                "transcription_started"->transcriptionStatus="in_progress"
                "transcription_final"->transcriptionStatus="final"
                "empty"->transcriptionStatus="empty"
            }
            if(stage=="tts_first_audio")synchronized(lock) {
                if(current(generation) && !sessionCancelled && sessionId==session)
                    setState("speaking","Aegis respondendo…")
            }
        })
    }
    private fun cancelBackend(){val active=synchronized(lock){val value=backend;backend=null;value};if(active!=null){active.requestCancellation();cleanup.execute{active.cancel()}}}
    private val playback=HandsFreePlayback(context)
    private val activationCue=HandsFreeActivationCue{metrics.add("beep_failures")}
    // Retain the registration for this process singleton's lifetime; no identity is persisted.
    private val identityObserver=NodeNativeSession.observeIdentity(context){identityChanged()}
    private fun currentIdentity()=NodeNativeSession.read(context)?.let{NodeNativeSession.fingerprint(it)}
    private fun identityChanged()=synchronized(lock) {
        val identity=currentIdentity()
        val active=continuityActivation
        conversationContinuity.identityChanged(identity)
        if(active!=null && active.identity!=identity) {
            continuityActivation=null;pendingActivation=false;sessionCancelled=true
            playback.cancel();activationCue.cancel();cancelBackend();closePanel(sessionId,true)
            metrics.event("session_identity_changed")
        }
    }
    private fun endConversationSession(token:String?,close:Boolean=true)=synchronized(lock) {
        if(sessionId!=token)return@synchronized
        conversationContinuity.end(continuityActivation);continuityActivation=null
        conversationId=null;sessionId=null;backend=null;if(close)closePanel(token)
    }
    init {
        modelReady=try{HandsFreeModel.read(context)!=null}catch(_:Throwable){false}
        metrics.restoreRequested(enabled,diagnostic)
    }
    internal fun attach(value:NodeVoiceInteractionService) {service=value;metrics.event("assistant_ready");recoverFromSystem()}
    internal fun detach(value:NodeVoiceInteractionService) {
        if(service!==value)return
        service=null;stop("assistant_unavailable","Selecione a Aegis como assistente e retome o hands-free.");metrics.event("assistant_shutdown")
    }
    private fun recordIdentity() {
        val role=NodeHandsFree.selected(context);val permission=NodeHandsFree.permission(context)
        val info=context.packageManager.getPackageInfo(context.packageName,0)
        metrics.identity(info.versionName?:"unknown",if(Build.VERSION.SDK_INT>=28)info.longVersionCode.toString() else @Suppress("DEPRECATION") info.versionCode.toString(),modelReady,role,permission)
    }
    fun state():JSONObject {
        val role=NodeHandsFree.selected(context);val permission=NodeHandsFree.permission(context)
        val info=context.packageManager.getPackageInfo(context.packageName,0)
        metrics.identity(info.versionName?:"unknown",if(Build.VERSION.SDK_INT>=28)info.longVersionCode.toString() else @Suppress("DEPRECATION") info.versionCode.toString(),modelReady,role,permission)
        return JSONObject().put("enabled",enabled).put("diagnostic",diagnostic).put("assistantSelected",role).put("permissionGranted",permission)
            .put("modelReady",modelReady).put("status",status).put("message",message).put("captureConfirmed",captureConfirmed && SystemClock.elapsedRealtime()-lastAudioReadMs<=1500 && status in setOf("listening","capturing","continuation"))
            .put("modelVersion","aegis-mww-fleurs-dev-2026-10-31")
            .put("collection",collection.state())
    }
    fun configure(value:Boolean,diagnosticValue:Boolean):JSONObject {
        val changed=enabled!=value || diagnostic!=diagnosticValue
        if(!changed){if(value)recoverFromSystem();return state()}
        enabled=value;diagnostic=diagnosticValue;check(prefs.edit().putBoolean("enabled",value).putBoolean("diagnostic",diagnosticValue).commit())
        metrics.configure(value,diagnosticValue);failures=0;retryNotBefore=0;retriesExhausted=false
        if(!value)stop("disabled","Hands-free desligado.") else {stop("starting","Preparando escuta local.");reconcile()}
        return state()
    }
    internal fun imported(){modelReady=true;metrics.event("model_imported");failures=0;retryNotBefore=0;retriesExhausted=false;stop("starting","Preparando escuta local.");reconcile()}
    internal fun importFailed(){metrics.event("model_import_failed");if(!modelReady)setState("model_invalid","Modelo ausente, corrompido ou incompatível. Importe o arquivo privado selecionado na PoC.")}
    internal fun recoverFromSystem(){failures=0;retryNotBefore=0;retriesExhausted=false;reconcile()}
    internal fun reconcile() {
        recordIdentity()
        if(!enabled){stop("disabled","Hands-free desligado.");return}
        if(!NodeHandsFree.selected(context)){stop("assistant_unavailable","Selecione a Aegis como assistente padrão.");return}
        if(!NodeHandsFree.permission(context)){stop("permission_missing","Permita o microfone para habilitar a escuta.");return}
        if(!modelReady){stop("model_missing","Importe o modelo privado Aegis selecionado na PoC.");return}
        if(manualLease!=null){setState("manual_audio","Áudio em uso pelo chat.");return}
        val host=service?:run{setState("assistant_unavailable","Aguardando o serviço de assistente do Android. Se necessário, selecione novamente a Aegis.");return}
        synchronized(lock) {
            if(worker?.isAlive==true || retriesExhausted || SystemClock.elapsedRealtime()<retryNotBefore)return
            if(!host.ensureForeground()){setState("service_unavailable","O Android não permitiu iniciar a escuta. Abra a Aegis e retome o hands-free.");return}
            val generation=epochCounter.incrementAndGet()
            worker=Thread({run(generation)},"AegisHandsFreeAudio").apply{isDaemon=true;start()}
        }
    }
    private fun current(generation:Int)=epoch==generation && enabled && service!=null && manualLease==null
    private fun setState(value:String,text:String) {
        status=value;message=text;metrics.state(value)
        val generation=epoch
        main.post{if(epoch==generation && status==value && message==text){NodeVoiceInteractionSession.current?.refresh(value,text);service?.updateNotification(text)}}
    }
    private fun stop(value:String,text:String) {
        val stoppedEpoch=synchronized(lock) {
            val captureActive=status=="capturing"
            epochCounter.incrementAndGet();captureConfirmed=false;pendingActivation=false;cancelledAt=HandsFreeTurnLatency.now();sessionCancelled=true;playback.cancel();activationCue.cancel()
            endReason=value;if(!captureActive)finishEvent("interrupted",value)
            releaseSessionPower(sessionPowerToken)
            try{recorder?.stop()}catch(_:Exception){}
            cancelBackend()
            conversationContinuity.invalidate();continuityActivation=null
            conversationId=null;sessionId=null;setState(value,text)
            epoch
        }
        main.post{if(epoch==stoppedEpoch){service?.stopHandsFreeForeground();NodeVoiceInteractionSession.current?.finishQuietly()}}
    }
    fun manualAcquire(lease:String,kind:String):Boolean {
        synchronized(lock) {
            if(manualLease!=null || pendingActivation || sessionId!=null || status in setOf("capturing","processing","speaking","continuation"))return false
            manualLease=lease;manualAudioKind=kind;metrics.event("manual_audio_acquired",JSONObject().put("kind",if(kind=="playback")"playback" else "capture"))
            epochCounter.incrementAndGet();try{recorder?.stop()}catch(_:Exception){}
        }
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
    fun cancelSession(expectedSession:String?=null) {
        synchronized(lock) {
            if(expectedSession!=null && expectedSession!=sessionId)return
            if(sessionId==null && !pendingActivation){if(diagnostic){cancelledAt=HandsFreeTurnLatency.now();diagnosticCancelEpoch.incrementAndGet()};return}
            val captureActive=status=="capturing"
            pendingActivation=false;cancelledAt=HandsFreeTurnLatency.now();sessionCancelled=true;endReason="explicit_cancel";playback.cancel();activationCue.cancel();cancelBackend();metrics.add("sessions_cancelled")
            // Capture owns its last in-flight read and FIFO finalization. A pending manual
            // activation has no capture yet and must persist its empty WAVs immediately.
            if(!captureActive)finishEvent("cancelled","explicit_cancel")
            conversationContinuity.end(continuityActivation);continuityActivation=null
            val token=sessionId;sessionId=null;conversationId=null;closePanel(token,true)
        }
    }
    internal fun activateFromAssistant():String? = synchronized(lock) {
        if(!enabled || manualLease!=null || service==null)return null
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
        return JSONObject().put("activatedAt",java.time.OffsetDateTime.now().toString())
            .put("appVersion",info.versionName?:JSONObject.NULL)
            .put("build",if(Build.VERSION.SDK_INT>=28)info.longVersionCode else @Suppress("DEPRECATION") info.versionCode.toLong())
            .put("modelVersion","aegis-mww-fleurs-dev-2026-10-31").put("modelSha256",HandsFreeModel.SHA256)
            .put("detectorConfig",HandsFreeMetrics.detectorConfiguration())
            .put("instantaneousScore",score(0)).put("decisionScore",score(1))
            .put("detectorSample",position?:JSONObject.NULL).put("activationOffsetInBlock",offset?:JSONObject.NULL)
            .put("positionResolutionSamples",if(position!=null)160 else JSONObject.NULL)
            .put("origin",origin).put("diagnosticMode",diagnostic)
            .put("initialScreenInteractive",initialInteractive).put("initialDeviceLocked",initialLocked)
            .put("sessionId",token).put("groupId",if(diagnostic)recordingGroupId?:token else token).put("humanLabel",JSONObject.NULL)
    }
    private fun finishEvent(finalState:String,reason:String?) {
        val id=activeEvent;activeEvent=null
        val transcription=when {
            transcriptionStatus=="in_progress" && finalState=="failed"->"failed"
            transcriptionStatus in setOf("requested","in_progress")->if(finalState=="cancelled")"cancelled" else "aborted_before_final"
            else->transcriptionStatus
        }
        collection.finish(id,finalState,reason,transcription)
    }
    private fun closePanel(token:String?,immediate:Boolean=false){if(token!=null){releaseSessionPower(token);main.post{NodeVoiceInteractionSession.current?.finishIfSession(token,immediate)}}}
    private fun beep(){activationCue.play()}

    private class MicrophoneUnavailable:Exception()
    private fun microphoneBlocked():Boolean {
        val manager=context.getSystemService(AudioManager::class.java)
        if(manager.mode!=AudioManager.MODE_NORMAL || manager.isMicrophoneMute)return true
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
            synchronized(lock){ownAudioSessions.add(it.audioSessionId);while(ownAudioSessions.size>8)ownAudioSessions.remove(ownAudioSessions.first())}
        }
    }
    private fun run(generation:Int) {
        var detector=0L
        try {
            val bytes=HandsFreeModel.read(context)?:error("model_missing")
            detector=MicroWakeWordNative.create(bytes);bytes.fill(0);check(detector!=0L){"model_runtime_incompatible"}
            var continuation=false
            while(current(generation)) {
                val command=capture(generation,detector,continuation)?:break
                if(!current(generation)){command.fill(0);break}
                if(command.isEmpty()){synchronized(lock){finishEvent(if(sessionCancelled)"cancelled" else "empty",endReason?:if(continuation)"continuation_timeout" else "no_speech")};continuation=false;endConversationSession(sessionId);if(!MicroWakeWordNative.reset(detector))error("detector_reset");continue}
                if(sessionCancelled){command.fill(0);synchronized(lock){finishEvent("cancelled",endReason?:"operation_cancelled")};continuation=false;endConversationSession(sessionId);if(!MicroWakeWordNative.reset(detector))error("detector_reset");continue}
                val session=sessionId?:UUID.randomUUID().toString().also{sessionId=it}
                val turnIdentity=metrics.identityToken()
                val trace=capturedTurnLatency
                var turnOutcome="failed"
                try {
                    val active=synchronized(lock){
                        check(current(generation) && !sessionCancelled)
                        setState("processing","Processando pedido…")
                        backend?:newBackend(generation,session).also{backend=it}
                    }
                    var playbackCompletedAt:Long?=null
                    val sink=playback.openStream({current(generation) && !sessionCancelled && sessionId==session},{event->
                        trace?.mark(event)
                        if(event=="player_drained")playbackCompletedAt=SystemClock.elapsedRealtime()
                    })
                    val turn=active.execute(command,session,conversationId,sink,trace,incremental=true)
                    command.fill(0)
                    synchronized(lock) {
                        if(!current(generation) || sessionCancelled || sessionId!=session)throw java.util.concurrent.CancellationException()
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
                        turnOutcome=if(turn.playedAudio)"played" else "empty"
                        if(!continuation){finishEvent(turnOutcome,if(turn.playedAudio)"session_completed" else "empty_command");endConversationSession(session)}
                    }
                }catch(error:Exception){
                    val cancelled=!current(generation) || sessionCancelled || error is java.util.concurrent.CancellationException
                    turnOutcome=if(cancelled)"cancelled" else "failed"
                    synchronized(lock){finishEvent(turnOutcome,endReason?:if(cancelled)"operation_cancelled" else if(transcriptionStatus=="in_progress")"transcription_failed" else "session_processing_failed")}
                    metrics.addForIdentity(if(cancelled)"turns_cancelled" else "sessions_failed",1,turnIdentity);continuation=false;cancelBackend();endConversationSession(session,false)
                    if(current(generation) && !cancelled)setState("session_failed","Não foi possível concluir o pedido. Diga Aegis para tentar novamente.")
                    closePanel(session)
                }finally {
                    command.fill(0)
                    trace?.let{it.mark("turn_ended");metrics.turnLatency(it,turnOutcome,turnIdentity)}
                    capturedTurnLatency=null
                }
                if(!MicroWakeWordNative.reset(detector))error("detector_reset")
            }
        }catch(error:Throwable) {
            if(current(generation)) {
                synchronized(lock){finishEvent("failed",if(error is MicrophoneUnavailable)"microphone_unavailable" else if(detector==0L)"model_runtime_unavailable" else "capture_or_detector_failure")}
                metrics.add("capture_failures");main.post{if(epoch==generation)service?.stopHandsFreeForeground()}
                if(detector==0L){modelReady=false;setState("model_incompatible","Falha de integridade ou compatibilidade do detector. Importe novamente o modelo privado.")}
                else {setState("microphone_unavailable","Escuta indisponível: confira o microfone, chamadas, permissões e o assistente padrão.")
                    if(++failures<=3) {
                        val delay=longArrayOf(5000,30000,120000)[failures-1];retryNotBefore=SystemClock.elapsedRealtime()+delay
                        main.postDelayed({if(epoch==generation && status=="microphone_unavailable")reconcile()},delay)
                    }else retriesExhausted=true}

            }
        }finally {
            recorder=null
            val token=synchronized(lock){val token=sessionId;finishEvent(if(sessionCancelled)"cancelled" else "interrupted",endReason?:"audio_worker_ended");cancelBackend();endConversationSession(token,false);token}
            closePanel(token)
            if(detector!=0L)MicroWakeWordNative.destroy(detector)
            synchronized(lock){if(worker===Thread.currentThread())worker=null}
            metrics.persist()
            // A configuration change may arrive during the previous capture thread's teardown.
            if(epoch!=generation && enabled && manualLease==null && service!=null)main.post{reconcile()}
        }
    }

    private fun capture(generation:Int,detector:Long,continuation:Boolean):ByteArray? {
        capturedTurnLatency=null
        val audio=openRecorder();recorder=audio
        val buffer=ShortArray(3200) // One 200ms microphone read, shared by all local consumers.
        val ring=HandsFreeRing()
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
        var lastVadSample:Int?=null
        var lastVadFrameAtMs:Long?=null
        var captureEvent:String?=null
        var contextMarginRemaining=0
        // Diagnostic mode retains the detector's continuous stream and never starts STT.
        // Its post window uses the existing 3s no-speech bound; overlapping events share reads.
        data class DiagnosticCapture(val id:String?,var remaining:Int,var margin:Int)
        val diagnosticCaptures=mutableListOf<DiagnosticCapture>()
        var diagnosticCancelGeneration=diagnosticCancelEpoch.get()
        var captureExit="capture_interrupted"
        var receivedAt=SystemClock.elapsedRealtime();var checkedAt=receivedAt
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
                setState(if(continuation)"continuation" else "listening",if(continuation)"Pode continuar…" else if(diagnostic)"Diagnóstico: detecção local sem comandos." else "Aguardando Aegis.")
                if(continuation)showSession()
            }
            while(current(generation)) {
                val count=audio.read(buffer,0,buffer.size,AudioRecord.READ_BLOCKING)
                val readReturnedAtMs=HandsFreeTurnLatency.now()
                if(!current(generation)){retainCancellationPrefix(count,readReturnedAtMs);return null}
                val now=SystemClock.elapsedRealtime()
                if(count<=0)throw MicrophoneUnavailable()
                if(captured || continuation)HandsFreePanelSignal.capture(sessionId,buffer,count,now)
                if(silenced.get())throw MicrophoneUnavailable()
                captureConfirmed=true;lastAudioReadMs=now
                if(now-checkedAt>=5000) {
                    checkedAt=now
                    if(!NodeHandsFree.permission(context) || !NodeHandsFree.selected(context) || microphoneBlocked())throw MicrophoneUnavailable()
                }
                val duration=count*1000L/16000
                metrics.capture(duration,minOf(duration,(now-receivedAt).coerceAtLeast(0)),!captured && !continuation,screenLocked,screenInteractive)
                if(now-receivedAt>duration+1000)metrics.add("captureUnknownGapMs",now-receivedAt-duration)
                receivedAt=now;metrics.maybeTick()
                if(sessionCancelled && (captured || continuation)) {
                    retainCancellationPrefix(count,readReturnedAtMs)
                    return ByteArray(0)
                }
                val pendingDiagnostics=diagnosticCaptures.iterator()
                val cancellationGeneration=diagnosticCancelEpoch.get()
                val diagnosticCancelled=diagnosticCancelGeneration!=cancellationGeneration
                diagnosticCancelGeneration=cancellationGeneration
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
                    val ended=commandState.append(buffer,count)
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
                        val wav=handsFreeWav(command,commandState.length)
                        activeTrace.mark("wav_ready");capturedTurnLatency=activeTrace;capturedSuccessfully=true
                        return wav
                    }
                    continue
                }
                streamSamples+=count
                ring.append(buffer,count)
                val event=if(continuation) {
                    val beforeDeadline=(count-((now-continuationDeadline).coerceAtLeast(0)*16)).coerceIn(0,count.toLong()).toInt()
                    when(continuationGate.process(buffer,count,beforeDeadline,now>=continuationDeadline)) {
                        HandsFreeContinuationGate.Decision.STARTED->1L
                        HandsFreeContinuationGate.Decision.WAIT->-1L
                        HandsFreeContinuationGate.Decision.TIMED_OUT->{captureExit="continuation_timeout";metrics.add("continuation_no_speech");return ByteArray(0)}
                    }
                } else {
                    val result=MicroWakeWordNative.process(detector,buffer,count)
                    detectorSamples+=count;result
                }
                if(event == -2L)error("detector_inference")
                if(event>=0 || pendingActivation) {
                    synchronized(lock) {
                        if(!current(generation))return null
                        if(continuation && sessionCancelled)return ByteArray(0)
                        val assistantActivation=pendingActivation;pendingActivation=false
                        if(!continuation)metrics.add("activations")
                        if(!continuation && !assistantActivation && !diagnostic) {
                            val activatedAt=SystemClock.elapsedRealtime()
                            continuityActivation=conversationContinuity.activate(currentIdentity(),activatedAt)
                            conversationId=continuityActivation?.conversationId
                        }
                        val boundary=if(!continuation && !assistantActivation)
                            HandsFreeActivationBoundary(detectorSamples-count,count,event) else null
                        val offset=boundary?.offset ?: if(assistantActivation)
                            (count-(readReturnedAtMs-pendingActivationAt).coerceAtLeast(0)*16).coerceIn(0,count.toLong()).toInt() else 0
                        if(!continuation) {
                            if(!assistantActivation){sessionCancelled=false;endReason=null;snapshotActivationScreen();transcriptionStatus="not_started"}
                            val token=if(assistantActivation)checkNotNull(sessionId) else UUID.randomUUID().toString()
                            val contextPcm=boundary?.context(ring) ?: ShortArray(minOf(ring.size,32000+count-offset)).also{ring.copyRangeTo(it,ring.size-it.size,it.size)}
                            try {
                                if(assistantActivation)collection.setTriggerContext(activeEvent,contextPcm)
                                else {
                                    val metadata=activationMetadata(if(diagnostic)"diagnostic" else "wake_word",event,offset,MicroWakeWordNative.activationScores(detector),token)
                                    if(diagnostic)metadata.put("groupId",recordingGroup)
                                    // Map the sample boundary to capture read return; buffering remains an explicit uncertainty.
                                    val observedAt=java.time.OffsetDateTime.now()
                                    val inferenceDelay=(HandsFreeTurnLatency.now()-readReturnedAtMs).coerceAtLeast(0)*1_000_000L
                                    metadata.put("activatedAt",observedAt.minusNanos(inferenceDelay+(count-offset)*1_000_000_000L/16000).toString())
                                    metadata.put("timestampBasis","sample_boundary_mapped_from_capture_read_return")
                                    val id=collection.begin(metadata,contextPcm)
                                    if(diagnostic)captureEvent=id else {sessionId=token;activeEvent=id;service?.acceptedHandsFreeActivation(token);holdSessionPower(token)}
                                }
                            }finally{contextPcm.fill(0)}
                            if(diagnostic) {
                                val id=if(assistantActivation)activeEvent.also{activeEvent=null;sessionId=null} else captureEvent
                                if(assistantActivation)closePanel(token)
                                collection.appendPost(id,buffer,offset,count-offset)
                                diagnosticCaptures.add(DiagnosticCapture(id,HandsFreeVad.NO_SPEECH_SAMPLES-(count-offset),3200-(count-offset)))
                                captureEvent=null;beep();metrics.event("diagnostic_activation");continue
                            }
                            captureEvent=activeEvent;contextMarginRemaining=3200-(count-offset)
                        }
                        // A continuation uses only its qualified onset; a wake command starts exactly
                        // at the native decision boundary, including the suffix in this same read.
                        val length=if(continuation) {
                            val start=checkNotNull(continuationGate.confirmedStartSample).toLong()
                            val retainedStart=streamSamples-ring.size
                            val local=(start-retainedStart).coerceIn(0,ring.size.toLong()).toInt()
                            ring.copyRangeTo(command,local,ring.size-local)
                        } else {
                            buffer.copyInto(command,0,offset,count);count-offset
                        }
                        captured=true;commandStarted=now
                        HandsFreePanelSignal.capture(sessionId,buffer,count,now)
                        if(!continuation)collection.appendPost(captureEvent,command,0,length)
                        speechClassifier.reset();commandCapture=HandsFreeCommandCapture(command,length,speechClassifier)
                        commandCapture?.lastActiveEndSample?.let{last->
                            lastVadSample=last;lastVadFrameAtMs=readReturnedAtMs-(length-last)*1000L/16000
                        }
                        val activeTrace=HandsFreeTurnLatency().also{it.mark("wake_accepted")};trace=activeTrace
                        val session=checkNotNull(sessionId)
                        val activeBackend=backend?:newBackend(generation,session).also{backend=it}
                        realtime=try{activeBackend.beginCapture(activeTrace){partial->
                            // The capture closure and trace gate delayed callbacks even in a continuation
                            // sharing the same session. UI-only text never enters collection or chat.
                            if(current(generation) && !sessionCancelled && sessionId==session && trace===activeTrace && !capturedSuccessfully)
                                main.post{if(current(generation) && !sessionCancelled && sessionId==session && trace===activeTrace && !capturedSuccessfully)
                                    NodeVoiceInteractionSession.current?.showPartial(session,activeTrace.turnId,partial)}
                        }.also{transcriptionStatus="requested";it.append(command,length)}}catch(_:Exception){null}
                        metrics.add("sessions_started");setState("capturing","Escutando pedido…");showSession();if(!continuation)beep()
                    }
                }
            }
            return null
        }catch(error:Throwable) {
            captureExit=if(error is MicrophoneUnavailable)"microphone_unavailable" else "capture_failure"
            throw error
        }finally {
            collection.finishCapture(captureEvent)
            for(item in diagnosticCaptures) {
                collection.finishCapture(item.id)
                collection.finish(item.id,"interrupted",endReason?:captureExit,"not_started")
            }
            if(!capturedSuccessfully){
                realtime?.close();backend?.abortCapture()
                synchronized(lock){finishEvent(if(sessionCancelled)if(endReason=="explicit_cancel")"cancelled" else "interrupted" else if(captureExit=="no_speech" || captureExit=="continuation_timeout")"empty" else "failed",endReason?:captureExit)}
            }
            trace=null
            captureConfirmed=false;recorder=null
            if(Build.VERSION.SDK_INT>=29 && recordingCallbackRegistered && recordingCallback!=null)try{audio.unregisterAudioRecordingCallback(recordingCallback)}catch(_:Exception){}
            try{audio.stop()}catch(_:Exception){};audio.release()
            classifier?.close()
            buffer.fill(0);command.fill(0);ring.clear()
            if(recordingGroupId==recordingGroup)recordingGroupId=null
        }
    }
}
