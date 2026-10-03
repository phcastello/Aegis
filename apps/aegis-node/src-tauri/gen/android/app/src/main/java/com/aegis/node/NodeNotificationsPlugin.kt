package com.aegis.node
import android.Manifest
import android.app.Activity
import android.os.Build
import app.tauri.PermissionState
import app.tauri.annotation.Command
import app.tauri.annotation.Permission
import app.tauri.annotation.PermissionCallback
import app.tauri.annotation.TauriPlugin
import app.tauri.plugin.Invoke
import app.tauri.plugin.Plugin
@TauriPlugin(permissions=[Permission(strings=[Manifest.permission.POST_NOTIFICATIONS],alias="notifications")])
class NodeNotificationsPlugin(private val activity:Activity):Plugin(activity) {
 @Command override fun requestPermissions(invoke:Invoke) {
  val prefs=NodeNativeNotifications.prefs(activity)
  if(Build.VERSION.SDK_INT<33 || getPermissionState("notifications")==PermissionState.GRANTED || prefs.getBoolean("asked",false)) { invoke.resolve(); return }
  prefs.edit().putBoolean("asked",true).commit()
  requestPermissionForAlias("notifications",invoke,"permissionCallback")
 }
 @PermissionCallback private fun permissionCallback(invoke:Invoke) {invoke.resolve()}
}
