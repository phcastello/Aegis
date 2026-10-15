package com.aegis.node

import android.content.Context
import android.util.Base64
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec

/** Reads the existing Rust keyring vault. Never copies or changes the installation credential. */
object NodeNativeSession {
  private const val VAULT = "keyring-aegis-node-identity-v1"
  private const val ENTRY = "installation\uFEFF@\uFEFFcom.aegis.node.identity.v1"
  data class Session(val origin: String, val nodeId: String, val credential: String)
  fun read(context: Context): Session? {return try {
    val encrypted = context.getSharedPreferences(VAULT, Context.MODE_PRIVATE).getString(ENTRY, null)
    if (encrypted == null) null else {
      val bytes = Base64.decode(encrypted, Base64.DEFAULT)
      require(bytes.size > 29 && bytes[0].toInt() == 12)
      val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
      val key = store.getKey(VAULT, null) ?: return null
      val cipher = Cipher.getInstance("AES/GCM/NoPadding")
      cipher.init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(128, bytes.copyOfRange(1, 13)))
      val record = JSONObject(String(cipher.doFinal(bytes.copyOfRange(13, bytes.size)), Charsets.UTF_8))
      val node = record.optJSONObject("node") ?: return null
      if (record.optBoolean("revoked") || !record.isNull("pending") || !node.optBoolean("enabled") || !node.isNull("revokedAt")) return null
      val origin = record.getString("origin")
      require(validOrigin(origin, context))
      val bound = NodeNativeNotifications.prefs(context)
      if (bound.getString("nodeId", null) != node.getString("id") || bound.getString("origin", null) != origin) null
      else Session(origin, node.getString("id"), record.getString("credential"))
    }
  } catch (_: Exception) { null }}

  fun validOrigin(origin: String, context: Context): Boolean = try {
    val u = java.net.URI(origin)
    (u.scheme == "https" || (u.scheme == "http" && (BuildConfig.DEBUG || (BuildConfig.AEGIS_FRAMEWORK_FIXTURE && u.host == "127.0.0.1" && u.port == 18104)))) && u.host != null && u.rawUserInfo == null && u.rawQuery == null && u.rawFragment == null && (u.path.isNullOrEmpty() || u.path == "/")
  } catch (_: Exception) { false }

  class HttpFailure(val status: Int) : Exception()
  fun call(context: Context, method: String, path: String, body: JSONObject? = null, tokenOnly: Boolean = false, expectedScope:String?=null): JSONObject {
    require(path.startsWith("/api/") && !path.contains("?") && !path.contains(".."))
    val session = if (tokenOnly) null else read(context) ?: throw HttpFailure(401)
    if(expectedScope!=null && (session?:read(context))?.nodeId!=expectedScope)throw HttpFailure(409)
    val origin = session?.origin ?: NodeNativeNotifications.prefs(context).getString("origin", null) ?: throw HttpFailure(401)
    require(validOrigin(origin, context))
    val connection = URL(origin + path).openConnection() as HttpURLConnection
    try {
      connection.connectTimeout = 10000; connection.readTimeout = 15000; connection.instanceFollowRedirects = false
      connection.requestMethod = method; connection.setRequestProperty("Accept", "application/json")
      session?.let { connection.setRequestProperty("Authorization", "AegisNode ${it.credential}") }
      if (body != null) {
        connection.doOutput = true; connection.setRequestProperty("Content-Type", "application/json")
        connection.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
      }
      val code = connection.responseCode
      if (code !in 200..299) throw HttpFailure(code)
      val bytes = connection.inputStream.use { it.readBytesBounded(if(path=="/api/nodes/geofences/manifest")1048576 else 262144) }
      return if (bytes.isEmpty()) JSONObject() else JSONObject(String(bytes, Charsets.UTF_8))
    } finally { connection.disconnect() }
  }
}
internal fun java.io.InputStream.readBytesBounded(max: Int): ByteArray {
  val output = java.io.ByteArrayOutputStream(); val block = ByteArray(8192)
  while (true) { val n = read(block); if (n < 0) break; require(output.size() + n <= max); output.write(block, 0, n) }
  return output.toByteArray()
}
