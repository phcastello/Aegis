package com.aegis.node

import kotlin.math.exp
import kotlin.math.log10
import kotlin.math.sqrt

/** Fixed display scale, with no AGC and no feedback into wake word, VAD or STT. */
internal fun handsFreePanelLevel(samples:ShortArray,count:Int):Float {
    val length=count.coerceIn(0,samples.size)
    if(length==0)return 0f
    var energy=0.0
    for(index in 0 until length){val value=samples[index].toDouble();energy+=value*value}
    return handsFreePanelEnergy(energy,length)
}

private fun handsFreePanelEnergy(energy:Double,count:Int):Float {
    if(count<=0 || energy<=0.0)return 0f
    val rms=sqrt(energy/count)/32768.0
    if(rms<=0.0012589254)return 0f // -58 dBFS: no automatic amplification of quiet noise.
    return ((20.0*log10(rms)+58.0)/46.0).coerceIn(0.0,1.0).toFloat()
}

/**
 * One audio producer, one visible UI consumer. Only energies/amplitudes and
 * timestamps are retained; PCM is read in place and never copied or retained.
 *
 * 20ms measurements are presented 200ms behind capture time. A 200ms microphone
 * delivery consequently supplies the next ten real presentation points instead
 * of stepping all bars together. 32 positions span 1.28s of that history. Linear
 * interpolation is presentation between measured envelopes, never invented PCM.
 *
 * The audio producer never acquires a UI lock. A volatile odd/even publication
 * sequence lets render copy a small fixed snapshot and discard a racing frame.
 */
internal object HandsFreePanelSignal {
    const val BAR_COUNT=32
    const val WINDOW_MS=20L
    const val BAR_STEP_MS=40L
    const val HISTORY_MS=BAR_COUNT*BAR_STEP_MS
    const val PRESENTATION_DELAY_MS=200L
    const val STALE_CLEAR_MS=1600L
    const val CAPACITY=80 // 1.6s includes visible history plus presentation delay.
    const val ATTACK_MS=40.0
    const val RELEASE_MS=150.0
    private const val WINDOW_SAMPLES=320
    private const val SAMPLE_NS=62500L
    private const val NS_PER_MS=1000000L
    private const val QUIET=0.002f

    private class Observation(val owner:Any,val session:String,val boundaryNs:Long=Long.MIN_VALUE) {
        @Volatile var publication=0L
        @Volatile var listener:(()->Unit)?=null
        val amplitudes=FloatArray(CAPACITY)
        val times=LongArray(CAPACITY)
        // These scratch arrays belong exclusively to the UI consumer.
        val renderAmplitudes=FloatArray(CAPACITY)
        val renderTimes=LongArray(CAPACITY)
        var written=0L
        var partialCount=0
        var partialEnergy=0.0
        var lastCaptureMs=Long.MIN_VALUE
        var lastCaptureEndNs=Long.MIN_VALUE
        var lastWindowNs=Long.MIN_VALUE
        var envelope=0f
        var quietStart=0f
        var quietElapsedMs=0.0

        fun append(energy:Double,endNs:Long) {
            if(lastWindowNs!=Long.MIN_VALUE && endNs<=lastWindowNs)return
            val level=handsFreePanelEnergy(energy,WINDOW_SAMPLES)
            val elapsedMs=if(lastWindowNs==Long.MIN_VALUE)WINDOW_MS.toDouble()
                else ((endNs-lastWindowNs)/NS_PER_MS.toDouble()).coerceAtLeast(1.0)
            val stepMs=if(elapsedMs>60.0) {
                envelope*=exp(-(elapsedMs-WINDOW_MS)/RELEASE_MS).toFloat()
                quietElapsedMs=0.0
                WINDOW_MS.toDouble()
            } else elapsedMs
            val mixed=if(level==0f) {
                // Measured silence reaches exact zero in 150ms; exponential tails
                // must not leave old dots visible beyond the requested history.
                if(quietElapsedMs==0.0)quietStart=envelope
                quietElapsedMs=(quietElapsedMs+stepMs).coerceAtMost(RELEASE_MS)
                quietStart*(1.0-quietElapsedMs/RELEASE_MS).toFloat()
            } else {
                quietElapsedMs=0.0
                val tau=if(level>envelope)ATTACK_MS else RELEASE_MS
                level+(envelope-level)*exp(-stepMs/tau).toFloat()
            }
            envelope=if(mixed<QUIET)0f else mixed
            val index=(written%CAPACITY).toInt()
            amplitudes[index]=envelope;times[index]=endNs;written++
            lastWindowNs=endNs
        }
    }
    @Volatile private var observation:Observation?=null

