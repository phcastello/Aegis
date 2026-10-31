package com.aegis.node

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.os.Build
import android.os.Handler
import android.os.Looper
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/** Tiny local PCM cue. Its worker never blocks capture, the UI or a detector handoff. */
internal class HandsFreeActivationCue(private val failure:()->Unit={}) {
    private val generation=AtomicInteger()
    private val executor=ThreadPoolExecutor(1,1,30,TimeUnit.SECONDS,ArrayBlockingQueue<Runnable>(1),
        {r->Thread(r,"AegisActivationCue").apply{isDaemon=true}},ThreadPoolExecutor.DiscardOldestPolicy())
        .apply{allowCoreThreadTimeOut(true)}
    @Volatile private var active:AudioTrack?=null
    @Volatile private var completion:CountDownLatch?=null
    fun play() {
        val token=generation.incrementAndGet()
        executor.execute {
            if(token!=generation.get())return@execute
            var player:AudioTrack?=null
            try {
                val pcm=HandsFreeCueSignal.samples
                val attrs=AudioAttributes.Builder().setUsage(if(Build.VERSION.SDK_INT>=26)AudioAttributes.USAGE_ASSISTANT else AudioAttributes.USAGE_ASSISTANCE_SONIFICATION)
                    .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION).build()
                player=AudioTrack.Builder().setAudioAttributes(attrs)
                    .setAudioFormat(AudioFormat.Builder().setSampleRate(HandsFreeCueSignal.SAMPLE_RATE).setChannelMask(AudioFormat.CHANNEL_OUT_MONO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
                    .setBufferSizeInBytes(pcm.size*2).setTransferMode(AudioTrack.MODE_STATIC).build()
                // MODE_STATIC is not fully initialized until its first PCM write.
                check(player.state==AudioTrack.STATE_INITIALIZED || player.state==AudioTrack.STATE_NO_STATIC_DATA)
                val done=CountDownLatch(1);active=player;completion=done
                player.setPlaybackPositionUpdateListener(object:AudioTrack.OnPlaybackPositionUpdateListener {
                    override fun onMarkerReached(track:AudioTrack){done.countDown()}
                    override fun onPeriodicNotification(track:AudioTrack){}
                },Handler(Looper.getMainLooper()))
                check(player.write(pcm,0,pcm.size,AudioTrack.WRITE_BLOCKING)==pcm.size)
                check(player.state==AudioTrack.STATE_INITIALIZED)
                player.notificationMarkerPosition=pcm.size;player.setVolume(.18f)
                if(token!=generation.get())return@execute
                player.play();done.await(500,TimeUnit.MILLISECONDS)
            }catch(_:Exception){failure()}
            finally {completion=null;active=null;try{player?.stop()}catch(_:Exception){};player?.release()}
        }
    }
    fun cancel(){generation.incrementAndGet();executor.queue.clear();completion?.countDown();try{active?.stop()}catch(_:Exception){}}
}
