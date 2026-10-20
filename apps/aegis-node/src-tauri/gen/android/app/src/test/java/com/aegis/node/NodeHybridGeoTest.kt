package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

/** Sensitivity comparison uses deterministic synthetic tracks, never confidence probabilities. */
class NodeHybridGeoTest {
 private val home=listOf(NodeGeoEngine.Circle(0.0,0.0,50.0))
 private fun fix(east:Double,at:Long,accuracy:Double=13.0)=NodeGeoEngine.Observation(0.0,Math.toDegrees(east/6371000.0),accuracy,at)
 @Test fun compareFullIntermediateAndOwnerMarginsForSmallResidence() {
  for(accuracy in listOf(13.0,30.0,65.0,100.0,200.0)) {
   val classifications=listOf(1.0,0.65,0.4).map{NodeGeoEngine.assess(home,fix(0.0,100000,accuracy),100000,it).classification}
   assertEquals(if(accuracy<50)"inside" else "unknown",classifications[0])
   assertEquals(if(accuracy*0.65<50)"inside" else "unknown",classifications[1])
   assertEquals(if(accuracy*0.4<50)"inside" else "unknown",classifications[2])
  }
  // The 0.4 margin improves availability but never converts proximity to arrival.
  for(factor in listOf(1.0,0.65,0.4))for(accuracy in listOf(13.0,30.0,65.0,100.0,200.0))assertNotEquals("inside",NodeGeoEngine.assess(home,fix(120.0,100000,accuracy),100000,factor).classification)
 }
 @Test fun initialBaselineStaysStrictButApproachMayUseTwoTolerantFixes() {
  val initial=NodeGeoEngine.hybrid(NodeGeoEngine.State(),home,fix(0.0,100000,100.0),100000,1)
  assertEquals("unknown",initial.state.confirmed);assertNull(initial.transition)
  var state=NodeGeoEngine.hybrid(initial.state,home,fix(300.0,131000,30.0),131000,1).state
  assertEquals("outside",state.confirmed)
  val first=NodeGeoEngine.hybrid(state,home,fix(0.0,162000,100.0),162000,1)
  assertNull(first.transition);assertTrue(first.needsConfirmation)
  val entered=NodeGeoEngine.hybrid(first.state,home,fix(0.0,193000,100.0),193000,1)
  assertEquals("enter",entered.transition)
  assertNull(NodeGeoEngine.hybrid(entered.state,home,fix(0.0,224000,100.0),224000,1).transition)
 }
 @Test fun envelopeOnlyApproachCannotProduceArrivalAndExitUsesRealBoundary() {
  var state=NodeGeoEngine.State("outside",lastCapturedAt=1)
  for((i,east)in listOf(180.0,130.0,85.0,130.0,220.0).withIndex()) {
   val at=100000L+i*31000
   val decision=NodeGeoEngine.hybrid(state,home,fix(east,at,30.0),at,1)
   assertNull(decision.transition);state=decision.state
  }
  state=NodeGeoEngine.State("inside",lastCapturedAt=1)
  val first=NodeGeoEngine.hybrid(state,home,fix(70.0,100000,30.0),100000,1)
  val exit=NodeGeoEngine.hybrid(first.state,home,fix(75.0,131000,30.0),131000,1)
  assertEquals("exit",exit.transition) // Still well inside the operational 200 m.
 }
 @Test fun lateApproachDoesNotNeedToObserveLiteralBoundaryCrossing() {
  val outside=NodeGeoEngine.State("outside",lastCapturedAt=10000)
  val inside=NodeGeoEngine.hybrid(outside,home,fix(0.0,100000,65.0),100000,1)
  assertNull(inside.transition)
  assertEquals("enter",NodeGeoEngine.hybrid(inside.state,home,fix(0.0,131000,65.0),131000,1).transition)
 }
 @Test fun unionSeamAndGapsKeepTheirPhysicalMeaningUnderEveryMargin() {
  val neighbor=NodeGeoEngine.Circle(0.0,Math.toDegrees(60.0/6371000.0),50.0)
  val disconnected=NodeGeoEngine.Circle(0.0,Math.toDegrees(200.0/6371000.0),50.0)
  for(factor in listOf(1.0,0.65,0.4)) {
   assertEquals("inside",NodeGeoEngine.assess(home+neighbor,fix(30.0,100000,30.0),100000,factor).classification)
   assertEquals("outside",NodeGeoEngine.assess(home+disconnected,fix(100.0,100000,30.0),100000,factor).classification)
  }
 }
 @Test fun rejectTeleportAndOutOfOrderFixesWithoutRejectingPlausibleMovement() {
  assertFalse(NodeGeoEngine.plausible(fix(0.0,100000),fix(5000.0,101000)))
  assertFalse(NodeGeoEngine.plausible(fix(0.0,100000),fix(0.0,99999)))
  assertTrue(NodeGeoEngine.plausible(fix(200.0,100000),fix(0.0,130000)))
 }
 @Test fun staleFutureAndTwoHundredMeterUncertaintyDoNotAcceptArrival() {
  val state=NodeGeoEngine.State("outside",lastCapturedAt=1)
  assertNull(NodeGeoEngine.hybrid(state,home,fix(0.0,100000,200.0),100000,1).transition)
  assertNull(NodeGeoEngine.hybrid(state,home,fix(0.0,100000),300001,1).transition)
  assertNull(NodeGeoEngine.hybrid(state,home,fix(0.0,300001),100000,1).transition)
 }
 @Test fun compareTheSameArrivalExitAndCafeTracksAcrossAllMargins() {
  val arrival=listOf(250.0,150.0,60.0,20.0,0.0,0.0)
  val exit=listOf(20.0,60.0,85.0,150.0,350.0,350.0)
  val cafe=listOf(250.0,180.0,120.0,85.0,120.0,250.0)
  fun acceptedIndex(track:List<Double>,accuracy:Double,factor:Double,initial:String,transition:String):Int? {
   var state=NodeGeoEngine.State(initial,lastCapturedAt=70000)
   val accepted=mutableListOf<Int>()
   for((index,east)in track.withIndex()) {
    // All policies see identical fresh samples, at most six acquisitions in150s.
    val at=100000L+index*30000
    val classification=NodeGeoEngine.assess(home,fix(east,at,accuracy),at,factor).classification
    val decision=NodeGeoEngine.observe(state,classification,at,1)
    if(decision.transition==transition)accepted.add(index)
    state=decision.state
   }
   assertTrue("One physical trajectory cannot create duplicate transitions",accepted.size<=1)
   return accepted.singleOrNull()
  }
  // Indices are zero-based; differences describe this fixture, not physical latency.
  val expectedArrival=mapOf(65.0 to listOf(null,5,4),100.0 to listOf(null,null,5))
  val expectedExit=mapOf(65.0 to listOf(4,4,3),100.0 to listOf(5,4,4))
  for(accuracy in listOf(65.0,100.0))for((index,factor)in listOf(1.0,0.65,0.4).withIndex()) {
   assertEquals("Arrival accuracy=$accuracy factor=$factor",expectedArrival.getValue(accuracy)[index],acceptedIndex(arrival,accuracy,factor,"outside","enter"))
   assertEquals("Exit accuracy=$accuracy factor=$factor",expectedExit.getValue(accuracy)[index],acceptedIndex(exit,accuracy,factor,"inside","exit"))
  }
  for(accuracy in listOf(13.0,30.0,65.0,100.0,200.0))for(factor in listOf(1.0,0.65,0.4)) {
   assertNull("Nearby cafe never counts as arrival accuracy=$accuracy factor=$factor",acceptedIndex(cafe,accuracy,factor,"outside","enter"))
  }
 }
 private fun currentSessionOffsets():List<Long> {
  val start=1000000L
  var attempts=0;var last=0L
  val offsets=mutableListOf<Long>()
  // Keep observing the scheduler after exhaustion; it must never keep GPS alive.
  for(offset in 0L..600000L step 1000)if(NodeMonitoringBudget.mayAcquire(start+offset,start+NodeMonitoringBudget.WINDOW_MILLIS,attempts,last)) {
   attempts++;last=start+offset;offsets.add(offset)
  }
  return offsets
 }
 private fun boundedTrack(offsets:List<Long>,accuracy:Double,distance:(Long)->Double):List<Long> {
  val start=1000000L
  // A timely envelope callback is useful evidence of approach, but its 200m
  // position is OUTSIDE the actual home. It cannot count as an inside fix.
  var previous=fix(200.0,start-1,accuracy)
  var state=NodeGeoEngine.hybrid(NodeGeoEngine.State("outside",lastCapturedAt=start-60000),home,previous,start,1).state
  val accepted=mutableListOf<Long>()
  for(offset in offsets) {
   val observation=fix(distance(offset),start+offset,accuracy)
   assertTrue("Synthetic walking observations remain physically plausible",NodeGeoEngine.plausible(previous,observation))
   val decision=NodeGeoEngine.hybrid(state,home,observation,start+offset,1)
   if(decision.transition=="enter")accepted.add(offset)
   state=decision.state;previous=observation
  }
  return accepted
 }
 @Test fun walkingFromEnvelopeLeavesTimeForTwoRealAreaFixesWithoutInnerEnter() {
  val oldOffsets=(0L..150000L step 30000).toList()
  val currentOffsets=currentSessionOffsets()
  assertEquals((0L..240000L step 30000).toList(),currentOffsets)
  val expected=mapOf(
   1.0 to listOf(210000L,210000L,210000L,240000L),
   1.2 to listOf(180000L,180000L,180000L,210000L),
   1.25 to listOf(180000L,180000L,180000L,210000L),
   1.4 to listOf(150000L,150000L,180000L,180000L)
  )
  for(speed in listOf(1.0,1.2,1.25,1.4))for((index,accuracy)in listOf(13.0,30.0,65.0,100.0).withIndex()) {
   val walking:(Long)->Double={offset->maxOf(0.0,200.0-speed*offset/1000.0)}
   // The old six attempts usually ended with zero or just one inside fix,
   // despite a normal arrival and a reliable outside baseline.
   val oldAccepted=if(speed==1.4 && accuracy<=30.0)listOf(150000L)else emptyList()
   assertEquals("Old budget speed=$speed accuracy=$accuracy",oldAccepted,boundedTrack(oldOffsets,accuracy,walking))
   assertEquals("Bounded walking arrival speed=$speed accuracy=$accuracy",listOf(expected.getValue(speed)[index]),boundedTrack(currentOffsets,accuracy,walking))
  }
  for(speed in listOf(1.0,1.2,1.25,1.4))assertTrue("A longer budget cannot make 200m uncertainty sufficient",boundedTrack(currentOffsets,200.0){offset->maxOf(0.0,200.0-speed*offset/1000.0)}.isEmpty())
 }
 @Test fun cafeOutsideTheRealAreaExhaustsTheBudgetWithoutArrivalOrContinuousRequests() {
  val offsets=currentSessionOffsets()
  for(accuracy in listOf(13.0,30.0,65.0,100.0,200.0))assertTrue("A cafe inside the envelope remains outside the home",boundedTrack(offsets,accuracy){offset->maxOf(120.0,200.0-offset/1000.0)}.isEmpty())
  assertEquals(9,offsets.size)
  assertTrue(offsets.all{it<NodeMonitoringBudget.WINDOW_MILLIS})
  assertFalse(NodeMonitoringBudget.mayAcquire(1000000L+600000L,1000000L+NodeMonitoringBudget.WINDOW_MILLIS,offsets.size,1000000L+offsets.last()))
 }
 @Test fun strictSessionBudgetsCannotLoopInNeighboringCafe() {
  val start=1000000L;val deadline=start+NodeMonitoringBudget.WINDOW_MILLIS
  var attempts=0;var last=0L
  for(offset in 0L..600000L step 1000) {
   val now=start+offset
   if(NodeMonitoringBudget.mayAcquire(now,deadline,attempts,last)){attempts++;last=now}
  }
  assertEquals(9,attempts)
  assertFalse(NodeMonitoringBudget.mayAcquire(deadline,deadline,0,0))
  assertFalse(NodeMonitoringBudget.mayStart(start+1000,start,emptyList()))
  assertTrue(NodeMonitoringBudget.mayStart(start+900000,start,listOf(start)))
  assertFalse(NodeMonitoringBudget.mayStart(start+1800000,0,listOf(start,start+900000,start+1200000)))
  assertTrue(NodeMonitoringBudget.mayStart(start+5400001,0,listOf(start,start+900000,start+1800000)))
 }
}
