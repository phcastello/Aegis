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
    async fn rejected(&self, credential: &str, code: &'static str) -> Result<bool, ()> {
        self.0
            .lock()
            .await
            .as_ref()
            .map_err(|_| ())?
            .transport_rejected(credential, code)
            .map_err(|_| ())
    }
}
pub struct NodeRuntime {
    identity: Arc<tokio::sync::Mutex<Result<NativeIdentity, String>>>,
    pub transport: NodeTransportManager,
    config: Option<TransportConfig>,
    pub cancel: tokio_util::sync::CancellationToken,
    #[cfg(target_os = "android")]
    app: tauri::AppHandle,
}
impl NodeRuntime {
    pub fn initialize(app: tauri::AppHandle) -> Self {
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
        let mut config = crate::config::configured_endpoint()
            .ok()
            .and_then(|endpoint| {
                TransportConfig::new(
                    &endpoint.base_url(),
                    env!("CARGO_PKG_VERSION"),
                    cfg!(debug_assertions) || cfg!(feature = "android-framework-fixture"),
                )
                .ok()
            });
        if let Some(config) = config.as_mut() {
            config.notifications = Some(crate::native_notifications::executor(&app));
        }
        Self {
            identity: Arc::new(tokio::sync::Mutex::new(identity)),
            transport: NodeTransportManager::default(),
            config,
            cancel: tokio_util::sync::CancellationToken::new(),
            #[cfg(target_os = "android")]
            app,
        }
    }
    pub fn shutdown(&self) {
        self.cancel.cancel();
        self.transport.shutdown();
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
        #[cfg(target_os = "android")]
        self.sync_push().await;
    }
    #[cfg(target_os = "android")]
    pub async fn sync_push(&self) {
        let guard = self.identity.lock().await;
        let Ok(identity) = guard.as_ref() else { return };
        let Ok(status) = identity.status().await else {
            return;
        };
        let node = status
            .node
            .as_ref()
            .filter(|n| n.enabled && n.revoked_at.is_none() && status.state == "paired");
        if !crate::native_notifications::bind(&self.app, node.map(|n| n.id.as_str())).await {
            return;
        }
        if node.is_some() {
            if let Some(push) = crate::native_notifications::push_state(&self.app).await {
                if push.configured {
                    if let Some(token) = push.token {
                        let _ = identity.register_push(&token).await;
                    }
                }
            }
        }
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
    #[cfg(target_os = "android")]
    runtime.sync_push().await;
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
pub async fn node_resolve_target(
    runtime: tauri::State<'_, NodeRuntime>,
    request: crate::node_capabilities::TargetRequest,
) -> Result<crate::node_capabilities::TargetResult, String> {
    let guard = runtime.identity.lock().await;
    guard.as_ref().map_err(Clone::clone)?.resolve(request).await
}
#[tauri::command]
pub async fn node_set_target_priority(
    runtime: tauri::State<'_, NodeRuntime>,
    id: String,
    priority: i32,
) -> Result<NodeView, String> {
    let guard = runtime.identity.lock().await;
    guard
        .as_ref()
        .map_err(Clone::clone)?
        .set_priority(id, priority)
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

#[tauri::command]
pub async fn node_test_notification(
    runtime: tauri::State<'_, NodeRuntime>,
    id: String,
) -> Result<serde_json::Value, String> {
    let guard = runtime.identity.lock().await;
    guard
        .as_ref()
        .map_err(Clone::clone)?
        .test_notification(id)
        .await
}
