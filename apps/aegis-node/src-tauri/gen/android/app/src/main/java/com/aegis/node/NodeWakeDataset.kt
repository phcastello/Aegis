package com.aegis.node

import android.content.Context
import androidx.work.*
import okhttp3.Call
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.Request
import okhttp3.RequestBody.Companion.asRequestBody
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.security.MessageDigest
import java.util.Base64
import java.util.UUID
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit

/** Only paired, explicitly enabled devices create this private upload queue. */
internal class NodeWakeDataset private constructor(private val context: Context) {
    companion object {
        private var instance: NodeWakeDataset? = null
        @Synchronized fun get(context: Context): NodeWakeDataset =
            instance ?: NodeWakeDataset(context.applicationContext).also { instance = it }
        private const val QUOTA = 100L * 1024 * 1024
    }

    private val prefs = context.getSharedPreferences("aegis-wake-dataset-v1", Context.MODE_PRIVATE)
    private val root = File(context.noBackupFilesDir, "wake-dataset-outbox")
    private val writer = ThreadPoolExecutor(1, 1, 30, TimeUnit.SECONDS, ArrayBlockingQueue(8),
        { runnable -> Thread(runnable, "AegisWakeDataset").apply { isDaemon = true } })
    private val calls = mutableSetOf<Call>()
    @Volatile var epoch: String? = prefs.getString("epoch", null)
        private set
    @Volatile var status: String = if (epoch == null) "ready" else prefs.getString("status", "ready") ?: "ready"
        private set
    private var skippedEvents = if (epoch == null) 0 else prefs.getLong("skippedEvents", 0L)

    init {
        writer.execute {
            synchronized(this) {
                try { recoverWakeDataset(root, epoch, ::schedule) }
                catch (_: Exception) { state("rejected") }
            }
        }
    }

    @Synchronized fun configure(enabled: Boolean) {
        if (!enabled) {
            val old = epoch ?: return
            check(prefs.edit().remove("epoch").remove("node").remove("origin").remove("status").remove("skippedEvents").commit())
            epoch = null
            calls.forEach { it.cancel() }
            WorkManager.getInstance(context).cancelAllWorkByTag("wake-dataset-" + old)
            // Detach this generation immediately. New consent always uses another directory.
            File(root, old).deleteRecursively()
            status = "ready"
            skippedEvents = 0
            return
        }
        val session = NodeNativeSession.read(context) ?: throw IllegalStateException("node_unavailable")
        if (epoch != null && matching(session)) return
        if (epoch != null) configure(false)
        val token = UUID.randomUUID().toString()
        check(prefs.edit().putString("epoch", token).putString("node", session.nodeId)
            .putString("origin", session.origin).putString("status", "ready").commit())
        epoch = token
        status = "ready"
    }

    @Synchronized fun snapshot(): JSONObject {
        val token = epoch
        val queue = wakeDatasetQueueFiles(token?.let { File(root, it) })
        return JSONObject().put("enabled", token != null).put("status", status)
            .put("pendingEvents", queue.pending).put("rejectedEvents", queue.rejected)
            .put("pendingBytes", queue.bytes).put("maximumBytes", QUOTA).put("skippedEvents", skippedEvents)
    }

    private fun matching(s: NodeNativeSession.Session) =
        s.nodeId == prefs.getString("node", null) && s.origin == prefs.getString("origin", null)

    private fun state(value: String) { status = value; prefs.edit().putString("status", value).apply() }
    private fun skip() {
        if (skippedEvents < Long.MAX_VALUE) skippedEvents++
        prefs.edit().putLong("skippedEvents", skippedEvents).apply()
        state("queue_full")
    }

