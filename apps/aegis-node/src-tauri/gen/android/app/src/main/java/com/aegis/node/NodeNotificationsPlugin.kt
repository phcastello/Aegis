package com.aegis.node
import android.Manifest
import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import app.tauri.PermissionState
import app.tauri.annotation.Command
import app.tauri.annotation.ActivityCallback
import androidx.activity.result.ActivityResult
import app.tauri.annotation.Permission
import app.tauri.annotation.PermissionCallback
import app.tauri.annotation.TauriPlugin
import app.tauri.plugin.Invoke
import app.tauri.plugin.Plugin
import app.tauri.plugin.JSObject

@TauriPlugin(permissions=[Permission(strings=[Manifest.permission.RECORD_AUDIO],alias="handsFreeAudio"),Permission(strings=[Manifest.permission.POST_NOTIFICATIONS],alias="notifications"),Permission(strings=[Manifest.permission.ACCESS_COARSE_LOCATION,Manifest.permission.ACCESS_FINE_LOCATION],alias="location"),Permission(strings=[Manifest.permission.ACCESS_COARSE_LOCATION],alias="approximateLocation"),Permission(strings=[Manifest.permission.ACCESS_BACKGROUND_LOCATION],alias="backgroundLocation")])
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
 @Command fun applyGeofenceCancellation(invoke:Invoke) {
  val args=org.json.JSONObject(invoke.getArgs().toString())
  NodeGeofences.background {
   var confirmed=false
   try {
    val scope=args.getString("scopeNodeId")
    val id=args.getString("ruleId");require(java.util.UUID.fromString(id).toString()==id && args.getLong("ruleRevision")>0)
    require(NodeNativeSession.read(activity)?.nodeId==scope)
    val state=NodeGeofences.sync(activity,flushOccurrences=false)
    val preferences=activity.getSharedPreferences("aegis-geofences-v1",android.content.Context.MODE_PRIVATE)
    val manifest=preferences.getString("manifest",null)?.let{org.json.JSONObject(it)}
    val excluded=manifest!=null && manifest.optString("observerNodeId")==scope && manifest.getJSONArray("rules").objects().none{it.getString("id")==id}
    confirmed=excluded && NodeNativeSession.read(activity)?.nodeId==scope && !preferences.getBoolean("needsInstall",true) && !state.optBoolean("syncPending",true) && state.optString("status") in setOf("installed","waiting_initial","active")
   }catch(_:Exception){NodeBackgroundWork.enqueue(activity)}
   if(!confirmed)NodeBackgroundWork.enqueue(activity)
   invoke.resolve(JSObject(org.json.JSONObject().put("state",if(confirmed)"confirmed" else "pending").put("message",if(confirmed)"Cancelamento confirmado neste Android." else "Cancelamento salvo; retirada no Android ainda pendente.").toString()))
  }
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
  NodeUpdates.background {try{invoke.resolve(JSObject(NodeUpdates.install(activity,args.getString("version"),args.getString("url"),args.getLong("versionCode"),args.getString("sha256")).toString()))}catch(_:Exception){invoke.reject("update_installer_failed")} }
 }
 @Command fun requestHandsFreePermission(invoke:Invoke) {
  if(getPermissionState("handsFreeAudio")==PermissionState.GRANTED){NodeHandsFree.runtime(activity).recoverFromSystem();invoke.resolve(JSObject(NodeHandsFree.state(activity)));return}
  requestPermissionForAlias("handsFreeAudio",invoke,"handsFreePermissionCallback")
 }
 @PermissionCallback private fun handsFreePermissionCallback(invoke:Invoke){NodeHandsFree.runtime(activity).recoverFromSystem();invoke.resolve(JSObject(NodeHandsFree.state(activity)))}
 @Command fun selectHandsFreeAssistant(invoke:Invoke) {
  val intent=if(Build.VERSION.SDK_INT>=29) {
   val roles=activity.getSystemService(android.app.role.RoleManager::class.java)
   if(roles.isRoleAvailable(android.app.role.RoleManager.ROLE_ASSISTANT))roles.createRequestRoleIntent(android.app.role.RoleManager.ROLE_ASSISTANT) else Intent(Settings.ACTION_VOICE_INPUT_SETTINGS)
  }else Intent(Settings.ACTION_VOICE_INPUT_SETTINGS)
  startActivityForResult(invoke,intent,"handsFreeAssistantCallback")
 }
 @ActivityCallback private fun handsFreeAssistantCallback(invoke:Invoke,result:ActivityResult){NodeHandsFree.runtime(activity).recoverFromSystem();invoke.resolve(JSObject(NodeHandsFree.state(activity)))}
 @Command fun importHandsFreeModel(invoke:Invoke) {
  val intent=Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*").putExtra(Intent.EXTRA_TITLE,"aegis.int8.tflite")
  startActivityForResult(invoke,intent,"handsFreeImportCallback")
 }
 @ActivityCallback private fun handsFreeImportCallback(invoke:Invoke,result:ActivityResult) {
  val uri=result.data?.data
  if(result.resultCode!=Activity.RESULT_OK || uri==null){invoke.resolve(JSObject(NodeHandsFree.state(activity)));return}
  handsFreeIo.execute{try{
    val runtime=NodeHandsFree.runtime(activity)
    val state=runtime.state();require(state.optString("status") !in setOf("capturing","processing","speaking","continuation"))
    activity.contentResolver.openInputStream(uri).use{input->require(input!=null);HandsFreeModel.import(activity,input)}
    runtime.imported();invoke.resolve(JSObject(NodeHandsFree.state(activity)))
  }catch(_:Throwable){NodeHandsFree.runtime(activity).importFailed();invoke.reject("Arquivo incompatível. Importe o modelo privado Aegis de 39.552 bytes selecionado na PoC.")}}
 }
 @Command fun exportHandsFreeMetrics(invoke:Invoke) {
  startActivityForResult(invoke,Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("application/json").putExtra(Intent.EXTRA_TITLE,"aegis-hands-free-metrics.json"),"handsFreeExportCallback")
 }
 @ActivityCallback private fun handsFreeExportCallback(invoke:Invoke,result:ActivityResult) {
  val uri=result.data?.data
  if(result.resultCode!=Activity.RESULT_OK || uri==null){invoke.resolve(JSObject(NodeHandsFree.state(activity)));return}
  handsFreeIo.execute{try{
    val bytes=NodeHandsFree.runtime(activity).metrics.export().toByteArray(Charsets.UTF_8)
    activity.contentResolver.openOutputStream(uri,"wt").use{output->require(output!=null);output.write(bytes)}
    invoke.resolve(JSObject(NodeHandsFree.state(activity)))
  }catch(_:Exception){invoke.reject("Não foi possível exportar as métricas. Escolha outro destino.")}}
 }
 companion object {private val handsFreeIo=java.util.concurrent.Executors.newSingleThreadExecutor{r->Thread(r,"AegisHandsFreeFiles").apply{isDaemon=true}}}

}