    fun attach(owner:Any,session:String?){observation=session?.let{Observation(owner,it)}}
    fun detach(owner:Any){if(observation?.owner===owner)observation=null}
    fun setListener(owner:Any,listener:(()->Unit)?){observation?.takeIf{it.owner===owner}?.listener=listener}
    fun reset(owner:Any,nowMs:Long=Long.MIN_VALUE) {
        val previous=observation?.takeIf{it.owner===owner}?:return
        val boundary=if(nowMs==Long.MIN_VALUE)Long.MIN_VALUE else nowMs*NS_PER_MS
        val replacement=Observation(owner,previous.session,boundary).also{it.listener=previous.listener}
        if(observation===previous)observation=replacement
    }

    fun capture(session:String?,samples:ShortArray,count:Int,nowMs:Long) {
        val target=observation?.takeIf{it.session==session}?:return
        val length=count.coerceIn(0,samples.size)
        if(length==0 || nowMs<=target.lastCaptureMs)return
        target.publication++ // Odd: UI skips this snapshot instead of blocking capture.
        try {
            val endNs=nowMs*NS_PER_MS
            val startNs=endNs-length*SAMPLE_NS
            if(target.lastCaptureEndNs!=Long.MIN_VALUE) {
                val gap=startNs-target.lastCaptureEndNs
                if(gap>40*NS_PER_MS || gap< -20*NS_PER_MS) {
                    // Do not combine partial windows over capture gaps or late batches.
                    target.partialCount=0;target.partialEnergy=0.0
                }
            }
            for(index in 0 until length) {
                val sampleStart=startNs+index*SAMPLE_NS
                if(sampleStart<target.boundaryNs)continue
                val value=samples[index].toDouble()
                target.partialEnergy+=value*value;target.partialCount++
                if(target.partialCount==WINDOW_SAMPLES) {
                    target.append(target.partialEnergy,sampleStart+SAMPLE_NS)
                    target.partialCount=0;target.partialEnergy=0.0
                }
            }
            target.lastCaptureMs=nowMs;target.lastCaptureEndNs=endNs
        }finally{target.publication++}
        // One notification per existing microphone block, never one per 20ms window.
        if(observation===target)target.listener?.invoke()
    }

