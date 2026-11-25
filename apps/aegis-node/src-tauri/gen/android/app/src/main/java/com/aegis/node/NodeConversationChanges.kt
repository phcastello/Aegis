package com.aegis.node

import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

/** Bounded process-local invalidations. Contains no transcript, audio or credential. */
internal class ConversationChangeJournal(private val capacity: Int = 128) {
  private var scope: String? = null
  private var generation = UUID.randomUUID().toString()
  private val unavailableGeneration = UUID.randomUUID().toString()
  private var revision = 0L
  private val changes = linkedMapOf<String, Long>()
  private fun bind(identity: String?) {
    if (scope == identity) return
    scope = identity; generation = UUID.randomUUID().toString(); revision = 0; changes.clear()
  }
  @Synchronized fun changed(identity: String, id: String): JSONObject {
    require(UUID.fromString(id).toString() == id)
    bind(identity)
    changes.remove(id); changes[id] = ++revision
    while (changes.size > capacity) changes.remove(changes.keys.first())
    return JSONObject().put("generation", generation).put("conversationId", id).put("revision", revision)
  }
  @Synchronized fun snapshot(identity: String?): JSONObject {
    // A temporary unreadable vault must hide, rather than consume, prior events.
    if (identity == null) return JSONObject().put("generation", unavailableGeneration).put("changes", JSONArray())
    bind(identity)
    return JSONObject().put("generation", generation).put("changes", JSONArray(changes.map { (id, sequence) ->
      JSONObject().put("conversationId", id).put("revision", sequence)
    }))
  }
}

internal object NodeConversationChanges {
  private val journal = ConversationChangeJournal()
  @Volatile var listener: ((JSONObject) -> Unit)? = null
  @Synchronized fun changed(session: NodeNativeSession.Session, id: String) {
    val event = journal.changed(NodeNativeSession.fingerprint(session), id)
    // Presentation failure must never fail an already persisted voice turn.
    try { listener?.invoke(event) } catch (_: Exception) { }
  }
  @Synchronized fun snapshot(session: NodeNativeSession.Session?): String = journal.snapshot(session?.let { NodeNativeSession.fingerprint(it) }).toString()
}
