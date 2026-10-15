package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

class NodeLocationScopeTest {
 @Test fun requestFromAnOldOrUnboundIdentityCannotBecomeANewIdentityObservation() {
  assertFalse(locationScopeIsCurrent("old-node","new-node"))
  assertFalse(locationScopeIsCurrent("old-node",null))
  assertFalse(locationScopeIsCurrent(null,"new-node"))
  assertTrue(locationScopeIsCurrent("same-node","same-node"))
  assertTrue(locationScopeIsCurrent(null,null)) // A local-only unpaired lookup remains unpaired.
 }
}
