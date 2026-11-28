//! Fixed domain IPC. The WebView cannot choose an origin, route, method, or credential.
use crate::node_commands::NodeRuntime;
use crate::node_domain_routes::route;
use serde_json::{json, Value};
use tauri::{AppHandle, Manager};

#[derive(serde::Deserialize, serde::Serialize)]
pub struct InstructionSettings {
    text: String,
    revision: uuid::Uuid,
}

#[tauri::command]
pub async fn node_custom_instructions(
    runtime: tauri::State<'_, NodeRuntime>,
    settings: Option<InstructionSettings>,
) -> Result<Value, String> {
    let path = "/settings/custom-instructions".to_owned();
    if let Some(settings) = settings {
        if settings.text.encode_utf16().count() > 2000 || settings.text.contains('\0') {
            return Err("Informe instruções válidas de até 2.000 caracteres.".into());
        }
        runtime.domain_call(reqwest::Method::PUT, path, json!(settings)).await
    } else {
        runtime.domain_call(reqwest::Method::GET, path, Value::Null).await
    }
}

#[derive(serde::Deserialize)]
#[serde(tag = "operation", rename_all = "snake_case", rename_all_fields = "camelCase", deny_unknown_fields)]
pub enum WakeDatasetRequest {
    List { node_id: Option<uuid::Uuid>, filter: Option<String>, offset: i32 },
    Audio { node_id: uuid::Uuid, event_id: uuid::Uuid, command: bool },
    Label { node_id: uuid::Uuid, event_id: uuid::Uuid, human_label: Option<String>, command_label: Option<String> },
    Delete { node_id: uuid::Uuid, event_id: uuid::Uuid },
}

#[tauri::command]
pub async fn node_wake_dataset(
    runtime: tauri::State<'_, NodeRuntime>,
    request: WakeDatasetRequest,
) -> Result<Value, String> {
    const ROOT: &str = "/settings/wake-dataset";
    let (method, path, payload) = match request {
        WakeDatasetRequest::List { node_id, filter, offset } => {
            if offset < 0 || !matches!(filter.as_deref(), None | Some("unreviewed" | "ignored" | "election_lost" | "verifier_rejected" | "false_positive" | "correct" | "not_directed")) {
                return Err("Selecione um filtro válido de ativações.".into());
            }
            let mut path = format!("{ROOT}?offset={offset}");
            if let Some(node_id) = node_id { path.push_str(&format!("&nodeId={node_id}")); }
            if let Some(filter) = filter { path.push_str(&format!("&filter={filter}")); }
            (reqwest::Method::GET, path, Value::Null)
        }
        WakeDatasetRequest::Audio { node_id, event_id, command } => {
            let clip = if command { "command" } else { "trigger" };
            (reqwest::Method::GET, format!("{ROOT}/{node_id}/{event_id}/audio/{clip}"), Value::Null)
        }
        WakeDatasetRequest::Label { node_id, event_id, human_label, command_label } => {
            if !matches!(human_label.as_deref(), None | Some("correct" | "false_positive" | "uncertain")) ||
                !matches!(command_label.as_deref(), None | Some("directed" | "not_directed" | "uncertain")) {
                return Err("Selecione rótulos válidos para a amostra.".into());
            }
            (reqwest::Method::PUT, format!("{ROOT}/{node_id}/{event_id}/labels"),
                json!({"humanLabel": human_label, "commandLabel": command_label}))
        }
        WakeDatasetRequest::Delete { node_id, event_id } => {
            (reqwest::Method::DELETE, format!("{ROOT}/{node_id}/{event_id}"), Value::Null)
        }
    };
    runtime.domain_call(method, path, payload).await
}

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
            return result
                .map_err(|_| "Não foi possível verificar a presença neste celular.".into());
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
    if operation == "cancel" {
        let identifier = id.clone().ok_or("Identificador necessário.")?;
        let mut value = call(&runtime, "geofences", &operation, id, payload).await?;
        #[cfg(target_os = "android")]
        {
            let scope = runtime.scope_id().await?;
            if scope.is_some() && scope.as_deref() == value["observerNodeId"].as_str() {
                let removal: Result<Value, _> = app
                    .state::<crate::native_notifications::AndroidBridge>()
                    .0
                    .run_mobile_plugin_async("applyGeofenceCancellation", json!({"ruleId":identifier,"ruleRevision":value["revision"],"scopeNodeId":scope}))
                    .await;
                let removal = removal.unwrap_or_else(|_| json!({"state":"pending","message":"Cancelamento salvo; retirada no Android ainda pendente."}));
                if removal["state"] == "confirmed" {
                    value["syncStatus"] = json!("removed");
                    value["syncedRevision"] = value["revision"].clone();
                }
                value["localRemoval"] = removal;
            } else {
                value["localRemoval"] = json!({"state":"remote_node"});
            }
        }
        let _ = (app, identifier);
        return Ok(value);
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
    return Ok(
        json!({"reminderId":crate::windows_notifications::consume_open(),"conversationId":crate::windows_notifications::consume_conversation_open()}),
    );
    #[cfg(not(any(windows, target_os = "android")))]
    {
        let _ = app;
        Ok(json!({"reminderId":null}))
    }
}

