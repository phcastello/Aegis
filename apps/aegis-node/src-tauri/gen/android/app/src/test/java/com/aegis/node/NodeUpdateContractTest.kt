package com.aegis.node

import java.io.File
import org.junit.Assert.*
import org.junit.Test

class NodeUpdateContractTest {
 @Test fun recoveryVersionsUseTheSameInstallerAsNormalDistributions() {
  for(value in listOf("0.7.2","0.7.2-unstable.4","0.7.2-unstable.5.rollback.1","0.7.2-unstable.5.rollback.2","0.7.3-rollback.1"))assertTrue(value,NodeUpdateContract.supportedVersion(value))
  for(value in listOf("0.7.2.rollback.1","0.7.2+rollback.1","0.7.2-unstable.05","0.7.2-unstable.5.rollback.0","0.7.2-unstable.5.rollback.1.rollback.2","0.7.2-beta.1","0.7.2/other"))assertFalse(value,NodeUpdateContract.supportedVersion(value))
 }
 @Test fun theVerifiedInstallerMustMatchThePublishedBytesIncludingCachedCopies() {
  val file=File.createTempFile("aegis-update-contract-",".apk.part")
  try {
   file.writeText("abc")
   val digest="ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
   assertTrue(NodeUpdateContract.matchesHash(file,digest))
   assertFalse(NodeUpdateContract.matchesHash(file,"0".repeat(64)))
   assertFalse(NodeUpdateContract.matchesHash(file,digest.uppercase()))
   file.appendText("tampered")
   assertFalse(NodeUpdateContract.matchesHash(file,digest))
  }finally{file.delete()}
 }
}
