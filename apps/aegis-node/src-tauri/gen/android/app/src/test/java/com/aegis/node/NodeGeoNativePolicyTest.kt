package com.aegis.node

import org.junit.Assert.*
import org.junit.Test
import kotlin.math.*

class NodeGeoNativePolicyTest {
  private val installed=100000L
  private val now=200000L
  private val home=listOf(circle(0.0,0.0,65.0))
  private fun circle(east:Double,north:Double,radius:Double)=NodeGeoEngine.Circle(Math.toDegrees(north/6371000.0),Math.toDegrees(east/6371000.0),radius)
  private fun observation(east:Double=0.0,north:Double=0.0,accuracy:Double=13.0,at:Long=now)=NodeGeoEngine.Observation(Math.toDegrees(north/6371000.0),Math.toDegrees(east/6371000.0),accuracy,at)
  private fun state(presence:String,at:Long=now-60000)=NodeGeoEngine.State(confirmed=presence,lastCapturedAt=at)
  private fun context(union:String,baselineAt:Long=now-60000)=NodeGeoEngine.NativeContext(installed,true,union,baselineAt=baselineAt)
  private fun event(transition:String="enter",location:NodeGeoEngine.Observation?=observation(),at:Long?=now,initial:Boolean=false)=NodeGeoEngine.NativeEvidence(transition,at,now,initial,location)