#[tauri::command]
pub async fn node_read_visibility(app: AppHandle) -> Result<bool, String> {
    #[cfg(target_os = "android")]
    {
        let value = crate::native_notifications::android_call(&app, "readVisibility", "{}".into())
            .await
            .map_err(|_| "Não foi possível confirmar a visibilidade.".to_string())?;
        return serde_json::from_str(&value)
            .map_err(|_| "Não foi possível confirmar a visibilidade.".to_string());
    }
    #[cfg(not(target_os = "android"))]
    {
        let _ = app;
        Err("Consulta disponível somente no Android.".into())
    }
}

#[tauri::command]
pub async fn node_take_shared_attachments(app: AppHandle) -> Result<Value, String> {
    #[cfg(target_os = "android")]
    {
        let bridge = app
            .try_state::<crate::native_notifications::AndroidBridge>()
            .ok_or("Compartilhamento indisponível.")?;
        return bridge
            .0
            .run_mobile_plugin_async("takeSharedAttachments", ())
            .await
            .map_err(|_| "Não foi possível receber os arquivos compartilhados.".into());
    }
    #[cfg(not(target_os = "android"))]
    {
        let _ = app;
        Ok(json!({"files":[]}))
    }
}

#[tauri::command]
pub async fn node_shared_attachment(
    app: AppHandle,
    operation: String,
    handle: uuid::Uuid,
    request_id: Option<uuid::Uuid>,
    upload_id: Option<uuid::Uuid>,
    index: Option<u32>,
    chunk_size: Option<u32>,
) -> Result<Value, String> {
    #[cfg(target_os = "android")]
    {
        let valid = match operation.as_str() {
            "release" => true,
            "cancel" => request_id.is_some(),
            "upload" => {
                request_id.is_some()
                    && upload_id.is_some()
                    && index.is_some()
                    && matches!(chunk_size, Some(1..=4_194_304))
            }
            _ => false,
        };
        if !valid {
            return Err("Operação de anexo inválida.".into());
        }
        let origin = crate::config::configured_endpoint()?.base_url();
        let bridge = app
            .try_state::<crate::native_notifications::AndroidBridge>()
            .ok_or("Compartilhamento indisponível.")?;
        return bridge
            .0
            .run_mobile_plugin_async(
                "sharedAttachment",
                json!({
                    "operation": operation, "handle": handle, "requestId": request_id,
                    "uploadId": upload_id, "index": index, "chunkSize": chunk_size, "origin": origin
                }),
            )
            .await
            .map_err(|_| {
                "Não foi possível enviar o arquivo compartilhado. Tente novamente.".into()
            });
    }
    #[cfg(not(target_os = "android"))]
    {
        let _ = (
            app, operation, handle, request_id, upload_id, index, chunk_size,
        );
        Err("Compartilhamento disponível somente no Android.".into())
    }
}

#[tauri::command]
pub async fn node_hide_keyboard(app: AppHandle) -> Result<(), String> {
    #[cfg(target_os = "android")]
    crate::native_notifications::android_call(&app, "hideKeyboard", "{}".into())
        .await
        .map_err(|_| "Não foi possível fechar o teclado.".to_string())?;
    let _ = app;
    Ok(())
}
