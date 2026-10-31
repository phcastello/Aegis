package com.aegis.node

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayOutputStream
import java.util.concurrent.CancellationException
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ExecutionException
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/** Simulated device tests validate ownership/drain/backpressure; they do not play physical audio. */
class NodeHandsFreePcmStreamTest {
    @Test fun usefulPcmIgnoresLeadingSilenceAndWaitsForSplitSignedSampleDelivery() {
        val device=Device(autoPlay=true)
        val events=mutableListOf<String>()
        val framer=HandsFreePcm16Framer(HandsFreeStreamingPcmSink({device}),
            onFirstPcmAvailable={events.add("available")},
            onFirstUsefulPcmAvailable={frame->events.add("useful:$frame")},
            onFirstUsefulPcmDelivered={events.add("delivered")})
        val pcm=byteArrayOf(0,0,1,0,-63,-1,63,0,-64,-1,-1,127)
        framer.accept(pcm.copyOfRange(0,9),9)
        assertEquals(listOf("available"),events)
        assertEquals(4,framer.samples)
        framer.accept(pcm.copyOfRange(9,10),1)
        assertEquals(listOf("available","useful:4","delivered"),events)
        framer.accept(pcm.copyOfRange(10,12),2);framer.finish()
        assertEquals(listOf("available","useful:4","delivered"),events)
        assertArrayEquals(pcm,device.recorded.toByteArray())
    }
    @Test fun failedUsefulPcmWriteNeverReportsDelivery() {
        val events=mutableListOf<String>()
        val sink=object:HandsFreePcmSink {
            override fun write(bytes:ByteArray,offset:Int,count:Int){throw CancellationException()}
            override fun finish(){}
            override fun abort(){}
            override fun close(){}
        }
        val framer=HandsFreePcm16Framer(sink,onFirstUsefulPcmAvailable={events.add("available")},onFirstUsefulPcmDelivered={events.add("delivered")})
        assertThrows(CancellationException::class.java){framer.accept(byteArrayOf(0,-128),2)}
        assertEquals(listOf("available"),events)
        assertEquals(0,framer.samples)
    }
    @Test fun oddHttpFragmentsReconstructEveryLittleEndianSample() {
        val device=Device(autoPlay=true)
        val sink=HandsFreeStreamingPcmSink({device})
        val framer=HandsFreePcm16Framer(sink)
        val expected=ByteArray(50000){(it%251).toByte()}
        val lengths=intArrayOf(1,3,8191,5,2,7,8192,13)
        var offset=0;var index=0
        while(offset<expected.size) {
            val size=minOf(lengths[index++%lengths.size],expected.size-offset)
            framer.accept(expected.copyOfRange(offset,offset+size),size);offset+=size
        }
        framer.finish();sink.close()
        assertArrayEquals(expected,device.recorded.toByteArray())
        assertEquals(expected.size/2L,framer.samples)
        assertTrue(device.maxWrite<=8192)
        assertTrue(device.closed)
    }

    @Test fun oddEofAndEmptyPcmCannotCompletePlayback() {
        for(data in listOf(byteArrayOf(),byteArrayOf(1),byteArrayOf(1,2,3))) {
            val device=Device(autoPlay=true);val sink=HandsFreeStreamingPcmSink({device})
            val framer=HandsFreePcm16Framer(sink)
            framer.accept(data,data.size)
            assertThrows(IllegalArgumentException::class.java){framer.finish()}
            sink.abort();sink.close()
            assertEquals(0,device.queuedFrames)
        }
    }

    @Test fun eofAndSuccessfulWritesDoNotMeanTheSamplesHavePlayed() {
        val device=Device(autoPlay=false)
        val events=java.util.concurrent.CopyOnWriteArrayList<String>()
        val sink=HandsFreeStreamingPcmSink({device},onPlaybackEvent={events.add(it)})
        sink.write(byteArrayOf(1,2,3,4),0,4)
        assertEquals(listOf("player_play_called"),events)
        val executor=Executors.newSingleThreadExecutor()
        try {
            val drained=executor.submit{sink.finish()}
            assertTrue(device.markerArmed.await(5,TimeUnit.SECONDS))
            assertFalse(drained.isDone)
            assertEquals(0,device.playedFrames())
            assertEquals(listOf("player_play_called"),events)
            device.advance(1);assertFalse(drained.isDone)
            device.advance(1);drained.get(5,TimeUnit.SECONDS)
            assertEquals(2,device.playedFrames())
            assertEquals(listOf("player_play_called","player_drained"),events)
        }finally{sink.abort();sink.close();executor.shutdownNow()}
    }

    @Test fun deviceBackpressureBoundsTheQueueAndAbortUnblocksTheProducer() {
        val device=Device(autoPlay=false,capacityFrames=4)
        val sink=HandsFreeStreamingPcmSink({device})
        val executor=Executors.newSingleThreadExecutor()
        try {
            val producer=executor.submit{sink.write(ByteArray(100000),0,100000)}
            assertTrue(device.backpressured.await(5,TimeUnit.SECONDS))
            assertFalse(producer.isDone)
            assertEquals(4,device.queuedFrames)
            sink.abort()
            assertThrows(ExecutionException::class.java){producer.get(5,TimeUnit.SECONDS)}
            assertEquals(0,device.queuedFrames)
            assertTrue(device.maxQueued<=4)
        }finally{sink.abort();sink.close();executor.shutdownNow()}
    }

