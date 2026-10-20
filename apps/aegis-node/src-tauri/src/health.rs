use crate::config::Endpoint;
use std::time::Duration;

#[derive(Debug, PartialEq, serde::Serialize)]
#[serde(rename_all = "lowercase")]
pub enum BackendStatus {
    Connected,
    Unavailable,
}

#[derive(Debug, serde::Serialize)]
pub struct HealthResult {
    pub status: BackendStatus,
    pub message: Option<&'static str>,
}

impl HealthResult {
    pub fn unavailable(message: &'static str) -> Self {
        Self {
            status: BackendStatus::Unavailable,
            message: Some(message),
        }
    }
}

#[derive(serde::Deserialize)]
struct HealthBody {
    status: String,
}

pub async fn check(endpoint: &Endpoint, timeout: Duration) -> HealthResult {
    let result = request(endpoint, timeout).await;
    match result {
        Ok(()) => HealthResult {
            status: BackendStatus::Connected,
            message: None,
        },
        Err(message) => HealthResult::unavailable(message),
    }
}

async fn request(endpoint: &Endpoint, timeout: Duration) -> Result<(), &'static str> {
    let client = reqwest::Client::builder()
        .timeout(timeout)
        .redirect(reqwest::redirect::Policy::none())
        .build()
        .map_err(|_| "Could not initialize the HTTP client.")?;
    let mut response = client
        .get(endpoint.health_url())
        .header(reqwest::header::ACCEPT, "application/json")
        .send()
        .await
        .map_err(network_error)?;
    if !response.status().is_success() {
        return Err("Backend health endpoint returned an unsuccessful HTTP status.");
    }
    // A diagnostic endpoint must not cause an unbounded allocation.
    let mut body = Vec::new();
    while let Some(chunk) = response.chunk().await.map_err(network_error)? {
        if body.len() + chunk.len() > 4096 {
            return Err("Backend health response exceeded the size limit.");
        }
        body.extend_from_slice(&chunk);
    }
    let body: HealthBody = serde_json::from_slice(&body)
        .map_err(|_| "Backend health endpoint returned an invalid response.")?;
    if body.status != "ok" {
        return Err("Backend did not report status ok.");
    }
    Ok(())
}

fn network_error(error: reqwest::Error) -> &'static str {
    if error.is_timeout() {
        "Backend health request timed out."
    } else {
        "Could not reach the configured backend. Check its URL, network and TLS certificate."
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::{
        io::{AsyncReadExt, AsyncWriteExt},
        net::TcpListener,
    };

    async fn serve(status: &str, body: &str, delay: Duration) -> Endpoint {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let endpoint = Endpoint::parse(
            &format!("http://{}", listener.local_addr().unwrap()),
            true,
            false,
        )
        .unwrap();
        let response = format!("HTTP/1.1 {status}\r\nContent-Length: {}\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n{body}", body.len());
        tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();
            let mut request = vec![0; 1024];
            let size = socket.read(&mut request).await.unwrap();
            assert!(std::str::from_utf8(&request[..size])
                .unwrap()
                .starts_with("GET /api/health HTTP/1.1\r\n"));
            tokio::time::sleep(delay).await;
            let _ = socket.write_all(response.as_bytes()).await;
        });
        endpoint
    }

    #[tokio::test]
    async fn connected_requires_success_and_aegis_status_ok() {
        let endpoint = serve("200 OK", r#"{"status":"ok"}"#, Duration::ZERO).await;
        let result = check(&endpoint, Duration::from_secs(2)).await;
        assert_eq!(result.status, BackendStatus::Connected);
        assert_eq!(result.message, None);
    }

    #[tokio::test]
    async fn unavailable_for_http_errors_redirects_and_invalid_contracts() {
        for (status, body) in [
            ("503 Service Unavailable", r#"{"status":"ok"}"#),
            ("302 Found", ""),
            ("204 No Content", ""),
            ("200 OK", "<html>proxy</html>"),
            ("200 OK", "{}"),
            ("200 OK", r#"{"status":"degraded"}"#),
            ("200 OK", r#"{"status":true}"#),
        ] {
            let endpoint = serve(status, body, Duration::ZERO).await;
            assert_eq!(
                check(&endpoint, Duration::from_secs(2)).await.status,
                BackendStatus::Unavailable
            );
        }
    }

    #[tokio::test]
    async fn oversized_response_is_rejected() {
        let endpoint = serve("200 OK", &"x".repeat(4097), Duration::ZERO).await;
        assert_eq!(
            check(&endpoint, Duration::from_secs(2)).await.message,
            Some("Backend health response exceeded the size limit.")
        );
    }

    #[tokio::test]
    async fn timeout_is_bounded_and_reported() {
        let endpoint = serve("200 OK", r#"{"status":"ok"}"#, Duration::from_secs(2)).await;
        assert_eq!(
            check(&endpoint, Duration::from_millis(100)).await.message,
            Some("Backend health request timed out.")
        );
    }

    #[tokio::test]
    async fn offline_then_retry_can_recover() {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let endpoint = Endpoint::parse(&format!("http://{address}"), true, false).unwrap();
        drop(listener);
        assert_eq!(
            check(&endpoint, Duration::from_secs(2)).await.status,
            BackendStatus::Unavailable
        );
        let listener = TcpListener::bind(address).await.unwrap();
        tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();
            let mut request = [0; 1024];
            socket.read(&mut request).await.unwrap();
            socket.write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 15\r\nConnection: close\r\n\r\n{\"status\":\"ok\"}").await.unwrap();
        });
        assert_eq!(
            check(&endpoint, Duration::from_secs(2)).await.status,
            BackendStatus::Connected
        );
    }

    // Opt-in read-only probe of a real backend; never stops a shared service.
    #[tokio::test]
    #[ignore = "requires AEGIS_NODE_TEST_API_BASE_URL pointing to a running Aegis backend"]
    async fn real_aegis_backend_health() {
        let endpoint = Endpoint::parse(
            &std::env::var("AEGIS_NODE_TEST_API_BASE_URL").unwrap(),
            true,
            false,
        )
        .unwrap();
        assert_eq!(
            check(&endpoint, Duration::from_secs(5)).await.status,
            BackendStatus::Connected
        );
    }
}
