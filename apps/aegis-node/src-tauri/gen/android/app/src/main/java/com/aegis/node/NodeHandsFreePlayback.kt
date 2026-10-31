package com.aegis.node

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioFocusRequest
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import android.os.Build
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/** Existing server PCM/voice, bounded in memory. Completion means every sample played. */
internal class HandsFreePlayback(private val context:Context) {
    @Volatile private var track:AudioTrack?=null
    @Volatile private var completion:CountDownLatch?=null
    private val generation=java.util.concurrent.atomic.AtomicInteger(0)
    fun cancel(){generation.incrementAndGet();completion?.countDown();try{track?.pause();track?.flush();track?.stop()}catch(_:Exception){}}
    fun play(pcm:ByteArray,stillCurrent:()->Boolean={true}) {
        require(pcm.isNotEmpty() && pcm.size%2==0 && pcm.size<=24000*2*180)
        val token=generation.get()
        fun current()=generation.get()==token && stillCurrent()
        check(current())
        val manager=context.getSystemService(AudioManager::class.java)
        val attributes=AudioAttributes.Builder().setUsage(if(Build.VERSION.SDK_INT>=26)AudioAttributes.USAGE_ASSISTANT else AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build()
        val focusListener=AudioManager.OnAudioFocusChangeListener{if(it<0)cancel()}
        val request=if(Build.VERSION.SDK_INT>=26)AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN_TRANSIENT).setAudioAttributes(attributes).setOnAudioFocusChangeListener(focusListener,android.os.Handler(android.os.Looper.getMainLooper())).build() else null
        var player:AudioTrack?=null
        var granted=false
        try {
            val focus=if(request!=null)manager.requestAudioFocus(request) else @Suppress("DEPRECATION") manager.requestAudioFocus(focusListener,AudioManager.STREAM_MUSIC,AudioManager.AUDIOFOCUS_GAIN_TRANSIENT)
            check(focus==AudioManager.AUDIOFOCUS_REQUEST_GRANTED){"audio_focus_unavailable"};granted=true
            val min=AudioTrack.getMinBufferSize(24000,AudioFormat.CHANNEL_OUT_MONO,AudioFormat.ENCODING_PCM_16BIT)
            val output=AudioTrack.Builder().setAudioAttributes(attributes).setAudioFormat(AudioFormat.Builder().setSampleRate(24000).setChannelMask(AudioFormat.CHANNEL_OUT_MONO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build()).setBufferSizeInBytes(maxOf(min,24000)).setTransferMode(AudioTrack.MODE_STREAM).build()
            player=output;check(output.state==AudioTrack.STATE_INITIALIZED)
            val done=CountDownLatch(1);completion=done;track=output
            check(current())
            output.setPlaybackPositionUpdateListener(object:AudioTrack.OnPlaybackPositionUpdateListener {
                override fun onMarkerReached(track:AudioTrack){done.countDown()}
                override fun onPeriodicNotification(track:AudioTrack){}
            },android.os.Handler(android.os.Looper.getMainLooper()))
            output.notificationMarkerPosition=pcm.size/2;output.play()
            var offset=0
            while(offset<pcm.size && current()) {
                val count=output.write(pcm,offset,minOf(8192,pcm.size-offset),AudioTrack.WRITE_BLOCKING)
                check(count>0){"audio_playback_failed"};offset+=count
            }
            // Marker is driven by the playback head, not final network byte or AudioTrack.write.
            val timeout=(pcm.size/48L+5000).coerceAtMost(185000)
            check(current() && done.await(timeout,TimeUnit.MILLISECONDS) && current()) {"audio_playback_incomplete"}
        } finally {
            completion=null;track=null;try{player?.stop()}catch(_:Exception){};player?.release()
            if(granted){if(request!=null)manager.abandonAudioFocusRequest(request) else @Suppress("DEPRECATION") manager.abandonAudioFocus(focusListener)}
        }
    }
}
