package com.aegis.node

import java.io.DataInputStream
import java.io.InputStream
import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.ln1p
import kotlin.math.sin
import kotlin.math.sqrt
import kotlin.math.tanh

/**
 * Streaming, reference-conditioned speech detector. The AGD4 model shares one
 * causal spectral encoder across the microphone, playback, and AEC outputs.
 * It matches incoming speech against 1.2 seconds of encoded playback history
 * and maintains a small recurrent state. It does not modify audio.
 */
internal class HandsFreeDoubleTalkDetector(private val model: Model) {
    private val spectra = Array(STREAMS) { FloatArray(FEATURES) }
    private val normalizedLevels = FloatArray(STREAMS)
    private val basePrevious = Array(STREAMS) { FloatArray(BASE) }
    private val baseOlder = Array(STREAMS) { FloatArray(BASE) }
    private val embedding = Array(STREAMS) { FloatArray(EMBEDDING) }
    private val reference = Array(HISTORY) { FloatArray(EMBEDDING) }
    private val referenceUnit = Array(HISTORY) { FloatArray(EMBEDDING) }
    private val referenceHarmonic = Array(HISTORY) { FloatArray(BINS) }
    private val referenceLevel = FloatArray(HISTORY)
    private val harmonic = FloatArray(BINS)
    private val unitQuery = FloatArray(EMBEDDING)
    private val similarity = FloatArray(HISTORY)
    private val weights = FloatArray(HISTORY)
    private val contexts = Array(3) { FloatArray(EMBEDDING) }
    private val matchStats = Array(3) { FloatArray(MATCH_FIELDS) }
    private val harmonicStats = FloatArray(4)
    private val encoderInput = FloatArray(FEATURES + BINS)
    private val encoderBase = FloatArray(BASE)
    private val temporalInput = FloatArray(BASE * 3)
    private val merged = FloatArray(MERGED)
    private val fused = FloatArray(FUSE)
    private val hidden = FloatArray(HIDDEN)
    private val nextHidden = FloatArray(HIDDEN)
    private val gatesInput = FloatArray(HIDDEN * 3)
    private val gatesState = FloatArray(HIDDEN * 3)
    private val fftReal = DoubleArray(FFT)
    private val fftImaginary = DoubleArray(FFT)
    private var historyPosition = HISTORY - 1
    private var frames = 0L
    private var softwareMode: Boolean? = null
    var lastScore: Float = 0f
        private set
    val ready: Boolean get() = frames >= 3
    val requiredVotes: Int get() = model.requiredVotes
    val windowFrames: Int get() = model.windowFrames

