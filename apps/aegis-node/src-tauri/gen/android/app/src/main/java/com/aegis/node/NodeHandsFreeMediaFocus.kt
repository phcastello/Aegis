package com.aegis.node

import android.content.Context
import android.media.AudioFocusRequest
import android.media.AudioManager
import android.os.Build
import android.os.Handler
import android.os.Looper

internal enum class HandsFreeSessionFocusResult { ACQUIRED, UNAVAILABLE, LOST }

/** A cancellation stops the response immediately, but audio focus should outlive its
 * native communication route. The timeout bounds this deferral if worker teardown stalls.
 */
internal class HandsFreeMediaFocusRelease(
    private val release: (String) -> Unit,
    private val releaseAll: () -> Unit,
    private val scheduleTimeout: (() -> Unit) -> Unit
) {
    private class Deferred(val token: String)
    private val gate = Any()
    private var routeToken: String? = null
    private var deferred: Deferred? = null

    fun routePreparing(token: String) = synchronized(gate) {
        routeToken = token
    }

    fun releaseWhenSafe(sessionToken: String?) {
        val token = synchronized(gate) { sessionToken ?: routeToken }
        if (token == null) { releaseAll(); return }
        val waiting = synchronized(gate) {
            if (routeToken != token) null
            else {
                if (deferred?.token == token) return
                Deferred(token).also { deferred = it }
            }
        }
        if (waiting == null) release(token)
        else scheduleTimeout {
            val expired = synchronized(gate) {
                if (deferred !== waiting) false
                else { deferred = null; true }
            }
            if (expired) release(waiting.token)
        }
    }

    fun routeClosed(token: String) {
        val waiting = synchronized(gate) {
            if (routeToken == token) routeToken = null
            if (deferred?.token == token) { deferred = null; true } else false
        }
        if (waiting) release(token)
    }
}

/** Audio focus belongs to an accepted conversation, never to the idle detector or a turn's track. */
internal class HandsFreeSessionMediaFocus(
    private val request: (() -> Unit) -> AutoCloseable?,
    private val onLoss: (String) -> Unit,
    private val observe: (String) -> Unit = {}
) {
    constructor(context: Context, onLoss: (String) -> Unit, observe: (String) -> Unit = {}) :
        this({ lost -> requestAndroidFocus(context, lost) }, onLoss, observe)

    private class Ownership(val token: String) {
        var attempts = 0
        var requesting = false
        var lease: AutoCloseable? = null
        var lost = false
    }

    private val gate = Any()
    private var current: Ownership? = null

    fun acquire(token: String, retryUnavailable: Boolean = false): HandsFreeSessionFocusResult {
        val (owner, previousLease, attempt) = synchronized(gate) {
            val previous = current
            val entry = if (previous?.token == token) previous else Ownership(token).also { current = it }
            if (entry.lost) return HandsFreeSessionFocusResult.LOST
            if (entry.lease != null) return HandsFreeSessionFocusResult.ACQUIRED
            if (entry.requesting || entry.attempts >= 2 || (entry.attempts > 0 && !retryUnavailable))
                return HandsFreeSessionFocusResult.UNAVAILABLE
            entry.requesting = true
            entry.attempts++
            Triple(entry, previous?.takeUnless { it === entry }?.lease, entry.attempts)
        }
        previousLease?.let(::releaseLease)

        var failed = false
        val lease = try {
            request { focusLost(owner, attempt) }
        } catch (_: Exception) {
            failed = true
            null
        }
        val result = synchronized(gate) {
            owner.requesting = false
            if (current !== owner || owner.lost) {
                HandsFreeSessionFocusResult.LOST
            } else {
                owner.lease = lease
                if (lease == null) HandsFreeSessionFocusResult.UNAVAILABLE else HandsFreeSessionFocusResult.ACQUIRED
            }
        }
        when (result) {
            HandsFreeSessionFocusResult.ACQUIRED -> observe("acquired")
            HandsFreeSessionFocusResult.UNAVAILABLE -> observe(if (failed) "failed" else "denied")
            HandsFreeSessionFocusResult.LOST -> lease?.let(::releaseLease)
        }
        return result
    }

    private fun focusLost(owner: Ownership, attempt: Int) {
        synchronized(gate) {
            // Android may deliver queued callbacks after abandon, a replacement or a denied request.
            if (current !== owner || owner.attempts != attempt || owner.lost ||
                (!owner.requesting && owner.lease == null)) return
            owner.lost = true
        }
        observe("lost")
        onLoss(owner.token)
    }

    fun release(token: String?) {
        if (token != null) releaseMatching { it.token == token }
    }

    fun releaseAll() = releaseMatching { true }

    private fun releaseMatching(matches: (Ownership) -> Boolean) {
        val lease = synchronized(gate) {
            val owner = current ?: return
            if (!matches(owner)) return
            current = null // Invalidate callbacks before the platform abandon.
            owner.lease
        }
        lease?.let(::releaseLease)
    }

    private fun releaseLease(lease: AutoCloseable) {
        try {
            lease.close()
        } catch (_: Exception) {
            observe("release_failed")
            return
        }
        observe("released")
    }

    companion object {
        private fun requestAndroidFocus(context: Context, onLoss: () -> Unit): AutoCloseable? {
            val manager = context.getSystemService(AudioManager::class.java)
            val listener = AudioManager.OnAudioFocusChangeListener { if (it < 0) onLoss() }
            // Speech recognition warrants exclusive transient focus, not a duck request.
            // Use the TTS media attributes and the exact same request for abandonment.
            val request = if (Build.VERSION.SDK_INT >= 26) {
                AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN_TRANSIENT_EXCLUSIVE)
                    .setAudioAttributes(handsFreeSpeechMediaAttributes())
                    .setAcceptsDelayedFocusGain(false)
                    .setWillPauseWhenDucked(true)
                    .setOnAudioFocusChangeListener(listener, Handler(Looper.getMainLooper()))
                    .build()
            } else null
            val lease = AutoCloseable {
                val released = if (request != null) manager.abandonAudioFocusRequest(request)
                    else @Suppress("DEPRECATION") manager.abandonAudioFocus(listener)
                check(released == AudioManager.AUDIOFOCUS_REQUEST_GRANTED) { "audio_focus_abandon_failed" }
            }
            val result = try {
                if (request != null) manager.requestAudioFocus(request)
                else @Suppress("DEPRECATION") manager.requestAudioFocus(listener, AudioManager.STREAM_MUSIC,
                    AudioManager.AUDIOFOCUS_GAIN_TRANSIENT_EXCLUSIVE)
            } catch (error: Exception) {
                // A throwing platform request may have partly registered the listener.
                try { lease.close() } catch (_: Exception) {}
                throw error
            }
            if (result == AudioManager.AUDIOFOCUS_REQUEST_GRANTED) return lease
            if (result == AudioManager.AUDIOFOCUS_REQUEST_DELAYED) lease.close()
            return null
        }
    }
}
