mod config;
mod health;
#[cfg(feature = "native-runtime")]
mod native_notifications;
mod node_capabilities;
#[cfg(feature = "native-runtime")]
mod node_commands;
mod node_domain_routes;
#[cfg(feature = "native-runtime")]
mod node_domains;
mod node_identity;
mod node_notification;
mod node_transport;
#[cfg(feature = "native-runtime")]
mod node_vault;
mod node_window_activation;
mod platform;
mod updates;
#[cfg(all(feature = "native-runtime", windows))]
mod windows_notifications;

#[cfg(all(feature = "native-runtime", windows))]
mod windows_updates;

#[cfg(feature = "native-runtime")]
#[derive(Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct WindowsDownloadProgress {
    received: u64,
    total: Option<u64>,
}

#[cfg(feature = "native-runtime")]
#[tauri::command]
async fn prepare_windows_update(
    webview: tauri::Webview,
    update_rid: u32,
    on_progress: tauri::ipc::Channel<WindowsDownloadProgress>,
) -> Result<u32, String> {
    #[cfg(windows)]
    {
        windows_updates::prepare(webview, update_rid, on_progress).await
    }
    #[cfg(not(windows))]
    {
        let _ = (webview, update_rid, on_progress);
        Err("update_installer_failed".into())
    }
}

#[cfg(feature = "native-runtime")]
#[tauri::command]
fn install_windows_update(
    webview: tauri::Webview,
    update_rid: u32,
    bytes_rid: u32,
) -> Result<(), String> {
    #[cfg(windows)]
    {
        windows_updates::install(webview, update_rid, bytes_rid)
    }
    #[cfg(not(windows))]
    {
        let _ = (webview, update_rid, bytes_rid);
        Err("update_installer_failed".into())
    }
}

fn process_session_id() -> &'static str {
    static ID: std::sync::OnceLock<String> = std::sync::OnceLock::new();
    ID.get_or_init(|| uuid::Uuid::new_v4().to_string())
}
#[cfg(test)]
mod process_tests {
    #[test]
    fn activity_recreation_keeps_process_identity() {
        assert_eq!(super::process_session_id(), super::process_session_id());
        assert!(uuid::Uuid::parse_str(super::process_session_id()).is_ok());
    }
}

#[cfg(feature = "native-runtime")]
#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct RuntimeInfo {
    platform: platform::Platform,
    backend_url: Option<String>,
    version: String,
    version_code: u32,
    source_commit: Option<String>,
    source_version: Option<String>,
    process_session_id: String,
}

#[cfg(feature = "native-runtime")]
#[tauri::command]
fn runtime_info() -> RuntimeInfo {
    RuntimeInfo {
        process_session_id: process_session_id().to_owned(),
        platform: platform::current(),
        version: env!("CARGO_PKG_VERSION").to_owned(),
        version_code: updates::installation_code(),
        source_commit: option_env!("AEGIS_SOURCE_COMMIT").map(str::to_owned),
        source_version: option_env!("AEGIS_SOURCE_VERSION").map(str::to_owned),
        backend_url: config::configured_endpoint()
            .ok()
            .map(|endpoint| endpoint.base_url()),
    }
}

#[cfg(feature = "native-runtime")]
#[tauri::command]
async fn check_backend() -> health::HealthResult {
    match config::configured_endpoint() {
        Ok(endpoint) => health::check(&endpoint, std::time::Duration::from_secs(5)).await,
        Err(message) => health::HealthResult::unavailable(message),
    }
}

#[cfg(feature = "native-runtime")]
#[tauri::command]
async fn check_android_update() -> Result<Option<updates::AndroidUpdate>, String> {
    updates::check(env!("CARGO_PKG_VERSION")).await
}

#[cfg(feature = "native-runtime")]
#[tauri::command]
async fn install_android_update(
    app: tauri::AppHandle,
    version: String,
) -> Result<serde_json::Value, String> {
    #[cfg(target_os = "android")]
    {
        use tauri::Manager;
        let update = updates::check(env!("CARGO_PKG_VERSION"))
            .await?
            .filter(|u| u.version == version)
            .ok_or("update_asset_mismatch")?;
        let result: Result<serde_json::Value, _> = app
            .state::<native_notifications::AndroidBridge>()
            .0
            .run_mobile_plugin_async("installUpdate", update)
            .await;
        return result.map_err(|_| "update_installer_failed".into());
    }
    #[cfg(not(target_os = "android"))]
    {
        let _ = (app, version);
        Err("Atualização Android disponível somente no celular.".into())
    }
}

