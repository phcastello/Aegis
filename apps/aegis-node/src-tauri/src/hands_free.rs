//! Closed Android assistant controls. Audio and credentials never cross WebView IPC.
use serde_json::{json, Value};

#[tauri::command]
pub async fn node_hands_free(
    app: tauri::AppHandle,
    operation: String,
    enabled: Option<bool>,
    diagnostic: Option<bool>,
    lease_id: Option<String>,
    audio_kind: Option<String>,
) -> Result<Value, String> {
    let (method, payload) = match operation.as_str() {
        "state" => ("handsFreeState", json!({})),
        "configure" => ("handsFreeConfigure", json!({"enabled": enabled.ok_or("Estado necessário.")?, "diagnostic": diagnostic.unwrap_or(false)})),
        "cancel" => ("handsFreeCancel", json!({})),
        "manualAcquire" | "manualRelease" => {
            let id = lease_id.ok_or("Identificador necessário.")?;
            let id = uuid::Uuid::parse_str(&id).map_err(|_| "Identificador inválido.")?;
            if id.is_nil() { return Err("Identificador inválido.".into()); }
            let kind = audio_kind.as_deref().unwrap_or("capture");
            if !matches!(kind, "capture" | "playback") { return Err("Uso de áudio inválido.".into()); }
            (if operation == "manualAcquire" { "handsFreeManualAcquire" } else { "handsFreeManualRelease" }, json!({"leaseId":id.to_string(),"kind":kind}))
        },
        "permission" | "assistant" | "import" | "export" => {
            #[cfg(target_os = "android")]
            {
                use tauri::Manager;
                let method = match operation.as_str() {
                    "permission" => "requestHandsFreePermission",
                    "assistant" => "selectHandsFreeAssistant",
                    "import" => "importHandsFreeModel",
                    _ => "exportHandsFreeMetrics",
                };
                let result: Result<Value, _> = app.state::<crate::native_notifications::AndroidBridge>()
                    .0.run_mobile_plugin_async(method, ()).await;
                return result.map_err(|_| "Não foi possível abrir o controle Android. Tente novamente.".into());
            }
            #[cfg(not(target_os = "android"))]
            return Err("Hands-free disponível no Android.".into());
        },
        _ => return Err("Controle hands-free desconhecido.".into()),
    };
    #[cfg(target_os = "android")]
    {
        let output = crate::native_notifications::android_call(&app, method, payload.to_string()).await
            .map_err(|_| "Controle hands-free temporariamente indisponível.".to_string())?;
        serde_json::from_str(&output).map_err(|_| "Estado hands-free inválido.".into())
    }
    #[cfg(not(target_os = "android"))]
    {
        let _ = (app, method, payload);
        Err("Hands-free disponível no Android.".into())
    }
}
