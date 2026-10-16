package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

class NodeGeofenceReportPolicyTest {
 @Test fun remoteRetryIsBoundedAndDoesNotOutliveTheLocalLease() {
  assertTrue(NodeGeofenceReportPolicy.retry(0,100,1000,true))
  assertTrue(NodeGeofenceReportPolicy.retry(4,100,1000,true))
  assertFalse(NodeGeofenceReportPolicy.retry(5,100,1000,true))
  assertFalse(NodeGeofenceReportPolicy.retry(0,1000,1000,true))
  assertFalse(NodeGeofenceReportPolicy.retry(0,100,1000,false))
 }
 @Test fun onlyTheReportedStateAndIdentityCanClearDurablePending() {
  assertTrue(NodeGeofenceReportPolicy.acknowledges(3,3,"revision","revision","node","node"))
  assertFalse(NodeGeofenceReportPolicy.acknowledges(4,3,"revision","revision","node","node"))
  assertFalse(NodeGeofenceReportPolicy.acknowledges(3,3,"new-revision","revision","node","node"))
  assertFalse(NodeGeofenceReportPolicy.acknowledges(3,3,"revision","revision","new-node","node"))
 }
}
