package com.aegis.node

import android.os.SystemClock
import java.time.Instant

/** A live command keeps the authenticated Rust clock across its short native call. */
class NodeNotificationClock private constructor(private val read:()->Long) {
 fun nowEpochSecond():Long=read()
 companion object {
  val wall=NodeNotificationClock{Instant.now().epochSecond}
  fun authenticated(epochMillis:Long):NodeNotificationClock=authenticated(epochMillis,SystemClock::elapsedRealtimeNanos)
  internal fun authenticated(epochMillis:Long,monotonicNanos:()->Long):NodeNotificationClock {
   require(epochMillis>0 && epochMillis<=Long.MAX_VALUE-300_000)
   val started=monotonicNanos()
   return NodeNotificationClock {
    val elapsed=monotonicNanos()-started
    check(elapsed>=0)
    Math.addExact(epochMillis,elapsed/1_000_000)/1000
   }
  }
 }
}
