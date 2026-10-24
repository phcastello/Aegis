package com.aegis.node

import android.app.Activity
import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInfo
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.core.content.FileProvider
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.TimeUnit
import java.util.concurrent.CountDownLatch

/** Only files created by this adapter are cleaned. Browser Downloads are never scanned. */
object NodeUpdates {
 private val executor=java.util.concurrent.Executors.newSingleThreadExecutor()
 fun background(action:()->Unit) { executor.execute(action) }
 private class Rejected(val safeCode:String):Exception()
 private fun requireUpdate(valid:Boolean,code:String) {if(!valid)throw Rejected(code)}
 private val name=Regex("aegis-update-[0-9]+\\.apk(?:\\.part)?")
 private fun directory(context:Context)=File(context.cacheDir,"aegis-updates").apply{mkdirs()}
 private fun flags():Int=if(Build.VERSION.SDK_INT>=28) PackageManager.GET_SIGNING_CERTIFICATES else PackageManager.GET_SIGNATURES
 private fun code(info:PackageInfo):Long=if(Build.VERSION.SDK_INT>=28)info.longVersionCode else info.versionCode.toLong()
 @Suppress("DEPRECATION") private fun signatures(info:PackageInfo):Set<String> = (if(Build.VERSION.SDK_INT>=28)info.signingInfo?.apkContentsSigners else info.signatures)?.map { NodeBackgroundWork.fingerprint(android.util.Base64.encodeToString(it.toByteArray(),android.util.Base64.NO_WRAP)) }?.toSet() ?: emptySet()
 fun cleanup(context:Context) {
  val installed=code(context.packageManager.getPackageInfo(context.packageName,0))
  directory(context).listFiles()?.filter{it.isFile&&name.matches(it.name)}?.forEach { file ->
   val target=file.name.removePrefix("aegis-update-").substringBefore(".").toLongOrNull()
   if(target!=null&&(installed>=target || System.currentTimeMillis()-file.lastModified()>TimeUnit.DAYS.toMillis(7))) file.delete()
  }
 }
 fun install(activity:Activity,version:String,url:String,versionCode:Long,sha256:String):JSONObject {
  var phase="verification"
  try {
  requireUpdate(NodeUpdateContract.supportedVersion(version),"update_version_mismatch")
  requireUpdate(url=="https://github.com/phcastello/Aegis/releases/download/node-v$version/Aegis-Android-arm64.apk","update_package_mismatch")
  requireUpdate(Regex("[0-9a-f]{64}").matches(sha256),"update_archive_invalid")
  cleanup(activity)
  val current=activity.packageManager.getPackageInfo(activity.packageName,flags())
  requireUpdate(versionCode in 1..2100000000L&&versionCode>code(current),"update_version_mismatch")
  val target=File(directory(activity),"aegis-update-$versionCode.apk")
  val partial=File(target.path+".part")
  if(target.exists())try{verify(activity,target,current,version,versionCode,sha256)}catch(_:Exception){check(target.delete())}
  if(!target.exists()) {
   partial.delete()
   try {
    phase="download"
    download(url,partial)
    phase="verification"
    verify(activity,partial,current,version,versionCode,sha256)
    check(partial.renameTo(target))
   }finally{partial.delete()}
  }
  verify(activity,target,current,version,versionCode,sha256)
  phase="install_permission"
  if(Build.VERSION.SDK_INT>=26&&!activity.packageManager.canRequestPackageInstalls()) {
   handoff(activity,Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,Uri.parse("package:${activity.packageName}")))
   return JSONObject().put("status","permission_required").put("message","Permita que a Aegis abra o instalador e toque em Atualizar novamente.")
  }
  val uri=FileProvider.getUriForFile(activity,"${activity.packageName}.updates",target)
  phase="installer_launch"
  val intent=Intent(Intent.ACTION_VIEW).setDataAndType(uri,"application/vnd.android.package-archive")
   .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
  intent.clipData=ClipData.newRawUri("Aegis update",uri)
  handoff(activity,intent)
  return JSONObject().put("status","installer_opened")
  }catch(failure:Exception) {
   val code=if(failure is Rejected)failure.safeCode else when(phase){
    "download"->"update_download_failed";"verification"->"update_archive_invalid"
    "install_permission"->"update_permission_failed";else->"update_installer_failed"
   }
   // Codes identify the failed phase without exposing URLs, payloads or exceptions.
   return JSONObject().put("status","failed").put("phase",phase).put("code",code)
  }
 }
 private fun handoff(activity:Activity,intent:Intent) {
  val finished=CountDownLatch(1)
  var accepted=false
  activity.runOnUiThread{try{activity.startActivity(intent);accepted=true}catch(_:Exception){}finally{finished.countDown()}}
  check(finished.await(10,TimeUnit.SECONDS) && accepted)
 }
 private fun verify(context:Context,file:File,current:PackageInfo,version:String,versionCode:Long,sha256:String) {
  requireUpdate(NodeUpdateContract.matchesHash(file,sha256),"update_archive_invalid")
  val archive=context.packageManager.getPackageArchiveInfo(file.absolutePath,flags()) ?: throw Rejected("update_archive_invalid")
  requireUpdate(archive.packageName==context.packageName,"update_package_mismatch")
  requireUpdate(archive.versionName==version&&code(archive)==versionCode,"update_version_mismatch")
  val previous=signatures(current);val next=signatures(archive)
  requireUpdate(previous.isNotEmpty()&&previous==next,"update_signature_mismatch")
 }
 private fun download(initial:String,target:File) {
  var url=URL(initial)
  repeat(6) {
   require(url.protocol=="https"&&url.host in setOf("github.com","release-assets.githubusercontent.com","objects.githubusercontent.com")&&url.userInfo==null)
   val connection=url.openConnection() as HttpURLConnection
   try {
    connection.connectTimeout=15000;connection.readTimeout=30000;connection.instanceFollowRedirects=false
    val status=connection.responseCode
    if(status in setOf(301,302,303,307,308)) {url=URL(url,connection.getHeaderField("Location")?:error("Missing redirect"));return@repeat}
    require(status==200)
    val limit=300L*1024*1024
    require(connection.contentLengthLong<=limit)
    connection.inputStream.use{input->target.outputStream().use{output->
     val buffer=ByteArray(65536);var total=0L
     while(true){val n=input.read(buffer);if(n<0)break;total+=n;require(total<=limit);output.write(buffer,0,n)}
     require(total>0);output.fd.sync()
    }}
    return
   }finally{connection.disconnect()}
  }
  error("Too many redirects")
 }
}
