package com.aegis.node

import kotlin.math.*

/**
 * The saved circles are the place, not a proxy for Android's delivery accuracy.
 * Reported accuracy is a confidence estimate, never a guaranteed physical bound.
 * A confidence disk must fit the complete union to establish presence; ambiguous
 * readings never erase an established presence or create a transition.
 */
object NodeGeoEngine {
  const val MAX_AGE_MILLIS = 120000L
  const val CONFIRMATION_MILLIS = 30000L
  // GMS can batch delivery while idle. Its optional triggering fix dates the
  // movement, whereas a point acquisition still has the shorter GPS lifetime.
  const val NATIVE_MAX_DELAY_MILLIS = 600000L
  const val NATIVE_BASELINE_WITHOUT_LOCATION_MILLIS = 1800000L
  const val NATIVE_CONFIRMATION_WINDOW_MILLIS = 120000L
  private const val EARTH_METERS = 6371000.0
  private const val GEOMETRY_MARGIN_METERS = 0.05
  private const val MAX_CIRCLES = 100
  data class Circle(val latitude: Double,val longitude: Double,val radiusMeters: Double)
  data class Observation(val latitude: Double,val longitude: Double,val accuracyMeters: Double,val capturedAt: Long)
  data class Assessment(val classification:String,val reasonCode:String)
  data class State(val confirmed: String="unknown",val candidate: String="unknown",val candidateAt: Long=0,val lastCapturedAt: Long=0)
  data class Decision(val state: State,val transition: String?=null,val needsConfirmation: Boolean=false)
  /** Internal evidence only: region/identity/revision validation belongs to the adapter. */
  data class NativeEvidence(val transition:String,val detectedAt:Long?,val receivedAt:Long,val initial:Boolean=false,val observation:Observation?=null,val configurationId:String?=null,val configurationRevision:String?=null,val observerNodeId:String?=null,val source:String="android_event")
  data class NativeContext(
    val installedAt:Long,
    val baselineReliable:Boolean,
    val unionPresence:String="unknown",
    val recentReliablePresence:String="unknown",
    val recentReliableAt:Long=0,
    val baselineAt:Long=0
  )
  data class NativeDecision(val state:State,val transition:String?=null,val needsConfirmation:Boolean=false,val reasonCode:String,val detectedAt:Long)
  fun canConfirmAgain(attempt:Int,now:Long,deadline:Long,hasCandidate:Boolean):Boolean = attempt<2 && hasCandidate && deadline-now>CONFIRMATION_MILLIS
  fun distance(a: Double,b: Double,c: Double,d: Double): Double {
    val p=Math.toRadians(c-a); val q=Math.toRadians(d-b)
    val h=sin(p/2).pow(2)+cos(Math.toRadians(a))*cos(Math.toRadians(c))*sin(q/2).pow(2)
    return 6371000.0*2*atan2(sqrt(h.coerceIn(0.0,1.0)),sqrt((1-h).coerceIn(0.0,1.0)))
  }
  fun assess(circles:List<Circle>,o:Observation,now:Long):Assessment {
    if(circles.isEmpty())return Assessment("unknown","no_geometry")
    if(now<o.capturedAt)return Assessment("unknown","future_location")
    if(now-o.capturedAt>MAX_AGE_MILLIS)return Assessment("unknown","stale_location")
    if(!o.latitude.isFinite() || o.latitude !in -90.0..90.0 || !o.longitude.isFinite() || o.longitude !in -180.0..180.0 || !o.accuracyMeters.isFinite() || o.accuracyMeters<=0.0 || o.accuracyMeters>200.0)return Assessment("unknown","accuracy_insufficient")
    if(circles.size>MAX_CIRCLES || circles.any{!it.latitude.isFinite() || it.latitude !in -90.0..90.0 || !it.longitude.isFinite() || it.longitude !in -180.0..180.0 || !it.radiusMeters.isFinite() || it.radiusMeters<=0.0 || it.radiusMeters>100000.0})return Assessment("unknown","invalid_geometry")
    // The floor guards implausibly optimistic providers; neither the saved
    // geometry nor the OS monitoring region is enlarged.
    val confidence=max(3.0,o.accuracyMeters)
    val distances=circles.map{distance(o.latitude,o.longitude,it.latitude,it.longitude)}
    if(circles.indices.any{distances[it]+confidence+GEOMETRY_MARGIN_METERS<circles[it].radiusMeters} || coveredByUnion(circles,o,confidence))return Assessment("inside","confirmed")
    if(circles.indices.all{distances[it]-confidence>circles[it].radiusMeters+GEOMETRY_MARGIN_METERS})return Assessment("outside","confirmed")
    val reason=if(circles.all{it.radiusMeters<=confidence})"accuracy_insufficient" else "near_boundary"
    return Assessment("unknown",reason)
  }
  private data class Vector(val x:Double,val y:Double,val z:Double) {
    operator fun plus(b:Vector)=Vector(x+b.x,y+b.y,z+b.z)
    operator fun minus(b:Vector)=Vector(x-b.x,y-b.y,z-b.z)
    operator fun times(k:Double)=Vector(x*k,y*k,z*k)
    fun dot(b:Vector)=x*b.x+y*b.y+z*b.z
    fun cross(b:Vector)=Vector(y*b.z-z*b.y,z*b.x-x*b.z,x*b.y-y*b.x)
    fun length()=sqrt(dot(this))
    fun unit()=this*(1.0/length())
    fun separation(b:Vector)=atan2(cross(b).length(),dot(b))
  }
  private fun vector(latitude:Double,longitude:Double):Vector {
    val lat=Math.toRadians(latitude);val lon=Math.toRadians(longitude)
    return Vector(cos(lat)*cos(lon),cos(lat)*sin(lon),sin(lat))
  }
  private class Cap(val center:Vector,val radius:Double) {
    private val reference=if(abs(center.z)<0.9)Vector(0.0,0.0,1.0) else Vector(1.0,0.0,0.0)
    val u=reference.cross(center).unit()
    val v=center.cross(u)
    fun point(angle:Double)=center*cos(radius)+(u*cos(angle)+v*sin(angle))*sin(radius)
    fun contains(point:Vector)=center.separation(point)<radius-1e-12
    fun cuts(other:Cap):List<Double> {
      val separation=center.separation(other.center)
      if(separation<1e-12)return emptyList()
      // Stable form of cos(other.radius)-cos(radius)*cos(separation),
      // avoiding subtraction of almost equal unit values at residential scale.
      val numerator= -2*sin((other.radius+radius)/2)*sin((other.radius-radius)/2)+cos(radius)*2*sin(separation/2).pow(2)
      val ratio=numerator/(sin(radius)*sin(separation))
      if(!ratio.isFinite() || ratio < -1.0-1e-10 || ratio > 1.0+1e-10)return emptyList()
      val middle=atan2(other.center.dot(v),other.center.dot(u))
      val delta=acos(ratio.coerceIn(-1.0,1.0))
      return listOf(normalize(middle-delta),normalize(middle+delta))
    }
    fun witnesses(others:List<Cap>):List<Vector> {
      val angles=(listOf(0.0)+others.flatMap{cuts(it)}).sorted()
      val points=ArrayList<Vector>(angles.size*2)
      for(i in angles.indices) {
        val start=angles[i];val end=if(i+1<angles.size)angles[i+1] else angles[0]+2*PI
        points.add(point(start));points.add(point((start+end)/2))
      }
      return points
    }
  }
  private fun normalize(angle:Double)=((angle%(2*PI))+2*PI)%(2*PI)
  /**
   * Exact spherical-circle arrangement, with a 5 cm conservative numeric margin.
   * Every boundary arc is split at ALL circle intersections: membership cannot
   * change between these vertices. Checking its midpoint is therefore exhaustive,
   * not spatial sampling. A covered perimeter alone misses interior holes; every
   * exposed union boundary inside the confidence disk is checked as well.
   * If the perimeter and center are covered and no such internal boundary exists,
   * there is no uncovered component. Bounded at 100 circles, O(n^3), no GIS library.
   */
  private fun coveredByUnion(circles:List<Circle>,o:Observation,confidence:Double):Boolean {
    val target=Cap(vector(o.latitude,o.longitude),confidence/EARTH_METERS)
    val caps=circles.map{Cap(vector(it.latitude,it.longitude),(it.radiusMeters-GEOMETRY_MARGIN_METERS).coerceAtLeast(0.0)/EARTH_METERS)}
    if(caps.none{it.contains(target.center)})return false
    if(target.witnesses(caps).any{p->caps.none{it.contains(p)}})return false
    for((i,cap)in caps.withIndex()) {
      val others=caps.filterIndexed{j,_->j!=i}
      if(cap.witnesses(others+target).any{p->target.contains(p) && others.none{it.contains(p)}})return false
    }
    return true
  }

