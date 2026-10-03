//! Native control plane only. Never expose credentials, URLs with secrets or library errors.
use futures_util::{SinkExt, StreamExt};
use serde::{Deserialize, Serialize};
use std::{
    future::Future,
    sync::{Arc, Mutex},
    time::Duration,
};
use tokio::{
    sync::Notify,
    task::JoinHandle,
    time::{timeout, Instant},
};
use tokio_tungstenite::{
    connect_async_with_config,
    tungstenite::{
        client::IntoClientRequest,
        protocol::{frame::coding::CloseCode, CloseFrame, WebSocketConfig},
        Error, Message,
    },
};
use tokio_util::sync::CancellationToken;

pub(crate) trait CredentialSource: Send + Sync + 'static {
    fn credential(&self) -> impl Future<Output = Result<Option<String>, ()>> + Send;
    fn rejected(
        &self,
        credential: &str,
        code: &'static str,
    ) -> impl Future<Output = Result<bool, ()>> + Send;
}
// Only fixed local classifications and numeric status codes cross the IPC boundary.
#[derive(Clone, Copy, Debug, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct TransportDiagnostic {
    pub phase: &'static str,
    pub reason: &'static str,
    pub http_status: Option<u16>,
    pub close_code: Option<u16>,
}
#[derive(Clone, Debug, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct TransportStatus {
    pub transport_state: &'static str,
    pub last_error: Option<&'static str>,
    pub last_diagnostic: Option<TransportDiagnostic>,
}
impl Default for TransportStatus {
    fn default() -> Self {
        Self {
            transport_state: "offline",
            last_error: None,
            last_diagnostic: None,
        }
    }
}
#[derive(Clone)]
pub(crate) struct TransportConfig {
    pub url: String,
    pub version: String,
    pub connect_timeout: Duration,
    pub healthy_reset: Duration,
    pub disabled_retry: Duration,
}
impl TransportConfig {
    pub fn new(origin: &str, version: &str, allow_http: bool) -> Result<Self, &'static str> {
        let endpoint = crate::config::Endpoint::parse(origin, allow_http, false)?;
        let mut url = url::Url::parse(&endpoint.base_url()).map_err(|_| "Backend inválido.")?;
        let scheme = if url.scheme() == "https" { "wss" } else { "ws" };
        url.set_scheme(scheme).map_err(|_| "Backend inválido.")?;
        url.set_path("/api/nodes/connect");
        Ok(Self {
            url: url.into(),
            version: version.into(),
            connect_timeout: Duration::from_secs(10),
            healthy_reset: Duration::from_secs(120),
            disabled_retry: Duration::from_secs(30),
        })
    }
}
struct Running {
    cancel: CancellationToken,
    task: JoinHandle<()>,
}
pub(crate) struct NodeTransportManager {
    running: Mutex<Option<Running>>,
    stopping: std::sync::atomic::AtomicBool,
    stop_gate: tokio::sync::Mutex<()>,
    wake: Arc<Notify>,
    status: Arc<Mutex<TransportStatus>>,
}
impl Default for NodeTransportManager {
    fn default() -> Self {
        Self {
            running: Mutex::new(None),
            stopping: std::sync::atomic::AtomicBool::new(false),
            stop_gate: tokio::sync::Mutex::new(()),
            wake: Arc::new(Notify::new()),
            status: Arc::new(Mutex::new(TransportStatus::default())),
        }
    }
}
impl NodeTransportManager {
    pub fn start<S: CredentialSource>(&self, source: Arc<S>, config: TransportConfig) -> bool {
        let mut slot = self.running.lock().unwrap();
        if self.stopping.load(std::sync::atomic::Ordering::SeqCst) {
            return false;
        }
        if slot.as_ref().is_some_and(|r| !r.task.is_finished()) {
            return false;
        }
        let cancel = CancellationToken::new();
        let task = tokio::spawn(run(
            source,
            config,
            cancel.clone(),
            self.wake.clone(),
            self.status.clone(),
        ));
        *slot = Some(Running { cancel, task });
        true
    }
    pub fn reconnect(&self) {
        self.wake.notify_one();
    }
    pub fn state(&self) -> TransportStatus {
        self.status.lock().unwrap().clone()
    }
    pub fn shutdown(&self) {
        if let Some(r) = self.running.lock().unwrap().as_ref() {
            r.cancel.cancel();
        }
    }
    pub async fn stop(&self) {
        let _serial = self.stop_gate.lock().await;
        let running = {
            let mut slot = self.running.lock().unwrap();
            self.stopping
                .store(true, std::sync::atomic::Ordering::SeqCst);
            slot.take()
        };
        if let Some(r) = running {
            r.cancel.cancel();
            let _ = r.task.await;
        }
        *self.status.lock().unwrap() = TransportStatus::default();
        self.stopping
            .store(false, std::sync::atomic::Ordering::SeqCst);
    }
}
impl Drop for NodeTransportManager {
    fn drop(&mut self) {
        if let Some(r) = self.running.get_mut().unwrap().take() {
            r.cancel.cancel();
            r.task.abort();
        }
    }
}
fn state(status: &Mutex<TransportStatus>, value: &'static str, error: Option<&'static str>) {
    let mut status = status.lock().unwrap();
    status.transport_state = value;
    status.last_error = error;
    if value == "online" {
        status.last_diagnostic = None;
    }
}
fn diagnose(
    status: &Mutex<TransportStatus>,
    phase: &'static str,
    reason: &'static str,
    http_status: Option<u16>,
    close_code: Option<u16>,
) {
    let diagnostic = TransportDiagnostic {
        phase,
        reason,
        http_status,
        close_code,
    };
    // No library error formatting: some errors carry request headers or server prose.
    eprintln!("Node transport phase={phase} reason={reason} http_status={http_status:?} close_code={close_code:?}");
    status.lock().unwrap().last_diagnostic = Some(diagnostic);
}
fn retry(status: &Mutex<TransportStatus>, phase: &'static str, reason: &'static str) -> Outcome {
    diagnose(status, phase, reason, None, None);
    Outcome::Retry
}
fn diagnosed_close(
    status: &Mutex<TransportStatus>,
    phase: &'static str,
    frame: Option<CloseFrame>,
) -> Outcome {
    let code = frame.map(|f| u16::from(f.code));
    let reason = match code {
        Some(4001) => "node_revoked",
        Some(4003) => "node_disabled",
        Some(4006) => "protocol_mismatch",
        Some(4008) => "heartbeat_timeout",
        Some(4000) => "connection_replaced",
        _ => "server_disconnected",
    };
    diagnose(status, phase, reason, None, code);
    code.map(classify_close).unwrap_or(Outcome::Retry)
}
fn diagnose_connect(status: &Mutex<TransportStatus>, error: &Error) {
    let (reason, http) = match error {
        Error::Http(response) => ("http_status", Some(response.status().as_u16())),
        Error::Tls(_) => ("tls_failed", None),
        Error::Protocol(_) | Error::HttpFormat(_) => ("upgrade_failed", None),
        _ => ("network_connect_failed", None),
    };
    diagnose(status, "upgrade", reason, http, None);
}
#[derive(Default)]
struct Backoff(u32);
impl Backoff {
    fn next(&mut self, jitter: f64) -> Duration {
        let seconds = (1u64 << self.0.min(5)).min(30);
        self.0 = self.0.saturating_add(1);
        Duration::from_secs_f64((seconds as f64 * (0.8 + jitter.clamp(0.0, 1.0) * 0.4)).min(30.0))
    }
    fn reset_if_healthy(&mut self, elapsed: Duration, threshold: Duration) {
        if elapsed >= threshold {
            self.0 = 0;
        }
    }
}
fn jitter() -> f64 {
    let mut bytes = [0; 4];
    if getrandom::fill(&mut bytes).is_err() {
        return 0.5;
    }
    u32::from_le_bytes(bytes) as f64 / u32::MAX as f64
}
#[derive(Clone, Copy, Debug, PartialEq)]
enum Outcome {
    Retry,
    Disabled,
    Revoked,
    Incompatible,
}
fn classify_close(code: u16) -> Outcome {
    match code {
        4001 => Outcome::Revoked,
        4003 => Outcome::Disabled,
        4006 => Outcome::Incompatible,
        _ => Outcome::Retry,
    }
}
fn classify_http(error: &Error) -> Outcome {
    if let Error::Http(response) = error {
        // Trust only the defined status + bounded error code, never server-provided prose.
        let body = response.body().as_ref().filter(|b| b.len() <= 4096);
        let code = response
            .headers()
            .get("X-Aegis-Node-Error")
            .and_then(|v| v.to_str().ok())
            .map(str::to_owned)
            .or_else(|| {
                body.and_then(|b| serde_json::from_slice::<serde_json::Value>(b).ok())
                    .and_then(|v| v.get("code").and_then(|v| v.as_str()).map(str::to_owned))
            });
        return match (response.status().as_u16(), code.as_deref()) {
            (401, Some("node_revoked" | "node_authentication_required")) => Outcome::Revoked,
            (403, Some("node_disabled")) => Outcome::Disabled,
            (409, Some("protocol_mismatch")) => Outcome::Incompatible,
            _ => Outcome::Retry,
        };
    }
    Outcome::Retry
}
async fn run<S: CredentialSource>(
    source: Arc<S>,
    config: TransportConfig,
    cancel: CancellationToken,
    wake: Arc<Notify>,
    status: Arc<Mutex<TransportStatus>>,
) {
    let mut backoff = Backoff::default();
    let mut attempted = false;
    loop {
        let credential = tokio::select! {
            _ = cancel.cancelled() => break,
            value = source.credential() => value,
        };
        let secret = match credential {
            Ok(Some(secret)) => secret,
            value => {
                state(
                    &status,
                    "offline",
                    if value.is_err() {
                        Some("Armazenamento seguro indisponível.")
                    } else {
                        None
                    },
                );
                tokio::select! { _ = cancel.cancelled() => break, _ = wake.notified() => {}, _ = tokio::time::sleep(Duration::from_secs(30)) => {} }
                continue;
            }
        };
        state(
            &status,
            if attempted {
                "reconnecting"
            } else {
                "connecting"
            },
            None,
        );
        attempted = true;
        let mut online_since = None;
        let result = tokio::select! {
            _ = cancel.cancelled() => break,
            _ = wake.notified() => continue,
            result = session(&config, &secret, &status, &cancel, &mut online_since) => result,
        };
        if let Some(since) = online_since {
            backoff.reset_if_healthy(since.elapsed(), config.healthy_reset);
        }
        let delay = match result {
            Outcome::Revoked => {
                // Stop retries even if durable tombstone storage fails. Never loop on an old 401.
                let saved = tokio::select! {
                    _ = cancel.cancelled() => break,
                    value = source.rejected(&secret, "node_revoked") => value,
                };
                if saved == Ok(true) {
                    backoff.0 = 0;
                    attempted = false;
                    continue; // A newer pairing owns the vault; keep this sole loop for it.
                }
                state(
                    &status,
                    "offline",
                    Some(if saved.is_ok() {
                        "Identidade revogada. Pareie novamente."
                    } else {
                        "Identidade revogada; armazenamento seguro indisponível."
                    }),
                );
                break;
            }
            Outcome::Incompatible => {
                state(&status, "offline", Some("Esta versão da Aegis não é compatível com o servidor. Atualize o aplicativo."));
                break;
            }
            Outcome::Disabled => {
                state(&status, "offline", Some("Este Node está desativado."));
                config.disabled_retry.mul_f64(1.0 + jitter() * 0.2)
            }
            Outcome::Retry => {
                state(
                    &status,
                    "reconnecting",
                    Some("Conexão interrompida. Reconectando automaticamente."),
                );
                backoff.next(jitter())
            }
        };
        tokio::select! { _ = cancel.cancelled() => break, _ = wake.notified() => {}, _ = tokio::time::sleep(delay) => {} }
    }
    if cancel.is_cancelled() {
        state(&status, "offline", None);
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Envelope {
    protocol_version: u32,
    #[serde(rename = "type")]
    kind: String,
    message_id: String,
    sent_at: String,
    payload: Option<serde_json::Value>,
}
fn envelope(kind: &str, payload: Option<serde_json::Value>) -> (String, Message) {
    let id = uuid::Uuid::new_v4().to_string();
    let value = serde_json::json!({"protocolVersion":1,"type":kind,"messageId":id,"sentAt":chrono::Utc::now().to_rfc3339()});
    let mut value = value;
    if let Some(payload) = payload {
        value["payload"] = payload;
    }
    (id, Message::Text(value.to_string().into()))
}
fn ack(message: Message, kind: &str, id: &str) -> Result<Envelope, Outcome> {
    match message {
        Message::Close(frame) => Err(frame
            .map(|f| classify_close(u16::from(f.code)))
            .unwrap_or(Outcome::Retry)),
        Message::Text(text) => {
            let parsed: Envelope =
                serde_json::from_str(&text).map_err(|_| Outcome::Incompatible)?;
            if parsed.protocol_version != 1
                || parsed.kind != kind
                || parsed.message_id != id
                || chrono::DateTime::parse_from_rfc3339(&parsed.sent_at).is_err()
            {
                return Err(Outcome::Incompatible);
            }
            Ok(parsed)
        }
        _ => Err(Outcome::Retry),
    }
}
async fn session(
    config: &TransportConfig,
    secret: &str,
    status: &Mutex<TransportStatus>,
    cancel: &CancellationToken,
    online_since: &mut Option<Instant>,
) -> Outcome {
    let mut request = match config.url.as_str().into_client_request() {
        Ok(r) => r,
        Err(_) => return Outcome::Incompatible,
    };
    let mut authorization = match format!("AegisNode {secret}")
        .parse::<tokio_tungstenite::tungstenite::http::HeaderValue>()
    {
        Ok(h) => h,
        Err(_) => return Outcome::Revoked,
    };
    authorization.set_sensitive(true);
    request.headers_mut().insert("Authorization", authorization);
    request
        .headers_mut()
        .insert("X-Aegis-Node-Protocol", "1".parse().unwrap());
    let limits = WebSocketConfig::default()
        .read_buffer_size(4096)
        .write_buffer_size(0)
        .max_write_buffer_size(8192)
        .max_frame_size(Some(4096))
        .max_message_size(Some(4096));
    let mut socket = match timeout(
        config.connect_timeout,
        connect_async_with_config(request, Some(limits), false),
    )
    .await
    {
        Ok(Ok((s, _))) => s,
        Ok(Err(e)) => {
            diagnose_connect(status, &e);
            return classify_http(&e);
        }
        Err(_) => return retry(status, "upgrade", "connect_timeout"),
    };
    let (id, hello) = envelope(
        "hello",
        Some(serde_json::json!({"appVersion":config.version})),
    );
    if !matches!(
        timeout(config.connect_timeout, socket.send(hello)).await,
        Ok(Ok(()))
    ) {
        return retry(status, "hello", "network_disconnected");
    }
    let received = match timeout(config.connect_timeout, socket.next()).await {
        Ok(Some(Ok(m))) => m,
        Err(_) => return retry(status, "hello", "hello_timeout"),
        _ => return retry(status, "hello", "network_disconnected"),
    };
    if let Message::Close(frame) = received {
        return diagnosed_close(status, "hello", frame);
    }
    let hello = match ack(received, "hello_ack", &id) {
        Ok(a) => a,
        Err(e) => {
            diagnose(status, "hello", "hello_rejected", None, None);
            return e;
        }
    };
    let payload = hello.payload.unwrap_or_default();
    let interval = payload["heartbeatSeconds"].as_u64().unwrap_or(0);
    let deadline = payload["timeoutSeconds"].as_u64().unwrap_or(0);
    if interval == 0 || interval > 300 || deadline < interval * 2 || deadline > 900 {
        diagnose(status, "hello", "protocol_error", None, None);
        return Outcome::Incompatible;
    }
    *online_since = Some(Instant::now());
    state(status, "online", None);
    let mut next_heartbeat = Instant::now() + Duration::from_secs(interval);
    let mut ack_deadline = Instant::now() + Duration::from_secs(deadline);
    let mut pending: Option<String> = None;
    loop {
        tokio::select! {
            _ = cancel.cancelled() => {
                let close = Message::Close(Some(CloseFrame { code: CloseCode::Normal, reason: "app_shutdown".into() }));
                let _ = timeout(Duration::from_secs(1), socket.send(close)).await;
                return Outcome::Retry;
            }
            _ = tokio::time::sleep_until(ack_deadline) => return retry(status, "heartbeat", "heartbeat_timeout"),
            _ = tokio::time::sleep_until(next_heartbeat), if pending.is_none() => {
                let (id, heartbeat) = envelope("heartbeat", None);
                if !matches!(timeout(config.connect_timeout, socket.send(heartbeat)).await, Ok(Ok(()))) { return retry(status, "heartbeat", "network_disconnected"); }
                pending = Some(id);
                next_heartbeat = Instant::now() + Duration::from_secs(interval);
            }
            received = socket.next() => {
                match received {
                    Some(Ok(Message::Ping(_))) => { if socket.flush().await.is_err() { return retry(status, "heartbeat", "network_disconnected"); } }
                    Some(Ok(Message::Pong(_))) => {},
                    Some(Ok(Message::Close(f))) => return diagnosed_close(status, "heartbeat", f),
                    Some(Ok(message)) => {
                        let Some(id) = pending.take() else { diagnose(status, "heartbeat", "protocol_error", None, None); return Outcome::Incompatible; };
                        if let Err(e) = ack(message, "heartbeat_ack", &id) { diagnose(status, "heartbeat", "heartbeat_rejected", None, None); return e; }
                        ack_deadline = Instant::now() + Duration::from_secs(deadline);
                    }
                    _ => return retry(status, "heartbeat", "network_disconnected"),
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
    use tokio::net::TcpListener;
    use tokio_tungstenite::{
        accept_hdr_async,
        tungstenite::handshake::server::{Request, Response},
    };
    struct Source {
        revoked: AtomicBool,
        available: AtomicBool,
        rejected: AtomicUsize,
    }
    impl Source {
        fn paired() -> Arc<Self> {
            Arc::new(Self {
                revoked: AtomicBool::new(false),
                available: AtomicBool::new(true),
                rejected: AtomicUsize::new(0),
            })
        }
    }
    impl CredentialSource for Source {
        async fn credential(&self) -> Result<Option<String>, ()> {
            Ok(
                (self.available.load(Ordering::SeqCst) && !self.revoked.load(Ordering::SeqCst))
                    .then(|| "test-secret-never-public".into()),
            )
        }
        async fn rejected(&self, _: &str, _: &'static str) -> Result<bool, ()> {
            self.revoked.store(true, Ordering::SeqCst);
            self.rejected.fetch_add(1, Ordering::SeqCst);
            Ok(false)
        }
    }
    async fn until(predicate: impl Fn() -> bool) {
        timeout(Duration::from_secs(8), async {
            while !predicate() {
                tokio::time::sleep(Duration::from_millis(5)).await;
            }
        })
        .await
        .unwrap();
    }
    fn config(port: u16) -> TransportConfig {
        let mut config =
            TransportConfig::new(&format!("http://127.0.0.1:{port}"), "0.7.0-stage.5", true)
                .unwrap();
        config.connect_timeout = Duration::from_secs(2);
        config.disabled_retry = Duration::from_millis(50);
        config
    }
    async fn fixture_at(
        port: u16,
        close_after: Option<u16>,
        connections: Arc<AtomicUsize>,
        heartbeats: Arc<AtomicUsize>,
    ) -> (u16, JoinHandle<()>) {
        let listener = TcpListener::bind(("127.0.0.1", port)).await.unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = tokio::spawn(async move {
            let mut peers = tokio::task::JoinSet::new();
            while let Ok((stream, _)) = listener.accept().await {
                let connections = connections.clone();
                let heartbeats = heartbeats.clone();
                peers.spawn(async move {
                    let mut socket =
                        accept_hdr_async(stream, |request: &Request, response: Response| {
                            assert_eq!(
                                request.headers()["authorization"],
                                "AegisNode test-secret-never-public"
                            );
                            assert_eq!(request.headers()["x-aegis-node-protocol"], "1");
                            assert!(request.uri().query().is_none());
                            Ok(response)
                        })
                        .await
                        .unwrap();
                    let count = connections.fetch_add(1, Ordering::SeqCst);
                    let message = socket.next().await.unwrap().unwrap();
                    let hello: serde_json::Value =
                        serde_json::from_str(message.to_text().unwrap()).unwrap();
                    assert_eq!(hello["type"], "hello");
                    assert_eq!(hello["payload"]["appVersion"], "0.7.0-stage.5");
                    let response = serde_json::json!({"protocolVersion":1,"type":"hello_ack","messageId":hello["messageId"],"sentAt":chrono::Utc::now().to_rfc3339(),"payload":{"heartbeatSeconds":1,"timeoutSeconds":3}});
                    socket
                        .send(Message::Text(response.to_string().into()))
                        .await
                        .unwrap();
                    if let Some(code) = close_after {
                        if code != 1000 || count == 0 {
                            socket
                                .send(Message::Close(Some(CloseFrame {
                                    code: CloseCode::from(code),
                                    reason: "defined_code".into(),
                                })))
                                .await
                                .unwrap();
                            return;
                        }
                    }
                    while let Some(Ok(message)) = socket.next().await {
                        if message.is_close() {
                            break;
                        }
                        let heartbeat: serde_json::Value =
                            serde_json::from_str(message.to_text().unwrap()).unwrap();
                        assert_eq!(heartbeat["type"], "heartbeat");
                        heartbeats.fetch_add(1, Ordering::SeqCst);
                        let response = serde_json::json!({"protocolVersion":1,"type":"heartbeat_ack","messageId":heartbeat["messageId"],"sentAt":chrono::Utc::now().to_rfc3339(),"payload":null});
                        if socket
                            .send(Message::Text(response.to_string().into()))
                            .await
                            .is_err()
                        {
                            break;
                        }
                    }
                });
            }
        });
        (port, server)
    }
    #[test]
    fn backoff_is_exponential_capped_with_jitter_and_resets_only_when_healthy() {
        let mut b = Backoff::default();
        assert_eq!(
            (0..7).map(|_| b.next(0.5).as_secs()).collect::<Vec<_>>(),
            [1, 2, 4, 8, 16, 30, 30]
        );
        b.reset_if_healthy(Duration::from_secs(1), Duration::from_secs(120));
        assert_eq!(b.next(0.5).as_secs(), 30);
        b.reset_if_healthy(Duration::from_secs(120), Duration::from_secs(120));
        assert_eq!(b.next(0.0), Duration::from_millis(800));
        assert!(b.next(1.0) > Duration::from_secs(2));
    }
    #[test]
    fn release_transport_never_downgrades_and_public_state_contains_no_secret() {
        assert!(TransportConfig::new("http://localhost:8000", "1.0.0", false).is_err());
        assert!(
            TransportConfig::new("https://aegis.example", "1.0.0", false)
                .unwrap()
                .url
                .starts_with("wss://")
        );
        assert!(
            TransportConfig::new("https://aegis.example?token=secret", "1.0.0", false).is_err()
        );
        assert!(!serde_json::to_string(&TransportStatus::default())
            .unwrap()
            .contains("secret"));
    }
    #[tokio::test]
    async fn connect_authenticate_heartbeat_duplicate_start_and_shutdown() {
        let connections = Arc::new(AtomicUsize::new(0));
        let heartbeats = Arc::new(AtomicUsize::new(0));
        let (port, server) = fixture_at(0, None, connections.clone(), heartbeats.clone()).await;
        let manager = NodeTransportManager::default();
        let source = Source::paired();
        assert!(manager.start(source.clone(), config(port)));
        assert!(!manager.start(source, config(port)));
        until(|| manager.state().transport_state == "online").await;
        until(|| heartbeats.load(Ordering::SeqCst) >= 4).await;
        assert_eq!(manager.state().transport_state, "online");
        assert_eq!(connections.load(Ordering::SeqCst), 1);
        let public = serde_json::to_string(&manager.state()).unwrap();
        assert!(!public.contains("test-secret"));
        manager.stop().await;
        assert_eq!(manager.state().transport_state, "offline");
        server.abort();
    }
    #[tokio::test]
    async fn server_disconnect_reconnects_automatically() {
        let connections = Arc::new(AtomicUsize::new(0));
        let heartbeats = Arc::new(AtomicUsize::new(0));
        let (port, server) = fixture_at(0, Some(1000), connections.clone(), heartbeats).await;
        let manager = NodeTransportManager::default();
        manager.start(Source::paired(), config(port));
        until(|| {
            connections.load(Ordering::SeqCst) >= 2 && manager.state().transport_state == "online"
        })
        .await;
        manager.stop().await;
        server.abort();
    }
    #[tokio::test]
    async fn disabled_close_retries_but_revoked_and_protocol_mismatch_stop() {
        for code in [4003, 4001, 4006] {
            let connections = Arc::new(AtomicUsize::new(0));
            let (port, server) = fixture_at(
                0,
                Some(code),
                connections.clone(),
                Arc::new(AtomicUsize::new(0)),
            )
            .await;
            let source = Source::paired();
            let manager = NodeTransportManager::default();
            manager.start(source.clone(), config(port));
            if code == 4003 {
                until(|| connections.load(Ordering::SeqCst) >= 2).await;
                assert!(!source.revoked.load(Ordering::SeqCst));
            } else {
                until(|| {
                    manager
                        .running
                        .lock()
                        .unwrap()
                        .as_ref()
                        .unwrap()
                        .task
                        .is_finished()
                })
                .await;
                assert_eq!(connections.load(Ordering::SeqCst), 1);
                assert_eq!(
                    source.rejected.load(Ordering::SeqCst),
                    usize::from(code == 4001)
                );
            }
            assert!(!serde_json::to_string(&manager.state())
                .unwrap()
                .contains("test-secret"));
            manager.stop().await;
            server.abort();
        }
    }
    #[tokio::test(start_paused = true)]
    async fn unpaired_never_connects_and_shutdown_cancels_wait() {
        let source = Source::paired();
        source.available.store(false, Ordering::SeqCst);
        let manager = NodeTransportManager::default();
        manager.start(source, config(1));
        tokio::task::yield_now().await;
        tokio::time::advance(Duration::from_secs(300)).await;
        assert_eq!(manager.state().transport_state, "offline");
        manager.stop().await;
    }
    #[test]
    fn error_response_contract_is_explicit_and_does_not_expose_server_prose() {
        use tokio_tungstenite::tungstenite::http::Response;
        for (status, code, expected) in [
            (401, "node_revoked", Outcome::Revoked),
            (403, "node_disabled", Outcome::Disabled),
            (409, "protocol_mismatch", Outcome::Incompatible),
            (401, "proxy_login", Outcome::Retry),
        ] {
            let error = Error::Http(Box::new(
                Response::builder()
                    .status(status)
                    .body(Some(
                        format!("{{\"code\":\"{code}\",\"error\":\"test-secret-never-public\"}}")
                            .into_bytes(),
                    ))
                    .unwrap(),
            ));
            assert_eq!(classify_http(&error), expected);
        }
    }
    #[tokio::test]
    async fn stripped_proxy_upgrade_reports_http_400_without_server_prose_or_secret() {
        let listener = TcpListener::bind(("127.0.0.1", 0)).await.unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = tokio::spawn(async move {
            while let Ok((stream, _)) = listener.accept().await {
                let _ = accept_hdr_async(stream, |_: &Request, _: Response| {
                    Err(tokio_tungstenite::tungstenite::http::Response::builder()
                        .status(400)
                        .header("X-Aegis-Node-Error", "websocket_required")
                        .body(Some(
                            "test-secret-never-public arbitrary server text".into(),
                        ))
                        .unwrap())
                })
                .await;
            }
        });
        let manager = NodeTransportManager::default();
        manager.start(Source::paired(), config(port));
        until(|| manager.state().last_diagnostic.is_some()).await;
        let diagnostic = manager.state().last_diagnostic.unwrap();
        assert_eq!(diagnostic.phase, "upgrade");
        assert_eq!(diagnostic.http_status, Some(400));
        assert_eq!(diagnostic.reason, "http_status");
        assert_eq!(manager.state().transport_state, "reconnecting");
        assert!(!serde_json::to_string(&manager.state())
            .unwrap()
            .contains("test-secret"));
        manager.stop().await;
        server.abort();
    }
    #[tokio::test]
    async fn silent_server_reports_hello_timeout() {
        let listener = TcpListener::bind(("127.0.0.1", 0)).await.unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = tokio::spawn(async move {
            let (stream, _) = listener.accept().await.unwrap();
            let _socket = tokio_tungstenite::accept_async(stream).await.unwrap();
            std::future::pending::<()>().await;
        });
        let manager = NodeTransportManager::default();
        let mut options = config(port);
        options.connect_timeout = Duration::from_millis(100);
        manager.start(Source::paired(), options);
        until(|| manager.state().last_diagnostic.is_some()).await;
        assert_eq!(
            manager.state().last_diagnostic.unwrap().reason,
            "hello_timeout"
        );
        assert_eq!(manager.state().last_diagnostic.unwrap().phase, "hello");
        manager.stop().await;
        server.abort();
    }
    #[tokio::test]
    async fn network_loss_and_server_restart_recover_without_app_restart() {
        let connections = Arc::new(AtomicUsize::new(0));
        let heartbeats = Arc::new(AtomicUsize::new(0));
        let (port, server) = fixture_at(0, None, connections.clone(), heartbeats.clone()).await;
        let manager = NodeTransportManager::default();
        let source = Source::paired();
        manager.start(source.clone(), config(port));
        until(|| manager.state().transport_state == "online").await;
        server.abort();
        let _ = server.await;
        until(|| manager.state().transport_state == "reconnecting").await;
        let (_, restarted) = fixture_at(port, None, connections.clone(), heartbeats).await;
        until(|| {
            connections.load(Ordering::SeqCst) >= 2 && manager.state().transport_state == "online"
        })
        .await;
        assert!(!source.revoked.load(Ordering::SeqCst));
        manager.stop().await;
        restarted.abort();
    }
    #[tokio::test]
    async fn tombstone_storage_failure_stops_old_credential_retries() {
        struct FailingSource;
        impl CredentialSource for FailingSource {
            async fn credential(&self) -> Result<Option<String>, ()> {
                Ok(Some("test-secret-never-public".into()))
            }
            async fn rejected(&self, _: &str, _: &'static str) -> Result<bool, ()> {
                Err(())
            }
        }
        let connections = Arc::new(AtomicUsize::new(0));
        let (port, server) = fixture_at(
            0,
            Some(4001),
            connections.clone(),
            Arc::new(AtomicUsize::new(0)),
        )
        .await;
        let manager = NodeTransportManager::default();
        manager.start(Arc::new(FailingSource), config(port));
        until(|| {
            manager
                .running
                .lock()
                .unwrap()
                .as_ref()
                .unwrap()
                .task
                .is_finished()
        })
        .await;
        assert_eq!(connections.load(Ordering::SeqCst), 1);
        assert_eq!(manager.state().transport_state, "offline");
        assert!(!serde_json::to_string(&manager.state())
            .unwrap()
            .contains("test-secret"));
        manager.stop().await;
        server.abort();
    }
}
