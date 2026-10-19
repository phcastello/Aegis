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
  // Native AndroidKeyStore/SharedPreferences only: no JNI callback or Rust mutex.
  private fun vault(context:Context):JSONObject? {
    val encrypted = context.getSharedPreferences(VAULT, Context.MODE_PRIVATE).getString(ENTRY, null)
    return if (encrypted == null) null else {
      val bytes = Base64.decode(encrypted, Base64.DEFAULT)
      require(bytes.size > 29 && bytes[0].toInt() == 12)
      val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
      val key = store.getKey(VAULT, null) ?: return null
      val cipher = Cipher.getInstance("AES/GCM/NoPadding")
      cipher.init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(128, bytes.copyOfRange(1, 13)))
      JSONObject(String(cipher.doFinal(bytes.copyOfRange(13, bytes.size)), Charsets.UTF_8))
    }
  }
  private fun authority(record:JSONObject?):NativeIdentityAuthority {
    val node=record?.optJSONObject("node")
    val active=record!=null&&node!=null&&!record.optBoolean("revoked")&&record.isNull("pending")&&node.optBoolean("enabled")&&node.isNull("revokedAt")
    return NativeIdentityAuthority(node?.optString("id"),record?.optString("origin"),active)
  }
  /** Authorizes native identity binding only; it never returns a credential/session. */
  fun bindingAllowed(context:Context,id:String?,origin:String?=null):Boolean = try {
    identityBindingAllowed(authority(vault(context)),id,origin)
  }catch(_:Exception){false}
  fun scopeIsCurrent(context:Context,id:String,origin:String?=null):Boolean = try {
    identityScopeMatches(authority(vault(context)),id,origin)
  }catch(_:Exception){false}
  fun read(context: Context): Session? {return try {
      val record = vault(context) ?: return null
      val node = record.optJSONObject("node") ?: return null
      if (record.optBoolean("revoked") || !record.isNull("pending") || !node.optBoolean("enabled") || !node.isNull("revokedAt")) return null
      val origin = record.getString("origin")
      require(validOrigin(origin, context))
      val bound = NodeNativeNotifications.prefs(context)
      if (bound.getString("nodeId", null) != node.getString("id") || bound.getString("origin", null) != origin) null
      else Session(origin, node.getString("id"), record.getString("credential"))
  } catch (_: Exception) { null }}

  fun validOrigin(origin: String, context: Context): Boolean = try {
    val u = java.net.URI(origin)
    (u.scheme == "https" || (u.scheme == "http" && (BuildConfig.DEBUG || (BuildConfig.AEGIS_FRAMEWORK_FIXTURE && u.host == "127.0.0.1" && u.port == 18104)))) && u.host != null && u.rawUserInfo == null && u.rawQuery == null && u.rawFragment == null && (u.path.isNullOrEmpty() || u.path == "/")
  } catch (_: Exception) { false }

  class HttpFailure(val status: Int) : Exception()
  class IdentityUnavailable : Exception()
  // The test hook affects one call-local identity read on its own thread only.
  // Public read()/binding checks and concurrent workers always use the real vault.
  private val unavailableCallIdentity=ThreadLocal<BooleanArray>()
  internal fun withUnavailableCallIdentityFixture(block:()->Unit):Boolean {
    check(BuildConfig.DEBUG || BuildConfig.AEGIS_FRAMEWORK_FIXTURE)
    check(unavailableCallIdentity.get()==null)
    val pending=booleanArrayOf(true);unavailableCallIdentity.set(pending)
    return try{block();!pending[0]}finally{unavailableCallIdentity.remove()}
  }
  private fun callIdentity(context:Context):Session? {
    if(BuildConfig.DEBUG || BuildConfig.AEGIS_FRAMEWORK_FIXTURE) {
      val pending=unavailableCallIdentity.get()
      if(pending!=null && pending[0]){pending[0]=false;return null}
    }
    return read(context)
  }
  fun call(context: Context, method: String, path: String, body: JSONObject? = null, tokenOnly: Boolean = false, expectedScope:String?=null,connectTimeoutMillis:Int=10000,readTimeoutMillis:Int=15000): JSONObject {
    require(path.startsWith("/api/") && !path.contains("?") && !path.contains(".."))
    val session = if (tokenOnly) null else callIdentity(context) ?: throw IdentityUnavailable()
    if(expectedScope!=null) {
      // An unreadable identity is temporary, not proof of a server conflict.
      val current=session?:callIdentity(context)?:throw IdentityUnavailable()
      if(current.nodeId!=expectedScope)throw HttpFailure(409)
    }
    val origin = session?.origin ?: NodeNativeNotifications.prefs(context).getString("origin", null) ?: throw IdentityUnavailable()
    require(validOrigin(origin, context))
    val connection = URL(origin + path).openConnection() as HttpURLConnection
    val deadline=android.os.SystemClock.elapsedRealtime()+connectTimeoutMillis+readTimeoutMillis
    fun remainingReadTimeout():Int=(deadline-android.os.SystemClock.elapsedRealtime()).coerceAtMost(readTimeoutMillis.toLong()).toInt().also{if(it<=0)throw java.net.SocketTimeoutException()}
    try {
      connection.connectTimeout = connectTimeoutMillis.coerceIn(1000,10000); connection.readTimeout = readTimeoutMillis.coerceIn(1000,15000); connection.instanceFollowRedirects = false
      connection.requestMethod = method; connection.setRequestProperty("Accept", "application/json")
      session?.let { connection.setRequestProperty("Authorization", "AegisNode ${it.credential}") }
      if (body != null) {
        connection.doOutput = true; connection.setRequestProperty("Content-Type", "application/json")
        connection.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
      }
      connection.readTimeout=remainingReadTimeout()
      val code = connection.responseCode
      if (code !in 200..299) throw HttpFailure(code)
      val bytes = connection.inputStream.use { stream ->
        val output=java.io.ByteArrayOutputStream();val block=ByteArray(8192)
        while(true) {
          connection.readTimeout=remainingReadTimeout()
          val count=stream.read(block);if(count<0)break
          require(output.size()+count<=(if(path=="/api/nodes/geofences/manifest")1048576 else 262144));output.write(block,0,count)
        }
        output.toByteArray()
      }
      return if (bytes.isEmpty()) JSONObject() else JSONObject(String(bytes, Charsets.UTF_8))
    } finally { connection.disconnect() }
  }
}
internal fun java.io.InputStream.readBytesBounded(max: Int): ByteArray {
  val output = java.io.ByteArrayOutputStream(); val block = ByteArray(8192)
  while (true) { val n = read(block); if (n < 0) break; require(output.size() + n <= max); output.write(block, 0, n) }
  return output.toByteArray()
}
