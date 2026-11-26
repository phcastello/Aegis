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

    @Test fun speechGainIsLinearAtOrdinaryLevelsAndMonotonicBoundedAtPeaks() {
        val values = ShortArray(65536) { (it - 32768).toShort() }
        val raw = encode(values)
        val before = raw.copyOf()
        val output = ByteArray(raw.size)
        HandsFreePlaybackLevel.amplify(raw, 0, raw.size, output)
        assertArrayEquals(before, raw)
        val boosted = decode(output)
        assertEquals(0, boosted[32768].toInt())
        assertEquals(1600, boosted[33768].toInt())
        assertEquals(-1600, boosted[31768].toInt())
        assertEquals(16000, boosted[42768].toInt())
        assertTrue(boosted.all { kotlin.math.abs(it.toInt()) < 30000 })
        for (i in 1 until boosted.size) assertTrue(boosted[i] >= boosted[i - 1])
        for (i in 1..32767) assertEquals(-boosted[32768 + i].toInt(), boosted[32768 - i].toInt())
    }

    @Test fun speechGainMatchesActualEchoReferenceAcrossFragmentsAndShortWrites() {
        val echo = HandsFreeBargeInEcho()
        val device = Device(autoPlay=true, capacityFrames=37)
        val player = HandsFreePlayback { _ -> device }
        val sink = player.openStream({ true }, {}, echo=echo)
        val framer = HandsFreePcm16Framer(sink)
        val source = encode(ShortArray(6100) { ((it * 977) % 55001 - 27500).toShort() })
        val before = source.copyOf()
        val lengths = intArrayOf(1, 3, 9505, 2, 9, 113)
        var sent = 0
        var index = 0
        while (sent < source.size) {
            val count = minOf(lengths[index++ % lengths.size], source.size - sent)
            framer.accept(source.copyOfRange(sent, sent + count), count)
            sent += count
        }
        framer.finish()
        sink.close()
        assertEquals(source.size / 2L, framer.samples)
        assertEquals(source.size / 2L, device.playedFrames())
        assertArrayEquals(before, source)
        val expected = ByteArray(source.size)
        HandsFreePlaybackLevel.amplify(source, 0, source.size, expected)
        val actual = device.recorded.toByteArray()
        assertArrayEquals(expected, actual)
        assertTrue(device.maxWrite <= 74)
        val played = decode(actual)
        // Independently resample the actual emitted device bytes at multiple heads.
        // Recomputing gain after the echo reference was offered fails these checks.
        for (head in listOf(720L, 2411L, 5305L, 6100L)) {
            val rendered = ShortArray(320)
            assertTrue(echo.copyRender(rendered, head))
            val expectedRender = ShortArray(rendered.size) { i ->
                val halfFrame = head * 2 - (rendered.size - i) * 3L
                val frame = (halfFrame / 2).toInt()
                if (halfFrame % 2 == 0L) played[frame]
                else ((played[frame].toInt() + played[frame + 1].toInt()) / 2).toShort()
            }
            assertArrayEquals(expectedRender, rendered)
        }
    }

    @Test fun normalPlaybackWithoutEchoKeepsTheOriginalPcmAndOffset() {
        val device = Device(autoPlay=true, capacityFrames=2)
        val player = HandsFreePlayback { _ -> device }
        val sink = player.openStream { true }
        val raw = encode(shortArrayOf(7, -16000, 32767, -32768, 26000, -7))
        val before = raw.copyOf()
        sink.write(raw, 2, raw.size - 4)
        sink.finish()
        sink.close()
        assertArrayEquals(before, raw)
        assertArrayEquals(raw.copyOfRange(2, raw.size - 2), device.recorded.toByteArray())
    }

    private fun encode(values: ShortArray): ByteArray = ByteArray(values.size * 2).also { bytes ->
        values.forEachIndexed { i, value ->
            bytes[2 * i] = value.toByte()
            bytes[2 * i + 1] = (value.toInt() shr 8).toByte()
        }
    }

    private fun decode(bytes: ByteArray): ShortArray = ShortArray(bytes.size / 2) { i ->
        ((bytes[2 * i].toInt() and 255) or (bytes[2 * i + 1].toInt() shl 8)).toShort()
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

    @Test fun bargeInGetsPlaybackHeadBeforeAnyAudioAndLosesItOnAbort() {
        val backing = Device(autoPlay = true)
        val events = mutableListOf<String>()
        val device = object : HandsFreePcmDevice by backing {
            override fun play() { events.add("play"); backing.play() }
            override fun write(bytes: ByteArray, offset: Int, count: Int): Int {
                events.add("write")
                return backing.write(bytes, offset, count)
            }
        }
        lateinit var head: () -> Long?
        val sink = HandsFreeStreamingPcmSink({ device }, onPlaybackReady = {
            head = it
            assertEquals(0L, head())
            events.add("ready")
        })
        try {
            sink.write(byteArrayOf(1, 2, 3, 4), 0, 4)
            sink.write(byteArrayOf(5, 6), 0, 2)
            assertEquals(listOf("ready", "play", "write", "write"), events)
            assertEquals(3L, head())
            sink.abort()
            assertNull(head())
            assertThrows(CancellationException::class.java) { sink.finish() }
        } finally {
            sink.close()
        }
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

    @Test fun pauseBeforeDeviceCreationKeepsTheFirstAudioOnTheOriginalTurn() {
        val created = CountDownLatch(1)
        val writerStarted = CountDownLatch(1)
        val device = Device(autoPlay = true)
        val sink = HandsFreeStreamingPcmSink({ created.countDown(); device })
        val executor = Executors.newSingleThreadExecutor()
        try {
            assertTrue(sink.pause())
            val producer = executor.submit {
                writerStarted.countDown()
                sink.write(byteArrayOf(1, 2, 3, 4), 0, 4)
                sink.finish()
            }
            assertTrue(writerStarted.await(5, TimeUnit.SECONDS))
            assertFalse(created.await(100, TimeUnit.MILLISECONDS))
            assertTrue(sink.resume())
            producer.get(5, TimeUnit.SECONDS)
            assertArrayEquals(byteArrayOf(1, 2, 3, 4), device.recorded.toByteArray())
            assertFalse(sink.pause())
            assertFalse(sink.resume())
        } finally { sink.close(); executor.shutdownNow() }
    }

    @Test fun pauseDuringPlaybackReadyDefersFirstPlayAndKeepsTheDevice() {
        val device = Device(autoPlay = true)
        val ready = CountDownLatch(1)
        val events = java.util.concurrent.CopyOnWriteArrayList<String>()
        val executor = Executors.newSingleThreadExecutor()
        lateinit var sink: HandsFreeStreamingPcmSink
        sink = HandsFreeStreamingPcmSink({ device },
            onPlaybackReady = {
                assertTrue(sink.pause())
                ready.countDown()
            },
            onPlaybackEvent = { events.add(it) })
        try {
            val producer = executor.submit {
                sink.write(byteArrayOf(1, 2), 0, 2)
                sink.finish()
            }
            assertTrue(ready.await(5, TimeUnit.SECONDS))
            assertFalse(device.firstWrite.await(100, TimeUnit.MILLISECONDS))
            assertTrue(events.isEmpty())
            assertTrue(sink.resume())
            producer.get(5, TimeUnit.SECONDS)
            assertArrayEquals(byteArrayOf(1, 2), device.recorded.toByteArray())
            assertEquals(1, events.count { it == "player_play_called" })
            assertEquals(1, events.count { it == "player_drained" })
        } finally { sink.close(); executor.shutdownNow() }
    }

    @Test fun pauseDuringBlockingWritePreservesPartialTransferAndQueuedFrames() {
        val device = Device(autoPlay = false, capacityFrames = 2)
        val sink = HandsFreeStreamingPcmSink({ device })
        val expected = byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8)
        val executor = Executors.newSingleThreadExecutor()
        try {
            val producer = executor.submit { sink.write(expected, 0, expected.size) }
            assertTrue(device.backpressured.await(5, TimeUnit.SECONDS))
            assertEquals(2, device.queuedFrames)
            assertTrue(sink.pause())
            assertTrue(device.pausedWrite.await(5, TimeUnit.SECONDS))
            device.advance(2)
            assertEquals(0L, device.playedFrames())
            assertEquals(2, device.queuedFrames)
            assertFalse(producer.isDone)
            assertTrue(sink.pause()) // idempotent
            assertTrue(sink.resume())
            device.advance(2)
            producer.get(5, TimeUnit.SECONDS)
            assertArrayEquals(expected, device.recorded.toByteArray())
            assertEquals(2L, device.playedFrames())
            assertEquals(2, device.queuedFrames)
            device.advance(2)
            sink.finish()
            assertEquals(4L, device.playedFrames())
            assertTrue(device.maxQueued <= 2)
        } finally { sink.abort(); sink.close(); executor.shutdownNow() }
    }

    @Test fun pausedDrainUsesPlaybackTimeAndDoesNotDiscardUnheardAudio() {
        val backing = Device(autoPlay = false)
        val observeResume = java.util.concurrent.atomic.AtomicBoolean(false)
        val readAfterResume = CountDownLatch(1)
        val device = object : HandsFreePcmDevice by backing {
            override fun playedFrames(): Long {
                if (observeResume.get()) readAfterResume.countDown()
                return backing.playedFrames()
            }
        }
        val now = java.util.concurrent.atomic.AtomicLong(1000L)
        val events = java.util.concurrent.CopyOnWriteArrayList<String>()
        val sink = HandsFreeStreamingPcmSink({ device }, onPlaybackEvent = { events.add(it) }, nanoTime = now::get)
        val executor = Executors.newSingleThreadExecutor()
        try {
            val expected = byteArrayOf(1, 2, 3, 4, 5, 6)
            sink.write(expected, 0, expected.size)
            backing.advance(1)
            val completed = executor.submit { sink.finish() }
            assertTrue(backing.markerArmed.await(5, TimeUnit.SECONDS))
            assertTrue(sink.pause())
            now.addAndGet(TimeUnit.SECONDS.toNanos(20))
            backing.advance(2)
            assertEquals(1L, backing.playedFrames())
            assertEquals(2, backing.queuedFrames)
            assertFalse(completed.isDone)
            observeResume.set(true)
            assertTrue(sink.resume())
            assertTrue(readAfterResume.await(5, TimeUnit.SECONDS))
            // A wall-clock drain deadline would have already expired by 15 s.
            // The producer must remain blocked until the queued frames play.
            assertThrows(java.util.concurrent.TimeoutException::class.java) {
                completed.get(100, TimeUnit.MILLISECONDS)
            }
            backing.advance(2)
            completed.get(5, TimeUnit.SECONDS)
            assertArrayEquals(expected, backing.recorded.toByteArray())
            assertEquals(3L, backing.playedFrames())
            assertEquals(listOf("player_play_called", "player_drained"), events)
        } finally { sink.abort(); sink.close(); executor.shutdownNow() }
    }

    @Test fun abortAndCloseAwakenPausedWriterOrDrainWithoutAResume() {
        val executor = Executors.newSingleThreadExecutor()
        try {
            for (useAbort in listOf(true, false)) {
                val device = Device(autoPlay = false, capacityFrames = 2)
                val sink = HandsFreeStreamingPcmSink({ device })
                try {
                    val producer = executor.submit { sink.write(ByteArray(12), 0, 12) }
                    assertTrue(device.backpressured.await(5, TimeUnit.SECONDS))
                    assertTrue(sink.pause())
                    if (useAbort) sink.abort() else sink.close()
                    val failure = assertThrows(ExecutionException::class.java) { producer.get(5, TimeUnit.SECONDS) }
                    assertTrue(failure.cause is CancellationException)
                    assertFalse(sink.resume())
                    assertEquals(0, device.queuedFrames)
                } finally { sink.close() }
            }
            val device = Device(autoPlay = false)
            val sink = HandsFreeStreamingPcmSink({ device })
            try {
                sink.write(byteArrayOf(1, 2, 3, 4), 0, 4)
                val drained = executor.submit { sink.finish() }
                assertTrue(device.markerArmed.await(5, TimeUnit.SECONDS))
                assertTrue(sink.pause())
                sink.abort()
                val failure = assertThrows(ExecutionException::class.java) { drained.get(5, TimeUnit.SECONDS) }
                assertTrue(failure.cause is CancellationException)
                assertFalse(sink.resume())
            } finally { sink.close() }
        } finally { executor.shutdownNow() }
    }

    @Test fun oldPausedTrackCannotResumeAfterNewTurnOrFocusLoss() {
        val devices = mutableListOf<Device>()
        val focusLoss = mutableListOf<() -> Unit>()
        val player = HandsFreePlayback { onLoss ->
            focusLoss.add(onLoss)
            Device(autoPlay = true).also { devices.add(it) }
        }
        val old = player.openStream { true }
        var fresh: HandsFreePcmSink? = null
        try {
            old.write(byteArrayOf(1, 2), 0, 2)
            assertTrue(old.pause())
            val next = player.openStream { true }.also { fresh = it }
            assertFalse(old.resume())
            old.close()
            next.write(byteArrayOf(3, 4), 0, 2)
            assertFalse(devices[1].closed)
            assertArrayEquals(byteArrayOf(3, 4), devices[1].recorded.toByteArray())
            assertTrue(next.pause())
            focusLoss[1]()
            assertFalse(next.resume())
        } finally { old.close(); fresh?.close() }
    }

    @Test fun zeroDeviceWriteWithoutPauseStillFails() {
        val backing = Device(autoPlay = true)
        val device = object : HandsFreePcmDevice by backing {
            override fun write(bytes: ByteArray, offset: Int, count: Int) = 0
        }
        val sink = HandsFreeStreamingPcmSink({ device })
        try {
            assertThrows(IllegalStateException::class.java) {
                sink.write(byteArrayOf(1, 2), 0, 2)
            }
        } finally { sink.close() }
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
        val pausedWrite=CountDownLatch(1)
        @Volatile var queuedFrames=0;private set
        @Volatile var maxQueued=0;private set
        @Volatile var maxWrite=0;private set
        @Volatile var closed=false;private set
        private var stopped=false
        private var paused=false
        private var played=0L
        private var marker=Int.MAX_VALUE
        private var reached:(()->Unit)?=null
        override fun write(bytes:ByteArray,offset:Int,count:Int):Int=synchronized(lock) {
            val deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(5)
            while(!stopped && !paused && queuedFrames>=capacityFrames) {
                backpressured.countDown()
                check(System.nanoTime()<deadline){"Test device timed out"}
                lock.wait(1000)
            }
            check(!stopped && !closed)
            if(paused){pausedWrite.countDown();return@synchronized 0}
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
            if(paused)return@synchronized
            val consumed=minOf(frames,queuedFrames);queuedFrames-=consumed;played+=consumed
            lock.notifyAll();if(played>=marker)reached?.invoke()
        }
        override fun play()=synchronized(lock){paused=false;lock.notifyAll()}
        override fun pause():Boolean=synchronized(lock) {
            if(stopped || closed)return@synchronized false
            paused=true;lock.notifyAll();true
        }
        override fun stop()=synchronized(lock){stopped=true;queuedFrames=0;lock.notifyAll()}
        override fun close(){stop();closed=true}
    }
}