    fun persist(clip: WakeDatasetClip, hold: Boolean = false) {
        if (clip.epoch != epoch) { clip.clear(); return }
        try {
            writer.execute {
                try {
                    val payload = clip.payload().toString().toByteArray(Charsets.UTF_8)
                    synchronized(this) {
                        if (epoch != clip.epoch) return@synchronized
                        val directory = File(root, clip.epoch)
                        check(directory.isDirectory || directory.mkdirs())
                        if (directory.listFiles().orEmpty().sumOf { it.length() } + payload.size > QUOTA) {
                            skip(); return@synchronized
                        }
                        val tmp = File(directory, clip.id + ".tmp")
                        try {
                            FileOutputStream(tmp).use { it.write(payload); it.fd.sync() }
                            check(tmp.renameTo(File(directory, clip.id + if (hold) ".hold" else ".json")))
                        } finally { tmp.delete() }
                        state("waiting")
                        if (!hold) schedule(clip.epoch, clip.id)
                    }
                } catch (_: Exception) { if (epoch == clip.epoch) state("rejected") }
                finally { clip.clear() }
            }
        } catch (_: java.util.concurrent.RejectedExecutionException) {
            clip.clear()
            synchronized(this) { if (epoch == clip.epoch) skip() }
        }
    }

    fun finishCommand(token: String, id: String, disposition: String) {
        try { writer.execute {
            synchronized(this) {
                if (epoch != token) return@synchronized
                val directory = File(root, token)
                val held = File(directory, id + ".hold")
                if (!held.isFile) return@synchronized
                val temp = File(directory, id + ".tmp")
                try {
                    val payload = JSONObject(held.readText()).put("commandDisposition", disposition)
                    FileOutputStream(temp).use { it.write(payload.toString().toByteArray()); it.fd.sync() }
                    check(temp.renameTo(File(directory, id + ".json")))
                    held.delete()
                    schedule(token, id)
                } catch (_: Exception) { state("rejected") }
                finally { temp.delete() }
            }
        } } catch (_: java.util.concurrent.RejectedExecutionException) { state("queue_full") }
    }

    private fun schedule(token: String, id: String) {
        val work = OneTimeWorkRequestBuilder<NodeWakeDatasetWorker>()
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
            .setInputData(workDataOf("epoch" to token, "event" to id)).addTag("wake-dataset-" + token)
            .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 30, TimeUnit.SECONDS).build()
        WorkManager.getInstance(context).enqueueUniqueWork("wake-dataset-" + token + "-" + id, ExistingWorkPolicy.KEEP, work)
    }

    fun upload(token: String, id: String, stopped: () -> Boolean): ListenableWorker.Result {
        val file = File(File(root, token), id + ".json")
        val call = synchronized(this) {
            if (token != epoch || !file.exists()) return ListenableWorker.Result.success()
            val session = NodeNativeSession.read(context) ?: return ListenableWorker.Result.retry()
            if (!matching(session)) { configure(false); return ListenableWorker.Result.success() }
            val request = Request.Builder().url(session.origin.trimEnd('/') + "/api/nodes/me/wake-samples/" + id)
                .header("Authorization", "AegisNode " + session.credential)
                .put(file.asRequestBody("application/json".toMediaType())).build()
            NodeHandsFreeBackend.transport.newCall(request).also { calls.add(it) }
        }
        try {
            if (stopped()) return ListenableWorker.Result.retry()
            call.execute().use { response ->
                synchronized(this) {
                    if (epoch != token) return ListenableWorker.Result.success()
                    if (response.code == 204) {
                        if (!file.delete() && file.exists()) return ListenableWorker.Result.retry()
                        state("sent")
                        return ListenableWorker.Result.success()
                    }
                    if (response.code in listOf(400, 409, 413, 422)) {
                        val rejected = file.resolveSibling(id + ".rejected")
                        if (!file.renameTo(rejected) && file.exists()) return ListenableWorker.Result.retry()
                        state("rejected")
                        return ListenableWorker.Result.failure()
                    }
                    state(if (response.code == 507) "server_full" else "waiting")
                    return ListenableWorker.Result.retry()
                }
            }
        } catch (_: IOException) { return ListenableWorker.Result.retry() }
        finally { synchronized(this) { calls.remove(call) } }
    }
}

internal data class WakeDatasetQueueFiles(val pending: Int, val rejected: Int, val bytes: Long)

internal fun wakeDatasetQueueFiles(directory: File?): WakeDatasetQueueFiles {
    val files = directory?.listFiles().orEmpty().filter { it.isFile && it.extension in setOf("json", "hold", "rejected") }
    val rejected = files.count { it.extension == "rejected" }
    return WakeDatasetQueueFiles(files.size - rejected, rejected, files.sumOf { it.length() })
}

