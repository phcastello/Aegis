//! Closed Android assistant controls. Audio and credentials never cross WebView IPC.
use serde_json::{json, Value};

#[derive(Debug, serde::Serialize)]
pub struct HandsFreeError {
    code: &'static str,
}
impl HandsFreeError {
    fn new(code: &'static str) -> Self {
        Self { code }
    }
}

#[tauri::command]
pub async fn node_hands_free(
    app: tauri::AppHandle,
    operation: String,
    enabled: Option<bool>,
    diagnostic: Option<bool>,
    lease_id: Option<String>,
    audio_kind: Option<String>,
    turn_id: Option<String>,
    assistant_message_id: Option<String>,
    speech_request_id: Option<String>,
) -> Result<Value, HandsFreeError> {
    let (method, payload) = match operation.as_str() {
        "state" => ("handsFreeState", json!({})),
        "configure" => (
            "handsFreeConfigure",
            json!({"enabled": enabled.ok_or_else(|| HandsFreeError::new("invalid_request"))?, "diagnostic": diagnostic.unwrap_or(false)}),
        ),
        "cancel" => ("handsFreeCancel", json!({})),
        "manualAcquire" | "manualRelease" => {
            let id = lease_id.ok_or_else(|| HandsFreeError::new("invalid_request"))?;
            let id =
                uuid::Uuid::parse_str(&id).map_err(|_| HandsFreeError::new("invalid_request"))?;
            if id.is_nil() {
                return Err(HandsFreeError::new("invalid_request"));
            }
            let kind = audio_kind.as_deref().unwrap_or("capture");
            if !matches!(kind, "capture" | "playback") {
                return Err(HandsFreeError::new("invalid_request"));
            }
            (
                if operation == "manualAcquire" {
                    "handsFreeManualAcquire"
                } else {
                    "handsFreeManualRelease"
                },
                json!({"leaseId":id.to_string(),"kind":kind}),
            )
        }
        "playSpeech" | "stopSpeech" => {
            let checked_id = |value: Option<String>| -> Result<String, HandsFreeError> {
                let id = uuid::Uuid::parse_str(&value.ok_or_else(|| HandsFreeError::new("invalid_request"))?)
                    .map_err(|_| HandsFreeError::new("invalid_request"))?;
                if id.is_nil() { return Err(HandsFreeError::new("invalid_request")); }
                Ok(id.to_string())
            };
            let speech = checked_id(speech_request_id)?;
            let payload = if operation == "playSpeech" {
                json!({"turnId":checked_id(turn_id)?,"assistantMessageId":checked_id(assistant_message_id)?,"speechRequestId":speech})
            } else { json!({"speechRequestId":speech}) };
            #[cfg(target_os = "android")]
            {
                use tauri::Manager;
                let bridge = app.try_state::<crate::native_notifications::AndroidBridge>()
                    .ok_or_else(|| HandsFreeError::new("bridge_unavailable"))?;
                let method = if operation == "playSpeech" { "playNativeSpeech" } else { "stopNativeSpeech" };
                let result: Result<Value, _> = bridge.0.run_mobile_plugin_async(method, payload).await;
                return result.map_err(|_| HandsFreeError::new("native_failure"));
            }
            #[cfg(not(target_os = "android"))]
            { let _ = payload; return Err(HandsFreeError::new("unsupported")); }
        }
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
                let bridge = app
                    .try_state::<crate::native_notifications::AndroidBridge>()
                    .ok_or_else(|| HandsFreeError::new("bridge_unavailable"))?;
                let result: Result<Value, _> = bridge.0.run_mobile_plugin_async(method, ()).await;
                return result.map_err(|_| HandsFreeError::new("native_failure"));
            }
            #[cfg(not(target_os = "android"))]
            return Err(HandsFreeError::new("unsupported"));
        }
        _ => return Err(HandsFreeError::new("invalid_request")),
    };
    #[cfg(target_os = "android")]
    {
        let output = crate::native_notifications::android_call(&app, method, payload.to_string())
            .await
            .map_err(|code| {
                use crate::node_notification::AndroidDiagnosticCode as Code;
                HandsFreeError::new(match code {
                    Code::AndroidJniUnavailable => "bridge_unavailable",
                    Code::AndroidNativeTimeout => "timeout",
                    Code::AndroidPayloadParseFailed => "invalid_response",
                    _ => "native_failure",
                })
            })?;
        serde_json::from_str(&output).map_err(|_| HandsFreeError::new("invalid_response"))
    }
    #[cfg(not(target_os = "android"))]
    {
        let _ = (app, method, payload);
        Err(HandsFreeError::new("unsupported"))
    }
}
