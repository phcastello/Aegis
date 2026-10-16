package com.aegis.node

import kotlin.math.*

/**
 * The saved circles are the place, not a proxy for Android's delivery accuracy.
 * Reported accuracy is a confidence estimate, never a guaranteed physical bound.
 * A confidence disk must fit a saved circle to establish presence; ambiguous
 * readings never erase an established presence or create a transition.
 */
object NodeGeoEngine {
  const val MAX_AGE_MILLIS = 120000L
  const val CONFIRMATION_MILLIS = 30000L
  data class Circle(val latitude: Double,val longitude: Double,val radiusMeters: Double)
  data class Observation(val latitude: Double,val longitude: Double,val accuracyMeters: Double,val capturedAt: Long)
  data class Assessment(val classification:String,val reasonCode:String)
  data class State(val confirmed: String="unknown",val candidate: String="unknown",val candidateAt: Long=0,val lastCapturedAt: Long=0)
  data class Decision(val state: State,val transition: String?=null,val needsConfirmation: Boolean=false)
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
    // A small fixed boundary floor provides hysteresis even for implausibly
    // optimistic providers. Unlike the previous 30 + accuracy erosion, this
    // leaves a usable 52 m interior in a 65 m circle with a 13 m observation.
    val confidence=max(3.0,o.accuracyMeters)
    val distances=circles.map{distance(o.latitude,o.longitude,it.latitude,it.longitude)}
    if(circles.indices.any{distances[it]+confidence<circles[it].radiusMeters})return Assessment("inside","confirmed")
    if(circles.indices.all{distances[it]-confidence>circles[it].radiusMeters})return Assessment("outside","confirmed")
    val reason=if(circles.all{it.radiusMeters<=confidence})"accuracy_insufficient" else "near_boundary"
    return Assessment("unknown",reason)
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
