//! Fixed domain IPC. The WebView cannot choose an origin, route, method, or credential.
use crate::node_commands::NodeRuntime;
use crate::node_domain_routes::route;
use serde_json::{json, Value};
use tauri::{AppHandle, Manager};
async fn call(
    runtime: &NodeRuntime,
    domain: &str,
    operation: &str,
    identifier: Option<String>,
    payload: Option<Value>,
) -> Result<Value, String> {
    let mut payload = payload.unwrap_or_else(|| json!({}));
    if !payload.is_object()
        || serde_json::to_vec(&payload)
            .map_err(|_| "Dados inválidos.")?
            .len()
            > 65536
    {
        return Err("Dados inválidos.".into());
    }
    let (method, path) = route(domain, operation, identifier, &mut payload)?;
    runtime.domain_call(method, path, payload).await
}
#[tauri::command]
pub async fn node_reminders(
    app: AppHandle,
    runtime: tauri::State<'_, NodeRuntime>,
    operation: String,
    id: Option<String>,
    payload: Option<Value>,
) -> Result<Value, String> {
    if operation == "action" {
        let mut body = payload.unwrap_or_else(|| json!({}));
        let identifier = id.clone().ok_or("Identificador necessário.")?;
        let (_, path) = route("reminders", "action", id, &mut body)?;
        let scope = runtime
            .scope_id()
            .await?
            .ok_or("Pareie este dispositivo para continuar.")?;
        match runtime.domain_action_call(path, body.clone(), &scope).await {
            Ok(value) => return Ok(value),
            Err((message, false)) => return Err(message),
            Err((message, true)) => {
                #[cfg(target_os = "android")]
                {
                    let result: Result<Value, _> = app
                        .state::<crate::native_notifications::AndroidBridge>()
                        .0
                        .run_mobile_plugin_async(
                            "queueReminderAction",
                            json!({"reminderId":identifier,"payload":body,"scopeNodeId":scope}),
                        )
                        .await;
                    return result
                        .map_err(|_| "Não foi possível guardar esta ação no aparelho.".into());
                }
                #[cfg(windows)]
                {
                    crate::windows_notifications::enqueue_ui(&app, &identifier, &body, &scope)?;
                    return Ok(json!({"pending":true,"applied":false}));
                }
                #[cfg(not(any(windows, target_os = "android")))]
                return Err(message);
            }
        }
    }
    let _ = app;
    call(&runtime, "reminders", &operation, id, payload).await
}
#[tauri::command]
pub async fn node_places(
    runtime: tauri::State<'_, NodeRuntime>,
    operation: String,
    id: Option<String>,
    payload: Option<Value>,
) -> Result<Value, String> {
    call(&runtime, "places", &operation, id, payload).await
}
#[tauri::command]
pub async fn node_location(
    app: AppHandle,
    runtime: tauri::State<'_, NodeRuntime>,
    operation: String,
    id: Option<String>,
    payload: Option<Value>,
) -> Result<Value, String> {
    if matches!(
        operation.as_str(),
        "permissions" | "requestPermission" | "current"
    ) {
        #[cfg(target_os = "android")]
        {
            let method = match operation.as_str() {
                "permissions" => "locationPermissions",
                "requestPermission" => "requestLocationPermission",
                _ => "currentLocation",
            };
            let result: Result<Value, _> = app
                .state::<crate::native_notifications::AndroidBridge>()
                .0
                .run_mobile_plugin_async(method, payload.unwrap_or_else(|| json!({})))
                .await;
            return result.map_err(|_| {
                "Não foi possível acessar localização. Confira as permissões.".into()
            });
        }
        #[cfg(not(target_os = "android"))]
        if operation != "current" {
            return Ok(
                json!({"supported":false,"foreground":false,"precise":false,"background":false,"servicesEnabled":false}),
            );
        }
    }
    let _ = app;
    call(&runtime, "location", &operation, id, payload).await
}
#[tauri::command]
pub async fn node_geofences(
    app: AppHandle,
    runtime: tauri::State<'_, NodeRuntime>,
    operation: String,
    id: Option<String>,
    payload: Option<Value>,
) -> Result<Value, String> {
    if matches!(operation.as_str(), "diagnostics" | "verifyPresence") {
        #[cfg(target_os = "android")]
        {
            let method = if operation == "diagnostics" {
                "geofenceDiagnostics"
            } else {
                "verifyGeofencePresence"
            };
            let result: Result<Value, _> = app
                .state::<crate::native_notifications::AndroidBridge>()
                .0
                .run_mobile_plugin_async(method, ())
                .await;
            return result.map_err(|_| "Não foi possível verificar a presença neste celular.".into());
        }
        #[cfg(not(target_os = "android"))]
        return Ok(json!({"supported":false,"rules":[]}));
    }
    if operation == "sync" {
        #[cfg(target_os = "android")]
        {
            let result: Result<Value, _> = app
                .state::<crate::native_notifications::AndroidBridge>()
                .0
                .run_mobile_plugin_async("syncGeofences", ())
                .await;
            return result.map_err(|_| "Sincronização pendente no celular.".into());
        }
        #[cfg(not(target_os = "android"))]
        return call(&runtime, "geofences", "sync", id, payload).await;
    }
    let _ = app;
    call(&runtime, "geofences", &operation, id, payload).await
}
#[tauri::command]
pub async fn node_settings_open(app: AppHandle, open: bool) -> Result<(), String> {
    #[cfg(target_os = "android")]
    crate::native_notifications::android_call(
        &app,
        "settingsOpen",
        json!({"open":open}).to_string(),
    )
    .await
    .map_err(|_| "Não foi possível configurar a navegação.".to_string())?;
    let _ = (app, open);
    Ok(())
}

#[tauri::command]
pub async fn node_consume_reminder_open(app: AppHandle) -> Result<Value, String> {
    #[cfg(target_os = "android")]
    {
        let value =
            crate::native_notifications::android_call(&app, "consumeReminderOpen", "{}".into())
                .await
                .map_err(|_| "Não foi possível abrir o lembrete.")?;
        return serde_json::from_str(&value)
            .map_err(|_| "Não foi possível abrir o lembrete.".into());
    }
    #[cfg(windows)]
    return Ok(json!({"reminderId":crate::windows_notifications::consume_open()}));
    #[cfg(not(any(windows, target_os = "android")))]
    {
        let _ = app;
        Ok(json!({"reminderId":null}))
    }
}
