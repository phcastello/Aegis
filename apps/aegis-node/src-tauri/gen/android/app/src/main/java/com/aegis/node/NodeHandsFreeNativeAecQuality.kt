package com.aegis.node

import kotlin.math.abs
import kotlin.math.sqrt

/** Relax native double-talk confirmation only while the microphone has
 * demonstrated very low residual during sustained active playback. */
internal class HandsFreeNativeAecQuality {
    private val activity = IntArray(40)
    private val renderHistory = ShortArray(4000+320)
    private val contiguousHistory = ShortArray(renderHistory.size)
    private var samplePosition=0
    private var historyFrames=0
    private var quality = false
    private var observed = 0
    var clearVotes = false
        private set
    var qualified = false
        private set
    var vetoed = false
        private set

    fun observe(mic: ShortArray, render: ShortArray, score: Float, strict: Boolean): Boolean {
        require(mic.size==320 && render.size==320)
        clearVotes=false
        vetoed=false
        // Keep the entire causal 250 ms lag horizon, including the current block.
        for(i in render.indices) {
            renderHistory[samplePosition]=render[i]
            samplePosition=(samplePosition+1)%renderHistory.size
        }
        historyFrames=minOf(historyFrames+render.size,renderHistory.size)
        val micLevel = rms(mic)
        val renderLevel = rms(render)
        val hardEcho = micLevel>=100 && (mic.count { abs(it.toInt())>=32000 }>=4 ||
            score>=.38f && maximumCorrelationSquared(mic)>=.75*.75)
        if(hardEcho) {
            vetoed=true
            activity.fill(0)
            observed=0
            qualified=false
            clearVotes=true
            quality=false
            return false
        }
        // New active quiet frames have value 2, active nonquiet frames 1,
        // and inactive frames 0. Count only actual playback observations.
        activity[observed%activity.size] = when {
            renderLevel<500 -> 0
            micLevel<=maxOf(32.0,renderLevel*.01) -> 2
            else -> 1
        }
        observed++
        var lastQuietAge = activity.size
        var quiet=0
        var active=0
        for(age in minOf(observed,activity.size)-1 downTo 0) {
            val value = activity[(observed-1-age)%activity.size]
            when(value) {
                2 -> {
                    quiet++
                    active++
                    lastQuietAge=age
                }
                1 -> active++
            }
        }
        // Activity after the most recent quiet observation could be the
        // user. Do not count that potential foreground against earlier quality.
        for(age in 0 until lastQuietAge.coerceAtMost(minOf(observed,activity.size))) {
            if(activity[(observed-1-age)%activity.size]==1) active--
        }
        qualified=quiet>=8 && quiet*5>=active*4 && lastQuietAge<=20
        clearVotes=quality!=qualified
        quality=qualified
        return strict || qualified && score>=.70f && micLevel>=100
    }

    private fun rms(frame: ShortArray): Double =
        sqrt(frame.sumOf { it.toDouble()*it }/frame.size)

    private fun maximumCorrelationSquared(mic: ShortArray): Double {
        var micPower=0.0
        var micSum=0.0
        for(i in mic.indices step 8) {
            val x=mic[i].toDouble()
            micPower+=x*x
            micSum+=x
        }
        micPower-=micSum*micSum/40
        if(micPower<=0) return 0.0
        val oldest=samplePosition-historyFrames+renderHistory.size
        for (i in 0 until historyFrames) contiguousHistory[i]=renderHistory[(oldest+i)%renderHistory.size]
        var best=0.0
        // Evaluate every integer delay because a narrowband echo's peak
        // may occur between coarser lag steps. Only run on likely speech.
        for(lag in 0..minOf(4000,historyFrames-mic.size)) {
            var referenceSum=0.0
            var referencePower=0.0
            var cross=0.0
            for(i in mic.indices step 8) {
                val y=contiguousHistory[historyFrames-mic.size-lag+i].toDouble()
                referenceSum+=y
                referencePower+=y*y
                cross+=y*mic[i]
            }
            referencePower-=referenceSum*referenceSum/40
            if(referencePower<=0) continue
            cross-=referenceSum*micSum/40
            best=maxOf(best,cross*cross/(micPower*referencePower))
            if(best>=.75*.75) return best
        }
        return best
    }

    fun reset() {
        activity.fill(0)
        renderHistory.fill(0)
        contiguousHistory.fill(0)
        samplePosition=0
        historyFrames=0
        observed=0
        qualified=false
        quality=false
        vetoed=false
        clearVotes=true
    }
}
