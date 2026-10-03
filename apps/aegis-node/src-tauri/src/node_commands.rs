use crate::node_identity::{HttpApi, Identity, IdentityStatus, NodeView, PairingCode};
use crate::node_vault::OsVault;
type NativeIdentity = Identity<OsVault, HttpApi>;
pub struct NodeRuntime(tokio::sync::Mutex<Result<NativeIdentity, String>>);
impl NodeRuntime {
    pub fn initialize() -> Self {
        let identity = (|| {
            let origin = crate::config::configured_endpoint()?.base_url();
            let vault = OsVault::initialize()?;
            let api = HttpApi::new(origin.clone())?;
            let platform = if cfg!(target_os = "android") {
                "android"
            } else if cfg!(windows) {
                "windows"
            } else {
                "unsupported"
            };
            Ok(Identity::new(
                vault,
                api,
                origin,
                platform.into(),
                env!("CARGO_PKG_VERSION").into(),
            ))
        })();
        Self(tokio::sync::Mutex::new(identity))
    }
}
#[tauri::command]
pub async fn node_status(runtime: tauri::State<'_, NodeRuntime>) -> Result<IdentityStatus, String> {
    let guard = runtime.0.lock().await;
    guard.as_ref().map_err(Clone::clone)?.status().await
}
#[tauri::command]
pub async fn node_pair(
    runtime: tauri::State<'_, NodeRuntime>,
    name: String,
    code: String,
) -> Result<IdentityStatus, String> {
    let guard = runtime.0.lock().await;
    guard.as_ref().map_err(Clone::clone)?.pair(name, code).await
}
#[tauri::command]
pub async fn node_list(runtime: tauri::State<'_, NodeRuntime>) -> Result<Vec<NodeView>, String> {
    let guard = runtime.0.lock().await;
    guard.as_ref().map_err(Clone::clone)?.list().await
}
#[tauri::command]
pub async fn node_rename(
    runtime: tauri::State<'_, NodeRuntime>,
    id: String,
    name: String,
) -> Result<NodeView, String> {
    let guard = runtime.0.lock().await;
    guard
        .as_ref()
        .map_err(Clone::clone)?
        .modify(id, "rename", Some(name))
        .await
}
#[tauri::command]
pub async fn node_set_enabled(
    runtime: tauri::State<'_, NodeRuntime>,
    id: String,
    enabled: bool,
) -> Result<NodeView, String> {
    let guard = runtime.0.lock().await;
    guard
        .as_ref()
        .map_err(Clone::clone)?
        .modify(id, if enabled { "enable" } else { "disable" }, None)
        .await
}
#[tauri::command]
pub async fn node_revoke(
    runtime: tauri::State<'_, NodeRuntime>,
    id: String,
) -> Result<NodeView, String> {
    let guard = runtime.0.lock().await;
    guard
        .as_ref()
        .map_err(Clone::clone)?
        .modify(id, "revoke", None)
        .await
}
#[tauri::command]
pub async fn node_create_pairing_code(
    runtime: tauri::State<'_, NodeRuntime>,
) -> Result<PairingCode, String> {
    let guard = runtime.0.lock().await;
    guard.as_ref().map_err(Clone::clone)?.create_code().await
}
