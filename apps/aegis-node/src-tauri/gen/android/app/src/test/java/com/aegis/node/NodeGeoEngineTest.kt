package com.aegis.node
import org.junit.Assert.*
import org.junit.Test

class NodeGeoEngineTest {
 private val first=NodeGeoEngine.Circle(0.0,0.0,200.0)
 private val second=NodeGeoEngine.Circle(0.0,0.002,200.0)
 private fun point(lon:Double,at:Long=100000,accuracy:Double=10.0)=NodeGeoEngine.Observation(0.0,lon,accuracy,at)
 @Test fun compoundOverlapRemainsInsideWhenOneCircleIsLeft(){assertEquals("inside",NodeGeoEngine.classify(listOf(first,second),point(0.0022),100000))}
 @Test fun badAccuracyOldAndFutureAreUnknown(){
  assertEquals("unknown",NodeGeoEngine.classify(listOf(first),point(0.0,accuracy=201.0),100000))
  assertEquals("unknown",NodeGeoEngine.classify(listOf(first),point(0.0,at=1),200000))
  assertEquals("unknown",NodeGeoEngine.classify(listOf(first),point(0.0,at=200000),100000))
 }
 @Test fun borderIsUncertain(){assertEquals("unknown",NodeGeoEngine.classify(listOf(first),point(0.0018),100000))}
 @Test fun initialOutsideIsBaselineWithoutExit(){val d=NodeGeoEngine.observe(NodeGeoEngine.State(),"outside",100000);assertNull(d.transition);assertEquals("outside",d.state.confirmed);assertFalse(d.needsConfirmation)}
 @Test fun realExitNeedsTwoFreshObservationsSeparatedByThirtySeconds(){
  val initial=NodeGeoEngine.State(confirmed="inside",lastCapturedAt=50000)
  val candidate=NodeGeoEngine.observe(initial,"outside",100000)
  assertNull(candidate.transition);assertTrue(candidate.needsConfirmation)
  val tooSoon=NodeGeoEngine.observe(candidate.state,"outside",110000);assertNull(tooSoon.transition)
  val confirmed=NodeGeoEngine.observe(tooSoon.state,"outside",130000);assertEquals("exit",confirmed.transition)
  assertNull(NodeGeoEngine.observe(confirmed.state,"outside",160000).transition)
 }
 @Test fun outOfOrderAndRepeatedEventsDoNotChangeState(){val state=NodeGeoEngine.State(confirmed="inside",lastCapturedAt=200000);assertEquals(state,NodeGeoEngine.observe(state,"outside",190000).state);assertEquals(state,NodeGeoEngine.observe(state,"outside",200000).state)}
 @Test fun uncertainSampleClearsPendingTransition(){val state=NodeGeoEngine.State("inside","outside",100000,100000);val result=NodeGeoEngine.observe(state,"unknown",130000);assertNull(result.transition);assertEquals("inside",result.state.confirmed);assertEquals("unknown",result.state.candidate)}
 @Test fun repeatRequiresOppositeConfirmedPresence(){
  var s=NodeGeoEngine.State(confirmed="inside",lastCapturedAt=1)
  s=NodeGeoEngine.observe(s,"outside",100000).state
  val exit=NodeGeoEngine.observe(s,"outside",130000);assertEquals("exit",exit.transition)
  assertNull(NodeGeoEngine.observe(exit.state,"outside",160000).transition)
  val enterCandidate=NodeGeoEngine.observe(exit.state,"inside",200000)
  val enter=NodeGeoEngine.observe(enterCandidate.state,"inside",230000);assertEquals("enter",enter.transition)
  val exitCandidate=NodeGeoEngine.observe(enter.state,"outside",300000)
  assertEquals("exit",NodeGeoEngine.observe(exitCandidate.state,"outside",330000).transition)
 }
}
