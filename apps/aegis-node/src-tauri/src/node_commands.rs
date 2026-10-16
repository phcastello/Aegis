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
    foreground: std::sync::atomic::AtomicBool,
    lifecycle_gate: tokio::sync::Mutex<()>,
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
            foreground: std::sync::atomic::AtomicBool::new(true),
            lifecycle_gate: tokio::sync::Mutex::new(()),
            #[cfg(target_os = "android")]
            app,
        }
    }
    pub(crate) async fn domain_call(
        &self,
        method: reqwest::Method,
        path: String,
        payload: serde_json::Value,
    ) -> Result<serde_json::Value, String> {
        let guard = self.identity.lock().await;
        guard
            .as_ref()
            .map_err(Clone::clone)?
            .domain_call(method, path, payload)
            .await
    }
    pub(crate) async fn domain_action_call(
        &self,
        path: String,
        payload: serde_json::Value,
        expected_scope: &str,
    ) -> Result<serde_json::Value, (String, bool)> {
        let guard = self.identity.lock().await;
        let identity = guard.as_ref().map_err(|e| (e.clone(), false))?;
        if identity
            .cached_node()
            .map_err(|e| (e, false))?
            .as_ref()
            .map(|n| n.id.as_str())
            != Some(expected_scope)
        {
            return Err((
                "A identidade mudou. Confira o lembrete antes de tentar novamente.".into(),
                false,
            ));
        }
        identity
            .domain_call_kind(reqwest::Method::POST, path, payload)
            .await
    }
    pub(crate) async fn scope_id(&self) -> Result<Option<String>, String> {
        let guard = self.identity.lock().await;
        Ok(guard
            .as_ref()
            .map_err(Clone::clone)?
            .cached_node()?
            .map(|node| node.id))
    }
    pub(crate) async fn has_identity(&self) -> bool {
        self.identity
            .lock()
            .await
            .as_ref()
            .ok()
            .and_then(|i| i.transport_credential().ok().flatten())
            .is_some()
    }
    pub fn shutdown(&self) {
        self.cancel.cancel();
        self.transport.shutdown();
    }
    pub async fn start(&self) {
        if cfg!(target_os = "android") && !self.foreground.load(std::sync::atomic::Ordering::SeqCst)
        {
            return;
        }
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
    pub async fn lifecycle(&self, foreground: bool) {
        let _gate = self.lifecycle_gate.lock().await;
        let previous = self
            .foreground
            .swap(foreground, std::sync::atomic::Ordering::SeqCst);
        if !foreground && cfg!(target_os = "android") {
            self.transport.stop().await;
        } else if foreground {
            self.start().await;
            if self.transport.state().transport_state != "online" && !previous {
                self.transport.reconnect();
            }
            #[cfg(target_os = "android")]
            self.sync_push().await;
        }
    }
    #[cfg(target_os = "android")]
    pub async fn sync_push(&self) {
        let guard = self.identity.lock().await;
        let Ok(identity) = guard.as_ref() else { return };
        let Ok(cached) = identity.cached_node() else {
            return;
        };
        let node = cached
            .as_ref()
            .filter(|n| n.enabled && n.revoked_at.is_none());
        if !crate::native_notifications::bind(&self.app, node.map(|n| n.id.as_str())).await {
            return;
        }
    }
    #[cfg(target_os = "android")]
    async fn reconcile_identity_binding(&self, before: Option<String>, after: Option<String>) {
        use crate::node_identity::NativeBindingTransition;
        use std::sync::atomic::Ordering::SeqCst;
        let transition = NativeBindingTransition::between(before.as_deref(), after.as_deref());
        if !transition.changed {
            return;
        }
        self.sync_push().await;
        if transition.should_start_transport(self.foreground.load(SeqCst)) {
            self.start().await;
            // start is idempotent; only wake a disconnected foreground channel.
            // Recheck lifecycle after the await so background never starts a retry.
            if transition.should_reconnect_transport(
                self.foreground.load(SeqCst),
                self.transport.state().transport_state == "online",
            ) {
                self.transport.reconnect();
            }
        }
    }
}
#[tauri::command]
pub async fn node_status(runtime: tauri::State<'_, NodeRuntime>) -> Result<IdentityStatus, String> {
    let guard = runtime.identity.lock().await;
    let identity = guard.as_ref().map_err(Clone::clone)?;
    #[cfg(target_os = "android")]
    let before = identity.cached_operational_node_id()?;
    let result = identity.status().await;
    #[cfg(target_os = "android")]
    let after = identity.cached_operational_node_id()?;
    drop(guard); // sync_push acquires the same identity lock before calling JNI.
    #[cfg(target_os = "android")]
    runtime.reconcile_identity_binding(before, after).await;
    result
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
    let identity = guard.as_ref().map_err(Clone::clone)?;
    #[cfg(target_os = "android")]
    let before = identity.cached_operational_node_id()?;
    let result = identity
        .modify(id, if enabled { "enable" } else { "disable" }, None)
        .await;
    #[cfg(target_os = "android")]
    let after = identity.cached_operational_node_id()?;
    drop(guard);
    #[cfg(target_os = "android")]
    runtime.reconcile_identity_binding(before, after).await;
    result
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
    let identity = guard.as_ref().map_err(Clone::clone)?;
    #[cfg(target_os = "android")]
    let before = identity.cached_operational_node_id()?;
    let result = identity.modify(id, "revoke", None).await;
    #[cfg(target_os = "android")]
    let after = identity.cached_operational_node_id()?;
    drop(guard);
    #[cfg(target_os = "android")]
    runtime.reconcile_identity_binding(before, after).await;
    result
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

#[tauri::command]
pub async fn node_set_reminders(
    runtime: tauri::State<'_, NodeRuntime>,
    id: String,
    receive_reminders: bool,
) -> Result<serde_json::Value, String> {
    let id = uuid::Uuid::parse_str(&id).map_err(|_| "Dispositivo inválido.")?;
    runtime
        .domain_call(
            reqwest::Method::PUT,
            format!("/{id}/reminders"),
            serde_json::json!({"receiveReminders":receive_reminders}),
        )
        .await
}
#[tauri::command]
pub async fn node_remove(
    runtime: tauri::State<'_, NodeRuntime>,
    id: String,
) -> Result<serde_json::Value, String> {
    let id = uuid::Uuid::parse_str(&id).map_err(|_| "Dispositivo inválido.")?;
    runtime
        .domain_call(
            reqwest::Method::DELETE,
            format!("/{id}"),
            serde_json::Value::Null,
        )
        .await
}
