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
    private val lock=Any()
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
                val track=AudioTrack.Builder().setAudioAttributes(attrs)
                    .setAudioFormat(AudioFormat.Builder().setSampleRate(HandsFreeCueSignal.SAMPLE_RATE).setChannelMask(AudioFormat.CHANNEL_OUT_MONO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
                    .setBufferSizeInBytes(pcm.size*2).setTransferMode(AudioTrack.MODE_STATIC).build()
                player=track
                // MODE_STATIC is not fully initialized until its first PCM write.
                check(track.state==AudioTrack.STATE_INITIALIZED || track.state==AudioTrack.STATE_NO_STATIC_DATA)
                val done=CountDownLatch(1)
                synchronized(lock){active=track;completion=done}
                track.setPlaybackPositionUpdateListener(object:AudioTrack.OnPlaybackPositionUpdateListener {
                    override fun onMarkerReached(track:AudioTrack){done.countDown()}
                    override fun onPeriodicNotification(track:AudioTrack){}
                },Handler(Looper.getMainLooper()))
                check(track.write(pcm,0,pcm.size,AudioTrack.WRITE_BLOCKING)==pcm.size)
                check(track.state==AudioTrack.STATE_INITIALIZED)
                check(track.setNotificationMarkerPosition(pcm.size)==AudioTrack.SUCCESS)
                check(track.setVolume(HandsFreeCueSignal.PLAYER_GAIN)==AudioTrack.SUCCESS)
                // A cancellation cannot stop the track and then race with a late play().
                val started=synchronized(lock){if(token!=generation.get())false else {track.play();true}}
                if(!started)return@execute
                val markerReached=done.await(500,TimeUnit.MILLISECONDS)
                synchronized(lock) {
                    if(token!=generation.get())return@execute
                    // The main looper may be busy while the mixer already finished.
                    // A missing marker is only a failure if the playback head is short.
                    check(markerReached || track.playbackHeadPosition>=pcm.size){"activation_cue_playback_timeout"}
                }
            }catch(_:InterruptedException){Thread.currentThread().interrupt();if(token==generation.get())failure()}
            catch(_:Exception){if(token==generation.get())failure()}
            finally {
                synchronized(lock){completion=null;active=null}
                try{player?.stop()}catch(_:Exception){}
                try{player?.release()}catch(_:Exception){}
            }
        }
    }
    fun cancel(){synchronized(lock){generation.incrementAndGet();executor.queue.clear();completion?.countDown();try{active?.stop()}catch(_:Exception){}}}
}