    fun observe(mic: ShortArray, render: ShortArray, cleaned: ShortArray = mic, linear: ShortArray = cleaned): Boolean {
        require(mic.size == FRAME_SAMPLES && render.size == FRAME_SAMPLES &&
            cleaned.size == FRAME_SAMPLES && linear.size == FRAME_SAMPLES)
        val software = cleaned !== mic || linear !== mic
        if (softwareMode != null && softwareMode != software) reset()
        softwareMode = software
        spectrum(mic,spectra[0])
        spectrum(render,spectra[1])
        if (cleaned === mic) spectra[0].copyInto(spectra[2]) else spectrum(cleaned,spectra[2])
        when {
            linear === mic -> spectra[0].copyInto(spectra[3])
            linear === cleaned -> spectra[2].copyInto(spectra[3])
            else -> spectrum(linear,spectra[3])
        }
        for (stream in 0 until STREAMS) encode(stream)

        historyPosition = (historyPosition + 1) % HISTORY
        embedding[1].copyInto(reference[historyPosition])
        normalized(embedding[1],referenceUnit[historyPosition])
        whiten(spectra[1],referenceHarmonic[historyPosition])
        referenceLevel[historyPosition] = spectra[1][BINS]
        // Exact AGD4 feature ordering: current mic/clean/linear/far, then
        // weighted far embeddings for each captured stream and match statistics.
        match(embedding[0],contexts[0],matchStats[0])
        match(embedding[2],contexts[1],matchStats[1])
        match(embedding[3],contexts[2],matchStats[2])
        whiten(spectra[0],harmonic)
        for (i in 0 until HISTORY) {
            val ref = referenceHarmonic[historyIndex(i)]
            var dot = 0f
            for (k in 0 until BINS) dot += harmonic[k] * ref[k]
            similarity[i] = dot
        }
        summarize(similarity,harmonicStats)
        var offset=0
        fun add(values:FloatArray) {
            values.copyInto(merged,offset)
            offset+=values.size
        }
        add(embedding[0]);add(embedding[2]);add(embedding[3]);add(embedding[1])
        for (value in contexts) add(value)
        for (stats in matchStats) add(stats)
        add(harmonicStats)
        add(normalizedLevels)
        var maxLevel=0f;var shortMax=0f;var sum=0f
        for (i in 0 until HISTORY) {
            val level=referenceLevel[historyIndex(i)]
            maxLevel=maxOf(maxLevel,level)
            if(i>=HISTORY-21)shortMax=maxOf(shortMax,level)
            sum+=level
        }
        merged[offset++]=maxLevel
        merged[offset++]=shortMax
        merged[offset++]=sum/HISTORY
        merged[offset++]=if(software)1f else 0f
        check(offset==MERGED)
        check(offset==merged.size)
        dense(merged,model.fuseWeights,model.fuseBias,fused)
        for(i in fused.indices)fused[i]=fused[i].coerceAtLeast(0f)
        dense(fused,model.gruInputWeights,model.gruInputBias,gatesInput)
        dense(hidden,model.gruStateWeights,model.gruStateBias,gatesState)
        for(i in 0 until HIDDEN){
            val reset=logistic(gatesInput[i]+gatesState[i])
            val update=logistic(gatesInput[HIDDEN+i]+gatesState[HIDDEN+i])
            val candidate=tanh((gatesInput[HIDDEN*2+i]+reset*gatesState[HIDDEN*2+i]).toDouble()).toFloat()
            nextHidden[i]=(1f-update)*candidate+update*hidden[i]
        }
        nextHidden.copyInto(hidden)
        var logit=model.scoreBias
        for(i in hidden.indices)logit+=hidden[i]*model.scoreWeights[i]
        lastScore=logistic(logit/3f)
        frames++
        return ready && lastScore >= (if (software) model.threshold else model.hardwareThreshold)
    }

    private fun encode(stream:Int) {
        val value=spectra[stream]
        var mean=0f
        for(i in 0 until BINS)mean+=value[i]
        mean/=BINS
        for(i in value.indices)encoderInput[i]=((value[i]-model.means[i])*model.scales[i]).coerceIn(-10f,10f)
        normalizedLevels[stream]=encoderInput[BINS]
        for(i in 0 until BINS)encoderInput[FEATURES+i]=value[i]-mean
        dense(encoderInput,model.baseWeights,model.baseBias,encoderBase)
        for(i in encoderBase.indices)encoderBase[i]=encoderBase[i].coerceAtLeast(0f)
        baseOlder[stream].copyInto(temporalInput)
        basePrevious[stream].copyInto(temporalInput,BASE)
        encoderBase.copyInto(temporalInput,BASE*2)
        dense(temporalInput,model.temporalWeights,model.temporalBias,embedding[stream])
        for(i in embedding[stream].indices)embedding[stream][i]=tanh(embedding[stream][i].toDouble()).toFloat()
        basePrevious[stream].copyInto(baseOlder[stream])
        encoderBase.copyInto(basePrevious[stream])
    }

    private fun historyIndex(chronological:Int):Int=(historyPosition+1+chronological)%HISTORY

