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
mod platform;
mod updates;
#[cfg(all(feature = "native-runtime", windows))]
mod windows_notifications;

#[cfg(feature = "native-runtime")]
#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct RuntimeInfo {
    platform: platform::Platform,
    backend_url: Option<String>,
    version: String,
}

#[cfg(feature = "native-runtime")]
#[tauri::command]
fn runtime_info() -> RuntimeInfo {
    RuntimeInfo {
        platform: platform::current(),
        version: env!("CARGO_PKG_VERSION").to_owned(),
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
            .ok_or("Atualização não disponível.")?;
        let result: Result<serde_json::Value, _> = app
            .state::<native_notifications::AndroidBridge>()
            .0
            .run_mobile_plugin_async("installUpdate", update)
            .await;
        return result.map_err(|_| "Não foi possível abrir o instalador verificado.".into());
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
        if args
            .iter()
            .any(|a| a.starts_with("aegis-reminder://action/"))
        {
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
        .plugin(tauri_plugin_updater::Builder::new().build())
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
                        && std::env::args()
                            .any(|a| a == "--autostart" || a == "--reminder-action")),
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
                windows_notifications::initialize(app.handle());
                if std::env::args().any(|a| a == "--autostart" || a == "--reminder-action") {
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
            if matches!(event, tauri::WindowEvent::Suspended) {
                let handle = window.app_handle().clone();
                tauri::async_runtime::spawn(async move {
                    handle
                        .state::<node_commands::NodeRuntime>()
                        .lifecycle(false)
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
            node_domains::node_consume_reminder_open,
            install_android_update
        ])
        .build(context)
        .expect("error while building Aegis")
        .run(|app, event| {
            use tauri::Manager;
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
