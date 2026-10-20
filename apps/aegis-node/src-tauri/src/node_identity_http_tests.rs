// Actual reqwest HTTP requests, not an Api trait mock. Never report auth/token bodies.
struct HttpFixture {
    origin: String,
    worker: std::thread::JoinHandle<Vec<(String, String)>>,
}
impl HttpFixture {
    fn new(routes: Vec<(&str, String, serde_json::Value)>) -> Self {
        use std::io::{Read, Write};
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let origin = format!("http://{}", listener.local_addr().unwrap());
        listener.set_nonblocking(true).unwrap();
        let routes: Vec<_> = routes
            .into_iter()
            .map(|(m, p, b)| (m.to_string(), p, b))
            .collect();
        let worker = std::thread::spawn(move || {
            let mut captured = Vec::new();
            for (method, path, body) in routes {
                let deadline = std::time::Instant::now() + std::time::Duration::from_secs(5);
                let mut stream = loop {
                    match listener.accept() {
                        Ok((stream, _)) => break stream,
                        Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                            assert!(
                                std::time::Instant::now() < deadline,
                                "HTTP fixture request timeout"
                            );
                            std::thread::sleep(std::time::Duration::from_millis(5));
                        }
                        Err(_) => panic!("HTTP fixture accept failed"),
                    }
                };
                // Winsock inherits the listener's nonblocking mode; reads below are bounded blocking reads.
                stream.set_nonblocking(false).unwrap();
                stream
                    .set_read_timeout(Some(std::time::Duration::from_secs(5)))
                    .unwrap();
                let mut bytes = Vec::new();
                loop {
                    let mut part = [0; 1024];
                    let n = stream.read(&mut part).unwrap();
                    assert!(n > 0, "incomplete HTTP request");
                    bytes.extend_from_slice(&part[..n]);
                    assert!(bytes.len() < 8192, "HTTP fixture input bound");
                    if let Some(end) = bytes.windows(4).position(|b| b == b"\r\n\r\n") {
                        let header = std::str::from_utf8(&bytes[..end]).unwrap();
                        let length = header
                            .lines()
                            .find_map(|line| {
                                let (key, value) = line.split_once(':')?;
                                key.eq_ignore_ascii_case("content-length")
                                    .then(|| value.trim().parse::<usize>().unwrap())
                            })
                            .unwrap_or(0);
                        if bytes.len() >= end + 4 + length {
                            break;
                        }
                    }
                }
                let header = std::str::from_utf8(&bytes).unwrap();
                let mut line = header.lines().next().unwrap().split_whitespace();
                let actual = (
                    line.next().unwrap().to_string(),
                    line.next().unwrap().to_string(),
                );
                // Compare only method/path: headers and payloads remain private.
                let matches = actual == (method, path);
                captured.push(actual);
                let (status, body) = if matches {
                    ("200 OK", body)
                } else {
                    (
                        "404 Not Found",
                        serde_json::json!({"code":"node_request_failed"}),
                    )
                };
                let body = body.to_string();
                write!(stream,"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",body.len()).unwrap();
            }
            captured
        });
        Self { origin, worker }
    }
    fn identity(&self) -> Identity<MemoryVault, HttpApi> {
        let vault = MemoryVault::default();
        vault
            .save(&Record {
                origin: self.origin.clone(),
                credential: Some("fixture-only-secret".into()),
                node: Some(node()),
                ..Record::default()
            })
            .unwrap();
        Identity::new(
            vault,
            HttpApi::new(self.origin.clone()).unwrap(),
            self.origin.clone(),
            "android".into(),
            "0.7.0-stage.9".into(),
        )
    }
    fn verify(self, expected: &[(&str, &str)]) {
        let actual = self.worker.join().expect("HTTP fixture worker");
        assert_eq!(
            actual,
            expected
                .iter()
                .map(|(m, p)| (m.to_string(), p.to_string()))
                .collect::<Vec<_>>()
        );
    }
}
fn me_route() -> (&'static str, String, serde_json::Value) {
    (
        "GET",
        "/api/nodes/me".into(),
        serde_json::to_value(node()).unwrap(),
    )
}
#[tokio::test]
async fn http_test_notification_uses_actual_controller_url() {
    let fixture = HttpFixture::new(vec![
        me_route(),
        (
            "POST",
            "/api/nodes/notifications/test".into(),
            serde_json::json!({"status":"success","transport":"live_websocket"}),
        ),
    ]);
    let result = fixture.identity().test_notification(node().id).await;
    fixture.verify(&[
        ("GET", "/api/nodes/me"),
        ("POST", "/api/nodes/notifications/test"),
    ]);
    assert!(result.is_ok(), "notification controller request failed");
}
#[tokio::test]
async fn http_register_push_uses_actual_controller_url() {
    let fixture = HttpFixture::new(vec![
        me_route(),
        (
            "PUT",
            "/api/nodes/me/push".into(),
            serde_json::json!({"registered":true}),
        ),
    ]);
    let result = fixture
        .identity()
        .register_push("fixture-only-push-token")
        .await;
    fixture.verify(&[("GET", "/api/nodes/me"), ("PUT", "/api/nodes/me/push")]);
    assert!(result.is_ok(), "push controller request failed");
}
#[tokio::test]
async fn http_existing_node_routes_keep_relative_path_convention() {
    use crate::node_capabilities::{RequiredCapability, TargetRequest};
    let id = node().id;
    let priority_path = format!("/api/nodes/{id}/priority");
    let fixture = HttpFixture::new(vec![
        me_route(),
        ("GET", "/api/nodes".into(), serde_json::json!([node()])),
        me_route(),
        (
            "POST",
            "/api/nodes/resolve".into(),
            serde_json::json!({"node":null,"code":"no_eligible_node","onlineNodes":0,"capabilityCompatibleNodes":0}),
        ),
        me_route(),
        (
            "POST",
            "/api/nodes/pairing-codes".into(),
            serde_json::json!({"code":"fixture-only-code","expiresAt":"2026-10-04T01:00:00Z"}),
        ),
        me_route(),
        (
            "PATCH",
            priority_path.clone(),
            serde_json::to_value(node()).unwrap(),
        ),
        me_route(),
    ]);
    let identity = fixture.identity();
    assert!(identity.list().await.is_ok());
    assert!(identity
        .resolve(TargetRequest {
            required_capabilities: vec![RequiredCapability {
                name: "audio.output".into(),
                minimum_version: 1
            }],
            preferred_node_id: None
        })
        .await
        .is_ok());
    assert!(identity.create_code().await.is_ok());
    assert!(identity.set_priority(id, 10).await.is_ok());
    fixture.verify(&[
        ("GET", "/api/nodes/me"),
        ("GET", "/api/nodes"),
        ("GET", "/api/nodes/me"),
        ("POST", "/api/nodes/resolve"),
        ("GET", "/api/nodes/me"),
        ("POST", "/api/nodes/pairing-codes"),
        ("GET", "/api/nodes/me"),
        ("PATCH", &priority_path),
        ("GET", "/api/nodes/me"),
    ]);
}

