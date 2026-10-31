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
    internal fun permission(context:Context)=context.checkSelfPermission(Manifest.permission.RECORD_AUDIO)==PackageManager.PERMISSION_GRANTED
    internal fun selected(context:Context)=VoiceInteractionService.isActiveService(context,ComponentName(context,NodeVoiceInteractionService::class.java))
}

internal class HandsFreeRuntime(private val context:Context) {
    private val prefs=context.getSharedPreferences("aegis-hands-free-v1",Context.MODE_PRIVATE)
    internal val metrics=HandsFreeMetrics(context)
    private val main=Handler(Looper.getMainLooper())
    private val cleanup=Executors.newSingleThreadExecutor{r->Thread(r,"AegisHandsFreeCancel").apply{isDaemon=true}}
    private val lock=Any()
    @Volatile private var service:NodeVoiceInteractionService?=null
    @Volatile private var worker:Thread?=null
    @Volatile private var recorder:AudioRecord?=null
    private val epochCounter=java.util.concurrent.atomic.AtomicInteger(0)
    private val epoch:Int get()=epochCounter.get()
    @Volatile private var manualLease:String?=null
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
    @Volatile internal var screenLocked=false
    @Volatile internal var screenInteractive=true
    private var continuationDeadline=0L
    private val ownAudioSessions=java.util.LinkedHashSet<Int>()
    internal fun isOwnAudioSession(id:Int):Boolean=synchronized(lock){ownAudioSessions.contains(id)}
    private var failures=0
    @Volatile private var retryNotBefore=0L
    @Volatile private var retriesExhausted=false
    private var conversationId:String?=null
    @Volatile private var sessionId:String?=null
    @Volatile private var backend:NodeHandsFreeBackend?=null
    private var capturedTurnLatency:HandsFreeTurnLatency?=null
    private fun newBackend(generation:Int,session:String):NodeHandsFreeBackend {
        val identity=metrics.identityToken()
        return NodeHandsFreeBackend(context){stage,duration->
            if(stage in setOf("stt","chat","tts_fetch","tts_first_audio","tts_total","tts_playback"))metrics.timing(stage,duration,identity)
            if(stage in setOf("transcribed","empty","failed"))metrics.addForIdentity("commands_${stage}",1,identity)
            if(stage=="tts_first_audio")synchronized(lock) {
                if(current(generation) && !sessionCancelled && sessionId==session)
                    setState("speaking","Aegis respondendo…")
            }
        }
    }
    private fun cancelBackend(){val active=synchronized(lock){val value=backend;backend=null;value};if(active!=null){active.requestCancellation();cleanup.execute{active.cancel()}}}
    private val playback=HandsFreePlayback(context)
    private val activationCue=HandsFreeActivationCue{metrics.add("beep_failures")}
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
            epochCounter.incrementAndGet();captureConfirmed=false;pendingActivation=false;sessionCancelled=true;playback.cancel();activationCue.cancel()
            try{recorder?.stop()}catch(_:Exception){}
            cancelBackend()
            conversationId=null;sessionId=null;setState(value,text)
            epoch
        }
        main.post{if(epoch==stoppedEpoch){service?.stopHandsFreeForeground();NodeVoiceInteractionSession.current?.finishQuietly()}}
    }
    fun manualAcquire(lease:String,kind:String):Boolean {
        synchronized(lock) {
            if(manualLease!=null || status in setOf("capturing","processing","speaking","continuation"))return false
            manualLease=lease;metrics.event("manual_audio_acquired",JSONObject().put("kind",if(kind=="playback")"playback" else "capture"))
            epochCounter.incrementAndGet();try{recorder?.stop()}catch(_:Exception){}
        }
        val active=worker
        if(active!=null && active!==Thread.currentThread())active.join(1500)
        if(active?.isAlive==true){manualLease=null;metrics.event("manual_audio_acquire_timeout");return false}
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
        synchronized(lock){if(manualLease!=lease)return;manualLease=null;leaseTimeout?.let{main.removeCallbacks(it)};leaseTimeout=null;metrics.event("manual_audio_released")}
        reconcile()
    }
    internal fun releaseActivityAudio(){manualLease?.let{manualRelease(it)}}
    fun cancelSession(expectedSession:String?=null) {
        synchronized(lock) {
            if(expectedSession!=null && expectedSession!=sessionId)return
            pendingActivation=false;sessionCancelled=true;playback.cancel();activationCue.cancel();cancelBackend();metrics.add("sessions_cancelled")
        }
    }
    internal fun activateFromAssistant():String? = synchronized(lock) {
        if(status=="listening") {
            val token=UUID.randomUUID().toString();sessionId=token;sessionCancelled=false;pendingActivation=true;return token
        }
        if(!enabled)setState("disabled","Habilite o hands-free nas configurações da Aegis.")
        return null
    }
    private fun showSession() {
        val token=sessionId?:return
        val generation=epoch
        main.post{if(epoch==generation && manualLease==null && sessionId==token && !sessionCancelled)try{service?.showHandsFreeSession(token)}catch(_:Exception){metrics.add("presentation_failures")}}
    }
    private fun closePanel(token:String?){if(token!=null)main.post{NodeVoiceInteractionSession.current?.finishIfSession(token)}}
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
                if(command.isEmpty()){val token=sessionId;continuation=false;conversationId=null;sessionId=null;backend=null;closePanel(token);if(!MicroWakeWordNative.reset(detector))error("detector_reset");continue}
                val session=sessionId?:UUID.randomUUID().toString().also{sessionId=it}
                if(sessionCancelled){command.fill(0);val token=sessionId;continuation=false;conversationId=null;sessionId=null;backend=null;closePanel(token);if(!MicroWakeWordNative.reset(detector))error("detector_reset");continue}
                val wake=context.getSystemService(PowerManager::class.java).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK,"Aegis:hands-free-session")
                val turnIdentity=metrics.identityToken()
                val trace=capturedTurnLatency
                var turnOutcome="failed"
                val heldAt=SystemClock.elapsedRealtime()
                try {
                    wake.acquire(600000)
                    val active=synchronized(lock){
                        check(current(generation) && !sessionCancelled)
                        setState("processing","Processando pedido…")
                        backend?:newBackend(generation,session).also{backend=it}
                    }
                    val sink=playback.openStream({current(generation) && !sessionCancelled && sessionId==session},{event->trace?.mark(event)})
                    val turn=active.execute(command,session,conversationId,sink,trace)
                    command.fill(0)
                    if(!current(generation) || sessionCancelled)throw java.util.concurrent.CancellationException()
                    conversationId=turn.conversationId
                    // execute returns only after the sink has drained the actual playback head.
                    continuation=turn.continueListening && !sessionCancelled && turn.playedAudio
                    continuationDeadline=SystemClock.elapsedRealtime()+2000
                    turnOutcome=if(turn.playedAudio)"played" else "empty"
                    if(!continuation){conversationId=null;sessionId=null;backend=null;closePanel(session)}
                }catch(error:Exception){
                    val cancelled=!current(generation) || sessionCancelled || error is java.util.concurrent.CancellationException
                    turnOutcome=if(cancelled)"cancelled" else "failed"
                    metrics.addForIdentity(if(cancelled)"turns_cancelled" else "sessions_failed",1,turnIdentity);continuation=false;conversationId=null;sessionId=null;cancelBackend()
                    if(current(generation) && !cancelled)setState("session_failed","Não foi possível concluir o pedido. Diga Aegis para tentar novamente.")
                    closePanel(session)
                }finally {
                    command.fill(0)
                    if(wake.isHeld)wake.release()
                    metrics.addForIdentity("sessionWakeLockMs",minOf(600000,SystemClock.elapsedRealtime()-heldAt),turnIdentity)
                    trace?.let{it.mark("turn_ended");metrics.turnLatency(it,turnOutcome,turnIdentity)}
                    capturedTurnLatency=null
                }
                if(!MicroWakeWordNative.reset(detector))error("detector_reset")
            }
        }catch(_:Throwable) {
            if(current(generation)) {
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
        val buffer=ShortArray(3200) // 200ms blocking capture, not 10ms Kotlin timers.
        val ring=HandsFreeRing()
        val command=ShortArray(360000) // 20s command plus 2.5s pre-roll, bounded.
        var commandStarted=0L;var captured=false
        var classifier:NativeHandsFreeSpeechClassifier?=null
        var commandCapture:HandsFreeCommandCapture?=null
        var lastVadSample:Int?=null
        var lastVadFrameAtMs:Long?=null
        var receivedAt=SystemClock.elapsedRealtime();var checkedAt=receivedAt
        var recordingCallback:AudioManager.AudioRecordingCallback?=null
        val silenced=java.util.concurrent.atomic.AtomicBoolean(false)
        var recordingCallbackRegistered=false
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
                check(NodeHandsFree.permission(context) && NodeHandsFree.selected(context))
                if(microphoneBlocked())throw MicrophoneUnavailable()
                audio.startRecording();check(audio.recordingState==AudioRecord.RECORDSTATE_RECORDING)
                setState(if(continuation)"continuation" else "listening",if(continuation)"Pode continuar…" else if(diagnostic)"Diagnóstico: detecção local sem comandos." else "Aguardando Aegis.")
                if(continuation)showSession()
            }
            while(current(generation)) {
                val count=audio.read(buffer,0,buffer.size,AudioRecord.READ_BLOCKING)
                val readReturnedAtMs=HandsFreeTurnLatency.now()
                if(!current(generation))return null
                val now=SystemClock.elapsedRealtime()
                if(count<=0)throw MicrophoneUnavailable()
                if(silenced.get())throw MicrophoneUnavailable()
                captureConfirmed=true;lastAudioReadMs=now
                if(now-checkedAt>=5000) {
                    checkedAt=now
                    if(!NodeHandsFree.permission(context) || !NodeHandsFree.selected(context) || microphoneBlocked())throw MicrophoneUnavailable()
                }
                val duration=count*1000L/16000
                metrics.capture(duration,minOf(duration,(now-receivedAt).coerceAtLeast(0)),!captured && !continuation,screenLocked,screenInteractive)
                if(now-receivedAt>duration+1000)metrics.add("captureUnknownGapMs",now-receivedAt-duration)
                receivedAt=now
                metrics.maybeTick()
                if(sessionCancelled && (captured || continuation))return ByteArray(0)
                if(captured) {
                    val commandState=checkNotNull(commandCapture)
                    val ended=commandState.append(buffer,count)
                    val last=commandState.lastActiveEndSample
                    if(last!=null && last!=lastVadSample) {
                        lastVadSample=last
                        lastVadFrameAtMs=readReturnedAtMs-(commandState.length-last)*1000L/16000
                    }
                    if(ended) {
                        val endpointAt=HandsFreeTurnLatency.now()
                        metrics.add("commandCaptureMs",now-commandStarted)
                        if(!commandState.hasUsefulAudio){metrics.add("commands_no_speech");return ByteArray(0)}
                        val trace=HandsFreeTurnLatency()
                        lastVadFrameAtMs?.let{trace.markAt("last_vad_active_frame_estimated",it)}
                        trace.markAt("endpoint_completed",endpointAt);trace.mark("wav_prepare_started")
                        val wav=handsFreeWav(command,commandState.length)
                        trace.mark("wav_ready");capturedTurnLatency=trace
                        return wav
                    }
                    continue
                }
                ring.append(buffer,count)
                val event=if(continuation) {
                    // The 2s deadline constrains onset, not the PCM block. Qualification may
                    // finish just afterward; an accepted beginning may continue for 20s.
                    val beforeDeadline=(count-((now-continuationDeadline).coerceAtLeast(0)*16)).coerceIn(0,count.toLong()).toInt()
                    when(continuationGate.process(buffer,count,beforeDeadline,now>=continuationDeadline)) {
                        HandsFreeContinuationGate.Decision.STARTED->1L
                        HandsFreeContinuationGate.Decision.WAIT->-1L
                        HandsFreeContinuationGate.Decision.TIMED_OUT->{metrics.add("continuation_no_speech");return ByteArray(0)}
                    }
                } else MicroWakeWordNative.process(detector,buffer,count)
                if(event == -2L)error("detector_inference")
                if(event>=0 || pendingActivation) {
                    synchronized(lock) {
                        // A blocking read/inference may finish after stop or a manual lease.
                        // Commit activation atomically with those transitions.
                        if(!current(generation))return null
                        if(continuation && sessionCancelled)return ByteArray(0)
                        val assistantActivation=pendingActivation;pendingActivation=false
                        if(!continuation)metrics.add("activations")
                        if(diagnostic){beep();metrics.event("diagnostic_activation");continue}
                        val length=ring.copyTo(command);captured=true;if(!continuation)sessionCancelled=false;commandStarted=now
                        if(!continuation && !assistantActivation)sessionId=UUID.randomUUID().toString()
                        // Keep the full pre-roll including audio received after the keyword. STT strips
                        // only the initial wake word; no capture release/reopen and no popup dependency.
                        speechClassifier.reset();commandCapture=HandsFreeCommandCapture(command,length,speechClassifier)
                        commandCapture?.lastActiveEndSample?.let{last->
                            lastVadSample=last;lastVadFrameAtMs=readReturnedAtMs-(length-last)*1000L/16000
                        }
                        metrics.add("sessions_started");setState("capturing","Escutando pedido…");showSession();if(!continuation)beep()
                    }
                }
            }
            return null
        }finally {
            captureConfirmed=false;recorder=null
            if(Build.VERSION.SDK_INT>=29 && recordingCallbackRegistered && recordingCallback!=null)try{audio.unregisterAudioRecordingCallback(recordingCallback)}catch(_:Exception){}
            try{audio.stop()}catch(_:Exception){};audio.release()
            classifier?.close()
            buffer.fill(0);command.fill(0);ring.clear()
        }
    }
}
