package com.aegis.node

/** Process-local eligibility; the owner serializes calls with its session lock. */
internal class HandsFreeConversationContinuity(private val now: () -> Long) {
    class Activation internal constructor(val identity: String?, val conversationId: String?)
    private var identity: String? = null
    private var current: Activation? = null
    private var conversationId: String? = null
    private var playbackCompletedAt: Long? = null

    /** A wake activation pins its destination, so capture/generation/playback cannot expire it. */
    fun activate(currentIdentity: String?, activatedAt: Long = now()): Activation {
        identityChanged(currentIdentity)
        val completedAt = playbackCompletedAt
        val elapsed = completedAt?.let { activatedAt - it }
        if (currentIdentity == null || elapsed == null || elapsed < 0 || elapsed >= WINDOW_MS) {
            conversationId = null
            playbackCompletedAt = null
        }
        return Activation(currentIdentity, conversationId).also { current = it }
    }

    /** Call only after the entire successful response has drained the actual playback head. */
    fun playbackCompleted(activation: Activation, destination: String, currentIdentity: String?, completedAt: Long = now()): Boolean {
        if (current !== activation) return false
        if (activation.identity == null || activation.identity != currentIdentity) {
            identityChanged(currentIdentity)
            return false
        }
        conversationId = destination
        playbackCompletedAt = completedAt
        return true
    }

    /** Empty/STT failure/cancel/error ends ownership without extending the preceding window. */
    fun end(activation: Activation?) { if (activation != null && current === activation) current = null }

    fun identityChanged(currentIdentity: String?) {
        if (identity == currentIdentity) return
        invalidate()
        identity = currentIdentity
    }

    fun invalidate() {
        current = null
        conversationId = null
        playbackCompletedAt = null
        identity = null
    }

    companion object { private const val WINDOW_MS = 10000L }
}
