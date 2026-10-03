//! Node secrets stay here and in the OS credential store. Public IPC DTOs never contain them.
use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine};
use serde::{Deserialize, Serialize};

#[derive(Clone, Deserialize, Serialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
pub struct NodeView {
    pub id: String,
    pub name: String,
    pub platform: String,
    pub enabled: bool,
    pub app_version: String,
    pub protocol_version: u32,
    pub paired_at: String,
    pub created_at: String,
    pub updated_at: Option<String>,
    pub revoked_at: Option<String>,
}
#[derive(Clone, Serialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
pub struct IdentityStatus {
    pub state: &'static str,
    pub node: Option<NodeView>,
    pub error: Option<String>,
}
impl IdentityStatus {
    fn new(state: &'static str, node: Option<NodeView>) -> Self {
        Self {
            state,
            node,
            error: None,
        }
    }
}
#[derive(Deserialize, Serialize, Clone)]
#[serde(rename_all = "camelCase")]
pub(crate) struct Pending {
    attempt_id: String,
    code: String,
    recovery_key: String,
    name: String,
    platform: String,
    app_version: String,
    protocol_version: u32,
}
#[derive(Default, Deserialize, Serialize, Clone)]
pub(crate) struct Record {
    origin: String,
    pending: Option<Pending>,
    credential: Option<String>,
    node: Option<NodeView>,
    revoked: bool,
}
pub(crate) trait Vault {
    fn load(&self) -> Result<Option<Record>, String>;
    fn save(&self, record: &Record) -> Result<(), String>;
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct Receipt {
    pub credential: String,
}
#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PairingCode {
    pub code: String,
    pub expires_at: String,
}
#[derive(Clone)]
pub(crate) struct ApiError {
    pub code: String,
    pub message: String,
}
pub(crate) trait Api {
    async fn pair(&self, request: &Pending) -> Result<Receipt, ApiError>;
    async fn finalize(&self, attempt: &str, credential: &str) -> Result<NodeView, ApiError>;
    async fn me(&self, credential: &str) -> Result<NodeView, ApiError>;
    async fn list(&self, credential: &str) -> Result<Vec<NodeView>, ApiError>;
    async fn modify(
        &self,
        credential: &str,
        id: &str,
        action: &str,
        name: Option<&str>,
    ) -> Result<NodeView, ApiError>;
    async fn create_code(&self, credential: &str) -> Result<PairingCode, ApiError>;
}
pub(crate) struct Identity<V: Vault, A: Api> {
    vault: V,
    api: A,
    origin: String,
    platform: String,
    version: String,
}
impl<V: Vault, A: Api> Identity<V, A> {
    pub fn new(vault: V, api: A, origin: String, platform: String, version: String) -> Self {
        Self {
            vault,
            api,
            origin,
            platform,
            version,
        }
    }
    fn load(&self) -> Result<Record, String> {
        let record = self.vault.load()?.unwrap_or_default();
        if !record.origin.is_empty() && record.origin != self.origin {
            return Err("A identidade pertence a outro backend. Restaure a configuração original; a credencial não será enviada.".into());
        }
        Ok(record)
    }
    fn failure(&self, record: &mut Record, error: ApiError) -> Result<IdentityStatus, String> {
        let state = match error.code.as_str() {
            "node_disabled" => "disabled",
            "node_revoked" | "node_authentication_required" => {
                // Replace the OS entry: do not keep the rejected credential in a local cache.
                *record = Record {
                    origin: self.origin.clone(),
                    revoked: true,
                    ..Record::default()
                };
                self.vault.save(record)?;
                "revoked"
            }
            "pairing_unavailable" | "invalid_node" if record.pending.is_some() => {
                *record = Record {
                    origin: self.origin.clone(),
                    ..Record::default()
                };
                self.vault.save(record)?;
                "pairingError"
            }
            _ => {
                if record.node.is_some() {
                    "paired"
                } else {
                    "pairingError"
                }
            }
        };
        Ok(IdentityStatus {
            state,
            node: record.node.clone(),
            error: Some(error.message),
        })
    }
    async fn resume(&self, mut record: Record) -> Result<IdentityStatus, String> {
        if let Some(pending) = record.pending.clone() {
            if record.credential.is_none() {
                // This also protects retries after a failed write became visible in OS memory.
                self.vault.save(&record)?;
                match self.api.pair(&pending).await {
                    Ok(receipt) => {
                        record.credential = Some(receipt.credential);
                    }
                    Err(error) => return self.failure(&mut record, error),
                }
            }
            // Android preferences may be readable in memory after a failed disk commit.
            // Require a fresh successful durable write even when resume already sees the credential.
            self.vault.save(&record)?;
            match self
                .api
                .finalize(&pending.attempt_id, record.credential.as_deref().unwrap())
                .await
            {
                Ok(node) => {
                    record.node = Some(node);
                    record.pending = None;
                    self.vault.save(&record)?;
                }
                Err(error) => return self.failure(&mut record, error),
            }
        }
        if let Some(credential) = record.credential.as_deref() {
            match self.api.me(credential).await {
                Ok(node) => {
                    record.node = Some(node);
                    self.vault.save(&record)?;
                    Ok(IdentityStatus::new("paired", record.node))
                }
                Err(error) => self.failure(&mut record, error),
            }
        } else {
            Ok(IdentityStatus::new(
                if record.revoked {
                    "revoked"
                } else {
                    "unpaired"
                },
                None,
            ))
        }
    }
    pub async fn status(&self) -> Result<IdentityStatus, String> {
        self.resume(self.load()?).await
    }
    pub async fn pair(&self, name: String, code: String) -> Result<IdentityStatus, String> {
        let mut record = self.load()?;
        if record.credential.is_some() || record.pending.is_some() {
            return self.resume(record).await;
        }
        if name.trim().is_empty()
            || name.chars().count() > 100
            || name.chars().any(char::is_control)
        {
            return Err("Informe um nome de até 100 caracteres.".into());
        }
        let mut key = [0u8; 32];
        getrandom::fill(&mut key).map_err(|_| "Não foi possível gerar uma tentativa segura.")?;
        record = Record {
            origin: self.origin.clone(),
            pending: Some(Pending {
                attempt_id: uuid::Uuid::new_v4().to_string(),
                recovery_key: URL_SAFE_NO_PAD.encode(key),
                code,
                name,
                platform: self.platform.clone(),
                app_version: self.version.clone(),
                protocol_version: 1,
            }),
            ..Record::default()
        };
        self.resume(record).await // Resume durably saves the checkpoint before any network operation.
    }
    async fn credential(&self) -> Result<String, String> {
        let status = self.status().await?;
        if status.state != "paired" || status.error.is_some() {
            return Err(status
                .error
                .unwrap_or_else(|| "Este dispositivo precisa estar pareado e ativo.".into()));
        }
        self.load()?
            .credential
            .ok_or_else(|| "Identidade indisponível.".into())
    }
    fn operation_error(&self, error: ApiError) -> String {
        match self
            .load()
            .and_then(|mut record| self.failure(&mut record, error.clone()))
        {
            Ok(_) => error.message,
            Err(message) => message,
        }
    }
    pub async fn list(&self) -> Result<Vec<NodeView>, String> {
        self.api
            .list(&self.credential().await?)
            .await
            .map_err(|e| self.operation_error(e))
    }
    pub async fn modify(
        &self,
        id: String,
        action: &str,
        name: Option<String>,
    ) -> Result<NodeView, String> {
        uuid::Uuid::parse_str(&id).map_err(|_| "Node inválido.")?;
        let node = self
            .api
            .modify(&self.credential().await?, &id, action, name.as_deref())
            .await
            .map_err(|e| self.operation_error(e))?;
        // Self-revocation is reflected immediately; management by another Node is observed on next interaction.
        let _ = self.status().await?;
        Ok(node)
    }
    pub async fn create_code(&self) -> Result<PairingCode, String> {
        self.api
            .create_code(&self.credential().await?)
            .await
            .map_err(|e| self.operation_error(e))
    }
}

