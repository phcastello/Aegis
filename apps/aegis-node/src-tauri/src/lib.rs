mod config;
mod health;
mod platform;
mod updates;

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
#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let builder = tauri::Builder::default().plugin(
        tauri_plugin_opener::Builder::new()
            .open_js_links_on_click(false)
            .build(),
    );
    #[cfg(windows)]
    let builder = builder
        .plugin(tauri_plugin_updater::Builder::new().build())
        .plugin(tauri_plugin_process::init());
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
            use tauri::webview::{PermissionKind, PermissionResponse};
            let config = &app.config().app.windows[0];
            let dev_origin = app.config().build.dev_url.as_ref().map(|url| url.origin());
            tauri::WebviewWindowBuilder::from_config(app, config)?
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
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            runtime_info,
            check_backend,
            check_android_update
        ])
        .run(context)
        .expect("error while running Aegis");
}