  @Test fun armedNativeArrivalUsesOneCompatibleFixAndNeverDuplicates() {
    val result=NodeGeoEngine.native(state("outside"),event(),context("inside"),home,now)
    assertEquals("enter",result.transition);assertFalse(result.needsConfirmation)
    assertEquals("native_enter_accepted",result.reasonCode)
    assertNull(NodeGeoEngine.native(result.state,event(),context("inside"),home,now).transition)
  }
  @Test fun initialInsideDoesNotArriveUntilExitAndNewEntry() {
    val initial=NodeGeoEngine.native(NodeGeoEngine.State(),event(initial=true),context("inside"),home,now)
    assertNull(initial.transition);assertEquals("inside",initial.state.confirmed)
    val exit=NodeGeoEngine.native(initial.state,event("exit",observation(east=200.0,at=now+1000),now+1000).copy(receivedAt=now+1000),context("outside",now),home,now+1000)
    assertEquals("exit",exit.transition)
    val enter=NodeGeoEngine.native(exit.state,event(at=now+2000,location=observation(at=now+2000)).copy(receivedAt=now+2000),context("inside",now+1000),home,now+2000)
    assertEquals("enter",enter.transition)
  }
  @Test fun noBaselineCannotCreateTransitionEvenWhenCallerSaysReliable() {
    val decision=NodeGeoEngine.native(NodeGeoEngine.State(),event(),context("inside"),home,now)
    assertNull(decision.transition);assertEquals("native_event_initial",decision.reasonCode)
  }
  @Test fun reinstallAndExplicitInitialArePresenceWithoutRetroactiveMovement() {
    val initialized=NodeGeoEngine.native(state("outside"),event(initial=true),context("inside"),home,now)
    assertNull(initialized.transition);assertEquals("inside",initialized.state.confirmed)
    val reboot=NodeGeoEngine.native(NodeGeoEngine.State(),event(),context("inside"),home,now)
    assertNull(reboot.transition)
    val old=NodeGeoEngine.native(state("outside"),event(at=installed-1),context("inside"),home,now)
    assertNull(old.transition);assertEquals("native_event_replaced",old.reasonCode)
  }
  @Test fun missingLocationAcceptsReliableRecentBaselineAndKnownUnion() {
    val accepted=NodeGeoEngine.native(state("outside"),event(location=null,at=null),context("inside"),home,now)
    assertEquals("enter",accepted.transition);assertFalse(accepted.needsConfirmation)
    val unknownMembership=NodeGeoEngine.native(state("outside"),event(location=null,at=null),context("unknown"),home,now)
    assertNull(unknownMembership.transition);assertTrue(unknownMembership.needsConfirmation)
    val oldNow=installed+NodeGeoEngine.NATIVE_BASELINE_WITHOUT_LOCATION_MILLIS+1
    val old=NodeGeoEngine.native(state("outside",installed),event(location=null,at=null).copy(receivedAt=oldNow),context("inside",installed),home,oldNow)
    assertNull(old.transition);assertTrue(old.needsConfirmation)
  }
  @Test fun delayedNativeFixUsesNativeLifetimeWhilePointAcquisitionStillExpiresAtTwoMinutes() {
    val at=installed+1
    val delayedAt=at+300000
    val fix=observation(at=at)
    assertEquals("stale_location",NodeGeoEngine.assess(home,fix,delayedAt).reasonCode)
    val evidence=event(location=fix,at=at).copy(receivedAt=delayedAt)
    assertEquals("enter",NodeGeoEngine.native(state("outside",installed),evidence,context("inside",installed),home,delayedAt).transition)
    val tooLate=NodeGeoEngine.native(state("outside",installed),evidence,context("inside",installed),home,at+NodeGeoEngine.NATIVE_MAX_DELAY_MILLIS+1)
    assertNull(tooLate.transition);assertEquals("native_event_stale",tooLate.reasonCode)
  }
  @Test fun contradictoryLocationNeedsOneBoundedConfirmationWithoutThirtySecondDelay() {
    val evidence=event(location=observation(east=200.0))
    val candidate=NodeGeoEngine.native(state("outside"),evidence,context("inside"),home,now)
    assertNull(candidate.transition);assertTrue(candidate.needsConfirmation)
    assertEquals("outside",candidate.state.confirmed)
    val confirmed=NodeGeoEngine.confirmNative(candidate.state,evidence,context("inside"),home,observation(at=now+1000),now+1000)
    assertEquals("enter",confirmed.transition)
    val rejected=NodeGeoEngine.confirmNative(candidate.state,evidence,context("inside"),home,observation(east=200.0,at=now+1000),now+1000)
    assertNull(rejected.transition);assertEquals("unknown",rejected.state.candidate);assertEquals("outside",rejected.state.confirmed)
  }
  @Test fun recentReliableContradictionIsNotConfusedWithOlderOppositeBaseline() {
    val baseline=state("outside",now-60000)
    val oldContext=context("inside").copy(recentReliablePresence="outside",recentReliableAt=now-60000)
    assertEquals("enter",NodeGeoEngine.native(baseline,event(location=null,at=null),oldContext,home,now).transition)
    val freshContext=oldContext.copy(recentReliableAt=now-1000)
    assertEquals("transition_contradictory_location",NodeGeoEngine.native(baseline,event(location=null,at=null),freshContext,home,now).reasonCode)
  }
  @Test fun poorAccuracyCanUseNativeEvidenceButCannotInventInitialGpsPresence() {
    for(accuracy in listOf(13.0,30.0,65.0,100.0,200.0)) {
      for(batterySaver in listOf(false,true)) {
        // Battery saver changes delivery, never the evidence decision itself.
        val result=NodeGeoEngine.native(state("outside"),event(location=observation(accuracy=accuracy)),context("inside"),home,now)
        assertEquals("accuracy=$accuracy saver=$batterySaver","enter",result.transition)
        assertFalse(result.needsConfirmation)
      }
    }
    val initialGps=NodeGeoEngine.assess(home,observation(accuracy=100.0),now)
    assertEquals("unknown",initialGps.classification)
  }
  @Test fun confirmationWorkMayRunLateButCannotCreateHoursOldArrival() {
    val evidence=event(location=null,at=null)
    val candidate=NodeGeoEngine.native(state("outside"),evidence,context("unknown"),home,now)
    val time=now+NodeGeoEngine.NATIVE_CONFIRMATION_WINDOW_MILLIS+1
    val expired=NodeGeoEngine.confirmNative(candidate.state,evidence,context("inside"),home,observation(at=time),time)
    assertNull(expired.transition);assertEquals("transition_expired",expired.reasonCode)
    assertEquals("outside",expired.state.confirmed)
  }
  @Test fun nativeExitWorksSymmetricallyAndNeedsANewCycleForRepeat() {
    val initial=state("inside")
    val evidence=event("exit",observation(east=200.0))
    val exited=NodeGeoEngine.native(initial,evidence,context("outside"),home,now)
    assertEquals("exit",exited.transition)
    assertNull(NodeGeoEngine.native(exited.state,evidence,context("outside"),home,now).transition)
    val enterTime=now+1000
    val entered=NodeGeoEngine.native(exited.state,event(location=observation(at=enterTime),at=enterTime).copy(receivedAt=enterTime),context("inside",now),home,enterTime)
    assertEquals("enter",entered.transition)
    val exitTime=now+2000
    assertEquals("exit",NodeGeoEngine.native(entered.state,evidence.copy(detectedAt=exitTime,receivedAt=exitTime,observation=observation(east=200.0,at=exitTime)),context("outside",enterTime),home,exitTime).transition)
  }
  @Test fun componentExitAndComponentEntryDoNotBecomePlaceTransitions() {
    val compound=listOf(circle(0.0,0.0,65.0),circle(100.0,0.0,65.0))
    val inside=state("inside")
    val leftFirst=NodeGeoEngine.native(inside,event("exit",observation(east=100.0)),context("inside"),compound,now)
    assertNull(leftFirst.transition);assertEquals("inside",leftFirst.state.confirmed);assertFalse(leftFirst.needsConfirmation)
    val enteredSecond=NodeGeoEngine.native(inside,event(location=observation(east=100.0)),context("inside"),compound,now)
    assertNull(enteredSecond.transition)
    val ambiguous=NodeGeoEngine.native(inside,event("exit",null),context("inside"),compound,now)
    assertEquals("native_union_still_inside",ambiguous.reasonCode)
    val exited=NodeGeoEngine.native(inside,event("exit",null),context("outside"),compound,now)
    assertEquals("exit",exited.transition)
  }
  @Test fun opposingNativeEvidenceClearsPendingCandidateWithoutChangingPresence() {
    val pending=state("outside").copy(candidate="inside",candidateAt=now-1000)
    val canceled=NodeGeoEngine.native(pending,event("exit",observation(east=200.0)),context("outside"),home,now)
    assertNull(canceled.transition);assertEquals("outside",canceled.state.confirmed)
    assertEquals("unknown",canceled.state.candidate)
    val compound=NodeGeoEngine.native(state("inside").copy(candidate="outside",candidateAt=now-1000),event("exit",null),context("inside"),home,now)
    assertNull(compound.transition);assertEquals("unknown",compound.state.candidate)
  }
  @Test fun laterReliableOppositeFixRejectsOldMovementEvenWhenThatFixIsNoLongerFresh() {
    val deliveredAt=now+200000
    val opposite=context("inside").copy(recentReliablePresence="outside",recentReliableAt=now+15000)
    val oldEvent=event(at=now+10000,location=observation(at=now+10000)).copy(receivedAt=deliveredAt)
    val rejected=NodeGeoEngine.native(state("outside"),oldEvent,opposite,home,deliveredAt)
    assertNull(rejected.transition);assertFalse(rejected.needsConfirmation)
    assertEquals("native_event_out_of_order",rejected.reasonCode)
    val corroborating=opposite.copy(recentReliablePresence="inside")
    assertEquals("enter",NodeGeoEngine.native(state("outside"),oldEvent,corroborating,home,deliveredAt).transition)
  }
  @Test fun newerUncertainFixCannotEraseValidDelayedNativeMovement() {
    val previous=state("outside",now-1000).copy(candidate="inside",candidateAt=now-1000)
    val result=NodeGeoEngine.native(previous,event(at=now-2000,location=observation(at=now-2000)),context("inside",now-60000),home,now)
    assertEquals("enter",result.transition);assertEquals("unknown",result.state.candidate)
  }
  @Test fun olderEventCannotReturnAfterNewerCycleAndMovePresenceBack() {
    val previous=state("outside",now-1000)
    val old=NodeGeoEngine.native(previous,event(at=now-2000,location=observation(at=now-2000)),context("inside",previous.lastCapturedAt),home,now)
    assertNull(old.transition);assertEquals(previous,old.state)
    assertEquals("native_event_out_of_order",old.reasonCode)
  }
  @Test fun unreliableBaselineCannotFireEvenWithCompatibleLocation() {
    val result=NodeGeoEngine.native(state("outside"),event(),context("inside").copy(baselineReliable=false),home,now)
    assertNull(result.transition);assertTrue(result.needsConfirmation)
  }
  @Test fun overlappingCirclesCanJointlyCoverConfidenceDiskWithoutOneContainingIt() {
    val circles=listOf(circle(-40.0,0.0,65.0),circle(40.0,0.0,65.0))
    assertEquals("inside",NodeGeoEngine.classify(circles,observation(accuracy=50.0),now))
    // The largest contained disk has a narrow waist near y=51.2m.
    assertEquals("unknown",NodeGeoEngine.classify(circles,observation(accuracy=52.0),now))
  }
  @Test fun completePerimeterCoverageDoesNotHideAnInteriorGap() {
    val ring=(0 until 12).map{val angle=it*2*PI/12;circle(150*cos(angle),150*sin(angle),100.0)}+circle(-60.0,0.0,70.0)
    // Center and perimeter are covered, but a hole remains near (40m, 0m).
    assertEquals("inside",NodeGeoEngine.classify(ring,observation(accuracy=3.0),now))
    assertEquals("unknown",NodeGeoEngine.classify(ring,observation(accuracy=200.0),now))
    assertEquals("outside",NodeGeoEngine.classify(ring,observation(east=40.0,accuracy=3.0),now))
  }
  @Test fun tangentCirclesAndUnexaminedGapsNeverClaimCompleteCoverage() {
    val tangent=listOf(circle(-65.0,0.0,65.0),circle(65.0,0.0,65.0))
    assertEquals("unknown",NodeGeoEngine.classify(tangent,observation(accuracy=13.0),now))
    val separated=listOf(circle(-66.0,0.0,65.0),circle(66.0,0.0,65.0))
    assertEquals("unknown",NodeGeoEngine.classify(separated,observation(accuracy=13.0),now))
    assertEquals("unknown",NodeGeoEngine.classify(home,observation(east=52.0,accuracy=13.0),now))
  }
  @Test fun multiCircleCoverWorksAcrossDateLineAndNearPole() {
    for(latitude in listOf(0.0,89.999)) {
      val longitude=179.9999
      val centers=if(latitude==0.0)listOf(NodeGeoEngine.Circle(latitude,longitude-0.00036,65.0),NodeGeoEngine.Circle(latitude,longitude+0.00036-360.0,65.0))
      else listOf(NodeGeoEngine.Circle(latitude-0.00036,longitude,65.0),NodeGeoEngine.Circle(latitude+0.00036,longitude,65.0))
      assertEquals("lat=$latitude","inside",NodeGeoEngine.classify(centers,NodeGeoEngine.Observation(latitude,longitude,50.0,now),now))
    }
  }
}
