package com.aegis.node
import org.junit.Assert.*
import org.junit.Test
class NodeNotificationPayloadTest {
 private fun data()=mapOf("nodeId" to "current-node", "type" to "notification.show", "version" to "1", "commandId" to "00000000-0000-4000-8000-000000000001", "expiresAt" to "2026-10-03T12:00:00Z", "title" to "Aegis", "body" to "Olá")
 @Test fun validInputNeedsMatchingNativeBinding() {assertNotNull(NodeNotificationPayload.fromData(data(),"current-node"));assertNull(NodeNotificationPayload.fromData(data(),null));assertNull(NodeNotificationPayload.fromData(data(),"another-node"))}
 @Test fun rejectsUnknownTypesAndExtraFields() {assertNull(NodeNotificationPayload.fromData(data()+mapOf("type" to "shell.execute"),"current-node"));assertNull(NodeNotificationPayload.fromData(data()+mapOf("extra" to "value"),"current-node"));assertNull(NodeNotificationPayload.fromData(data()+mapOf("version" to "2"),"current-node"))}
 @Test fun validatesUuidAndBounds() {assertNull(NodeNotificationPayload.fromData(data()+mapOf("commandId" to "invalid"),"current-node"));assertFalse(NodeNotificationPayload.validText("", "body"));assertFalse(NodeNotificationPayload.validText("a".repeat(121), "body"));assertFalse(NodeNotificationPayload.validText("title", "a".repeat(2001)));assertFalse(NodeNotificationPayload.validText("bad\n", "body"));assertTrue(NodeNotificationPayload.validText("title", "line\nnext\t"))}
 @Test fun backendRfc3339OffsetsAndFractionalPrecisionShareAnInstant() {
  val expected = NodeNotificationPayload.expiryEpochSecond("2026-10-04T19:30:00.1234567Z")
  assertEquals(expected, NodeNotificationPayload.expiryEpochSecond("2026-10-04T19:30:00.1234567+00:00"))
  assertEquals(expected, NodeNotificationPayload.expiryEpochSecond("2026-10-04T16:30:00.1234567-03:00"))
  assertEquals(expected, NodeNotificationPayload.expiryEpochSecond("2026-10-04T21:30:00.1234567+02:00"))
 }
 @Test fun invalidExpiryDoesNotBecomeAValidInstant() {
  for (invalid in listOf("invalid", "2026-10-04T19:30:00", "2026-13-04T19:30:00Z")) {
   try { NodeNotificationPayload.expiryEpochSecond(invalid); fail("invalid_expiry_accepted") }
   catch (_: java.time.format.DateTimeParseException) { }
  }
 }
}
