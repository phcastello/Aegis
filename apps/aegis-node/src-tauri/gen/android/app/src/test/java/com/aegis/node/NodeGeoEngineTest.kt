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
 @Test fun preActivationCacheCannotArmAnExitAndQuietPresenceDoesNotExpire() {
  val activatedAt=100000L
  val cached=NodeGeoEngine.observe(NodeGeoEngine.State(),"inside",activatedAt-90000,activatedAt)
  assertEquals("unknown",cached.state.confirmed);assertFalse(cached.needsConfirmation)
  val outside=NodeGeoEngine.observe(cached.state,"outside",activatedAt+1,activatedAt)
  assertEquals("outside",outside.state.confirmed);assertNull(outside.transition)
  assertNull(NodeGeoEngine.observe(outside.state,"outside",activatedAt+31000,activatedAt).transition)
  val enterCandidate=NodeGeoEngine.observe(outside.state,"inside",activatedAt+32000,activatedAt)
  val entered=NodeGeoEngine.observe(enterCandidate.state,"inside",activatedAt+63000,activatedAt)
  assertEquals("enter",entered.transition)
  // The cutoff rejects older captures; it does not expire hours of valid presence.
  val exitCandidate=NodeGeoEngine.observe(entered.state,"outside",activatedAt+3600000,activatedAt)
  assertTrue(exitCandidate.needsConfirmation);assertNull(exitCandidate.transition)
  assertEquals("exit",NodeGeoEngine.observe(exitCandidate.state,"outside",activatedAt+3631000,activatedAt).transition)
 }
 @Test fun restoredPresenceOutsideNeedsARealReentryBeforeAnExit() {
  val registeredAt=200000L
  val outside=NodeGeoEngine.observe(NodeGeoEngine.State(),"outside",registeredAt+1,registeredAt)
  assertNull(outside.transition);assertEquals("outside",outside.state.confirmed)
  val entering=NodeGeoEngine.observe(outside.state,"inside",registeredAt+1000,registeredAt)
  assertNull(entering.transition)
  val inside=NodeGeoEngine.observe(entering.state,"inside",registeredAt+32000,registeredAt)
  assertEquals("enter",inside.transition)
  val exiting=NodeGeoEngine.observe(inside.state,"outside",registeredAt+33000,registeredAt)
  assertNull(exiting.transition)
  assertEquals("exit",NodeGeoEngine.observe(exiting.state,"outside",registeredAt+64000,registeredAt).transition)
 }
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
 @Test fun unknownEventCanUseTwoBoundedAcquisitionsWithoutPollingForever() {
  val initial=NodeGeoEngine.State(confirmed="inside",lastCapturedAt=1)
  val poor=NodeGeoEngine.observe(initial,"unknown",1000);assertTrue(poor.needsConfirmation)
  val first=NodeGeoEngine.observe(poor.state,"outside",31000);assertNull(first.transition)
  assertTrue(NodeGeoEngine.canConfirmAgain(1,31000,90000,first.state.candidate!="unknown"))
  val second=NodeGeoEngine.observe(first.state,"outside",62000);assertEquals("exit",second.transition)
  assertFalse(NodeGeoEngine.canConfirmAgain(2,62000,90000,true))
  assertFalse(NodeGeoEngine.canConfirmAgain(1,61000,90000,true))
  assertFalse(NodeGeoEngine.canConfirmAgain(1,31000,90000,false))
 }
 private fun meters(east:Double,accuracy:Double,at:Long=100000)=point(Math.toDegrees(east/6371000.0),at,accuracy)
 @Test fun residentialCirclesHaveHonestConfidenceAcrossAccuracyAndPositionFixtures() {
  val home=NodeGeoEngine.Circle(0.0,0.0,65.0)
  val neighboring=NodeGeoEngine.Circle(0.0,Math.toDegrees(100.0/6371000.0),65.0)
  val fixtures=listOf(
   Triple(0.0,listOf("inside","inside","unknown","unknown","unknown"),"center"),
   Triple(40.0,listOf("inside","unknown","unknown","unknown","unknown"),"intermediate"),
   Triple(65.0,List(5){"unknown"},"border"),
   Triple(200.0,listOf("outside","outside","outside","outside","unknown"),"exterior")
  )
  val accuracies=listOf(13.0,30.0,65.0,100.0,200.0)
  for((east,expected,label)in fixtures)for((i,accuracy)in accuracies.withIndex())assertEquals("$label accuracy=$accuracy",expected[i],NodeGeoEngine.classify(listOf(home),meters(east,accuracy),100000))
  for(accuracy in accuracies) {
   assertEquals("second center accuracy=$accuracy",if(accuracy<65.0)"inside" else "unknown",NodeGeoEngine.classify(listOf(home,neighboring),meters(100.0,accuracy),100000))
  }
  // Crossing the overlap does not become an exit from the union. At the narrow
  // seam, uncertain readings preserve the established presence.
  var state=NodeGeoEngine.observe(NodeGeoEngine.State(),NodeGeoEngine.classify(listOf(home,neighboring),meters(0.0,13.0),100000),100000).state
  for((i,east)in listOf(40.0,50.0,60.0,100.0).withIndex()) {
   val captured=131000L+i*31000
   val decision=NodeGeoEngine.observe(state,NodeGeoEngine.classify(listOf(home,neighboring),meters(east,13.0,captured),captured),captured)
   assertEquals("inside",decision.state.confirmed);assertNull(decision.transition);state=decision.state
  }
 }
 @Test fun largerExplicitAreasCanAccommodateWorseAccuracyWithoutChangingTheirGeometry() {
  val larger=NodeGeoEngine.Circle(0.0,0.0,200.0)
  for(accuracy in listOf(13.0,30.0,65.0,100.0))assertEquals("inside",NodeGeoEngine.classify(listOf(larger),meters(0.0,accuracy),100000))
  assertEquals("unknown",NodeGeoEngine.classify(listOf(larger),meters(0.0,200.0),100000))
  assertEquals("unknown",NodeGeoEngine.classify(listOf(larger),meters(190.0,13.0),100000))
  assertEquals("outside",NodeGeoEngine.classify(listOf(larger),meters(500.0,200.0),100000))
  assertEquals(200.0,larger.radiusMeters,0.0)
 }
 @Test fun lowAccuracyNeverBootstrapsResidentialPresenceEvenAfterTenMinutes() {
  val home=listOf(NodeGeoEngine.Circle(0.0,0.0,65.0))
  var state=NodeGeoEngine.State()
  // Battery saver is an ordinary provider condition, not permission to claim
  // more certainty. Repeated low-quality fixes do not average away its error.
  for(i in 0..20) {
   val at=100000L+i*31000
   val reading=NodeGeoEngine.assess(home,meters(0.0,100.0,at),at)
   assertEquals("accuracy_insufficient",reading.reasonCode)
   val decision=NodeGeoEngine.observe(state,reading.classification,at)
   assertEquals("unknown",decision.state.confirmed);assertNull(decision.transition);state=decision.state
  }
  val goodAt=state.lastCapturedAt+31000
  val confirmed=NodeGeoEngine.observe(state,NodeGeoEngine.classify(home,meters(40.0,13.0,goodAt),goodAt),goodAt)
  assertEquals("inside",confirmed.state.confirmed);assertNull(confirmed.transition)
  val imprecise=NodeGeoEngine.observe(confirmed.state,"unknown",goodAt+31000)
  assertEquals("inside",imprecise.state.confirmed);assertNull(imprecise.transition)
  val exiting=NodeGeoEngine.observe(imprecise.state,NodeGeoEngine.classify(home,meters(300.0,100.0,goodAt+62000),goodAt+62000),goodAt+62000)
  assertNull(exiting.transition)
  assertEquals("exit",NodeGeoEngine.observe(exiting.state,"outside",goodAt+93000).transition)
 }
 @Test fun staleCandidatesCannotTurnIsolatedObservationsIntoPersistence() {
  val old=NodeGeoEngine.State("inside","outside",100000,100000)
  val afterGap=NodeGeoEngine.observe(old,"outside",1000000)
  assertNull(afterGap.transition);assertEquals(1000000L,afterGap.state.candidateAt)
  assertEquals("exit",NodeGeoEngine.observe(afterGap.state,"outside",1030000).transition)
 }
 @Test fun assessmentReasonsDistinguishUnavailableGeometryStaleAndImpreciseObservations() {
  val home=listOf(NodeGeoEngine.Circle(0.0,0.0,65.0))
  assertEquals("no_geometry",NodeGeoEngine.assess(emptyList(),meters(0.0,13.0),100000).reasonCode)
  assertEquals("stale_location",NodeGeoEngine.assess(home,meters(0.0,13.0,1),200000).reasonCode)
  assertEquals("future_location",NodeGeoEngine.assess(home,meters(0.0,13.0,200000),100000).reasonCode)
  assertEquals("accuracy_insufficient",NodeGeoEngine.assess(home,meters(0.0,100.0),100000).reasonCode)
  assertEquals("near_boundary",NodeGeoEngine.assess(home,meters(60.0,13.0),100000).reasonCode)
  assertEquals("unknown",NodeGeoEngine.classify(home,NodeGeoEngine.Observation(Double.NaN,0.0,13.0,100000),100000))
  assertEquals("unknown",NodeGeoEngine.classify(home,meters(0.0,Double.NaN),100000))
 }

}
