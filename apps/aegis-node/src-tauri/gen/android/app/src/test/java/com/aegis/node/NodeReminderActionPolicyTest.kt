package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

class NodeReminderActionPolicyTest {
 @Test fun authenticationWaitsWhileDefinitiveRejectionsRemainClassified() {
  assertFalse(NodeReminderActionPolicy.permanent(NodeReminderActionPolicy.httpResult(401)))
  for(status in listOf(400,403,404,409,410))assertTrue(NodeReminderActionPolicy.permanent(NodeReminderActionPolicy.httpResult(status)))
  for(status in listOf(408,429,500,503))assertFalse(NodeReminderActionPolicy.permanent(NodeReminderActionPolicy.httpResult(status)))
 }
 @Test fun retriesStartAtThirtySecondsAndRemainBoundedAfterLongOutages() {
  assertEquals(30000,NodeReminderActionPolicy.delayMillis(1))
  assertTrue(NodeReminderActionPolicy.delayMillis(3)>NodeReminderActionPolicy.delayMillis(2))
  assertEquals(21600000,NodeReminderActionPolicy.delayMillis(100))
  assertTrue(NodeReminderActionPolicy.retryWorker(7,true))
  assertFalse(NodeReminderActionPolicy.retryWorker(8,true))
  assertFalse(NodeReminderActionPolicy.retryWorker(0,false))
 }
}
