package com.aegis.node

import kotlin.math.log10
import kotlin.math.sqrt

/** Display-only RMS. Reads the already captured block and never retains PCM. */
internal fun handsFreePanelLevel(samples:ShortArray,count:Int):Float {
    val length=count.coerceIn(0,samples.size)
    if(length==0)return 0f
    var energy=0.0
    for(index in 0 until length){val value=samples[index].toDouble();energy+=value*value}
    val rms=sqrt(energy/length)/32768.0
    if(rms<=0.001)return 0f // -60 dBFS floor; quiet input is not amplified automatically.
    return ((20.0*log10(rms)+60.0)/48.0).coerceIn(0.0,1.0).toFloat()
}

/** One bounded latest-value slot, attached only while the native panel is visible. */
internal object HandsFreePanelSignal {
    private class Observation(val owner:Any,val session:String) {
        @Volatile var level=0f
        @Volatile var receivedAt=Long.MIN_VALUE
    }
    @Volatile private var observation:Observation?=null
    fun attach(owner:Any,session:String?){observation=session?.let{Observation(owner,it)}}
    fun detach(owner:Any){if(observation?.owner===owner)observation=null}
    fun reset(owner:Any){observation?.takeIf{it.owner===owner}?.let{it.level=0f;it.receivedAt=Long.MIN_VALUE}}
    fun capture(session:String?,samples:ShortArray,count:Int,nowMs:Long) {
        val target=observation?.takeIf{it.session==session}?:return
        target.level=handsFreePanelLevel(samples,count)
        target.receivedAt=nowMs
    }
    fun level(owner:Any?,nowMs:Long):Float {
        val target=observation?.takeIf{it.owner===owner}?:return 0f
        if(target.receivedAt==Long.MIN_VALUE)return 0f
        val age=nowMs-target.receivedAt
        return if(age in 0..400)target.level else 0f
    }
}

/** The status text deliberately excludes transport errors and operational diagnostics. */
internal enum class HandsFreePanelState(val label:String,val animated:Boolean=false) {
    CAPTURING("Estou ouvindo",true),CONTINUATION("Pode continuar",true),
    PROCESSING("Só um instante",true),SPEAKING("Aegis está falando",true),
    ERROR("Não consegui concluir. Tente novamente."),IDLE("Aegis");
    companion object {
        fun fromRuntime(status:String)=when(status) {
            "capturing"->CAPTURING
            "continuation"->CONTINUATION
            "processing"->PROCESSING
            "speaking"->SPEAKING
            "listening"->IDLE
            else->ERROR
        }
    }
}

/** A terminal error survives a queued return-to-listening until dismissal or a new presentation. */
internal class HandsFreePanelPresentation {
    var state=HandsFreePanelState.IDLE
        private set
    private var terminalError=false
    fun begin(){terminalError=false;state=HandsFreePanelState.IDLE}
    fun refresh(status:String):Boolean {
        if(terminalError)return false
        state=HandsFreePanelState.fromRuntime(status)
        return true
    }
    fun delayClose(immediate:Boolean,failed:Boolean):Boolean {
        if(immediate)return false
        if(failed){terminalError=true;state=HandsFreePanelState.ERROR}
        return state==HandsFreePanelState.ERROR
    }
}
