package com.aegis.node
import android.Manifest
import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import app.tauri.PermissionState
import app.tauri.annotation.Command
import app.tauri.annotation.Permission
import app.tauri.annotation.PermissionCallback
import app.tauri.annotation.TauriPlugin
import app.tauri.plugin.Invoke
import app.tauri.plugin.Plugin
import app.tauri.plugin.JSObject

@TauriPlugin(permissions=[Permission(strings=[Manifest.permission.POST_NOTIFICATIONS],alias="notifications"),Permission(strings=[Manifest.permission.ACCESS_COARSE_LOCATION,Manifest.permission.ACCESS_FINE_LOCATION],alias="location"),Permission(strings=[Manifest.permission.ACCESS_COARSE_LOCATION],alias="approximateLocation"),Permission(strings=[Manifest.permission.ACCESS_BACKGROUND_LOCATION],alias="backgroundLocation")])
class NodeNotificationsPlugin(private val activity:Activity):Plugin(activity) {
 @Command override fun requestPermissions(invoke:Invoke) {
  val prefs=NodeNativeNotifications.prefs(activity)
  if(Build.VERSION.SDK_INT<33 || getPermissionState("notifications")==PermissionState.GRANTED || prefs.getBoolean("asked",false)) { invoke.resolve(); return }
  prefs.edit().putBoolean("asked",true).commit()
  requestPermissionForAlias("notifications",invoke,"permissionCallback")
 }
 @PermissionCallback private fun permissionCallback(invoke:Invoke) {invoke.resolve()}
 @Command fun locationPermissions(invoke:Invoke) { invoke.resolve(JSObject(NodeGeofenceRuntime.permissions(activity))) }
 @Command fun requestLocationPermission(invoke:Invoke) {
  val args=org.json.JSONObject(invoke.getArgs().toString())
  val background=args.optBoolean("background",false)
  if(background) {
   if(!NodeLocation.permissions(activity).getBoolean("foreground")) { invoke.reject("Permita localização em uso antes de habilitar lembretes por lugar.");return }
   if(Build.VERSION.SDK_INT>=30) activity.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,Uri.parse("package:${activity.packageName}")))
   else if(Build.VERSION.SDK_INT==29) {requestPermissionForAlias("backgroundLocation",invoke,"locationPermissionCallback");return}
   invoke.resolve(JSObject(NodeLocation.permissions(activity).toString()));return
  }
  requestPermissionForAlias(if(args.optBoolean("precise",false)) "location" else "approximateLocation",invoke,"locationPermissionCallback")
 }
 @PermissionCallback private fun locationPermissionCallback(invoke:Invoke) {invoke.resolve(JSObject(NodeLocation.permissions(activity).toString()))}
 @Command fun currentLocation(invoke:Invoke) {
  NodeGeofences.background { try{invoke.resolve(JSObject(NodeLocation.current(activity).toString()))}catch(_:Exception){invoke.reject("Não foi possível obter uma localização recente.")} }
 }
 @Command fun syncGeofences(invoke:Invoke) {
  NodeGeofences.background {try{invoke.resolve(JSObject(NodeGeofences.sync(activity).toString()))}catch(_:Exception){invoke.reject("Sincronização pendente. Verifique a conexão e as permissões.")} }
 }
 @Command fun geofenceDiagnostics(invoke:Invoke) {
  NodeGeofences.background {try{invoke.resolve(JSObject(NodeGeofenceRuntime.diagnostics(activity)))}catch(_:Exception){invoke.reject("Não foi possível consultar a presença neste celular.")} }
 }
 @Command fun verifyGeofencePresence(invoke:Invoke) {
  NodeGeofences.background {try{invoke.resolve(JSObject(NodeGeofenceRuntime.verifyPresence(activity)))}catch(_:Exception){invoke.reject("Localização indisponível. Tente novamente em instantes.")} }
 }
 @Command fun queueReminderAction(invoke:Invoke) {
  try{val args=invoke.getArgs();invoke.resolve(JSObject(NodeReminderActions.enqueueUi(activity,args.getString("reminderId"),args.getJSONObject("payload"),args.getString("scopeNodeId")).toString()))}catch(_:Exception){invoke.reject("Não foi possível guardar esta ação no aparelho.")}
 }
 @Command fun installUpdate(invoke:Invoke) {
  val args=org.json.JSONObject(invoke.getArgs().toString())
  NodeUpdates.background {try{invoke.resolve(JSObject(NodeUpdates.install(activity,args.getString("version"),args.getString("url"),args.getLong("versionCode")).toString()))}catch(_:Exception){invoke.reject("update_installer_failed")} }
 }
}
