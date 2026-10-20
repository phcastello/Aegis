package com.aegis.node

/** Statistical-free bounded delivery policy; 401 waits for the existing Node identity. */
internal object NodeReminderActionPolicy {
  fun delayMillis(attempt:Int):Long=(30000L shl (attempt.coerceIn(1,11)-1)).coerceAtMost(6*60*60*1000L)
  fun httpResult(status:Int):String=when(status) {
    400->"rejected_invalid";403->"rejected_forbidden";404->"rejected_missing";409->"rejected_conflict";410->"rejected_expired";401->"credential_unavailable";else->"temporary_http"
  }
  fun permanent(result:String):Boolean=result.startsWith("rejected_")
  fun retryWorker(attempt:Int,pending:Boolean):Boolean=pending && attempt<8
}