    @Test fun cancelDuringDrainNeverLooksLikeNormalCompletion() {
        val device=Device(autoPlay=false)
        val sink=HandsFreeStreamingPcmSink({device})
        sink.write(byteArrayOf(1,2,3,4),0,4)
        val executor=Executors.newSingleThreadExecutor()
        try {
            val drained=executor.submit{sink.finish()}
            assertTrue(device.markerArmed.await(5,TimeUnit.SECONDS))
            sink.abort()
            val error=assertThrows(ExecutionException::class.java){drained.get(5,TimeUnit.SECONDS)}
            assertTrue(error.cause is CancellationException)
            assertEquals(0,device.queuedFrames)
        }finally{sink.close();executor.shutdownNow()}
    }

    @Test fun markerMissRaceIsCoveredByASecondPlaybackHeadRead() {
        val device=Device(autoPlay=false,playWhileArming=true)
        val sink=HandsFreeStreamingPcmSink({device})
        sink.write(byteArrayOf(1,2,3,4),0,4)
        // Device reaches end before marker callback registration. No callback is delivered.
        sink.finish();assertEquals(2,device.playedFrames());sink.close()
    }

    @Test fun cancelledLazyStreamAndOldCloseCannotContaminateTheNextTurn() {
        val created=mutableListOf<Device>()
        val player=HandsFreePlayback { _ -> Device(autoPlay=true).also{created.add(it)} }
        val neverStarted=player.openStream{true}
        player.cancel()
        assertThrows(CancellationException::class.java){neverStarted.write(byteArrayOf(1,2),0,2)}
        assertTrue(created.isEmpty());neverStarted.close()
        val old=player.openStream{true};old.write(byteArrayOf(1,2),0,2)
        val fresh=player.openStream{true};fresh.write(byteArrayOf(3,4,5,6),0,4)
        old.close();old.abort()
        assertFalse(created[1].closed)
        assertArrayEquals(byteArrayOf(3,4,5,6),created[1].recorded.toByteArray())
        fresh.finish();fresh.close()
        assertTrue(created.all{it.closed})
    }

    @Test fun lateFocusLossFromAnOldTrackCannotCancelTheNextTurn() {
        val created=mutableListOf<Device>();val focusLoss=mutableListOf<()->Unit>()
        val player=HandsFreePlayback { onLoss ->
            focusLoss.add(onLoss);Device(autoPlay=true).also{created.add(it)}
        }
        val old=player.openStream{true};old.write(byteArrayOf(1,2),0,2)
        val fresh=player.openStream{true};fresh.write(byteArrayOf(3,4),0,2)
        focusLoss[0]()
        fresh.write(byteArrayOf(5,6),0,2)
        assertFalse(created[1].closed)
        assertArrayEquals(byteArrayOf(3,4,5,6),created[1].recorded.toByteArray())
        fresh.finish();old.close();fresh.close()
        // The current track still reacts to its own focus loss and drops pending samples.
        val current=player.openStream{true};current.write(byteArrayOf(7,8),0,2)
        focusLoss[2]()
        assertThrows(CancellationException::class.java){current.write(byteArrayOf(9,10),0,2)}
        current.close()
    }

    internal class Device(
        private val autoPlay:Boolean,
        private val capacityFrames:Int=24000,
        private val playWhileArming:Boolean=false
    ):HandsFreePcmDevice {
        private val lock=java.lang.Object()
        val recorded=ByteArrayOutputStream()
        val markerArmed=CountDownLatch(1)
        val firstWrite=CountDownLatch(1)
        val backpressured=CountDownLatch(1)
        @Volatile var queuedFrames=0;private set
        @Volatile var maxQueued=0;private set
        @Volatile var maxWrite=0;private set
        @Volatile var closed=false;private set
        private var stopped=false
        private var played=0L
        private var marker=Int.MAX_VALUE
        private var reached:(()->Unit)?=null
        override fun write(bytes:ByteArray,offset:Int,count:Int):Int=synchronized(lock) {
            val deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(5)
            while(!stopped && queuedFrames>=capacityFrames) {
                backpressured.countDown()
                check(System.nanoTime()<deadline){"Test device timed out"}
                lock.wait(1000)
            }
            check(!stopped && !closed)
            val accepted=minOf(count,(capacityFrames-queuedFrames)*2)
            require(accepted>0 && accepted%2==0)
            recorded.write(bytes,offset,accepted);maxWrite=maxOf(maxWrite,accepted)
            queuedFrames+=accepted/2;maxQueued=maxOf(maxQueued,queuedFrames)
            if(autoPlay){played+=queuedFrames;queuedFrames=0}
            firstWrite.countDown();accepted
        }
        override fun playedFrames():Long=synchronized(lock){played}
        override fun endMarker(frame:Int,reached:()->Unit)=synchronized(lock) {
            if(playWhileArming){played+=queuedFrames;queuedFrames=0;markerArmed.countDown();return@synchronized}
            marker=frame;this.reached=reached;markerArmed.countDown()
            if(played>=marker)reached()
        }
        fun advance(frames:Int)=synchronized(lock) {
            val consumed=minOf(frames,queuedFrames);queuedFrames-=consumed;played+=consumed
            lock.notifyAll();if(played>=marker)reached?.invoke()
        }
        override fun play(){}
        override fun stop()=synchronized(lock){stopped=true;queuedFrames=0;lock.notifyAll()}
        override fun close(){stop();closed=true}
    }
}