#[cfg(feature = "native-runtime")]
#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    #[cfg(feature = "android-framework-fixture")]
    native_notifications::fixture_panic_diagnostics();
    let builder = tauri::Builder::default();
    // One Windows process owns this installation's credential checkpoint at a time.
    // Register first so a second launch exits before secure-store initialization.
    #[cfg(windows)]
    let builder = builder.plugin(tauri_plugin_single_instance::init(|app, args, _| {
        if node_window_activation::background_launch(&args) {
            windows_notifications::activate(app, &args);
            return;
        }
        use tauri::Manager;
        if let Some(window) = app.get_webview_window("main") {
            let _ = window.show();
            let _ = window.unminimize();
            let _ = window.set_focus();
        }
    }));
    let builder = builder.plugin(
        tauri_plugin_opener::Builder::new()
            .open_js_links_on_click(false)
            .build(),
    );
    #[cfg(windows)]
    let builder = builder
        .plugin(
            tauri_plugin_updater::Builder::new()
                .default_version_comparator(|current, release| {
                    updates::recommended_update_eligible(&current, &release.version)
                })
                .build(),
        )
        .plugin(tauri_plugin_process::init())
        .plugin(
            tauri_plugin_autostart::Builder::new()
                .args(["--autostart"])
                .build(),
        );
    let builder = builder.plugin(tauri_plugin_notification::init());
    #[cfg(target_os = "android")]
    let builder = builder.plugin(native_notifications::plugin());
    let mut context = tauri::generate_context!();
    if let Ok(endpoint) = config::configured_endpoint() {
        if let Some(csp) = context.config_mut().app.security.csp.as_mut() {
            *csp = tauri::utils::config::Csp::Policy(csp.to_string().replace(
                "connect-src ",
                &format!("connect-src {} ", endpoint.base_url()),
            ));
        }
    }
    builder
        .setup(|app| {
            use tauri::Manager;
            app.manage(node_commands::NodeRuntime::initialize(app.handle().clone()));
            let handle = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                handle
                    .state::<node_commands::NodeRuntime>()
                    .initialize_transport()
                    .await;
            });
            use tauri::webview::{PermissionKind, PermissionResponse};
            let config = &app.config().app.windows[0];
            let dev_origin = app.config().build.dev_url.as_ref().map(|url| url.origin());
            tauri::WebviewWindowBuilder::from_config(app, config)?
                .visible(
                    !(cfg!(windows)
                        && node_window_activation::background_launch(
                            &std::env::args().collect::<Vec<_>>(),
                        )),
                )
                .on_navigation(move |url| {
                    url.origin().ascii_serialization() == "https://tauri.localhost"
                        || (cfg!(debug_assertions)
                            && dev_origin
                                .as_ref()
                                .is_some_and(|origin| origin == &url.origin()))
                })
                .on_new_window(|_, _| tauri::webview::NewWindowResponse::Deny)
                .on_permission_request(|_, kind| match kind {
                    PermissionKind::Microphone | PermissionKind::Autoplay => {
                        PermissionResponse::Default
                    }
                    _ => PermissionResponse::Deny,
                })
                .build()?;
            #[cfg(windows)]
            {
                native_notifications::tray(app.handle())?;
                let opened_window = windows_notifications::initialize(app.handle());
                if node_window_activation::hide_after_activation(
                    &std::env::args().collect::<Vec<_>>(),
                    opened_window,
                ) {
                    if let Some(window) = app.get_webview_window("main") {
                        window.hide()?;
                    }
                }
            }
            Ok(())
        })
        .on_window_event(|window, event| {
            use tauri::Manager;
            #[cfg(windows)]
            if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                api.prevent_close();
                let _ = window.hide();
            }
            #[cfg(target_os = "android")]
            if matches!(
                event,
                tauri::WindowEvent::Suspended | tauri::WindowEvent::Resumed
            ) {
                // Android Activity onPause/onResume are window events. The event
                // loop's RunEvent::Resumed is a poll wake, not an Activity resume.
                let foreground = matches!(event, tauri::WindowEvent::Resumed);
                window
                    .state::<node_commands::NodeRuntime>()
                    .signal_lifecycle(foreground);
                let handle = window.app_handle().clone();
                tauri::async_runtime::spawn(async move {
                    handle
                        .state::<node_commands::NodeRuntime>()
                        .lifecycle(foreground)
                        .await;
                });
            }
            if matches!(event, tauri::WindowEvent::Focused(true)) {
                let runtime = window.state::<node_commands::NodeRuntime>();
                if matches!(
                    runtime.transport.state().transport_state,
                    "offline" | "reconnecting"
                ) {
                    runtime.transport.reconnect();
                }
            }
        })
        .invoke_handler(tauri::generate_handler![
            runtime_info,
            check_backend,
            check_android_update,
            prepare_windows_update,
            install_windows_update,
            node_commands::node_status,
            node_commands::node_transport_status,
            node_commands::node_transport_reconnect,
            node_commands::node_pair,
            node_commands::node_list,
            node_commands::node_rename,
            node_commands::node_set_enabled,
            node_commands::node_set_target_priority,
            node_commands::node_resolve_target,
            node_commands::node_revoke,
            native_notifications::node_exit,
            native_notifications::node_notification_settings,
            native_notifications::node_request_notification_permission,
            native_notifications::node_set_autostart,
            node_commands::node_test_notification,
            node_commands::node_create_pairing_code,
            node_commands::node_set_reminders,
            node_commands::node_remove,
            node_domains::node_reminders,
            node_domains::node_places,
            node_domains::node_location,
            node_domains::node_geofences,
            node_domains::node_settings_open,
            node_domains::node_hide_keyboard,
            node_domains::node_consume_reminder_open,
            install_android_update
        ])
        .build(context)
        .expect("error while building Aegis")
        .run(|app, event| {
            use tauri::Manager;
            #[cfg(not(target_os = "android"))]
            if matches!(event, tauri::RunEvent::Resumed) {
                let handle = app.clone();
                tauri::async_runtime::spawn(async move {
                    handle
                        .state::<node_commands::NodeRuntime>()
                        .lifecycle(true)
                        .await;
                });
            }
            if matches!(
                event,
                tauri::RunEvent::ExitRequested { .. } | tauri::RunEvent::Exit
            ) {
                app.state::<node_commands::NodeRuntime>().shutdown();
            }
        });
}

// Exercise the exact dependency error check without requiring an Android JNI runtime.
#[cfg(test)]
#[path = "../vendor/android-native-keyring-store/src/commit_result.rs"]
mod android_commit_result;