    /** Writes oldest-left/newest-right amplitudes into a caller-owned array. */
    fun render(owner:Any?,nowMs:Long,output:FloatArray):Boolean {
        require(output.size>=BAR_COUNT)
        val target=observation?.takeIf{it.owner===owner}?:run{output.fill(0f);return false}
        val version=target.publication
        if(version and 1L!=0L)return true // Preserve the last complete visual frame.
        val written=target.written
        val count=minOf(written,CAPACITY.toLong()).toInt()
        val received=target.lastCaptureMs
        val oldest=written-count
        for(index in 0 until count) {
            val source=((oldest+index)%CAPACITY).toInt()
            target.renderAmplitudes[index]=target.amplitudes[source]
            target.renderTimes[index]=target.times[source]
        }
        if(target.publication!=version)return true
        if(observation!==target){output.fill(0f);return false}
        val age=if(received==Long.MIN_VALUE)Long.MAX_VALUE else nowMs-received
        if(count==0 || age<0 || age>=STALE_CLEAR_MS){output.fill(0f);return false}
        val headNs=(nowMs-PRESENTATION_DELAY_MS)*NS_PER_MS
        // Capture dropout fades the whole history; it must not freeze on the last word.
        val missingFade=if(age<=400)1f else ((1400-age)/1000f).coerceIn(0f,1f).let{it*it}
        var active=false
        for(index in 0 until BAR_COUNT) {
            val at=headNs-(BAR_COUNT-1-index)*BAR_STEP_MS*NS_PER_MS
            var level=sample(target,at,count)*missingFade
            if(level<QUIET)level=0f
            output[index]=level
            if(level>0f)active=true
        }
        // Pending measured speech wakes vsync before its delayed point becomes visible.
        if(!active)for(index in 0 until count) {
            if(target.renderTimes[index]>headNs && target.renderAmplitudes[index]>QUIET){active=true;break}
        }
        if(observation!==target){output.fill(0f);return false}
        return active
    }

    private fun sample(target:Observation,at:Long,count:Int):Float {
        val times=target.renderTimes;val values=target.renderAmplitudes
        val first=times[0]
        if(at<first) {
            val before=first-WINDOW_MS*NS_PER_MS
            return if(at<=before)0f else values[0]*((at-before).toDouble()/(first-before)).toFloat()
        }
        for(index in 1 until count)if(at<=times[index]) {
            val previous=times[index-1];val span=times[index]-previous
            if(span>60*NS_PER_MS) {
                // A capture gap is decay, not a bridge implying sustained unmeasured speech.
                val start=times[index]-WINDOW_MS*NS_PER_MS
                if(at<start)return values[index-1]*exp(-(at-previous)/NS_PER_MS.toDouble()/RELEASE_MS).toFloat()
                val before=values[index-1]*exp(-(start-previous)/NS_PER_MS.toDouble()/RELEASE_MS).toFloat()
                val fraction=((at-start).toDouble()/(times[index]-start)).coerceIn(0.0,1.0).toFloat()
                return before+(values[index]-before)*fraction
            }
            val fraction=((at-previous).toDouble()/span).coerceIn(0.0,1.0).toFloat()
            return values[index-1]+(values[index]-values[index-1])*fraction
        }
        return values[count-1]*exp(-(at-times[count-1])/NS_PER_MS.toDouble()/RELEASE_MS).toFloat()
    }
}

/** The status text deliberately excludes transport errors and operational diagnostics. */
internal enum class HandsFreePanelState(val label:String,val animated:Boolean=false) {
    CAPTURING("Estou ouvindo",true),CONTINUATION("Pode continuar",true),
    PROCESSING("Processando",true),SPEAKING("Aegis está falando",true),
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

/** Prevent display timeout only for the current visible conversation panel. */
internal fun handsFreePanelKeepsScreenOn(state:HandsFreePanelState,visible:Boolean,hasSession:Boolean)=
    visible && hasSession && when(state) {
        HandsFreePanelState.CAPTURING,HandsFreePanelState.CONTINUATION,
        HandsFreePanelState.PROCESSING,HandsFreePanelState.SPEAKING->true
        else->false
    }

/** A terminal error survives a queued return-to-listening until dismissal or a new presentation. */
internal class HandsFreePanelPresentation {
    class Snapshot internal constructor(val session:String?)
    @Volatile private var activeSnapshot:Snapshot?=null
    var state=HandsFreePanelState.IDLE
        private set
    private var terminalError=false
    fun begin(session:String?=null){activeSnapshot=Snapshot(session);terminalError=false;state=HandsFreePanelState.IDLE}
    fun snapshot():Snapshot?=activeSnapshot
    fun isCurrent(snapshot:Snapshot)=activeSnapshot===snapshot
    fun end(){activeSnapshot=null}
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
