package com.aegis.node
import android.Manifest
import android.app.Activity
import android.os.Build
import app.tauri.PermissionState
import app.tauri.annotation.Command
import app.tauri.annotation.InvokeArg
import app.tauri.annotation.Permission
import app.tauri.annotation.PermissionCallback
import app.tauri.annotation.TauriPlugin
import app.tauri.plugin.Invoke
import app.tauri.plugin.JSObject
import app.tauri.plugin.Plugin
import com.google.firebase.FirebaseApp
@InvokeArg class NotificationInput { lateinit var title:String; lateinit var body:String }
@InvokeArg class ShowArgs { lateinit var commandId:String; lateinit var expiresAt:String; lateinit var capability:String; var capabilityVersion:Int=0; lateinit var input:NotificationInput }
@InvokeArg class BindArgs { var nodeId:String?=null }
@TauriPlugin(permissions=[Permission(strings=[Manifest.permission.POST_NOTIFICATIONS],alias="notifications")])
class NodeNotificationsPlugin(private val activity:Activity):Plugin(activity) {
 @Command fun state(invoke:Invoke) {
  NodeNativeNotifications.initialize(activity)
  invoke.resolve(JSObject().put("configured",FirebaseApp.getApps(activity).isNotEmpty()).put("granted",NodeNativeNotifications.granted(activity)).put("token",NodeNativeNotifications.prefs(activity).getString("token",null)))
 }
 @Command fun bind(invoke:Invoke) { val args=invoke.parseArgs(BindArgs::class.java);NodeNativeNotifications.prefs(activity).edit().putString("nodeId",args.nodeId).commit();invoke.resolve() }
 @Command fun show(invoke:Invoke) {
  val args=invoke.parseArgs(ShowArgs::class.java)
  val result=if(args.capability!="notification.show" || args.capabilityVersion!=1) "unsupported" else NodeNativeNotifications.show(activity,args.commandId,args.expiresAt,args.input.title,args.input.body)
  invoke.resolve(JSObject().put("status",result))
 }
 @Command override fun requestPermissions(invoke:Invoke) {
  val prefs=NodeNativeNotifications.prefs(activity)
  if(Build.VERSION.SDK_INT<33 || getPermissionState("notifications")==PermissionState.GRANTED || prefs.getBoolean("asked",false)) { invoke.resolve(); return }
  prefs.edit().putBoolean("asked",true).commit()
  requestPermissionForAlias("notifications",invoke,"permissionCallback")
 }
 @PermissionCallback private fun permissionCallback(invoke:Invoke) {invoke.resolve()}
}
