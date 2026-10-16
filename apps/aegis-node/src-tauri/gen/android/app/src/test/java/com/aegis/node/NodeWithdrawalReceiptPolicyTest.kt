package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

class NodeWithdrawalReceiptPolicyTest {
 @Test fun aCapturedFcmScopeCannotApplyAfterPairingChangesAuthority() {
  val captured="old-node";var authority="new-node";var applied=false
  assertFalse(applyForCurrentIdentity({authority==captured}){applied=true})
  assertFalse(applied)
  authority=captured
  assertTrue(applyForCurrentIdentity({authority==captured}){applied=true})
  assertTrue(applied)
 }
 @Test fun onlyRequestedPairsAreAcknowledgedAndReplayIsIdempotent() {
  val one="first" to 1L;val two="second" to 2L
  assertEquals(setOf(one),NodeWithdrawalReceiptPolicy.received(setOf(one,two),listOf(one,one,"first" to 2L,"unknown" to 1L)))
  assertTrue(NodeWithdrawalReceiptPolicy.received(setOf(one),emptyList()).isEmpty())
 }
 @Test fun receiptRetryBudgetStopsAfterSixAttempts() {
  assertTrue(NodeWithdrawalReceiptPolicy.retry(0,true))
  assertTrue(NodeWithdrawalReceiptPolicy.retry(4,true))
  assertFalse(NodeWithdrawalReceiptPolicy.retry(5,true))
  assertFalse(NodeWithdrawalReceiptPolicy.retry(0,false))
 }
}