/** Called before writes on startup. A completed row wins over an older, held copy. */
internal fun recoverWakeDataset(root: File, epoch: String?, schedule: (String, String) -> Unit) {
    for (folder in root.listFiles().orEmpty()) {
        if (!folder.isDirectory) continue
        if (folder.name != epoch) { folder.deleteRecursively(); continue }
        val readyIds = mutableSetOf<String>()
        for (file in folder.listFiles().orEmpty()) {
            when (file.extension) {
                "tmp" -> file.delete()
                "hold" -> {
                    val ready = file.resolveSibling(file.nameWithoutExtension + ".json")
                    val rejected = file.resolveSibling(file.nameWithoutExtension + ".rejected")
                    if (ready.exists() || rejected.exists()) file.delete() else check(file.renameTo(ready))
                    if (ready.exists()) readyIds.add(ready.nameWithoutExtension)
                }
                "json" -> readyIds.add(file.nameWithoutExtension)
            }
        }
        for (id in readyIds) schedule(epoch, id)
    }
}

class NodeWakeDatasetWorker(context: Context, parameters: WorkerParameters) : Worker(context, parameters) {
    override fun doWork(): Result {
        val token = inputData.getString("epoch") ?: return Result.failure()
        val id = inputData.getString("event") ?: return Result.failure()
        if (runCatching { UUID.fromString(token).toString() == token && UUID.fromString(id).toString() == id }.getOrDefault(false).not())
            return Result.failure()
        return NodeWakeDataset.get(applicationContext).upload(token, id) { isStopped }
    }
}

/** Reuse the detector ring, but count only whole reads received under one continuous consent. */
internal class WakeDatasetConsentWindow {
    private var epoch: String? = null
    private var samples = 0

    fun received(beforeRead: String?, afterRead: String?, count: Int): String? {
        val current = beforeRead?.takeIf { it == afterRead }
        if (current != epoch) {
            epoch = current
            samples = 0
        }
        if (current != null) samples = minOf(35200, samples + count)
        return current
    }

    fun context(ring: HandsFreeRing, boundary: HandsFreeActivationBoundary): ShortArray =
        boundary.context(ring, minOf(32000, samples - boundary.postCount))
}

/** Owns only a detected candidate's short context and bounded post-decision samples. */
internal class WakeDatasetClip(val epoch: String, val decision: Long, private val context: ShortArray,
    private val metadata: JSONObject, val id: String = UUID.randomUUID().toString()) {
    var disposition = "candidate"
    var commandDisposition = "unknown"
    private var post = ShortArray(48000)
    private var length = 0
    fun append(data: ShortArray, start: Int, count: Int) {
        if (disposition == "activated" && post.size < 320000) post = post.copyOf(320000)
        val n = minOf(count, post.size - length)
        data.copyInto(post, length, start, start + n)
        length += n
    }
    val complete get() = length == post.size && disposition != "activated"

    fun groupWithEpisode(episodeId: String?) {
        if (episodeId != null && metadata.optString("origin") == "wake_word")
            metadata.put("groupId", episodeId)
    }

    fun payload(): JSONObject {
        val result = JSONObject().put("schemaVersion", 1).put("disposition", disposition).put("commandDisposition", commandDisposition)
        for (key in listOf("activatedAt", "groupId", "origin", "appVersion", "build", "modelVersion", "modelSha256",
            "instantaneousScore", "decisionScore", "verifierScore"))
            if (!metadata.isNull(key)) result.put(key, metadata.get(key))
        fun audio(name: String, digest: String, pcm: ShortArray, samples: Int) {
            val wav = handsFreeWav(pcm, samples)
            result.put(name, Base64.getEncoder().encodeToString(wav))
            result.put(digest, MessageDigest.getInstance("SHA-256").digest(wav).joinToString("") { "%02x".format(it) })
        }
        audio("wav", "sha256", context, context.size)
        if (length > 0) audio("postWav", "postSha256", post, length)
        return result
    }
    fun verifierScore(value: Float?) { if (value != null && value.isFinite()) metadata.put("verifierScore", value.toDouble()) }
    fun clear() { context.fill(0); post.fill(0) }
}
