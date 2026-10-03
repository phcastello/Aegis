use crate::node_notification::{NotificationCommand, NotificationExecutor, NotificationSink};
use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Manager};
#[cfg(target_os = "android")]
pub struct AndroidBridge(pub tauri::plugin::PluginHandle<tauri::Wry>);
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
    ) -> std::pin::Pin<Box<dyn std::future::Future<Output = &'static str> + Send + 'a>> {
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
                    "success"
                } else {
                    "failed"
                }
            }
            #[cfg(target_os = "android")]
            {
                let payload = serde_json::to_string(command).unwrap_or_default();
                match android_call(&self.0, "showNodeNotification", payload)
                    .await
                    .as_deref()
                {
                    Some("success") => "success",
                    Some("permission_denied") => "permission_denied",
                    Some("duplicate") => "duplicate",
                    Some("expired") => "expired",
                    _ => "failed",
                }
            }
            #[cfg(not(any(windows, target_os = "android")))]
            {
                let _ = command;
                "unsupported"
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
async fn android_call(app: &AppHandle, method: &'static str, payload: String) -> Option<String> {
    let (tx, rx) = tokio::sync::oneshot::channel();
    let webview = app.get_webview_window("main")?;
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
                    let _ = env.exception_clear();
                }
                let _ = tx.send(result.ok());
            });
        })
        .ok()?;
    tokio::time::timeout(std::time::Duration::from_secs(3), rx)
        .await
        .ok()?
        .ok()?
}
#[cfg(target_os = "android")]
pub async fn push_state(app: &AppHandle) -> Option<PrivatePushState> {
    serde_json::from_str(&android_call(app, "nodePushState", "{}".into()).await?).ok()
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
        == Some("success")
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
