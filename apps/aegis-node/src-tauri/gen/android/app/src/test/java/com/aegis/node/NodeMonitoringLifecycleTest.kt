package com.aegis.node

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

class NodeMonitoringLifecycleTest {
 private fun coordinator():NodeMonitoringLifecycle {var next=0;return NodeMonitoringLifecycle{"owner-${++next}"}}

 @Test fun aStartAcceptedBeforeItsCallbackPreventsTheOldStartIdFromStoppingIt() {
  val lifecycle=coordinator()
  synchronized(lifecycle.lock) {
   val owner=lifecycle.admit()
   var amsLastStart:Int
   val lastDeliveredStart=1
   // AMS has accepted another start, but its onStartCommand has not run yet.
   assertEquals(owner,lifecycle.admit());amsLastStart=2
   assertFalse(lifecycle.stopIfIdle(owner,{false},{amsLastStart==lastDeliveredStart}))
   assertFalse(lifecycle.isStopping(owner));assertEquals(owner,lifecycle.admit())
  }
 }

 @Test fun aSuccessfulStopAllocatesANewOwnerBeforeTheOldDestroyArrives() {
  val lifecycle=coordinator()
  synchronized(lifecycle.lock) {
   val old=lifecycle.admit()
   assertTrue(lifecycle.stopIfIdle(old,{false},{true}))
   val replacement=lifecycle.admit()
   assertNotEquals(old,replacement)
   lifecycle.destroyed(old)
   assertEquals("Old onDestroy must retain the replacement generation",replacement,lifecycle.admit())
   assertFalse(lifecycle.isStopping(replacement))
  }
 }

 @Test fun anotherActivePlacePreventsStoppingTheSharedService() {
  val lifecycle=coordinator()
  synchronized(lifecycle.lock) {
   val owner=lifecycle.admit()
   var attemptedStop=false
   assertFalse(lifecycle.stopIfIdle(owner,{true},{attemptedStop=true;true}))
   assertFalse(attemptedStop);assertEquals(owner,lifecycle.admit())
  }
 }

 @Test fun admissionAndAmsStartAreAtomicWithTheIdleDecision() {
  val lifecycle=coordinator()
  val admitted=CountDownLatch(1);val mayStart=CountDownLatch(1);val finished=CountDownLatch(2)
  val failure=AtomicReference<Throwable?>()
  var active=false;var amsAccepted=false;var stopCalled=false
  val owner=synchronized(lifecycle.lock){lifecycle.admit()}
  val start=Thread {
   try {synchronized(lifecycle.lock){
    assertEquals(owner,lifecycle.admit());active=true;admitted.countDown()
    check(mayStart.await(5,TimeUnit.SECONDS));amsAccepted=true
   }}catch(error:Throwable){failure.compareAndSet(null,error)}finally{finished.countDown()}
  }
  val stop=Thread {
   try {check(admitted.await(5,TimeUnit.SECONDS));synchronized(lifecycle.lock){
    lifecycle.stopIfIdle(owner,{active},{stopCalled=true;!amsAccepted})
   }}catch(error:Throwable){failure.compareAndSet(null,error)}finally{finished.countDown()}
  }
  start.start();stop.start();assertTrue(admitted.await(5,TimeUnit.SECONDS));mayStart.countDown()
  assertTrue(finished.await(5,TimeUnit.SECONDS))
  assertNull(failure.get())
  synchronized(lifecycle.lock){assertTrue(active);assertTrue(amsAccepted);assertFalse(stopCalled)}
 }
}
