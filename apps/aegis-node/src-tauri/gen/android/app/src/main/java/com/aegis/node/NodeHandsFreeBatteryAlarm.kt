package com.aegis.node

import android.app.AlarmManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import java.net.URI
import java.util.concurrent.atomic.AtomicBoolean

/** One wakeup per stage boundary; never keeps the off window awake. */
internal class HandsFreeBatteryAlarm internal constructor(
    private val sdkInt: Int,
    private val backend: HandsFreeBatteryAlarmBackend
) {
    constructor(context: Context): this(Build.VERSION.SDK_INT, AndroidBatteryAlarmBackend(context.applicationContext))

    fun schedule(token: HandsFreeBatteryAutomation.Token): String {
        val exact = sdkInt < 31 || try { backend.canScheduleExactAlarms() } catch (_: SecurityException) { false }
        if (exact) {
            try {
                backend.setExact(token)
                return "exact"
            } catch (_: SecurityException) {
                // Permission may be revoked between checking it and scheduling.
            }
        }
        backend.setInexact(token)
        return "inexact"
    }

    fun cancel(token: HandsFreeBatteryAutomation.Token) = backend.cancel(token)
}

internal interface HandsFreeBatteryAlarmBackend {
    fun canScheduleExactAlarms(): Boolean
    fun setExact(token: HandsFreeBatteryAutomation.Token)
    fun setInexact(token: HandsFreeBatteryAutomation.Token)
    fun cancel(token: HandsFreeBatteryAutomation.Token)
}

/** URI is the identity: PendingIntent ignores ordinary Intent extras. */
internal object HandsFreeBatteryAlarmIntents {
    const val ACTION = "com.aegis.node.action.BATTERY_STUDY_BOUNDARY"
    private const val PREFIX = "aegis-battery-study://transition"
    private const val MAX_URI_LENGTH = 512
    private val identifier = Regex("[A-Za-z0-9_-]{1,128}")

    fun data(token: HandsFreeBatteryAutomation.Token): String {
        require(identifier.matches(token.sequenceId) && identifier.matches(token.runId))
        require(token.stageIndex in 0..2 && token.deadlineElapsedMs > 0)
        return "$PREFIX/${token.sequenceId}/${token.stageIndex}/${token.runId}/${token.deadlineElapsedMs}"
    }

    fun parse(action: String?, data: String?): HandsFreeBatteryAutomation.Token? {
        if (action != ACTION || data == null || data.length > MAX_URI_LENGTH) return null
        return try {
            val uri = URI(data)
            if (uri.scheme != "aegis-battery-study" || uri.rawAuthority != "transition" ||
                uri.rawQuery != null || uri.rawFragment != null) return null
            val parts = uri.rawPath?.split('/') ?: return null
            if (parts.size != 5 || parts[0] != "" || !identifier.matches(parts[1]) ||
                !identifier.matches(parts[3])) return null
            val stage = parts[2].toIntOrNull() ?: return null
            val deadline = parts[4].toLongOrNull() ?: return null
            if (stage !in 0..2 || deadline <= 0 || parts[2] != stage.toString() ||
                parts[4] != deadline.toString()) return null
            HandsFreeBatteryAutomation.Token(parts[1], stage, parts[3], deadline)
        } catch (_: IllegalArgumentException) { null }
          catch (_: java.net.URISyntaxException) { null }
    }

    fun create(context: Context, token: HandsFreeBatteryAutomation.Token): Intent =
        Intent(context, NodeHandsFreeBatteryAlarmReceiver::class.java).apply {
            action = ACTION
            this.data = Uri.parse(HandsFreeBatteryAlarmIntents.data(token))
        }

    fun parse(intent: Intent): HandsFreeBatteryAutomation.Token? = parse(intent.action, intent.dataString)
}

private class AndroidBatteryAlarmBackend(private val context: Context): HandsFreeBatteryAlarmBackend {
    private val alarms: AlarmManager get() = checkNotNull(context.getSystemService(AlarmManager::class.java))
    override fun canScheduleExactAlarms(): Boolean = Build.VERSION.SDK_INT < 31 || alarms.canScheduleExactAlarms()
    private fun pending(token: HandsFreeBatteryAutomation.Token, flags: Int): PendingIntent? =
        PendingIntent.getBroadcast(context, 0, HandsFreeBatteryAlarmIntents.create(context, token), flags or PendingIntent.FLAG_IMMUTABLE)

    override fun setExact(token: HandsFreeBatteryAutomation.Token) {
        alarms.setExactAndAllowWhileIdle(AlarmManager.ELAPSED_REALTIME_WAKEUP, token.deadlineElapsedMs,
            checkNotNull(pending(token, PendingIntent.FLAG_UPDATE_CURRENT)))
    }
    override fun setInexact(token: HandsFreeBatteryAutomation.Token) {
        alarms.setAndAllowWhileIdle(AlarmManager.ELAPSED_REALTIME_WAKEUP, token.deadlineElapsedMs,
            checkNotNull(pending(token, PendingIntent.FLAG_UPDATE_CURRENT)))
    }
    override fun cancel(token: HandsFreeBatteryAutomation.Token) {
        val operation = pending(token, PendingIntent.FLAG_NO_CREATE) ?: return
        try { alarms.cancel(operation) } finally { operation.cancel() }
    }
}

/** Callback, exception and timeout can race; each transition resource is released once. */
internal class HandsFreeBatteryAlarmCompletion(
    private val cancelTimeout: (Runnable) -> Unit,
    private val release: () -> Unit,
    private val finish: () -> Unit
): Runnable {
    private val completed = AtomicBoolean(false)
    override fun run() {
        if (!completed.compareAndSet(false, true)) return
        try { cancelTimeout(this) } catch (_: RuntimeException) { }
        try { release() } catch (_: RuntimeException) { }
        try { finish() } catch (_: RuntimeException) { }
    }
}

/** Non-exported in the manifest. It only hands a validated boundary to the supervisor. */
class NodeHandsFreeBatteryAlarmReceiver: BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val token = HandsFreeBatteryAlarmIntents.parse(intent) ?: return
        val pending = goAsync()
        val main = Handler(Looper.getMainLooper())
        var lock: PowerManager.WakeLock? = null
        val completion = HandsFreeBatteryAlarmCompletion(
            { main.removeCallbacks(it) },
            { lock?.let { if (it.isHeld) it.release() } },
            { pending.finish() }
        )
        try {
            val power = checkNotNull(context.getSystemService(PowerManager::class.java))
            val transitionLock = power.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "Aegis:BatteryTransition")
            lock = transitionLock
            transitionLock.setReferenceCounted(false)
            transitionLock.acquire(TRANSITION_TIMEOUT_MS)
            // Timeout only releases the broadcast/transition lease. The supervisor owns
            // cancellation and must never declare an unfinished transition successful.
            if (!main.postDelayed(completion, TRANSITION_TIMEOUT_MS)) {
                completion.run()
                return
            }
            NodeHandsFree.batteryAlarm(context.applicationContext, token) { completion.run() }
        } catch (_: RuntimeException) {
            completion.run()
        }
    }

    companion object {
        internal const val TRANSITION_TIMEOUT_MS = 10000L
    }
}
