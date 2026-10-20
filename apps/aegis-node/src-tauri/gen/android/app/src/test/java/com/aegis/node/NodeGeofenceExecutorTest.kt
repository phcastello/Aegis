package com.aegis.node

import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

class NodeGeofenceExecutorTest {
 @Test fun nativeEventsDoNotWaitForLongOperations() {
  val running=CountDownLatch(1);val release=CountDownLatch(1);val finished=CountDownLatch(1);val event=CountDownLatch(1)
  NodeGeofences.background{running.countDown();try{release.await(5,TimeUnit.SECONDS)}finally{finished.countDown()}}
  try {
   assertTrue(running.await(2,TimeUnit.SECONDS))
   NodeGeofences.event{event.countDown()}
   assertTrue("A native transition cannot wait behind an installer or HTTP request",event.await(2,TimeUnit.SECONDS))
  }finally{release.countDown();assertTrue(finished.await(2,TimeUnit.SECONDS))}
 }
}
