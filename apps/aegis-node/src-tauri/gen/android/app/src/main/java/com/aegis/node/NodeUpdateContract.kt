package com.aegis.node

import java.io.File
import java.security.MessageDigest

/** Portable checks used by the installer before any system handoff. */
internal object NodeUpdateContract {
 private val version=Regex("(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)(?:-(?:unstable\\.[1-9][0-9]*(?:\\.rollback\\.[1-9][0-9]*)?|rollback\\.[1-9][0-9]*))?")
 fun supportedVersion(value:String):Boolean {
  if(!version.matches(value))return false
  val core=value.substringBefore('-').split('.').map{it.toIntOrNull()?:return false}
  if(core[0]>20||core[1]>99||core[2]>999)return false
  return value.substringAfter('-',"").split('.').filter{it.firstOrNull()?.isDigit()==true}.all{it.toIntOrNull() in 1..65535}
 }
 fun matchesHash(file:File,expected:String):Boolean {
  if(!Regex("[0-9a-f]{64}").matches(expected))return false
  val digest=MessageDigest.getInstance("SHA-256")
  file.inputStream().use{input->val buffer=ByteArray(65536);while(true){val n=input.read(buffer);if(n<0)break;digest.update(buffer,0,n)}}
  return digest.digest().joinToString(""){"%02x".format(it)}==expected
 }
}
