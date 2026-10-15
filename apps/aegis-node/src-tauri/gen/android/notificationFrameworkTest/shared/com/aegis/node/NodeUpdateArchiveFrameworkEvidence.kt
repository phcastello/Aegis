package com.aegis.node

import android.content.Context
import android.content.pm.PackageInfo
import android.content.pm.PackageManager
import android.os.Build
import org.junit.Assert.*
import java.io.File
import java.security.MessageDigest
import java.util.UUID

/** PackageManager inspects real signed bytes in both updater cache filename forms. */
@Suppress("DEPRECATION")
internal fun exerciseUpdateArchiveFramework(context:Context) {
 val flags=if(Build.VERSION.SDK_INT>=28)PackageManager.GET_SIGNING_CERTIFICATES else PackageManager.GET_SIGNATURES
 fun signers(info:PackageInfo):Set<String> {
  val values=if(Build.VERSION.SDK_INT>=28)info.signingInfo?.apkContentsSigners else info.signatures
  assertFalse(values.isNullOrEmpty())
  return values!!.map{signature->MessageDigest.getInstance("SHA-256").digest(signature.toByteArray()).joinToString(""){"%02x".format(it)}}.toSet()
 }
 fun code(info:PackageInfo)=if(Build.VERSION.SDK_INT>=28)info.longVersionCode else info.versionCode.toLong()
 val installed=context.packageManager.getPackageInfo(context.packageName,flags)
 val expected=signers(installed)
 val token=UUID.randomUUID().toString()
 for(suffix in listOf(".apk.part",".apk")) {
  val copy=File(context.cacheDir,"aegis-archive-inspection-$token$suffix")
  try {
   File(context.applicationInfo.sourceDir).inputStream().use{input->copy.outputStream().use{output->input.copyTo(output)}}
   val archive=context.packageManager.getPackageArchiveInfo(copy.path,flags)
   assertNotNull("The real installed APK must remain inspectable in $suffix cache",archive)
   assertEquals(context.packageName,archive!!.packageName)
   assertEquals(installed.versionName,archive.versionName);assertEquals(code(installed),code(archive))
   assertTrue(expected==signers(archive))
  }finally{assertTrue(!copy.exists()||copy.delete())}
 }
 android.util.Log.i("AegisNodeNativeTest","phase=update_archive_source_inspection errorType=Checkpoint")
}
