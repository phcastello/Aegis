package com.aegis.node

import java.io.File
import kotlin.math.PI
import kotlin.math.max
import kotlin.math.pow
import kotlin.math.roundToInt
import kotlin.math.sin

/** Synthetic PCM only. Compiled alongside the production signal, never packaged. */
object HandsFreeWaveformVisualFixture {
    private fun amplitude(timeMs:Double):Double {
        val seconds=timeMs/1000.0
        val voice=when {
            timeMs<800.0->0.0
            timeMs<2200.0->700.0
            timeMs<4200.0->3600.0
            timeMs<5100.0->10500.0
            timeMs<6600.0->0.0
            timeMs<7600.0->2800.0
            else->0.0
        }
        // Measured energy follows short deterministic syllables, not random bar motion.
        val syllable=max(0.0,sin(2.0*PI*3.1*seconds)).pow(1.4)
        return voice*syllable
    }
    private fun pcm(startMs:Int)=ShortArray(3200) { index->
        val timeMs=startMs+index/16.0
        val t=timeMs/1000.0
        val carrier=sin(2.0*PI*173*t)+.28*sin(2.0*PI*346*t)+.12*sin(2.0*PI*519*t)
        (amplitude(timeMs)*carrier).roundToInt().coerceIn(-32768,32767).toShort()
    }
    @JvmStatic fun main(args:Array<String>) {
        val owner=Any()
        val session="synthetic-visual-fixture"
        val bars=FloatArray(HandsFreePanelSignal.BAR_COUNT)
        val frames=StringBuilder(320000)
        var nextBlockEnd=200
        HandsFreePanelSignal.attach(owner,session)
        try {
            frames.append("{\"scope\":\"Production Kotlin signal; synthetic PCM; host visualization, not Android physical\",")
            frames.append("\"fps\":60,\"barCount\":${HandsFreePanelSignal.BAR_COUNT},\"windowMs\":${HandsFreePanelSignal.WINDOW_MS},")
            frames.append("\"historyMs\":${HandsFreePanelSignal.HISTORY_MS},\"presentationDelayMs\":${HandsFreePanelSignal.PRESENTATION_DELAY_MS},\"frames\":[")
            for(frame in 0..780) {
                val now=(frame*1000.0/60).roundToInt()
                while(nextBlockEnd<=now && nextBlockEnd<=9600) {
                    val samples=pcm(nextBlockEnd-200)
                    HandsFreePanelSignal.capture(session,samples,samples.size,nextBlockEnd.toLong())
                    nextBlockEnd+=200
                }
                val state=when {
                    now<9600->"capturing"
                    now<10700->"processing"
                    now<12000->"speaking"
                    now<12500->"error"
                    else->"idle"
                }
                if(now>=9600)HandsFreePanelSignal.reset(owner,now.toLong())
                val active=HandsFreePanelSignal.render(owner,now.toLong(),bars)
                if(frame>0)frames.append(',')
                frames.append("{\"atMs\":$now,\"state\":\"$state\",\"active\":$active,\"bars\":[")
                bars.forEachIndexed { i,value->if(i>0)frames.append(',');frames.append(value) }
                frames.append("]}")
            }
            frames.append("]}\n")
            File(args.single()).writeText(frames.toString())
        }finally{HandsFreePanelSignal.detach(owner)}
    }
}
