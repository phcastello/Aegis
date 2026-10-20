package com.aegis.node

/** Remote receipts have a separate lifetime from an installed native registration. */
internal object NodeGeofenceReportPolicy {
 const val MAX_ATTEMPTS=6
 fun retry(attempt:Int,now:Long,leaseUntil:Long,pending:Boolean):Boolean =
  pending && attempt+1<MAX_ATTEMPTS && now<leaseUntil

 fun acknowledges(currentEpoch:Long,sentEpoch:Long,currentRevision:String?,sentRevision:String,currentScope:String?,sentScope:String):Boolean =
  currentEpoch==sentEpoch && currentRevision==sentRevision && currentScope==sentScope
}
