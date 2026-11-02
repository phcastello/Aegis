package com.aegis.node

import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString.Companion.toByteString
import org.json.JSONObject
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/** One capture attempt. Never exposes partial text as a command. No passive connection. */
internal class HandsFreeRealtimeTranscription(
    private val transport: OkHttpClient,
    private val identity: NodeNativeSession.Session,
    private val checkIdentity: () -> Unit,
    val requestId: String = UUID.randomUUID().toString(),
    private val trace: HandsFreeTurnLatency? = null,
    private val finalTimeoutMilliseconds: Long = 12000,
    private val checkActive: () -> Unit = {},
    private val onPartial: (String) -> Unit = {}
) : AutoCloseable {
    private val lock = Any()
    private val completed = CountDownLatch(1)
    private var socket: WebSocket? = null
    private var failed = false
    private var closed = false
    private var committed = false
    private var ready = false
    private var totalBytes = 0L
    private var finalText: String? = null
    private val partialDisplay=HandsFreePartialDisplayLimiter()
    private fun displayPartial(value:String) { try { onPartial(value) } catch (_:Exception) { /* Presentation cannot fail STT. */ } }
    // OkHttp's pre-connect queue is bounded here too, independently of its 16 MiB cap.
    private val pending = ArrayDeque<ByteArray>()
    private var pendingBytes = 0

    init {
        checkIdentity()
        trace?.transcription(requestId); trace?.mark("stt_realtime_connect_started")
        val request = Request.Builder().url(identity.origin + "/api/voice/transcriptions/realtime?transcriptionRequestId=" + requestId)
            .header("Authorization", "AegisNode ${identity.credential}")
            .header("Cache-Control", "no-store")
            .apply { trace?.let { header("X-Aegis-Turn-Id", it.turnId) } }.build()
        socket = transport.newWebSocket(request, object : WebSocketListener() {
            override fun onMessage(webSocket: WebSocket, text: String) {
                try {
                    checkIdentity(); checkActive(); require(text.length <= 65536)
                    val event = JSONObject(text)
                    require(event.optString("transcriptionRequestId") == requestId)
                    synchronized(lock) {
                        if (closed || failed || finalText != null) return
                        when (event.getString("type")) {
                            "ready" -> {
                                check(!ready); ready = true
                                trace?.mark("stt_realtime_ready")
                                while (pending.isNotEmpty()) {
                                    val bytes = pending.removeFirst(); pendingBytes -= bytes.size
                                    try { check(webSocket.send(bytes.toByteString())) } finally { bytes.fill(0) }
                                }
                                if (committed) check(webSocket.send("{\"type\":\"commit\"}"))
                            }
                            "partial" -> if(ready)partialDisplay.offer(event.getString("text"))?.let{displayPartial(it)}
                            "final" -> {
                                check(committed && finalText == null)
                                finalText = event.getString("text")
                                partialDisplay.clear();displayPartial("")
                                completed.countDown()
                                trace?.mark("stt_response_received")
                            }
                            "error" -> failLocked()
                            else -> error("invalid_stt_event")
                        }
                    }
                } catch (_: Exception) { fail() }
            }
            override fun onFailure(webSocket: WebSocket, error: Throwable, response: Response?) { fail() }
            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                synchronized(lock) { if (finalText == null) failLocked() }
            }
        })
    }

    fun append(samples: ShortArray, count: Int) {
        require(count in 0..samples.size)
        synchronized(lock) {
            if (closed || failed) return
            try {
                checkActive(); check(!committed)
                require(totalBytes + count * 2 <= 720000) // Transport ceiling; controller applies its 20s post-activation bound.
                val bytes = ByteArray(count * 2)
                for (i in 0 until count) { bytes[i * 2] = samples[i].toByte(); bytes[i * 2 + 1] = (samples[i].toInt() shr 8).toByte() }
                totalBytes += bytes.size
                if (!ready) {
                    // A slow connection must not hold a growing microphone backlog.
                    if (pendingBytes + bytes.size > 160000) { bytes.fill(0); failLocked(); return }
                    for (offset in bytes.indices step 6400) {
                        pending.addLast(bytes.copyOfRange(offset,minOf(bytes.size,offset+6400)))
                    }
                    pendingBytes += bytes.size; bytes.fill(0)
                } else {
                    try {
                        val target = checkNotNull(socket)
                        check(target.queueSize() + bytes.size <= 160000)
                        for (offset in bytes.indices step 6400) check(target.send(bytes.toByteString(offset,minOf(6400,bytes.size-offset))))
                    } finally { bytes.fill(0) }
                }
            } catch (_: Exception) { failLocked() }
        }
    }

    /** Called exactly at local endpoint; a provider segment cannot authorize this transition. */
    fun commit() = synchronized(lock) {
        if (closed || failed) return@synchronized
        try {
            checkIdentity(); check(!committed); committed = true
            trace?.mark("stt_commit_requested")
            // Explicit manual commit supports short real utterances. Never wait or pad audio.
            if (totalBytes == 0L) { failLocked(); return@synchronized }
            if (ready) check(checkNotNull(socket).send("{\"type\":\"commit\"}"))
        } catch (_: Exception) { failLocked() }
    }

    /** null authorizes one batch attempt using the already captured WAV, never a second chat. */
    fun awaitFinal(): String? {
        if (!completed.await(finalTimeoutMilliseconds, TimeUnit.MILLISECONDS)) fail()
        checkIdentity()
        return synchronized(lock) { if (!committed || failed || closed) null else finalText }
    }
    private fun failLocked() {
        if(failed || closed || finalText!=null)return
        failed = true; pending.forEach { it.fill(0) }; pending.clear(); pendingBytes = 0
        partialDisplay.clear();displayPartial("")
        socket?.cancel(); completed.countDown()
    }
    private fun fail() = synchronized(lock) { if (!closed) failLocked() }
    override fun close() = synchronized(lock) {
        if (closed) return@synchronized
        closed = true; pending.forEach { it.fill(0) }; pending.clear(); pendingBytes = 0
        partialDisplay.clear();displayPartial("")
        socket?.cancel(); completed.countDown()
    }
}