#[tokio::test(flavor = "multi_thread")]
#[ignore = "requires the isolated ASP.NET NodeRuntimeFixture; run scripts/node-transport-probe/native-http.mjs"]
async fn http_live_notification_and_push_reach_real_backend() {
    use crate::node_notification::{NotificationCommand, NotificationExecutor, NotificationSink};
    use crate::node_transport::{CredentialSource, NodeTransportManager, TransportConfig};
    use std::sync::atomic::{AtomicUsize, Ordering};
    struct Source(String);
    impl CredentialSource for Source {
        async fn credential(&self) -> Result<Option<String>, ()> {
            Ok(Some(self.0.clone()))
        }
        async fn rejected(&self, _: &str, _: &'static str) -> Result<bool, ()> {
            Ok(false)
        }
    }
    struct NativeFixtureSink(Arc<AtomicUsize>);
    impl NotificationSink for NativeFixtureSink {
        fn show<'a>(
            &'a self,
            _: &'a NotificationCommand,
        ) -> std::pin::Pin<Box<dyn std::future::Future<Output = crate::node_notification::NotificationResult> + Send + 'a>>
        {
            Box::pin(async move {
                self.0.fetch_add(1, Ordering::SeqCst);
                crate::node_notification::NotificationResult::new(crate::node_notification::NotificationStatus::Success)
            })
        }
    }
    let code_file =
        std::env::var("AEGIS_NATIVE_HTTP_FIXTURE_CODE").expect("isolated bootstrap file required");
    let origin = "http://127.0.0.1:18104";
    let identity = Identity::new(
        MemoryVault::default(),
        HttpApi::new(origin.into()).unwrap(),
        origin.into(),
        "android".into(),
        "0.7.0-stage.9".into(),
    );
    let code = std::fs::read_to_string(code_file).unwrap();
    let paired = identity
        .pair("Native HttpApi fixture".into(), code)
        .await
        .unwrap();
    assert_eq!(paired.state, "paired");
    let id = paired.node.unwrap().id;
    let source = Arc::new(Source(identity.transport_credential().unwrap().unwrap()));
    let displayed = Arc::new(AtomicUsize::new(0));
    let mut config = TransportConfig::new(origin, "0.7.0-stage.9", true).unwrap();
    config.capabilities = vec![crate::node_capabilities::NodeCapability {
        name: "notification.show".into(),
        version: 1,
    }];
    config.notifications = Some(Arc::new(NotificationExecutor::new(NativeFixtureSink(
        displayed.clone(),
    ))));
    let manager = NodeTransportManager::default();
    manager.start(source, config);
    tokio::time::timeout(std::time::Duration::from_secs(10), async {
        while manager.state().transport_state != "online" {
            tokio::time::sleep(std::time::Duration::from_millis(25)).await;
        }
    })
    .await
    .unwrap();
    assert_eq!(identity.list().await.unwrap()[0].availability, "online");
    let result = identity.test_notification(id.clone()).await.unwrap();
    assert_eq!(result["node"]["id"], id);
    assert_eq!(result["transport"], "live_websocket");
    assert_eq!(result["status"], "success");
    assert_eq!(displayed.load(Ordering::SeqCst), 1);
    identity
        .register_push("isolated-fixture-token-not-a-real-fcm-endpoint")
        .await
        .unwrap();
    identity
        .register_push("isolated-fixture-refreshed-token-not-a-real-fcm-endpoint")
        .await
        .unwrap();
    let count: serde_json::Value = reqwest::get(format!("{origin}/fixture/nodes/{id}/push-count"))
        .await
        .unwrap()
        .json()
        .await
        .unwrap();
    assert_eq!(count["count"], 1);
    assert_eq!(count["valid"], 1);
    manager.stop().await;
    identity.modify(id, "revoke", None).await.unwrap();
    println!("Native HttpApi -> real controller -> live dispatcher -> Rust WebSocket -> notification fixture -> command_result success; push register + refresh = one valid registration PASS (OS display/FCM delivery not exercised).");
}
