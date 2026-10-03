use crate::node_identity::{HttpApi, Identity, IdentityStatus, NodeView, PairingCode};
use crate::node_vault::OsVault;
type NativeIdentity = Identity<OsVault, HttpApi>;
use crate::node_transport::{
    CredentialSource, NodeTransportManager, TransportConfig, TransportStatus,
};
use std::sync::Arc;
struct NativeSource(Arc<tokio::sync::Mutex<Result<NativeIdentity, String>>>);
impl CredentialSource for NativeSource {
    async fn credential(&self) -> Result<Option<String>, ()> {
        self.0
            .lock()
            .await
            .as_ref()
            .map_err(|_| ())?
            .transport_credential()
            .map_err(|_| ())
    }
    async fn rejected(&self, code: &'static str) -> Result<(), ()> {
        self.0
            .lock()
            .await
            .as_ref()
            .map_err(|_| ())?
            .transport_rejected(code)
            .map_err(|_| ())
    }
}
pub struct NodeRuntime {
    identity: Arc<tokio::sync::Mutex<Result<NativeIdentity, String>>>,
    pub transport: NodeTransportManager,
    config: Option<TransportConfig>,
}
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
        let config = crate::config::configured_endpoint()
            .ok()
            .and_then(|endpoint| {
                TransportConfig::new(
                    &endpoint.base_url(),
                    env!("CARGO_PKG_VERSION"),
                    cfg!(debug_assertions),
                )
                .ok()
            });
        Self {
            identity: Arc::new(tokio::sync::Mutex::new(identity)),
            transport: NodeTransportManager::default(),
            config,
        }
    }
    pub async fn start(&self) {
        if let Some(config) = &self.config {
            self.transport.start(
                Arc::new(NativeSource(self.identity.clone())),
                config.clone(),
            );
        }
    }
    pub async fn initialize_transport(&self) {
        // Resume any pending Stage 03 checkpoint before enabling the native loop.
        if let Ok(identity) = self.identity.lock().await.as_ref() {
            let _ = identity.status().await;
        }
        self.start().await;
    }
}
#[tauri::command]
pub async fn node_status(runtime: tauri::State<'_, NodeRuntime>) -> Result<IdentityStatus, String> {
    let guard = runtime.identity.lock().await;
    guard.as_ref().map_err(Clone::clone)?.status().await
}
#[tauri::command]
pub async fn node_pair(
    runtime: tauri::State<'_, NodeRuntime>,
    name: String,
    code: String,
) -> Result<IdentityStatus, String> {
    let guard = runtime.identity.lock().await;
    let result = guard.as_ref().map_err(Clone::clone)?.pair(name, code).await;
    drop(guard);
    runtime.start().await;
    runtime.transport.reconnect();
    result
}
#[tauri::command]
pub async fn node_list(runtime: tauri::State<'_, NodeRuntime>) -> Result<Vec<NodeView>, String> {
    let guard = runtime.identity.lock().await;
    guard.as_ref().map_err(Clone::clone)?.list().await
}
#[tauri::command]
pub async fn node_rename(
    runtime: tauri::State<'_, NodeRuntime>,
    id: String,
    name: String,
) -> Result<NodeView, String> {
    let guard = runtime.identity.lock().await;
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
    let guard = runtime.identity.lock().await;
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
    let guard = runtime.identity.lock().await;
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
    let guard = runtime.identity.lock().await;
    guard.as_ref().map_err(Clone::clone)?.create_code().await
}

#[tauri::command]
pub fn node_transport_status(runtime: tauri::State<'_, NodeRuntime>) -> TransportStatus {
    runtime.transport.state()
}
#[tauri::command]
pub fn node_transport_reconnect(runtime: tauri::State<'_, NodeRuntime>) {
    runtime.transport.reconnect();
}