    private fun match(query:FloatArray,context:FloatArray,stats:FloatArray) {
        normalized(query,unitQuery)
        for(i in 0 until HISTORY){
            val ref=referenceUnit[historyIndex(i)]
            var dot=0f
            for(k in 0 until EMBEDDING)dot+=unitQuery[k]*ref[k]
            similarity[i]=dot
        }
        summarize(similarity,stats)
        // summarize returns [maximum, short maximum, mean, lag], whereas
        // learned attention requires [maximum, short maximum, mean, current,
        // lag, maximum attention].
        stats[4]=stats[3]
        stats[3]=similarity.last()
        var sum=0f
        for(i in weights.indices){
            val weight=exp(((similarity[i]-stats[0])*8f).toDouble()).toFloat()
            weights[i]=weight;sum+=weight
        }
        stats[5]=1f/sum
        context.fill(0f)
        for(i in 0 until HISTORY){
            val ref=reference[historyIndex(i)]
            val weight=weights[i]/sum
            for(k in context.indices)context[k]+=ref[k]*weight
        }
    }

    private fun summarize(values:FloatArray,out:FloatArray){
        var peak=-Float.MAX_VALUE;var shortPeak=-Float.MAX_VALUE;var sum=0f;var at=0
        for(i in values.indices){
            val value=values[i]
            if(value>peak){peak=value;at=i}
            if(i>=HISTORY-21)shortPeak=maxOf(shortPeak,value)
            sum+=value
        }
        out[0]=peak;out[1]=shortPeak;out[2]=sum/HISTORY;out[3]=(HISTORY-1-at).toFloat()/(HISTORY-1)
    }

    private fun normalized(input:FloatArray,out:FloatArray){
        var energy=0f
        for(x in input)energy+=x*x
        val norm=sqrt(energy).coerceAtLeast(1e-6f)
        for(i in input.indices)out[i]=input[i]/norm
    }
    private fun whiten(input:FloatArray,out:FloatArray){
        for(i in 0 until BINS){
            var sum=0f;val a=maxOf(0,i-4);val b=minOf(BINS-1,i+4)
            for(k in a..b)sum+=input[k]
            out[i]=input[i]-sum/(b-a+1)
        }
        normalized(out,out)
    }

    private fun spectrum(samples:ShortArray,out:FloatArray){
        fftReal.fill(0.0);fftImaginary.fill(0.0)
        var energy=0.0
        for(i in samples.indices){
            val x=samples[i].toDouble()
            energy+=x*x
            fftReal[BIT_REVERSE[i]]=x*HANN[i]
        }
        var width=2
        while(width<=FFT){
            val half=width/2;val step=FFT/width
            var base=0
            while(base<FFT){
                for(j in 0 until half){
                    val i=base+j;val k=i+half
                    val c=TWIDDLE_COS[j*step];val s=TWIDDLE_SIN[j*step]
                    val r=fftReal[k]*c-fftImaginary[k]*s
                    val im=fftReal[k]*s+fftImaginary[k]*c
                    fftReal[k]=fftReal[i]-r;fftImaginary[k]=fftImaginary[i]-im
                    fftReal[i]+=r;fftImaginary[i]+=im
                }
                base+=width
            }
            width*=2
        }
        for(i in 0 until BINS){
            val k=i+1
            out[i]=ln1p(sqrt(fftReal[k]*fftReal[k]+fftImaginary[k]*fftImaginary[k])/256.0).toFloat()
        }
        out[BINS]=ln1p(sqrt(energy/FRAME_SAMPLES)/32.0).toFloat()
    }

    fun reset() {
        for(group in arrayOf(spectra,basePrevious,baseOlder,embedding,reference,referenceUnit,referenceHarmonic,contexts,matchStats)){
            for(frame in group)frame.fill(0f)
        }
        for(array in arrayOf(normalizedLevels,referenceLevel,harmonic,unitQuery,similarity,weights,harmonicStats,encoderInput,
            encoderBase,temporalInput,merged,fused,hidden,nextHidden,gatesInput,gatesState))array.fill(0f)
        fftReal.fill(0.0);fftImaginary.fill(0.0)
        historyPosition=HISTORY-1;frames=0;softwareMode=null;lastScore=0f
    }
    private fun logistic(value:Float):Float=(1.0/(1.0+exp(-value.toDouble().coerceIn(-50.0,50.0)))).toFloat()
    private fun dense(input:FloatArray,w:FloatArray,b:FloatArray,out:FloatArray){
        for(i in out.indices){
            var v=b[i]
            var j=i*input.size
            for(x in input)v+=x*w[j++]
            out[i]=v
        }
    }

