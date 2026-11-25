package com.aegis.node

/** Window policy only: hiding a window never expresses a processing cancellation. */
internal class HandsFreePresentationPolicy {
    data class Decision(val wakeScreen:Boolean,val keepScreenOn:Boolean)
    private var token:String?=null
    private var wakeAttempted=false
    private var screenSuppressed=false
    @Synchronized fun accepted(session:String) {
        if(token!=session){token=session;wakeAttempted=false;screenSuppressed=false}
    }
    @Synchronized fun begin(session:String,wakeScreen:Boolean):Decision {
        accepted(session)
        val wake=wakeScreen && !wakeAttempted && !screenSuppressed
        // One presentation attempt per activation, even when the platform denies waking.
        wakeAttempted=true
        return Decision(wake,!screenSuppressed)
    }
    @Synchronized fun screenOff(){if(token!=null)screenSuppressed=true}
}

/** One bounded temporary display value, with no history or command semantics. */
internal class HandsFreePartialDisplayLimiter(private val clock:()->Long={System.nanoTime()/1_000_000}) {
    private var lastAt:Long?=null
    private var lastText=""
    @Synchronized fun offer(value:String):String? {
        val text=value.take(MAX_CHARACTERS).replace('\n',' ').replace('\r',' ')
        val now=clock();val previous=lastAt
        if(text==lastText || (previous!=null && now-previous<MIN_UPDATE_MS))return null
        lastAt=now;lastText=text
        return text
    }
    @Synchronized fun clear(){lastText="";lastAt=null}
    companion object {const val MAX_CHARACTERS=240;const val MIN_UPDATE_MS=100L}
}
