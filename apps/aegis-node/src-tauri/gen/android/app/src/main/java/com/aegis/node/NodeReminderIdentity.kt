package com.aegis.node

/** Include accepted posts that NotificationManager has not made visible yet. */
internal fun identityReminderTags(persisted:List<Pair<String,Long>>,active:List<Pair<String,Int>>):Set<Pair<String,Int>> =
 persisted.map { (id,generation)->"reminder:$id:$generation" to 1 }.toSet()+active.filter { (tag,_)->tag.startsWith("reminder:") }

internal fun supersededReminderTag(id:String,previous:Long,next:Long):Pair<String,Int>? =
 if(next>previous)"reminder:$id:$previous" to 1 else null

internal data class NativeIdentityAuthority(val nodeId:String?,val origin:String?,val active:Boolean)
internal fun identityBindingAllowed(authority:NativeIdentityAuthority,targetId:String?,targetOrigin:String?=null):Boolean =
 if(targetId==null)!authority.active&&(targetOrigin==null||authority.origin==null||authority.origin==targetOrigin) else authority.active&&identityScopeMatches(authority,targetId,targetOrigin)
internal fun identityScopeMatches(authority:NativeIdentityAuthority,targetId:String,targetOrigin:String?=null):Boolean =
 authority.nodeId==targetId&&(targetOrigin==null||authority.origin==targetOrigin)

/** Call under the store's existing gate; a captured identity is not current authority. */
internal inline fun applyForCurrentIdentity(current:()->Boolean,action:()->Unit):Boolean {
 if(!current())return false
 action();return true
}
