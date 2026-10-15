package com.aegis.node

import kotlin.math.*

/** Deterministic union/hysteresis machine. Accuracy is a confidence heuristic, never a physical bound. */
object NodeGeoEngine {
  data class Circle(val latitude: Double,val longitude: Double,val radiusMeters: Double)
  data class Observation(val latitude: Double,val longitude: Double,val accuracyMeters: Double,val capturedAt: Long)
  data class State(val confirmed: String="unknown",val candidate: String="unknown",val candidateAt: Long=0,val lastCapturedAt: Long=0)
  data class Decision(val state: State,val transition: String?=null,val needsConfirmation: Boolean=false)
  fun distance(a: Double,b: Double,c: Double,d: Double): Double {
    val p=Math.toRadians(c-a); val q=Math.toRadians(d-b)
    val h=sin(p/2).pow(2)+cos(Math.toRadians(a))*cos(Math.toRadians(c))*sin(q/2).pow(2)
    return 6371000.0*2*atan2(sqrt(h.coerceIn(0.0,1.0)),sqrt((1-h).coerceIn(0.0,1.0)))
  }
  fun classify(circles: List<Circle>,o: Observation,now: Long): String {
    if(circles.isEmpty() || now-o.capturedAt !in 0..120000 || !o.accuracyMeters.isFinite() || o.accuracyMeters <= 0.0 || o.accuracyMeters > 200.0) return "unknown"
    val margin=30.0+min(o.accuracyMeters,100.0)
    if(circles.any { distance(o.latitude,o.longitude,it.latitude,it.longitude) <= max(0.0,it.radiusMeters-margin) }) return "inside"
    if(circles.all { distance(o.latitude,o.longitude,it.latitude,it.longitude) >= it.radiusMeters+margin }) return "outside"
    return "unknown"
  }
  fun observe(previous: State,classification: String,capturedAt: Long): Decision {
    if(capturedAt<=previous.lastCapturedAt) return Decision(previous)
    if(classification=="unknown") return Decision(previous.copy(candidate="unknown",candidateAt=0,lastCapturedAt=capturedAt),needsConfirmation=true)
    if(previous.confirmed=="unknown" && classification!="unknown") return Decision(State(classification,"unknown",0,capturedAt))
    if(classification==previous.confirmed) return Decision(previous.copy(candidate="unknown",candidateAt=0,lastCapturedAt=capturedAt))
    if(classification!=previous.candidate) return Decision(previous.copy(candidate=classification,candidateAt=capturedAt,lastCapturedAt=capturedAt),needsConfirmation=true)
    if(capturedAt-previous.candidateAt<30000) return Decision(previous.copy(lastCapturedAt=capturedAt),needsConfirmation=true)
    val transition=if(previous.confirmed=="unknown") null else if(classification=="inside") "enter" else "exit"
    return Decision(State(classification,"unknown",0,capturedAt),transition)
  }
}
