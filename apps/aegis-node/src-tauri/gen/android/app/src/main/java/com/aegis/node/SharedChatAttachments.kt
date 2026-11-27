package com.aegis.node

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Process
import android.provider.OpenableColumns
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream

internal object SharedChatAttachments {
    private const val MAX_FILES = 4
    private const val MAX_BYTES = 8 * 1024 * 1024
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

    // Called on the plugin's IO executor. No URI or local path is accepted from the WebView.
    fun take(context: Context): JSONObject {
        val (uris, error) = synchronized(this) {
            val batch = pending to pendingError
            pending = emptyList()
            pendingError = null
            batch
        }
        val result = JSONObject().put("files", JSONArray())
        if (error != null) result.put("error", error)
        return try {
            var remaining = MAX_BYTES
            val files = JSONArray()
            for (uri in uris) {
                // An exported share target must never read its own providers on another app's behalf.
                val authority = uri.authority!!.substringAfterLast('@')
                require(context.packageManager.resolveContentProvider(authority, 0)?.packageName != context.packageName)
                require(context.checkUriPermission(uri, Process.myPid(), Process.myUid(), Intent.FLAG_GRANT_READ_URI_PERMISSION)
                    == PackageManager.PERMISSION_GRANTED)
                val resolver = context.contentResolver
                val name = resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
                    if (cursor.moveToFirst()) cursor.getString(0) else null
                } ?: uri.lastPathSegment?.substringAfterLast('/') ?: "arquivo"
                val buffer = ByteArrayOutputStream()
                resolver.openInputStream(uri).use { stream ->
                    requireNotNull(stream)
                    val block = ByteArray(16 * 1024)
                    while (true) {
                        val count = stream.read(block)
                        if (count < 0) break
                        if (count > remaining) return JSONObject().put("files", JSONArray())
                            .put("error", "Os anexos de uma mensagem podem somar até 8 MiB.")
                        remaining -= count
                        buffer.write(block, 0, count)
                    }
                }
                files.put(JSONObject().put("fileName", name).put("contentType", resolver.getType(uri) ?: "application/octet-stream")
                    .put("dataBase64", Base64.encodeToString(buffer.toByteArray(), Base64.NO_WRAP)))
            }
            result.put("files", files)
        } catch (_: Exception) {
            result.put("error", "Não foi possível ler o arquivo. Compartilhe novamente ou use o botão de anexo.")
        }
    }
}
