package com.aegis.node

import java.io.IOException
import java.util.UUID
import java.util.concurrent.TimeUnit
import kotlin.math.log10
import kotlin.math.sqrt
import okhttp3.Call
import okhttp3.Callback
import okhttp3.MediaType.Companion.toMediaType
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

    val activationId = UUID.randomUUID().toString()
    @Volatile private var authority: NodeNativeSession.Session? = null
    private var inFlight: Call? = null
    @Volatile private var closed = false
    @Volatile private var granted = false

    fun claim(window: HandsFreeVerificationWindow, score: Float): Decision {
        val session = readSession() ?: return Decision.UNAVAILABLE
        val remaining = deadlineMs - clock()
        if (!current() || closed || remaining !in 1..MAX_AGE_MS) return Decision.UNAVAILABLE
        val payload = JSONObject().put("activationId", activationId)
        signalToNoiseDb(window)?.let { payload.put("snrDb", it) }
        if (includeConfidence) payload.put("confidence", score.toDouble())
        val request = request(session, "/claim").post(payload.toString().toRequestBody(JSON)).build()
        val call = NodeHandsFreeBackend.transport.newCall(request)
        call.timeout().timeout(remaining, TimeUnit.MILLISECONDS)
        synchronized(this) {
            if (closed || !current()) return Decision.UNAVAILABLE
            authority = session
            inFlight = call
        }
        return try {
            call.execute().use { response ->
                if (!response.isSuccessful || !currentAuthority()) return Decision.UNAVAILABLE
                val bytes = response.body?.byteStream()?.readBytesBounded(2048) ?: return Decision.UNAVAILABLE
                val outcome = JSONObject(String(bytes, Charsets.UTF_8))
                if (outcome.optString("activationId") != activationId || !currentAuthority()) return Decision.UNAVAILABLE
                when (outcome.optString("decision")) {
                    "granted" -> {
                        granted = true
                        Decision.GRANTED
                    }
                    "suppressed" -> Decision.SUPPRESSED
                    else -> Decision.UNAVAILABLE
                }
            }
        } catch (_: Exception) {
            Decision.UNAVAILABLE
        } finally {
            synchronized(this) { inFlight = null }
        }
    }

    fun grantedIdentity(): String = NodeNativeSession.fingerprint(checkNotNull(authority))

    /** Recheck at the activation boundary, including a grant queued behind a blocking audio read. */
    fun canActivate(): Boolean = granted && currentAuthority()

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
}