#[cfg(feature = "native-runtime")]
pub(crate) struct HttpApi {
    client: reqwest::Client,
    origin: String,
}
#[cfg(feature = "native-runtime")]
impl HttpApi {
    pub fn new(origin: String) -> Result<Self, String> {
        Ok(Self {
            client: reqwest::Client::builder()
                .redirect(reqwest::redirect::Policy::none())
                .timeout(std::time::Duration::from_secs(10))
                .build()
                .map_err(|_| "Transporte de Node indisponível.")?,
            origin,
        })
    }
    async fn call<T: serde::de::DeserializeOwned>(
        &self,
        method: reqwest::Method,
        path: &str,
        credential: Option<&str>,
        body: Option<serde_json::Value>,
    ) -> Result<T, ApiError> {
        let unavailable = || {
            ApiError { code: "network_unavailable".into(), message: "Não foi possível consultar o registro de Nodes. Verifique o backend e tente novamente.".into() }
        };
        let mut request = self
            .client
            .request(method, format!("{}/api/nodes{path}", self.origin));
        if let Some(secret) = credential {
            request = request.header("Authorization", format!("AegisNode {secret}"));
        }
        if let Some(json) = body {
            request = request.json(&json);
        }
        let mut response = request.send().await.map_err(|_| unavailable())?;
        let success = response.status().is_success();
        let mut bytes = Vec::new();
        while let Some(chunk) = response.chunk().await.map_err(|_| unavailable())? {
            if bytes.len() + chunk.len() > 2 * 1024 * 1024 {
                return Err(unavailable());
            }
            bytes.extend_from_slice(&chunk);
        }
        if !success {
            let json: serde_json::Value = serde_json::from_slice(&bytes).unwrap_or_default();
            return Err(ApiError {
                code: json["code"]
                    .as_str()
                    .unwrap_or("node_request_failed")
                    .into(),
                message: json["error"]
                    .as_str()
                    .unwrap_or("Operação de Node recusada pelo backend.")
                    .into(),
            });
        }
        serde_json::from_slice(&bytes).map_err(|_| unavailable())
    }
}
#[cfg(feature = "native-runtime")]
impl Api for HttpApi {
    async fn pair(&self, request: &Pending) -> Result<Receipt, ApiError> {
        self.call(
            reqwest::Method::POST,
            "/pair",
            None,
            Some(serde_json::to_value(request).unwrap()),
        )
        .await
    }
    async fn finalize(&self, attempt: &str, credential: &str) -> Result<NodeView, ApiError> {
        self.call(
            reqwest::Method::POST,
            "/pair/finalize",
            None,
            Some(serde_json::json!({"attemptId":attempt,"credential":credential})),
        )
        .await
    }
    async fn me(&self, credential: &str) -> Result<NodeView, ApiError> {
        self.call(reqwest::Method::GET, "/me", Some(credential), None)
            .await
    }
    async fn list(&self, credential: &str) -> Result<Vec<NodeView>, ApiError> {
        self.call(reqwest::Method::GET, "", Some(credential), None)
            .await
    }
    async fn modify(
        &self,
        credential: &str,
        id: &str,
        action: &str,
        name: Option<&str>,
    ) -> Result<NodeView, ApiError> {
        let path = if action == "rename" {
            format!("/{id}")
        } else {
            format!("/{id}/{action}")
        };
        self.call(
            if action == "rename" {
                reqwest::Method::PATCH
            } else {
                reqwest::Method::POST
            },
            &path,
            Some(credential),
            name.map(|name| serde_json::json!({"name":name})),
        )
        .await
    }
    async fn create_code(&self, credential: &str) -> Result<PairingCode, ApiError> {
        self.call(
            reqwest::Method::POST,
            "/pairing-codes",
            Some(credential),
            None,
        )
        .await
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{Arc, Mutex};
    #[derive(Default)]
    struct Storage {
        record: Option<Record>,
        writes: u32,
        fail_at: Option<u32>,
        fail_from: Option<u32>,
        memory_on_failure: bool,
    }
    #[derive(Clone, Default)]
    struct MemoryVault(Arc<Mutex<Storage>>);
    impl Vault for MemoryVault {
        fn load(&self) -> Result<Option<Record>, String> {
            Ok(self.0.lock().unwrap().record.clone())
        }
        fn save(&self, record: &Record) -> Result<(), String> {
            let mut storage = self.0.lock().unwrap();
            storage.writes += 1;
            if storage.fail_at == Some(storage.writes)
                || storage
                    .fail_from
                    .is_some_and(|start| storage.writes >= start)
            {
                if storage.memory_on_failure {
                    storage.record = Some(record.clone());
                }
                return Err("secure storage unavailable".into());
            }
            storage.record = Some(record.clone());
            Ok(())
        }
    }
    #[derive(Default)]
    struct Server {
        node: Option<NodeView>,
        pairs: u32,
        finalize: u32,
        error: Option<ApiError>,
        lose_finalize: bool,
    }
    #[derive(Clone, Default)]
    struct FakeApi(Arc<Mutex<Server>>);
    fn node() -> NodeView {
        NodeView {
            id: "e45a9713-2b44-4455-8916-06ca41d581e7".into(),
            name: "PC Pedro".into(),
            platform: "windows".into(),
            enabled: true,
            app_version: "0.7.0-stage.4".into(),
            protocol_version: 1,
            paired_at: "2026-10-02T00:00:00Z".into(),
            created_at: "2026-10-02T00:00:00Z".into(),
            updated_at: None,
            revoked_at: None,
        }
    }
    impl Api for FakeApi {
        async fn pair(&self, _: &Pending) -> Result<Receipt, ApiError> {
            let mut state = self.0.lock().unwrap();
            state.pairs += 1;
            if let Some(error) = &state.error {
                return Err(error.clone());
            }
            Ok(Receipt {
                credential: "test-secret-never-ipc".into(),
            })
        }
        async fn finalize(&self, _: &str, _: &str) -> Result<NodeView, ApiError> {
            let mut state = self.0.lock().unwrap();
            state.finalize += 1;
            if let Some(error) = &state.error {
                return Err(error.clone());
            }
            state.node.get_or_insert_with(node);
            if state.lose_finalize {
                state.lose_finalize = false;
                return Err(ApiError {
                    code: "network_unavailable".into(),
                    message: "response lost".into(),
                });
            }
            Ok(state.node.clone().unwrap())
        }
        async fn me(&self, _: &str) -> Result<NodeView, ApiError> {
            let state = self.0.lock().unwrap();
            if let Some(error) = &state.error {
                return Err(error.clone());
            }
            Ok(state.node.clone().unwrap())
        }
        async fn list(&self, _: &str) -> Result<Vec<NodeView>, ApiError> {
            Ok(vec![self.0.lock().unwrap().node.clone().unwrap()])
        }
        async fn modify(
            &self,
            _: &str,
            _: &str,
            action: &str,
            name: Option<&str>,
        ) -> Result<NodeView, ApiError> {
            let mut state = self.0.lock().unwrap();
            let node = state.node.as_mut().unwrap();
            if action == "rename" {
                node.name = name.unwrap().into();
            }
            Ok(node.clone())
        }
        async fn create_code(&self, _: &str) -> Result<PairingCode, ApiError> {
            Ok(PairingCode {
                code: "single-use-code".into(),
                expires_at: "2026-10-02T00:10:00Z".into(),
            })
        }
    }
    fn client(vault: MemoryVault, api: FakeApi) -> Identity<MemoryVault, FakeApi> {
        Identity::new(
            vault,
            api,
            "https://aegis.example".into(),
            "windows".into(),
            "0.7.0-stage.4".into(),
        )
    }
    #[tokio::test]
    async fn unpaired_does_not_call_server() {
        let api = FakeApi::default();
        assert_eq!(
            client(MemoryVault::default(), api.clone())
                .status()
                .await
                .unwrap()
                .state,
            "unpaired"
        );
        assert_eq!(api.0.lock().unwrap().pairs, 0);
    }
    #[tokio::test]
    async fn restart_and_update_keep_the_same_identity_without_repairing() {
        let vault = MemoryVault::default();
        let api = FakeApi::default();
        let initial = client(vault.clone(), api.clone())
            .pair("PC".into(), "code".into())
            .await
            .unwrap();
        let updated = Identity::new(
            vault,
            api.clone(),
            "https://aegis.example".into(),
            "windows".into(),
            "0.7.0-stage.5".into(),
        );
        for _ in 0..3 {
            assert_eq!(
                updated.status().await.unwrap().node.unwrap().id,
                initial.node.as_ref().unwrap().id
            );
        }
        assert_eq!(api.0.lock().unwrap().pairs, 1);
        assert!(!serde_json::to_string(&initial)
            .unwrap()
            .contains("test-secret"));
    }
    #[tokio::test]
    async fn first_storage_failure_prevents_backend_attempt() {
        let vault = MemoryVault::default();
        vault.0.lock().unwrap().fail_at = Some(1);
        let api = FakeApi::default();
        assert!(client(vault, api.clone())
            .pair("PC".into(), "code".into())
            .await
            .is_err());
        assert_eq!(api.0.lock().unwrap().pairs, 0);
    }
    #[tokio::test]
    async fn failed_initial_checkpoint_visible_in_memory_cannot_start_pairing_on_retry() {
        let vault = MemoryVault::default();
        {
            let mut storage = vault.0.lock().unwrap();
            storage.fail_from = Some(1);
            storage.memory_on_failure = true;
        }
        let api = FakeApi::default();
        let identity = client(vault.clone(), api.clone());
        assert!(identity.pair("PC".into(), "code".into()).await.is_err());
        assert!(vault.load().unwrap().unwrap().pending.is_some());
        assert!(identity.status().await.is_err());
        assert_eq!(api.0.lock().unwrap().pairs, 0);
        vault.0.lock().unwrap().fail_from = None;
        assert_eq!(identity.status().await.unwrap().state, "paired");
        assert_eq!(api.0.lock().unwrap().pairs, 1);
    }
    #[tokio::test]
    async fn credential_storage_failure_resumes_same_attempt_after_restart() {
        let vault = MemoryVault::default();
        vault.0.lock().unwrap().fail_at = Some(2);
        let api = FakeApi::default();
        assert!(client(vault.clone(), api.clone())
            .pair("PC".into(), "code".into())
            .await
            .is_err());
        let attempt = vault
            .0
            .lock()
            .unwrap()
            .record
            .as_ref()
            .unwrap()
            .pending
            .as_ref()
            .unwrap()
            .attempt_id
            .clone();
        assert_eq!(api.0.lock().unwrap().finalize, 0);
        vault.0.lock().unwrap().fail_at = None;
        assert_eq!(
            client(vault.clone(), api).status().await.unwrap().state,
            "paired"
        );
        assert!(vault
            .0
            .lock()
            .unwrap()
            .record
            .as_ref()
            .unwrap()
            .pending
            .is_none());
        assert!(!attempt.is_empty());
    }
    #[tokio::test]
    async fn readable_memory_after_failed_commit_never_confirms_durable_storage() {
        let vault = MemoryVault::default();
        {
            let mut storage = vault.0.lock().unwrap();
            storage.fail_from = Some(2);
            storage.memory_on_failure = true;
        }
        let api = FakeApi::default();
        let identity = client(vault.clone(), api.clone());
        assert!(identity.pair("PC".into(), "code".into()).await.is_err());
        assert!(vault.load().unwrap().unwrap().credential.is_some()); // In-memory readback is not durability.
        assert!(identity.status().await.is_err());
        assert_eq!(api.0.lock().unwrap().finalize, 0);
        vault.0.lock().unwrap().fail_from = None;
        assert_eq!(identity.status().await.unwrap().state, "paired");
        assert_eq!(api.0.lock().unwrap().pairs, 1);
    }
    #[tokio::test]
    async fn lost_finalize_response_recovers_without_second_pair_request() {
        let vault = MemoryVault::default();
        let api = FakeApi::default();
        api.0.lock().unwrap().lose_finalize = true;
        assert_eq!(
            client(vault.clone(), api.clone())
                .pair("PC".into(), "code".into())
                .await
                .unwrap()
                .state,
            "pairingError"
        );
        assert_eq!(
            client(vault, api.clone()).status().await.unwrap().state,
            "paired"
        );
        assert_eq!(api.0.lock().unwrap().pairs, 1);
    }
    #[tokio::test]
    async fn disable_keeps_secret_revoke_erases_it() {
        let vault = MemoryVault::default();
        let api = FakeApi::default();
        let identity = client(vault.clone(), api.clone());
        identity.pair("PC".into(), "code".into()).await.unwrap();
        api.0.lock().unwrap().error = Some(ApiError {
            code: "node_disabled".into(),
            message: "disabled".into(),
        });
        assert_eq!(identity.status().await.unwrap().state, "disabled");
        assert!(vault
            .0
            .lock()
            .unwrap()
            .record
            .as_ref()
            .unwrap()
            .credential
            .is_some());
        api.0.lock().unwrap().error = None;
        assert_eq!(identity.status().await.unwrap().state, "paired");
        api.0.lock().unwrap().error = Some(ApiError {
            code: "node_revoked".into(),
            message: "revoked".into(),
        });
        assert_eq!(identity.status().await.unwrap().state, "revoked");
        assert!(vault
            .0
            .lock()
            .unwrap()
            .record
            .as_ref()
            .unwrap()
            .credential
            .is_none());
        assert_eq!(client(vault, api).status().await.unwrap().state, "revoked");
    }
    #[tokio::test]
    async fn invalid_pair_code_can_be_replaced_after_definitive_rejection() {
        let vault = MemoryVault::default();
        let api = FakeApi::default();
        let identity = client(vault.clone(), api.clone());
        api.0.lock().unwrap().error = Some(ApiError {
            code: "pairing_unavailable".into(),
            message: "invalid code".into(),
        });
        assert_eq!(
            identity
                .pair("PC".into(), "wrong".into())
                .await
                .unwrap()
                .state,
            "pairingError"
        );
        assert!(vault
            .0
            .lock()
            .unwrap()
            .record
            .as_ref()
            .unwrap()
            .pending
            .is_none());
        api.0.lock().unwrap().error = None;
        assert_eq!(
            identity
                .pair("PC".into(), "correct".into())
                .await
                .unwrap()
                .state,
            "paired"
        );
    }
    #[tokio::test]
    async fn network_failure_preserves_identity_and_rename_uses_backend() {
        let vault = MemoryVault::default();
        let api = FakeApi::default();
        let identity = client(vault.clone(), api.clone());
        let paired = identity.pair("PC".into(), "code".into()).await.unwrap();
        identity
            .modify(paired.node.unwrap().id, "rename", Some("Pixel".into()))
            .await
            .unwrap();
        assert_eq!(identity.list().await.unwrap()[0].name, "Pixel");
        api.0.lock().unwrap().error = Some(ApiError {
            code: "network_unavailable".into(),
            message: "offline".into(),
        });
        assert_eq!(identity.status().await.unwrap().state, "paired");
        assert!(vault
            .0
            .lock()
            .unwrap()
            .record
            .as_ref()
            .unwrap()
            .credential
            .is_some());
    }
    #[tokio::test]
    async fn changing_backend_never_sends_existing_identity_to_other_origin() {
        let vault = MemoryVault::default();
        let api = FakeApi::default();
        client(vault.clone(), api.clone())
            .pair("PC".into(), "code".into())
            .await
            .unwrap();
        let other = Identity::new(
            vault,
            api.clone(),
            "https://other.example".into(),
            "windows".into(),
            "0.7.0-stage.4".into(),
        );
        assert!(other.status().await.is_err());
        assert_eq!(api.0.lock().unwrap().pairs, 1);
    }
}
