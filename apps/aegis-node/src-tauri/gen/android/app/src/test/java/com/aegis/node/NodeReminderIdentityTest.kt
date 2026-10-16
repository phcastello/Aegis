package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

class NodeReminderIdentityTest {
 @Test fun queuedPostsAreWithdrawnEvenBeforeTheyAreVisibleInTheSystem() {
  assertEquals(setOf("reminder:occurrence:3" to 1),identityReminderTags(listOf("occurrence" to 3L),emptyList()))
 }
 @Test fun legacyActiveGenerationsAreIncludedWithoutTouchingOtherNotifications() {
  assertEquals(setOf("reminder:occurrence:3" to 1,"reminder:occurrence:2" to 1),identityReminderTags(listOf("occurrence" to 3L),listOf("reminder:occurrence:3" to 1,"reminder:occurrence:2" to 1,"chat" to 9)))
 }
 @Test fun advancingGenerationRetiresOnlyItsPreviousPresentation() {
  assertEquals("reminder:occurrence:2" to 1,supersededReminderTag("occurrence",2,3))
  assertNull(supersededReminderTag("occurrence",3,3))
  assertNull(supersededReminderTag("occurrence",3,2))
 }
 @Test fun rawVaultAuthorityAllowsFirstBindingAndRejectsLateBindAndUnbind() {
  val current=NativeIdentityAuthority("new","https://aegis.invalid",true)
  assertTrue(identityBindingAllowed(current,"new","https://aegis.invalid"))
  assertFalse(identityBindingAllowed(current,"old","https://aegis.invalid"))
  assertFalse(identityBindingAllowed(current,"new","https://other.invalid"))
  assertFalse(identityBindingAllowed(current,null))
  assertFalse(identityBindingAllowed(NativeIdentityAuthority("old","https://aegis.invalid",false),null,"https://other.invalid"))
  for(inactive in listOf(NativeIdentityAuthority("old","https://aegis.invalid",false),NativeIdentityAuthority(null,null,false))){assertTrue(identityBindingAllowed(inactive,null));assertFalse(identityBindingAllowed(inactive,"old"))}
 }
 @Test fun aCapturedOldIdentityCannotExecuteAfterTheAuthoritativeVaultChanges() {
  var authority=NativeIdentityAuthority("old","https://aegis.invalid",true)
  val captured="old"
  authority=NativeIdentityAuthority("new","https://aegis.invalid",true)
  var applied=false
  assertFalse(applyForCurrentIdentity({identityBindingAllowed(authority,captured)}){applied=true})
  assertFalse(applied)
  assertTrue(applyForCurrentIdentity({identityBindingAllowed(authority,"new")}){applied=true})
  assertTrue(applied)
 }
 @Test fun lateUnbindAndUnauthorizedOldScopeCannotClearANewManifest() {
  val current=NativeIdentityAuthority("new","https://aegis.invalid",true)
  var cleared=false
  assertFalse(applyForCurrentIdentity({identityBindingAllowed(current,null,"https://aegis.invalid")}){cleared=true})
  assertFalse(applyForCurrentIdentity({identityScopeMatches(current,"old","https://aegis.invalid")}){cleared=true})
  assertFalse(cleared)
  assertTrue(identityScopeMatches(current.copy(active=false),"new","https://aegis.invalid"))
 }
}
