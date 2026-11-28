package com.aegis.node

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.AssetFileDescriptor
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.ImageDecoder
import android.graphics.Matrix
import android.media.ExifInterface
import android.net.Uri
import android.os.Build
import android.os.ParcelFileDescriptor
import android.os.Process
import android.provider.OpenableColumns
import android.system.ErrnoException
import android.system.Os
import android.system.OsConstants
import android.util.Base64
import android.webkit.MimeTypeMap
import okhttp3.Call
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.File
import java.io.InputStream
import java.util.UUID
import java.util.concurrent.Callable
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import org.json.JSONArray
import org.json.JSONObject

internal object SharedChatAttachments {
    private const val MAX_FILES = 20
    private const val TOO_MANY = "Adicione até $MAX_FILES anexos por mensagem."
    private const val MAX_SHARED_TEXT = 16_384
    private const val TEXT_TOO_LONG = "O texto compartilhado é longo demais."
    private const val EMPTY_SHARE = "Compartilhe um arquivo, link ou texto."
    private const val MAX_BYTES = 512L * 1024 * 1024
    private const val MAX_CHUNK = 4 * 1024 * 1024
    private const val THUMBNAIL_EDGE = 256
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
    private var pendingText = emptyList<String>()
    private var pendingError: String? = null

    @Synchronized
    @Suppress("DEPRECATION")
    fun accept(intent: Intent?): Boolean {
        if (intent == null || intent.action !in listOf(Intent.ACTION_SEND, Intent.ACTION_SEND_MULTIPLE)) return false
        try {
            val clips = intent.clipData
            require(clips == null || clips.itemCount <= MAX_FILES) { TOO_MANY }
            val streams = if (intent.action == Intent.ACTION_SEND)
                listOfNotNull(intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM))
            else intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM).orEmpty()
            val clipItems = if (clips == null) emptyList() else (0 until clips.itemCount).map(clips::getItemAt)
            val uris = streams.ifEmpty {
                clipItems.mapNotNull { it.uri }
            }.distinct()
            val texts = listOfNotNull(intent.getCharSequenceExtra(Intent.EXTRA_TEXT)).ifEmpty {
                clipItems.filter { it.uri == null }.mapNotNull { it.text }
            }.mapNotNull {
                require(it.length <= MAX_SHARED_TEXT) { TEXT_TOO_LONG }
                it.toString().trim().takeIf(String::isNotEmpty)
            }
            require(uris.isNotEmpty() || texts.isNotEmpty()) { EMPTY_SHARE }
            require(uris.size + pending.size <= MAX_FILES && texts.size + pendingText.size <= MAX_FILES) { TOO_MANY }
            require(texts.sumOf(String::length) + pendingText.sumOf(String::length) <= MAX_SHARED_TEXT) { TEXT_TOO_LONG }
            require(uris.all { it.scheme == "content" && !it.authority.isNullOrEmpty() })
            pending = pending + uris
            pendingText = pendingText + texts
        } catch (error: Exception) {
            pendingError = when (error.message) {
                TOO_MANY, EMPTY_SHARE, TEXT_TOO_LONG -> error.message
                else -> "Não foi possível receber os arquivos compartilhados."
            }
        }
        return true
    }

    // Only URI grants received from the system share intent are registered as sources.
    fun take(context: Context): JSONObject {
        val (uris, texts, error) = synchronized(this) {
            val batch = Triple(pending, pendingText, pendingError)
            pending = emptyList()
            pendingText = emptyList()
            pendingError = null
            batch
        }
        tempDirectory(context)
        val files = JSONArray()
        val added = mutableListOf<String>()
        val result = JSONObject().put("files", files)
        if (texts.isNotEmpty()) result.put("texts", JSONArray(texts))
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
                val file = JSONObject().put("handle", handle).put("fileName", name)
                    .put("contentType", type).put("sizeBytes", source.size)
                val inferred = MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase())
                if (type.startsWith("image/") || (type == "application/octet-stream" && inferred?.startsWith("image/") == true)) {
                    imageThumbnail(context, uri, source)?.let { file.put("previewUrl", it) }
                }
                files.put(file)
            }
        } catch (failure: Exception) {
            added.forEach { sources.remove(it)?.close() }
            return result.put("files", JSONArray()).put("error", if (failure.message == TOO_LARGE) TOO_LARGE
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

    private fun imageThumbnail(context: Context, uri: Uri, source: Source): String? {
        var temporary: Source? = null
        try {
            val bitmap = if (Build.VERSION.SDK_INT >= 29) {
                val input = source.cached?.let { ImageDecoder.createSource(it) } ?: ImageDecoder.createSource(Callable {
                    val descriptor = requireNotNull(context.contentResolver.openAssetFileDescriptor(uri, "r"))
                    try {
                        Os.lseek(descriptor.fileDescriptor, 0L, OsConstants.SEEK_CUR)
                        descriptor
                    } catch (_: ErrnoException) {
                        // Some providers hand out pipes. Give the decoder a seekable file instead
                        // of letting it buffer the entire compressed source in native memory.
                        descriptor.close()
                        val copy = cacheUnknownSize(context, source.open)
                        temporary = copy
                        val fd = ParcelFileDescriptor.open(copy.cached!!, ParcelFileDescriptor.MODE_READ_ONLY)
                        AssetFileDescriptor(fd, 0, copy.size)
                    }
                })
                ImageDecoder.decodeBitmap(input) { decoder, info, _ ->
                    val longest = maxOf(info.size.width, info.size.height)
                    if (longest > THUMBNAIL_EDGE) decoder.setTargetSize(
                        maxOf(1, (info.size.width.toLong() * THUMBNAIL_EDGE / longest).toInt()),
                        maxOf(1, (info.size.height.toLong() * THUMBNAIL_EDGE / longest).toInt())
                    )
                    decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE
                }
            } else legacyThumbnail(source) ?: return null
            try {
                val bytes = ByteArrayOutputStream()
                if (!bitmap.compress(Bitmap.CompressFormat.JPEG, 82, bytes)) return null
                return "data:image/jpeg;base64," + Base64.encodeToString(bytes.toByteArray(), Base64.NO_WRAP)
            } finally {
                bitmap.recycle()
            }
        } catch (_: Exception) {
            // The original can still be attached when a format or thumbnail is unavailable.
            return null
        } finally {
            temporary?.close()
        }
    }

    private fun legacyThumbnail(source: Source): Bitmap? {
        val options = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        source.open().use { BitmapFactory.decodeStream(it, null, options) }
        if (options.outWidth <= 0 || options.outHeight <= 0) return null
        val longest = maxOf(options.outWidth, options.outHeight)
        options.inJustDecodeBounds = false
        options.inSampleSize = 1
        while (options.inSampleSize.toLong() * THUMBNAIL_EDGE < longest) options.inSampleSize *= 2
        val bitmap = source.open().use { BitmapFactory.decodeStream(it, null, options) } ?: return null
        if (options.outMimeType != "image/jpeg") return bitmap

        // BitmapFactory ignores camera orientation. Bound the EXIF read to the JPEG header.
        val orientation = try {
            val header = ByteArray(minOf(source.size, 128 * 1024L).toInt())
            DataInputStream(source.open()).use { it.readFully(header) }
            ExifInterface(header.inputStream()).getAttributeInt(ExifInterface.TAG_ORIENTATION, 1)
        } catch (_: Exception) { return bitmap }
        val transform = Matrix()
        when (orientation) {
            ExifInterface.ORIENTATION_FLIP_HORIZONTAL -> transform.setScale(-1f, 1f)
            ExifInterface.ORIENTATION_ROTATE_180 -> transform.setRotate(180f)
            ExifInterface.ORIENTATION_FLIP_VERTICAL -> transform.setScale(1f, -1f)
            ExifInterface.ORIENTATION_TRANSPOSE -> { transform.setRotate(90f); transform.postScale(-1f, 1f) }
            ExifInterface.ORIENTATION_ROTATE_90 -> transform.setRotate(90f)
            ExifInterface.ORIENTATION_TRANSVERSE -> { transform.setRotate(-90f); transform.postScale(-1f, 1f) }
            ExifInterface.ORIENTATION_ROTATE_270 -> transform.setRotate(270f)
            else -> return bitmap
        }
        return Bitmap.createBitmap(bitmap, 0, 0, bitmap.width, bitmap.height, transform, true).also {
            if (it !== bitmap) bitmap.recycle()
        }
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