  /** Native transition evidence does not inherit the two-GPS-capture rule. */
  fun native(previous:State,evidence:NativeEvidence,context:NativeContext,circles:List<Circle>,now:Long):NativeDecision {
    val at=evidence.detectedAt?:evidence.receivedAt
    fun unchanged(code:String,confirm:Boolean=false,clearCandidate:Boolean=false)=NativeDecision(
      if(confirm)previous.copy(candidate=if(evidence.transition=="enter")"inside" else "outside",candidateAt=evidence.receivedAt) else if(clearCandidate)previous.copy(candidate="unknown",candidateAt=0) else previous,
      needsConfirmation=confirm,reasonCode=code,detectedAt=at)
    if(evidence.transition!="enter" && evidence.transition!="exit")return unchanged("native_transition_unknown")
    if(evidence.receivedAt>now || at>now || at>evidence.receivedAt)return unchanged("native_event_future")
    if(evidence.receivedAt<context.installedAt || at<context.installedAt)return unchanged("native_event_replaced")
    if(now-at>NATIVE_MAX_DELAY_MILLIS || now-evidence.receivedAt>NATIVE_MAX_DELAY_MILLIS)return unchanged("native_event_stale")
    val baselineAt=if(context.baselineAt>0)context.baselineAt else previous.lastCapturedAt
    // Newer uncertain fixes/candidates must not erase an older valid native
    // movement. Order against confirmed presence, not every acquisition.
    if(evidence.detectedAt!=null && at<=baselineAt)return unchanged(if(at==baselineAt)"native_event_duplicate" else "native_event_out_of_order")
    val expected=if(evidence.transition=="enter")"inside" else "outside"
    val associated=evidence.observation?.let {
      if(it.capturedAt<context.installedAt || it.capturedAt>now || now-it.capturedAt>NATIVE_MAX_DELAY_MILLIS)Assessment("unknown","stale_location")
      else assess(circles,it,it.capturedAt)
    }?:Assessment("unknown","location_absent")
    val established=if(associated.classification!="unknown")associated.classification else context.unionPresence
    if(evidence.initial || previous.confirmed=="unknown") {
      // Initial/restore callbacks can establish presence, never movement. An
      // ambiguous EXIT with only some component regions outside is insufficient.
      val baseline=if(established in setOf("inside","outside"))State(established,"unknown",0,maxOf(previous.lastCapturedAt,at)) else previous
      return NativeDecision(baseline,reasonCode="native_event_initial",detectedAt=at)
    }
    val opposite=if(expected=="inside")"outside" else "inside"
    // A later reliable opposite fix proves this timestamped movement was
    // superseded. That ordering proof does not expire while delivery is delayed.
    if(evidence.detectedAt!=null && context.recentReliablePresence==opposite && context.recentReliableAt>at)return unchanged("native_event_out_of_order")
    val recentConflict=context.recentReliablePresence==opposite && context.recentReliableAt>=
      (evidence.detectedAt?:evidence.receivedAt-30000L) && now-context.recentReliableAt in 0..MAX_AGE_MILLIS
    // Leaving one circle while another still contains us is not leaving the place,
    // even when its associated fix reliably confirms presence in the other circle.
    if(expected=="outside" && context.unionPresence=="inside" && associated.classification!="outside")return unchanged("native_union_still_inside",clearCandidate=true)
    if(associated.classification==opposite || recentConflict)return unchanged("transition_contradictory_location",true)
    if(previous.confirmed==expected)return unchanged("native_no_place_transition",clearCandidate=true)
    if(!context.baselineReliable)return unchanged("native_baseline_unreliable",true)
    val locationSupports=associated.classification==expected
    val membershipSupports=context.unionPresence==expected
    val baselineFreshAt=maxOf(baselineAt,context.recentReliableAt.takeIf{context.recentReliablePresence==previous.confirmed}?:0)
    if(!locationSupports && (!membershipSupports || baselineAt<context.installedAt || now-baselineFreshAt !in 0..NATIVE_BASELINE_WITHOUT_LOCATION_MILLIS))return unchanged("transition_needs_confirmation",true)
    return NativeDecision(State(expected,"unknown",0,maxOf(previous.lastCapturedAt,at)),evidence.transition,reasonCode="native_${evidence.transition}_accepted",detectedAt=at)
  }

