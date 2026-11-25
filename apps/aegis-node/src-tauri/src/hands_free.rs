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

fn battery_start_payload(mode: Option<&str>) -> Result<Value, HandsFreeError> {
    let mode = mode.ok_or_else(|| HandsFreeError::new("invalid_request"))?;
    if !matches!(mode, "off" | "capture_only" | "detection") { return Err(HandsFreeError::new("invalid_request")); }
    Ok(json!({"mode": mode}))
}

fn battery_auto_control(operation: &str) -> Result<(&'static str, Value), HandsFreeError> {
    match operation {
        "batteryAutoStart" => Ok(("handsFreeBatteryAutoStart", json!({"validation":false}))),
        "batteryAutoValidate" => Ok(("handsFreeBatteryAutoStart", json!({"validation":true}))),
        "batteryAutoCancel" => Ok(("handsFreeBatteryAutoCancel", json!({}))),
        _ => Err(HandsFreeError::new("invalid_request")),
    }
}

#[tauri::command]
pub async fn node_hands_free(
    app: tauri::AppHandle,
    operation: String,
    enabled: Option<bool>,
    diagnostic: Option<bool>,
    battery_mode: Option<String>,
    collection_enabled: Option<bool>,
    collection_limit_mi_b: Option<u32>,
    lease_id: Option<String>,
    audio_kind: Option<String>,
    turn_id: Option<String>,
    assistant_message_id: Option<String>,
    speech_request_id: Option<String>,
) -> Result<Value, HandsFreeError> {
    #[cfg(windows)]
    {
        let _ = (battery_mode, collection_enabled, collection_limit_mi_b, audio_kind, turn_id, assistant_message_id, speech_request_id);
        return tauri::async_runtime::spawn_blocking(move || {
            use tauri::Manager;
            app.state::<crate::windows_hands_free::WindowsHandsFree>()
                .operate(&operation, enabled, diagnostic, lease_id)
                .map_err(HandsFreeError::new)
        }).await.map_err(|_| HandsFreeError::new("native_failure"))?;
    }
    #[cfg(not(windows))]
    {
    let (method, payload) = match operation.as_str() {
        "state" => ("handsFreeState", json!({})),
        "conversationChanges" => ("conversationChanges", json!({})),
        "configure" => (
            "handsFreeConfigure",
            json!({"enabled": enabled.ok_or_else(|| HandsFreeError::new("invalid_request"))?, "diagnostic": diagnostic.unwrap_or(false)}),
        ),
        "batteryStart" => ("handsFreeBatteryStart", battery_start_payload(battery_mode.as_deref())?),
        "batteryFinish" => ("handsFreeBatteryFinish", json!({})),
        "batteryAutoStart" | "batteryAutoValidate" | "batteryAutoCancel" => battery_auto_control(operation.as_str())?,
        "cancel" => ("handsFreeCancel", json!({})),
        "collectionConfigure" => {
            let limit = collection_limit_mi_b.ok_or_else(|| HandsFreeError::new("invalid_request"))?;
            if !(10..=2048).contains(&limit) { return Err(HandsFreeError::new("invalid_request")); }
            ("handsFreeCollectionConfigure", json!({"enabled":collection_enabled.ok_or_else(|| HandsFreeError::new("invalid_request"))?,"limitMiB":limit}))
        }
        "collectionDelete" => ("handsFreeCollectionDelete", json!({})),
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
        "mediaReferenceStart" | "mediaReferenceStop" => {
            #[cfg(target_os = "android")]
            {
                use tauri::Manager;
                let method = if operation == "mediaReferenceStart" {
                    "requestHandsFreeMediaReference"
                } else {
                    "stopHandsFreeMediaReference"
                };
                let bridge = app.try_state::<crate::native_notifications::AndroidBridge>()
                    .ok_or_else(|| HandsFreeError::new("bridge_unavailable"))?;
                let result: Result<Value, _> = bridge.0.run_mobile_plugin_async(method, ()).await;
                return result.map_err(|_| HandsFreeError::new("native_failure"));
            }
            #[cfg(not(target_os = "android"))]
            return Err(HandsFreeError::new("unsupported"));
        }
        "permission" | "assistant" | "import" | "export" | "batteryExport" => {
            #[cfg(target_os = "android")]
            {
                use tauri::Manager;
                let method = match operation.as_str() {
                    "permission" => "requestHandsFreePermission",
                    "assistant" => "selectHandsFreeAssistant",
                    "import" => "importHandsFreeModel",
                    "export" => "exportHandsFreeMetrics",
                    _ => "exportHandsFreeBatteryMetrics",
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
}

#[cfg(test)]
mod battery_tests {
    use super::*;
    #[test]
    fn only_explicit_battery_modes_cross_the_native_boundary() {
        for mode in ["off", "capture_only", "detection"] {
            assert_eq!(battery_start_payload(Some(mode)).unwrap(), json!({"mode":mode}));
        }
    }
    #[test]
    fn missing_unknown_or_unbounded_battery_modes_are_rejected() {
        for mode in [None, Some(""), Some("diagnostic"), Some("off\n"), Some("/private/audio")] {
            assert_eq!(battery_start_payload(mode).unwrap_err().code, "invalid_request");
        }
    }
}

#[cfg(test)]
mod battery_auto_tests {
    use super::*;
    #[test]
    fn automatic_controls_bind_validation_to_the_closed_operation_only() {
        assert_eq!(battery_auto_control("batteryAutoStart").unwrap(), ("handsFreeBatteryAutoStart",json!({"validation":false})));
        assert_eq!(battery_auto_control("batteryAutoValidate").unwrap(), ("handsFreeBatteryAutoStart",json!({"validation":true})));
        assert_eq!(battery_auto_control("batteryAutoCancel").unwrap(), ("handsFreeBatteryAutoCancel",json!({})));
    }
    #[test]
    fn arbitrary_automation_names_and_durations_are_not_native_controls() {
        for operation in ["autoStart", "batteryAutoStart?duration=1", "batteryAutoValidate\n", "setAlarm", "batteryAutoFinish"] {
            assert_eq!(battery_auto_control(operation).unwrap_err().code,"invalid_request");
        }
    }
}
