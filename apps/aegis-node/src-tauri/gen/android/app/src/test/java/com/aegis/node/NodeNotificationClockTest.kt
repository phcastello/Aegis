package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

class NodeNotificationClockTest {
 @Test fun authenticatedClockAdvancesOnlyByMonotonicElapsedTime() {
  var elapsed=5_000_000_000L
  val clock=NodeNotificationClock.authenticated(1_000_000_000){elapsed}
  assertEquals(1_000_000L,clock.nowEpochSecond())
  elapsed+=999_999_999
  assertEquals(1_000_000L,clock.nowEpochSecond())
  elapsed+=1
  assertEquals(1_000_001L,clock.nowEpochSecond())
  elapsed+=29_000_000_000L
  assertEquals(1_000_030L,clock.nowEpochSecond())
 }
 @Test fun fractionalAnchorIsRoundedOnlyAfterAddingNativeElapsedTime() {
  var elapsed=0L
  val clock=NodeNotificationClock.authenticated(1_000_900){elapsed}
  assertEquals(1000L,clock.nowEpochSecond())
  elapsed=200_000_000
  assertEquals(1001L,clock.nowEpochSecond())
  elapsed=1_800_000_000
  assertEquals(1002L,clock.nowEpochSecond())
 }
 @Test fun invalidOrOverflowingAuthenticatedAnchorIsRejected() {
  for(anchor in listOf(0L,-1L,Long.MAX_VALUE)) {
   try {NodeNotificationClock.authenticated(anchor){0L};fail("invalid clock accepted")}
   catch(_:IllegalArgumentException){}
  }
 }
}
