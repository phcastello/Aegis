package com.aegis.node

import java.io.IOException
import java.net.InetSocketAddress
import java.net.Proxy
import java.util.UUID
import java.util.concurrent.TimeUnit
import kotlin.math.log10
import kotlin.math.sqrt
import okhttp3.Call
import okhttp3.Callback
import okhttp3.Connection
import okhttp3.EventListener
import okhttp3.Handshake
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.Protocol
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import org.json.JSONObject

/** Runs on the candidate verifier worker. The recorder keeps the command prefix locally
 * until the coordinator confirms that this Node owns the automatic activation. */
internal class HandsFreeWakeElection(
    private val readSession: () -> NodeNativeSession.Session?,
    private val clock: () -> Long,
    private val deadlineMs: Long,
    private val current: () -> Boolean,
    private val includeConfidence: Boolean
) : AutoCloseable {
    enum class Decision { GRANTED, SUPPRESSED, UNAVAILABLE }
    data class CoordinatorTiming(val serverWaitMs: Long, val transportOverheadMs: Long,
        val authenticationMs: Long? = null)
    data class TransportTiming(val dnsMs: Long?, val connectMs: Long?, val tlsMs: Long?,
        val requestToHeadersMs: Long?, val responseBodyMs: Long?, val reused: Boolean?)

    val activationId = UUID.randomUUID().toString()
    @Volatile private var authority: NodeNativeSession.Session? = null
    private var inFlight: Call? = null
    @Volatile private var closed = false
    @Volatile private var granted = false
    @Volatile var episodeId: String? = null
        private set
    @Volatile var coordinatorTiming: CoordinatorTiming? = null
        private set
    @Volatile var transportTiming: TransportTiming? = null
        private set
    @Volatile var identityPreparationMs = 0L
        private set
    @Volatile var identityResponseRecheckMs = 0L
        private set
    @Volatile var identityActivationRecheckMs = 0L
        private set

    fun claim(window: HandsFreeVerificationWindow, score: Float): Decision {
        val identityStarted = clock()
        val session = readSession()
        identityPreparationMs = (clock() - identityStarted).coerceAtLeast(0)
        if (session == null) return Decision.UNAVAILABLE
        val remaining = deadlineMs - clock()
        if (!current() || closed || remaining !in 1..MAX_AGE_MS) return Decision.UNAVAILABLE
        val payload = JSONObject().put("activationId", activationId)
        signalToNoiseDb(window)?.let { payload.put("snrDb", it) }
        if (includeConfidence) payload.put("confidence", score.toDouble())
        // Credit only the time already spent after the primary decision. The deadline
        // remains unchanged; older servers ignore the optional header.
        val request = request(session, "/claim")
            .header("X-Aegis-Detection-Age-Ms", (MAX_AGE_MS - remaining).toString())
            .post(payload.toString().toRequestBody(JSON)).build()
        // The event listener observes this one request on the shared pool. It
        // uses no addresses, URL, payload, timing probes, or separate connections.
        val trace = WakeTransportTrace(clock)
        val call = NodeHandsFreeBackend.transport.newBuilder().eventListener(trace).build().newCall(request)
        call.timeout().timeout(remaining, TimeUnit.MILLISECONDS)
        synchronized(this) {
            if (closed || !current()) return Decision.UNAVAILABLE
            authority = session
            inFlight = call
        }
        return try {
            val callStartedAt = clock()
            call.execute().use { response ->
                if (!response.isSuccessful || !responseAuthority()) return Decision.UNAVAILABLE
                val bytes = response.body?.byteStream()?.readBytesBounded(2048) ?: return Decision.UNAVAILABLE
                val outcome = JSONObject(String(bytes, Charsets.UTF_8))
                if (outcome.optString("activationId") != activationId || !responseAuthority()) return Decision.UNAVAILABLE
                val decision = when (outcome.optString("decision")) {
                    "granted" -> {
                        coordinatorTiming = parseCoordinatorTiming(response, clock() - callStartedAt)
                        transportTiming = trace.snapshot()
                        granted = true
                        Decision.GRANTED
                    }
                    "suppressed" -> Decision.SUPPRESSED
                    else -> Decision.UNAVAILABLE
                }
                if (decision != Decision.UNAVAILABLE) rememberEpisode(outcome)
                decision
            }
        } catch (_: Exception) {
            Decision.UNAVAILABLE
        } finally {
            synchronized(this) { inFlight = null }
        }
    }

    private fun rememberEpisode(outcome: JSONObject) {
        val value = outcome.opt("episodeId") as? String ?: return
        val id = try { UUID.fromString(value).toString() } catch (_: IllegalArgumentException) { return }
        if (!id.equals(value, ignoreCase = true) || id == "00000000-0000-0000-0000-000000000000") return
        synchronized(this) {
            if (!closed) episodeId = id
        }
    }

    fun grantedIdentity(): String = NodeNativeSession.fingerprint(checkNotNull(authority))

    /** Recheck at the activation boundary, including a grant queued behind a blocking audio read. */
    fun canActivate(): Boolean {
        if (!granted) return false
        val started = clock()
        return try { currentAuthority() }
        finally { identityActivationRecheckMs += (clock() - started).coerceAtLeast(0) }
    }

    private fun responseAuthority(): Boolean {
        val started = clock()
        return try { currentAuthority() }
        finally { identityResponseRecheckMs += (clock() - started).coerceAtLeast(0) }
    }

    private fun currentAuthority(): Boolean = !closed && current() && deadlineMs - clock() in 1..MAX_AGE_MS &&
        authority?.let { it == readSession() } == true

    override fun close() {
        val session = synchronized(this) {
            if (closed) return
            closed = true
            val pending = inFlight ?: return
            pending.cancel()
            authority
        } ?: return
        // Cancellation may race the POST at the server. Pending ownership expires even
        // if the best-effort withdrawal cannot reach it.
        if (session != readSession()) return
        val cancel = NodeHandsFreeBackend.transport.newCall(request(session, "/$activationId").delete().build())
        cancel.timeout().timeout(1, TimeUnit.SECONDS)
        cancel.enqueue(object : Callback {
            override fun onFailure(call: Call, e: IOException) {}
            override fun onResponse(call: Call, response: Response) { response.close() }
        })
    }

    private fun request(session: NodeNativeSession.Session, suffix: String) = Request.Builder()
        .url(session.origin.trimEnd('/') + "/api/nodes/me/voice-activations" + suffix)
        .header("Authorization", "AegisNode ${session.credential}")
        .header("Accept", "application/json")
        .header("Cache-Control", "no-store")

    companion object {
        const val MAX_AGE_MS = 2000L
        private val JSON = "application/json".toMediaType()

        /** Optional server instrumentation cannot change an otherwise valid decision.
         * Ignore ambiguous, malformed and implausible values rather than inventing a split. */
        private fun parseTiming(headers: List<String>, limitMs: Long): Long? {
            val value = headers.singleOrNull() ?: return null
            if (value.isEmpty() || value.any { it !in '0'..'9' }) return null
            return value.toLongOrNull()?.takeIf { it in 0..MAX_AGE_MS && it <= limitMs }
        }

        private fun parseCoordinatorTiming(response: Response, roundTripMs: Long): CoordinatorTiming? {
            val serverWait = parseTiming(response.headers.values("X-Aegis-Election-Wait-Ms"), roundTripMs) ?: return null
            val overhead = roundTripMs - serverWait
            val auth = parseTiming(response.headers.values("X-Aegis-Node-Auth-Ms"), overhead)
            return CoordinatorTiming(serverWait, overhead, auth)
        }

        /** Shared with Windows: 500 ms ending at the wake sample vs the preceding 1000 ms.
         * Omit the estimate during recorder warmup. No PCM is sent to the coordinator. */
        fun signalToNoiseDb(window: HandsFreeVerificationWindow): Double? {
            val end = window.candidateSample
            if (end < 24000 || end > window.samples.size) return null
            fun rms(start: Int, stop: Int): Double {
                var power = 0.0
                for (i in start until stop) {
                    val sample = window.samples[i].toDouble()
                    power += sample * sample
                }
                return sqrt(power / (stop - start)).coerceAtLeast(1.0)
            }
            return (20.0 * log10(rms(end - 8000, end) / rms(end - 24000, end - 8000))).coerceIn(-60.0, 100.0)
        }
    }

    private class WakeTransportTrace(private val clock: () -> Long) : EventListener() {
        private var dnsStart: Long? = null
        private var connectStart: Long? = null
        private var tlsStart: Long? = null
        private var requestEnd: Long? = null
        private var bodyStart: Long? = null
        private var dns: Long? = null
        private var connect: Long? = null
        private var tls: Long? = null
        private var headers: Long? = null
        private var body: Long? = null
        private var acquired = false

        private fun duration(start: Long?): Long? = start?.let { (clock() - it).takeIf { ms -> ms in 0..MAX_AGE_MS } }
        private fun accumulate(total: Long?, value: Long?): Long? = value?.let { ((total ?: 0) + it).takeIf { ms -> ms <= MAX_AGE_MS } } ?: total
        override fun dnsStart(call: Call, domainName: String) { dnsStart = clock() }
        override fun dnsEnd(call: Call, domainName: String, inetAddressList: List<java.net.InetAddress>) {
            dns = accumulate(dns, duration(dnsStart)); dnsStart = null
        }
        override fun connectStart(call: Call, inetSocketAddress: InetSocketAddress, proxy: Proxy) { connectStart = clock() }
        override fun secureConnectStart(call: Call) { tlsStart = clock() }
        override fun secureConnectEnd(call: Call, handshake: Handshake?) { tls = accumulate(tls, duration(tlsStart)); tlsStart = null }
        override fun connectEnd(call: Call, inetSocketAddress: InetSocketAddress, proxy: Proxy, protocol: Protocol?) {
            connect = accumulate(connect, duration(connectStart)); connectStart = null
        }
        override fun connectionAcquired(call: Call, connection: Connection) { acquired = true }
        override fun requestBodyEnd(call: Call, byteCount: Long) { requestEnd = clock() }
        override fun responseHeadersEnd(call: Call, response: Response) { headers = duration(requestEnd) }
        override fun responseBodyStart(call: Call) { bodyStart = clock() }
        override fun responseBodyEnd(call: Call, byteCount: Long) { body = duration(bodyStart) }
        fun snapshot() = TransportTiming(dns, connect, tls, headers, body,
            if (acquired) connect == null && connectStart == null else null)
    }
}