    internal class Model private constructor(
        val threshold:Float,val hardwareThreshold:Float,val requiredVotes:Int,val windowFrames:Int,
        val means:FloatArray,val scales:FloatArray,
        val baseWeights:FloatArray,val baseBias:FloatArray,val temporalWeights:FloatArray,val temporalBias:FloatArray,
        val fuseWeights:FloatArray,val fuseBias:FloatArray,val gruInputWeights:FloatArray,val gruInputBias:FloatArray,
        val gruStateWeights:FloatArray,val gruStateBias:FloatArray,val scoreWeights:FloatArray,val scoreBias:Float
    ){
        companion object{
            fun load(stream:InputStream):Model{
                val input=DataInputStream(stream.buffered())
                require(input.readInt()==0x41474434){"invalid double-talk model"} // AGD4
                require(input.readInt()==FEATURES&&input.readInt()==BASE&&input.readInt()==EMBEDDING&&
                    input.readInt()==HIDDEN&&input.readInt()==FUSE&&input.readInt()==HISTORY)
                val coherence=input.readInt()
                require(coherence==0)
                require(input.readInt()==MERGED+coherence)
                val votes=input.readInt();val window=input.readInt()
                require(votes in 1..window&&window in 1..20)
                val threshold=input.readFloat();val hardwareThreshold=input.readFloat()
                require(threshold.isFinite()&&threshold in 0f..1f&&hardwareThreshold.isFinite()&&hardwareThreshold in 0f..1f)
                fun floats(count:Int):FloatArray=FloatArray(count){
                    input.readFloat().also {require(it.isFinite())}
                }
                val mean=floats(FEATURES);val scale=floats(FEATURES)
                require(scale.all{it>0f})
                val baseW=floats(BASE*(FEATURES+BINS));val baseB=floats(BASE)
                val temporalW=floats(EMBEDDING*BASE*3);val temporalB=floats(EMBEDDING)
                val fuseW=floats(FUSE*(MERGED+coherence));val fuseB=floats(FUSE)
                val giw=floats(3*HIDDEN*FUSE);val gib=floats(3*HIDDEN)
                val ghw=floats(3*HIDDEN*HIDDEN);val ghb=floats(3*HIDDEN)
                val scoreW=floats(HIDDEN);val scoreB=floats(1)[0]
                require(input.read()==-1){"unexpected double-talk model payload"}
                return Model(threshold,hardwareThreshold,votes,window,mean,scale,
                    baseW,baseB,temporalW,temporalB,fuseW,fuseB,giw,gib,ghw,ghb,scoreW,scoreB)
            }
        }
    }

    companion object{
        const val ASSET="hands-free-barge-in/doubletalk.bin"
        const val FRAME_SAMPLES=320
        private const val BINS=192
        private const val FEATURES=BINS+1
        private const val BASE=64
        private const val EMBEDDING=48
        private const val HIDDEN=64
        private const val FUSE=96
        private const val HISTORY=61
        private const val STREAMS=4
        private const val MATCH_FIELDS=6
        private const val MERGED=EMBEDDING*7+18+4+4+3+1
        private const val FFT=512
        private val HANN=DoubleArray(FRAME_SAMPLES){.5-.5*cos(2*PI*it/(FRAME_SAMPLES-1))}
        private val BIT_REVERSE=IntArray(FFT){Integer.reverse(it) ushr 23}
        private val TWIDDLE_COS=DoubleArray(FFT/2){cos(-2*PI*it/FFT)}
        private val TWIDDLE_SIN=DoubleArray(FFT/2){sin(-2*PI*it/FFT)}
    }
}
