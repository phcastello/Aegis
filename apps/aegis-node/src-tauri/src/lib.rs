mod config;
mod health;
mod platform;

#[cfg(feature = "native-runtime")]
#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct RuntimeInfo {
    platform: platform::Platform,
    backend_url: Option<String>,
}

#[cfg(feature = "native-runtime")]
#[tauri::command]
fn runtime_info() -> RuntimeInfo {
    RuntimeInfo {
        platform: platform::current(),
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
#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![runtime_info, check_backend])
        .run(tauri::generate_context!())
        .expect("error while running Aegis");
}
