use crate::node_notification::{
    NotificationCommand, NotificationExecutor, NotificationResult, NotificationSink,
    NotificationStatus,
};
use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Manager};
#[cfg(target_os = "android")]
pub struct AndroidBridge(pub tauri::plugin::PluginHandle<tauri::Wry>);
#[cfg(feature = "android-framework-fixture")]
pub fn fixture_panic_diagnostics() {
    std::panic::set_hook(Box::new(|panic| {
        let file = panic
            .location()
            .map(|location| location.file())
            .unwrap_or("");
        let phase = if file.ends_with("platform_impl/android/ndk_glue.rs") {
            "tao_activity_jni"
        } else if file.ends_with("node_commands.rs") {
            "runtime_state"
        } else if file.ends_with("native_notifications.rs") {
            "native_bridge"
        } else if file.contains("tauri-plugin-notification") {
            "notification_plugin_init"
        } else if file.contains("/wry-") {
            "wry_webview"
        } else {
            "tauri_runtime"
        };
        let message = panic
            .payload()
            .downcast_ref::<String>()
            .map(String::as_str)
            .or_else(|| panic.payload().downcast_ref::<&str>().copied())
            .unwrap_or("");
        let error_type = if message.contains("JavaException") {
            "JavaException"
        } else {
            "NativePanic"
        };
        // Test-only hook. Never emit the panic message, source location or stack.
        eprintln!("AegisNodeNativeTest phase={phase} errorType={error_type}");
    }));
}
#[cfg(target_os = "android")]
pub fn plugin() -> tauri::plugin::TauriPlugin<tauri::Wry> {
    tauri::plugin::Builder::new("node-notifications")
        .setup(|app, api| {
            let handle =
                api.register_android_plugin("com.aegis.node", "NodeNotificationsPlugin")?;
            app.manage(AndroidBridge(handle));
            Ok(())
        })
        .build()
}
struct NativeSink(AppHandle);
impl NotificationSink for NativeSink {
    fn show<'a>(
        &'a self,
        command: &'a NotificationCommand,
    ) -> std::pin::Pin<Box<dyn std::future::Future<Output = NotificationResult> + Send + 'a>> {
        Box::pin(async move {
            #[cfg(windows)]
            {
                use tauri_plugin_notification::NotificationExt;
                if self
                    .0
                    .notification()
                    .builder()
                    .title(&command.input.title)
                    .body(&command.input.body)
                    .show()
                    .is_ok()
                {
                    NotificationResult::new(NotificationStatus::Success)
                } else {
                    NotificationResult::new(NotificationStatus::Failed)
                }
            }
            #[cfg(target_os = "android")]
            {
                use crate::node_notification::AndroidDiagnosticCode as Code;
                let payload = match serde_json::to_string(command) {
                    Ok(payload) => payload,
                    Err(_) => {
                        return NotificationResult::diagnostic(
                            NotificationStatus::Failed,
                            Code::AndroidInvalidNotificationPayload,
                        )
                    }
                };
                match android_call(&self.0, "showNodeNotification", payload).await {
                    Ok(output) => serde_json::from_str::<NotificationResult>(&output)
                        .unwrap_or_else(|_| {
                            NotificationResult::diagnostic(
                                NotificationStatus::Failed,
                                Code::AndroidPayloadParseFailed,
                            )
                        }),
                    Err(code) => NotificationResult::diagnostic(NotificationStatus::Failed, code),
                }
            }
            #[cfg(not(any(windows, target_os = "android")))]
            {
                let _ = command;
                NotificationResult::new(NotificationStatus::Unsupported)
            }
        })
    }
}
pub fn executor(app: &AppHandle) -> std::sync::Arc<NotificationExecutor> {
    std::sync::Arc::new(NotificationExecutor::new(NativeSink(app.clone())))
}
#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PrivatePushState {
    pub token: Option<String>,
    pub configured: bool,
    pub granted: bool,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NotificationSettings {
    pub granted: bool,
    pub push_configured: bool,
    pub autostart: bool,
}
// Private, fixed JNI calls only. The upstream plugin callback unwraps send() after a
// dropped receiver, so bounded/cancelable operations use this safe oneshot adapter.
#[cfg(target_os = "android")]
async fn android_call(
    app: &AppHandle,
    method: &'static str,
    payload: String,
) -> Result<String, crate::node_notification::AndroidDiagnosticCode> {
    use crate::node_notification::AndroidDiagnosticCode as Code;
    let (tx, rx) = tokio::sync::oneshot::channel();
    let webview = app
        .get_webview_window("main")
        .ok_or(Code::AndroidJniUnavailable)?;
    webview
        .with_webview(move |native| {
            native.jni_handle().exec(move |env, activity, _| {
                if tx.is_closed() {
                    return;
                }
                let result = (|| -> Result<String, jni::errors::Error> {
                    let text = env.new_string(payload)?;
                    let object = jni::objects::JObject::from(text);
                    let value = env
                        .call_method(
                            activity,
                            method,
                            "(Ljava/lang/String;)Ljava/lang/String;",
                            &[jni::objects::JValue::Object(&object)],
                        )?
                        .l()?;
                    let string = jni::objects::JString::from(value);
                    let output = env.get_string(&string)?.into();
                    Ok(output)
                })();
                if result.is_err() {
                    log_jni_failure(env);
                }
                let _ = tx.send(result.map_err(|_| Code::AndroidJniCallFailed));
            });
        })
        .map_err(|_| Code::AndroidJniUnavailable)?;
    tokio::time::timeout(std::time::Duration::from_secs(3), rx)
        .await
        .map_err(|_| Code::AndroidNativeTimeout)?
        .map_err(|_| Code::AndroidJniCallFailed)?
}
#[cfg(target_os = "android")]
fn log_jni_failure(env: &mut jni::JNIEnv<'_>) {
    let throwable = env.exception_occurred().ok();
    let _ = env.exception_clear();
    let error_type = throwable
        .and_then(|throwable| {
            let class = env
                .call_method(throwable, "getClass", "()Ljava/lang/Class;", &[])
                .ok()?
                .l()
                .ok()?;
            let name = env
                .call_method(class, "getSimpleName", "()Ljava/lang/String;", &[])
                .ok()?
                .l()
                .ok()?;
            let string = jni::objects::JString::from(name);
            let value: String = env.get_string(&string).ok()?.into();
            Some(value)
        })
        .filter(|name| {
            !name.is_empty()
                && name.len() <= 64
                && name.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'_')
        })
        .unwrap_or_else(|| "JniError".into());
    let _ = env.exception_clear();
    if let Ok(message) = env.new_string(format!("phase=jni_call errorType={error_type}")) {
        if let Ok(tag) = env.new_string("AegisNodeNotification") {
            let _ = env.call_static_method(
                "android/util/Log",
                "e",
                "(Ljava/lang/String;Ljava/lang/String;)I",
                &[(&tag).into(), (&message).into()],
            );
        }
    }
    let _ = env.exception_clear();
}
#[cfg(target_os = "android")]
pub async fn push_state(app: &AppHandle) -> Option<PrivatePushState> {
    serde_json::from_str(&android_call(app, "nodePushState", "{}".into()).await.ok()?).ok()
}
#[cfg(target_os = "android")]
pub async fn bind(app: &AppHandle, id: Option<&str>) -> bool {
    android_call(
        app,
        "bindNodePush",
        serde_json::json!({"nodeId":id}).to_string(),
    )
    .await
    .as_deref()
        == Ok("success")
}
#[tauri::command]
pub async fn node_notification_settings(app: AppHandle) -> NotificationSettings {
    #[cfg(target_os = "android")]
    {
        let state = push_state(&app).await.unwrap_or_default();
        NotificationSettings {
            granted: state.granted,
            push_configured: state.configured,
            autostart: false,
        }
    }
    #[cfg(windows)]
    {
        use tauri_plugin_autostart::ManagerExt;
        NotificationSettings {
            granted: true,
            push_configured: false,
            autostart: app.autolaunch().is_enabled().unwrap_or(false),
        }
    }
    #[cfg(not(any(windows, target_os = "android")))]
    {
        let _ = app;
        NotificationSettings {
            granted: false,
            push_configured: false,
            autostart: false,
        }
    }
}
#[tauri::command]
pub async fn node_request_notification_permission(app: AppHandle) -> Result<(), String> {
    #[cfg(target_os = "android")]
    {
        let result: Result<serde_json::Value, _> = app
            .state::<AndroidBridge>()
            .0
            .run_mobile_plugin_async("requestPermissions", ())
            .await;
        result
            .map(|_| ())
            .map_err(|_| "Não foi possível solicitar permissão.".into())
    }
    #[cfg(not(target_os = "android"))]
    {
        let _ = app;
        Ok(())
    }
}
#[tauri::command]
pub fn node_set_autostart(app: AppHandle, enabled: bool) -> Result<(), String> {
    #[cfg(windows)]
    {
        use tauri_plugin_autostart::ManagerExt;
        let result = if enabled {
            app.autolaunch().enable()
        } else {
            app.autolaunch().disable()
        };
        result.map_err(|_| "Não foi possível configurar início automático.".into())
    }
    #[cfg(not(windows))]
    {
        let _ = (app, enabled);
        Err("Disponível somente no Windows.".into())
    }
}
#[cfg(windows)]
pub fn open(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
}
#[cfg(windows)]
pub fn tray(app: &AppHandle) -> tauri::Result<()> {
    use tauri::{
        menu::{Menu, MenuItem},
        tray::TrayIconBuilder,
    };
    let open = MenuItem::with_id(app, "open", "Abrir Aegis", true, None::<&str>)?;
    let exit = MenuItem::with_id(app, "exit", "Sair", true, None::<&str>)?;
    let menu = Menu::with_items(app, &[&open, &exit])?;
    let mut builder = TrayIconBuilder::new()
        .menu(&menu)
        .tooltip("Aegis")
        .on_menu_event(|app, event| match event.id.as_ref() {
            "open" => self::open(app),
            "exit" => {
                app.state::<crate::node_commands::NodeRuntime>().shutdown();
                app.exit(0);
            }
            _ => {}
        });
    if let Some(icon) = app.default_window_icon() {
        builder = builder.icon(icon.clone());
    }
    builder.build(app)?;
    Ok(())
}

#[tauri::command]
pub fn node_exit(app: AppHandle) {
    app.state::<crate::node_commands::NodeRuntime>().shutdown();
    app.exit(0);
}
