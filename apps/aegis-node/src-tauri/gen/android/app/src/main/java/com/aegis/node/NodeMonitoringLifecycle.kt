package com.aegis.node

import java.util.UUID

/** Callers hold [lock] across session admission + startService or idle + stopSelfResult.
 * A stopped Android ServiceRecord may still deliver onDestroy after its replacement starts. */
internal class NodeMonitoringLifecycle(private val nextOwner:()->String={UUID.randomUUID().toString()}) {
 val lock=Any()
 private var current:String?=null
 private val stopping=mutableSetOf<String>()
 fun admit():String=current?.takeUnless{it in stopping}?:nextOwner().also{current=it}
 fun stopping(owner:String){stopping.add(owner)}
 fun isStopping(owner:String)=owner in stopping
 fun stopIfIdle(owner:String,hasActive:()->Boolean,stop:()->Boolean):Boolean {
  if(hasActive())return false
  return stop().also{if(it)stopping(owner)}
 }
 fun destroyed(owner:String) {
  stopping.remove(owner)
  if(current==owner)current=null
 }
}
