package com.aegis.node

import java.util.concurrent.atomic.AtomicBoolean

/** Snapshot of the exact native lease: completion never depends on a WebView callback. */
internal class ManualPlaybackLease(
    private val id:String,
    private val matches:(String)->Boolean,
    private val release:(String)->Unit
) : AutoCloseable {
    private val closed=AtomicBoolean(false)
    fun isCurrent():Boolean=!closed.get() && matches(id)
    override fun close(){if(closed.compareAndSet(false,true))release(id)}
}