  /** Exactly one bounded point confirmation; it need not run at a 30 s boundary. */
  fun confirmNative(previous:State,evidence:NativeEvidence,context:NativeContext,circles:List<Circle>,observation:Observation,now:Long):NativeDecision {
    val at=evidence.detectedAt?:evidence.receivedAt
    fun preserve(code:String)=NativeDecision(previous.copy(candidate="unknown",candidateAt=0),reasonCode=code,detectedAt=at)
    if(evidence.transition !in setOf("enter","exit"))return preserve("native_transition_unknown")
    if(now-evidence.receivedAt !in 0..NATIVE_CONFIRMATION_WINDOW_MILLIS || now-at>NATIVE_MAX_DELAY_MILLIS)return preserve("transition_expired")
    if(at<context.installedAt || observation.capturedAt<context.installedAt || observation.capturedAt<evidence.receivedAt || observation.capturedAt<previous.lastCapturedAt)return preserve("transition_confirmation_old")
    val assessment=assess(circles,observation,now)
    val expected=if(evidence.transition=="enter")"inside" else "outside"
    if(assessment.classification=="unknown")return preserve("transition_confirmation_insufficient")
    if(assessment.classification!=expected)return preserve("transition_confirmation_contradicted")
    if(evidence.initial || previous.confirmed=="unknown")return NativeDecision(State(expected,"unknown",0,observation.capturedAt),reasonCode="native_event_initial",detectedAt=at)
    if(previous.confirmed==expected)return preserve("native_no_place_transition")
    if(!context.baselineReliable)return preserve("native_baseline_unreliable")
    return NativeDecision(State(expected,"unknown",0,observation.capturedAt),evidence.transition,reasonCode="transition_accepted",detectedAt=at)
  }
  fun classify(circles:List<Circle>,o:Observation,now:Long):String=assess(circles,o,now).classification
  fun observe(previous: State,classification: String,capturedAt: Long,notBefore:Long=0): Decision {
    if(capturedAt<notBefore || capturedAt<=previous.lastCapturedAt)return Decision(previous)
    if(classification=="unknown")return Decision(previous.copy(candidate="unknown",candidateAt=0,lastCapturedAt=capturedAt),needsConfirmation=true)
    if(previous.confirmed=="unknown")return Decision(State(classification,"unknown",0,capturedAt))
    if(classification==previous.confirmed)return Decision(previous.copy(candidate="unknown",candidateAt=0,lastCapturedAt=capturedAt))
    // Two captures must describe the same short-lived movement. A candidate
    // from hours ago cannot establish temporal persistence today.
    if(classification!=previous.candidate || capturedAt-previous.candidateAt>MAX_AGE_MILLIS)return Decision(previous.copy(candidate=classification,candidateAt=capturedAt,lastCapturedAt=capturedAt),needsConfirmation=true)
    if(capturedAt-previous.candidateAt<CONFIRMATION_MILLIS)return Decision(previous.copy(lastCapturedAt=capturedAt),needsConfirmation=true)
    val transition=if(classification=="inside")"enter" else "exit"
    return Decision(State(classification,"unknown",0,capturedAt),transition)
  }
}
