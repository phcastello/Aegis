package com.aegis.node

import android.content.Context
import java.util.concurrent.atomic.AtomicReference

/** Manual chat shares the assistant's PCM player, attributes, unity gain and playback-head drain. */
internal object NodeManualSpeech {
    private data class Active(val id:String,val backend:NodeHandsFreeBackend,val player:HandsFreePlayback)
    private val active=AtomicReference<Active?>()
    private val gate=Any()
    private val cancelledIds=linkedSetOf<String>()
    fun speak(context:Context,turnId:String,messageId:String,speechRequestId:String) {
        val runtime=NodeHandsFree.runtime(context)
        check(runtime.manualPlaybackActive()){ "manual_playback_lease_required" }
        val backend=NodeHandsFreeBackend(context)
        val player=HandsFreePlayback(context.applicationContext)
        val attempt=Active(speechRequestId,backend,player)
        synchronized(gate) {
            check(speechRequestId !in cancelledIds){"manual_speech_cancelled"}
            check(active.compareAndSet(null,attempt)){"manual_speech_busy"}
        }
        try {
            val sink=player.openStream{active.get()===attempt && runtime.manualPlaybackActive()}
            backend.speakExisting(turnId,messageId,speechRequestId,sink)
        } finally { active.compareAndSet(attempt,null);player.cancel() }
    }
    fun cancel(id:String?=null) {
        val attempt=synchronized(gate) {
            if(id!=null) {cancelledIds.add(id);while(cancelledIds.size>64)cancelledIds.remove(cancelledIds.first())}
            val value=active.get()?:return
            if(id!=null && value.id!=id)return
            if(!active.compareAndSet(value,null))return
            value
        }
        run {
            attempt.backend.requestCancellation();attempt.player.cancel()
            Thread({attempt.backend.cancel()},"AegisManualSpeechCancel").apply{isDaemon=true;start()}
        }
    }
}
