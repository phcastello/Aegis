package com.aegis.node

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Handler
import android.os.Looper
import androidx.core.content.ContextCompat
import org.json.JSONObject

/**
 * The projection belongs to an explicit, in-memory consent session. This does
 * not open a microphone, infer from other apps' identities or send their PCM
 * outside the process. Revocation immediately makes the ring inaccessible.
 */
internal object NodeExternalPlaybackReference {
    internal data class Consent(val id: Long, val resultCode: Int, val data: Intent)

    private val timeline = HandsFreeExternalReferenceTimeline()
    private val main = Handler(Looper.getMainLooper())
    private var sequence = 0L
    private var captureSequence = 0L
    private var session = 0L
    private var capture = 0L
    private var pending: Consent? = null
    private var releaseOwner: ((Long) -> Unit)? = null
    private var status = "off"
    @Volatile private var referenceAvailable = false
    @Volatile private var referenceGeneration = 0L

    // This ownership survives logical revocation until the physical AudioRecord
    // is released. Otherwise the existing microphone policy would mistake its
    // asynchronous cleanup for a different app recording the microphone.
    @Volatile private var ownedSessions: Set<Int> = emptySet()

    fun active(): Boolean = referenceAvailable
    fun generation(): Long = referenceGeneration
    fun recording(): Boolean = ownedSessions.isNotEmpty()
    fun ownsAudioSession(sessionId: Int): Boolean = ownedSessions.contains(sessionId)

    @Synchronized fun state(): JSONObject = JSONObject()
        .put("supported", Build.VERSION.SDK_INT >= 29)
        .put("session", session != 0L)
        .put("status", if (Build.VERSION.SDK_INT >= 29) status else "unsupported")

    @Synchronized fun beginConsent(context: Context): Long? {
        if (Build.VERSION.SDK_INT < 29 || session != 0L || ownedSessions.isNotEmpty()) return null
        // Eligibility is checked by the caller outside this lock before asking.
        if (!NodeHandsFree.permission(context)) {
            status = "blocked"
            return null
        }
        session = ++sequence
        status = "requesting"
        return session
    }

    @Synchronized fun current(id: Long) = id != 0L && session == id

    @Synchronized fun cancelPendingConsent() {
        if (status == "requesting") finish(session, "off")
    }

    fun finishConsent(context: Context, id: Long, resultCode: Int, data: Intent?) {
        if (resultCode != Activity.RESULT_OK || data == null) {
            finish(id, "denied")
            return
        }
        if (!NodeHandsFree.externalReferenceAllowed(context)) {
            finish(id, "blocked")
            return
        }
        synchronized(this) {
            if (!current(id) || status != "requesting") return
            pending = Consent(id, resultCode, data)
            status = "starting"
        }
        try {
            ContextCompat.startForegroundService(context, Intent(context, NodeExternalPlaybackReferenceService::class.java)
                .setAction(NodeExternalPlaybackReferenceService.ACTION_START).putExtra("sessionId", id))
        } catch (_: RuntimeException) {
            finish(id, "unavailable")
        }
    }

    @Synchronized fun takeConsent(id: Long): Consent? {
        if (!current(id) || pending?.id != id) return null
        return pending.also { pending = null }
    }

    @Synchronized fun authorized(id: Long, release: (Long) -> Unit): Boolean {
        if (!current(id)) return false
        releaseOwner = release
        status = "waiting"
        return true
    }

    @Synchronized fun beginCapture(id: Long): Long? {
        if (!current(id) || capture != 0L) return null
        capture = ++captureSequence
        clearReference()
        status = "warming"
        return capture
    }

    @Synchronized fun ownRecord(id: Int) { if (id > 0) ownedSessions += id }
    @Synchronized fun releaseRecord(id: Int) { ownedSessions -= id }

    @Synchronized fun offer(id: Long, captureId: Long, pcm: ShortArray, count: Int, endNanos: Long?, nowNanos: Long) {
        if (!current(id) || capture != captureId) return
        val before = timeline.generation
        timeline.offer(pcm, count, endNanos)
        if (before != timeline.generation) referenceGeneration++
        val available = timeline.hasReference(nowNanos)
        if (available != referenceAvailable) referenceGeneration++
        referenceAvailable = available
        status = if (referenceAvailable) "ready" else "unavailable"
        signalReferenceReaders()
    }

    /** Give the independent playback recorder a short chance to deliver the newest
     * microphone block. Never invent future render samples or wait indefinitely.
     */
    @Synchronized
    @Suppress("PLATFORM_CLASS_MAPPED_TO_KOTLIN")
    fun awaitNewestFrame(endNanos: Long, expectedGeneration: Long) {
        val deadline = System.nanoTime() + 45_000_000L
        while (referenceAvailable && referenceGeneration == expectedGeneration) {
            val now = System.nanoTime()
            val ahead = timeline.aheadOfReference(endNanos, now) ?: return
            // A substantially later timestamp indicates a stream/clock mismatch,
            // rather than a reference read that is about to be delivered.
            if (ahead > 60_000_000L) return
            val remaining = deadline - now
            if (remaining <= 0L) return
            try {
                (this as java.lang.Object).wait(remaining / 1_000_000L, (remaining % 1_000_000L).toInt())
            } catch (_: InterruptedException) {
                Thread.currentThread().interrupt()
                return
            }
        }
    }

    @Synchronized fun copyFrame(endNanos: Long, output: ShortArray): Boolean {
        if (!referenceAvailable || session == 0L || capture == 0L) {
            output.fill(0)
            return false
        }
        val now = System.nanoTime()
        if (!timeline.hasReference(now)) {
            referenceAvailable = false
            status = "unavailable"
            referenceGeneration++
            signalReferenceReaders()
            output.fill(0)
            return false
        }
        return timeline.copyFrame(endNanos, output, now)
    }

    @Synchronized fun endCapture(id: Long, captureId: Long, outcome: String) {
        if (!current(id) || capture != captureId) return
        capture = 0L
        clearReference()
        status = outcome
    }

    @Synchronized fun markStatus(id: Long, outcome: String) {
        if (current(id)) status = outcome
    }

    @Synchronized fun finish(id: Long, outcome: String) {
        if (!current(id)) return
        pending = null
        releaseOwner = null
        session = 0L
        capture = 0L
        clearReference()
        status = outcome
    }

    fun stop(context: Context) {
        val release: (() -> Unit)?
        val stopGeneration = synchronized(this) {
            if (session == 0L && ownedSessions.isEmpty() && pending == null) {
                status = "off"
                return
            }
            val oldSession = session
            release = releaseOwner?.let { owner -> { owner(oldSession) } }
            if (oldSession != 0L) finish(oldSession, "off")
            sequence
        }
        // No binder calls from the microphone/session lock. Target the old
        // service owner even when a new permission dialog opens before cleanup.
        val app = context.applicationContext
        main.post {
            if (release != null) release()
            else {
                val stillStopped = synchronized(this) { session == 0L && sequence == stopGeneration }
                if (stillStopped) try {
                    app.stopService(Intent(app, NodeExternalPlaybackReferenceService::class.java))
                } catch (_: RuntimeException) { }
            }
        }
    }

    private fun clearReference() {
        timeline.clear()
        referenceAvailable = false
        referenceGeneration++
        signalReferenceReaders()
    }

    @Suppress("PLATFORM_CLASS_MAPPED_TO_KOTLIN")
    private fun signalReferenceReaders() { (this as java.lang.Object).notifyAll() }
}
