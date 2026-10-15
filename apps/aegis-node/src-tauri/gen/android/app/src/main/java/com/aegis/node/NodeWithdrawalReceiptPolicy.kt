package com.aegis.node

internal object NodeWithdrawalReceiptPolicy {
 const val MAX_ITEMS=100
 const val MAX_ATTEMPTS=6
 const val TTL_MILLIS=7L*24*60*60*1000
 fun retry(attempt:Int,pending:Boolean)=pending && attempt+1<MAX_ATTEMPTS
 fun received(requested:Set<Pair<String,Long>>,received:List<Pair<String,Long>>):Set<Pair<String,Long>> = received.toSet().intersect(requested)
}
