package com.aegis.node

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Process
import android.provider.OpenableColumns
import okhttp3.Call
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.DataInputStream
import java.io.File
import java.io.InputStream
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import org.json.JSONArray
import org.json.JSONObject

internal object SharedChatAttachments {
    private const val MAX_FILES = 4
    private const val MAX_BYTES = 512L * 1024 * 1024
    private const val MAX_CHUNK = 4 * 1024 * 1024
    private const val TOO_LARGE = "O arquivo excede 512 MiB."
    private val sources = ConcurrentHashMap<String, Source>()
    private class Transfer {
        private var call: Call? = null
        private var cancelled = false
        @Synchronized fun bind(request: Call) {
            check(!cancelled)
            call = request
        }
        @Synchronized fun cancel() { cancelled = true; call?.cancel() }
        @Synchronized fun checkActive() { check(!cancelled) }
    }
    private val requests = ConcurrentHashMap<String, Transfer>()
    private val client = OkHttpClient.Builder().followRedirects(false).followSslRedirects(false)
        .connectTimeout(15, TimeUnit.SECONDS).callTimeout(90, TimeUnit.SECONDS).build()
    private var cache: File? = null
    private var pending = emptyList<Uri>()
    private var pendingError: String? = null

    @Synchronized
    @Suppress("DEPRECATION")
    fun accept(intent: Intent?): Boolean {
        if (intent == null || intent.action !in listOf(Intent.ACTION_SEND, Intent.ACTION_SEND_MULTIPLE)) return false
        try {
            val streams = if (intent.action == Intent.ACTION_SEND)
                listOfNotNull(intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM))
            else intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM).orEmpty()
            val uris = streams.ifEmpty {
                val clips = intent.clipData ?: return@ifEmpty emptyList()
                require(clips.itemCount <= MAX_FILES)
                (0 until clips.itemCount).mapNotNull { clips.getItemAt(it).uri }
            }
            require(uris.isNotEmpty()) { "Compartilhe um arquivo para anexá-lo à mensagem." }
            require(pending.size + uris.size <= MAX_FILES) { "Anexe até quatro arquivos por mensagem." }
            require(uris.all { it.scheme == "content" && !it.authority.isNullOrEmpty() })
            pending = pending + uris
        } catch (error: Exception) {
            pendingError = when (error.message) {
                "Anexe até quatro arquivos por mensagem.", "Compartilhe um arquivo para anexá-lo à mensagem." -> error.message
                else -> "Não foi possível receber os arquivos compartilhados."
            }
        }
        return true
    }

    // Only URI grants received from the system share intent are registered as sources.
    fun take(context: Context): JSONObject {
        val (uris, error) = synchronized(this) {
            val batch = pending to pendingError
            pending = emptyList()
            pendingError = null
            batch
        }
        tempDirectory(context)
        val files = JSONArray()
        val added = mutableListOf<String>()
        val result = JSONObject().put("files", files)
        if (error != null) result.put("error", error)
        try {
            val resolver = context.contentResolver
            for (uri in uris) {
                val authority = uri.authority!!.substringAfterLast('@')
                require(context.packageManager.resolveContentProvider(authority, 0)?.packageName != context.packageName)
                require(context.checkUriPermission(uri, Process.myPid(), Process.myUid(), Intent.FLAG_GRANT_READ_URI_PERMISSION)
                    == PackageManager.PERMISSION_GRANTED)
                var name = uri.lastPathSegment?.substringAfterLast('/') ?: "arquivo"
                var size: Long? = null
                val metadata = try { resolver.query(uri, null, null, null, null) } catch (_: RuntimeException) { null }
                metadata?.use { cursor ->
                    if (cursor.moveToFirst()) {
                        val nameIndex = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                        if (nameIndex >= 0 && !cursor.isNull(nameIndex)) name = cursor.getString(nameIndex)
                        val sizeIndex = cursor.getColumnIndex(OpenableColumns.SIZE)
                        if (sizeIndex >= 0 && !cursor.isNull(sizeIndex)) size = cursor.getLong(sizeIndex).takeIf { it >= 0 }
                    }
                }
                val type = resolver.getType(uri) ?: "application/octet-stream"
                val open = { requireNotNull(resolver.openInputStream(uri)) }
                val source = size?.let { require(it <= MAX_BYTES) { TOO_LARGE }; Source(it, open) }
                    ?: cacheUnknownSize(context, open)
                val handle = UUID.randomUUID().toString()
                sources[handle] = source
                added.add(handle)
                files.put(JSONObject().put("handle", handle).put("fileName", name)
                    .put("contentType", type).put("sizeBytes", source.size))
            }
        } catch (failure: Exception) {
            added.forEach { sources.remove(it)?.close() }
            return JSONObject().put("files", JSONArray()).put("error", if (failure.message == TOO_LARGE) TOO_LARGE
                else "Não foi possível ler o arquivo. Compartilhe novamente ou use o botão de anexo.")
        }
        return result
    }

    @Synchronized private fun tempDirectory(context: Context): File {
        cache?.let { return it }
        val directory = File(context.cacheDir, "shared-chat-files")
        directory.mkdirs()
        // Handles are process-local. Files from a previous process are no longer referenced.
        directory.listFiles()?.forEach { if (it.isFile) it.delete() }
        cache = directory
        return directory
    }

    private fun cacheUnknownSize(context: Context, open: () -> InputStream): Source {
        val file = File.createTempFile("share-", ".part", tempDirectory(context))
        try {
            var total = 0L
            open().use { input -> file.outputStream().use { output ->
                val buffer = ByteArray(64 * 1024)
                while (true) {
                    val count = input.read(buffer)
                    if (count < 0) break
                    total += count
                    require(total <= MAX_BYTES) { TOO_LARGE }
                    output.write(buffer, 0, count)
                }
            } }
            return Source(total, { file.inputStream() }, file)
        } catch (failure: Exception) {
            file.delete()
            throw failure
        }
    }

    private class Source(val size: Long, val open: () -> InputStream, val cached: File? = null) {
        private var input: DataInputStream? = null
        private var position = 0L
        private var closed = false

        @Synchronized fun chunk(offset: Long, length: Int): ByteArray {
            check(!closed)
            require(offset in 0 until size && length in 1..MAX_CHUNK && offset + length <= size)
            try {
                if (input == null || offset < position) {
                    input?.close()
                    input = DataInputStream(open())
                    position = 0
                }
                val stream = requireNotNull(input)
                while (position < offset) {
                    val skipped = stream.skip(offset - position)
                    if (skipped > 0) position += skipped else { stream.readByte(); position++ }
                }
                val bytes = ByteArray(length)
                stream.readFully(bytes)
                position += length
                if (position == size) {
                    require(stream.read() == -1)
                    stream.close()
                    input = null
                }
                return bytes
            } catch (failure: Exception) {
                input?.close()
                input = null
                throw failure
            }
        }

        @Synchronized fun close() {
            closed = true
            try { input?.close() } finally { input = null; cached?.delete() }
        }
    }

    fun release(handle: String) { sources.remove(handle)?.close() }

    fun begin(requestId: String) { check(requests.putIfAbsent(requestId, Transfer()) == null) }
    fun cancel(requestId: String) { requests[requestId]?.cancel() }
    fun finish(requestId: String) { requests.remove(requestId) }

    fun upload(args: JSONObject): Int {
        val transfer = requireNotNull(requests[args.getString("requestId")])
        transfer.checkActive()
        val source = requireNotNull(sources[args.getString("handle")])
        val index = args.getInt("index")
        val chunkSize = args.getInt("chunkSize")
        require(index >= 0 && chunkSize in 1..MAX_CHUNK)
        val offset = index.toLong() * chunkSize
        val payload = source.chunk(offset, minOf(source.size - offset, chunkSize.toLong()).toInt())
        val uploadId = UUID.fromString(args.getString("uploadId"))
        val url = "${args.getString("origin")}/api/chat/uploads/$uploadId/chunks/$index"
        val request = Request.Builder().url(url).put(payload.toRequestBody("application/octet-stream".toMediaType())).build()
        val call = client.newCall(request)
        transfer.bind(call)
        return call.execute().use { it.code }
    }
}
