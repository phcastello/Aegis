//! The only remotely executable operation. No credential or arbitrary OS error is public.
use serde::{Deserialize, Serialize};
use std::{collections::VecDeque, future::Future, pin::Pin};
use tokio::sync::Mutex;
#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NotificationInput {
    pub title: String,
    pub body: String,
}
#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NotificationCommand {
    pub command_id: uuid::Uuid,
    pub capability: String,
    pub capability_version: u32,
    pub expires_at: String,
    pub input: NotificationInput,
}
// Diagnostics are a closed, bounded vocabulary; never native error prose.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum AndroidDiagnosticCode {
    AndroidJniUnavailable,
    AndroidJniCallFailed,
    AndroidNativeTimeout,
    AndroidPayloadParseFailed,
    AndroidExpiryParseFailed,
    AndroidInvalidCommandId,
    AndroidInvalidNotificationPayload,
    AndroidPermissionDenied,
    AndroidPermissionCheckFailed,
    AndroidChannelMissing,
    AndroidPendingIntentFailed,
    AndroidNotificationBuildFailed,
    AndroidNotificationPostFailed,
    AndroidDedupeReadFailed,
    AndroidDedupePersistFailed,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum NotificationStatus {
    Success,
    Expired,
    Unsupported,
    PermissionDenied,
    Failed,
    Duplicate,
}
#[derive(Clone, Debug, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NotificationResult {
    pub status: NotificationStatus,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub diagnostic_code: Option<AndroidDiagnosticCode>,
}
impl NotificationResult {
    pub fn new(status: NotificationStatus) -> Self {
        Self {
            status,
            diagnostic_code: None,
        }
    }
    pub fn diagnostic(status: NotificationStatus, code: AndroidDiagnosticCode) -> Self {
        Self {
            status,
            diagnostic_code: Some(code),
        }
    }
}
pub trait NotificationSink: Send + Sync {
    fn show<'a>(
        &'a self,
        command: &'a NotificationCommand,
    ) -> Pin<Box<dyn Future<Output = NotificationResult> + Send + 'a>>;
}
pub struct NotificationExecutor {
    sink: Box<dyn NotificationSink>,
    recent: Mutex<VecDeque<(uuid::Uuid, i64)>>,
}
impl NotificationExecutor {
    pub fn new(sink: impl NotificationSink + 'static) -> Self {
        Self {
            sink: Box::new(sink),
            recent: Mutex::new(VecDeque::new()),
        }
    }
    pub async fn execute(&self, c: &NotificationCommand, now: i64) -> NotificationResult {
        let started = tokio::time::Instant::now();
        if c.capability != "notification.show" || c.capability_version != 1 {
            return NotificationResult::new(NotificationStatus::Unsupported);
        }
        let expiry = match chrono::DateTime::parse_from_rfc3339(&c.expires_at) {
            Ok(d) => d.timestamp(),
            Err(_) => return NotificationResult::new(NotificationStatus::Failed),
        };
        if expiry <= now {
            return NotificationResult::new(NotificationStatus::Expired);
        }
        if expiry > now + 300 || c.command_id.is_nil() || !valid(&c.input) {
            return NotificationResult::new(NotificationStatus::Failed);
        }
        // Serialize the short native API invocation with dedupe reservation; never hold the socket writer.
        let mut recent = self.recent.lock().await;
        let now = now + started.elapsed().as_secs() as i64;
        if expiry <= now {
            return NotificationResult::new(NotificationStatus::Expired);
        }
        recent.retain(|(_, at)| now.saturating_sub(*at) < 600);
        if recent.iter().any(|(id, _)| *id == c.command_id) {
            return NotificationResult::new(NotificationStatus::Duplicate);
        }
        let result = tokio::time::timeout(std::time::Duration::from_secs(3), self.sink.show(c))
            .await
            .unwrap_or_else(|_| {
                #[cfg(target_os = "android")]
                {
                    NotificationResult::diagnostic(
                        NotificationStatus::Failed,
                        AndroidDiagnosticCode::AndroidNativeTimeout,
                    )
                }
                #[cfg(not(target_os = "android"))]
                {
                    NotificationResult::new(NotificationStatus::Failed)
                }
            });
        if matches!(
            result.status,
            NotificationStatus::Success | NotificationStatus::Duplicate
        ) {
            if recent.len() == 256 {
                recent.pop_front();
            }
            recent.push_back((c.command_id, now));
        }
        result
    }
}
pub fn valid(input: &NotificationInput) -> bool {
    !input.title.trim().is_empty()
        && input.title.chars().count() <= 120
        && input.body.chars().count() <= 2000
        && input.title.len() + input.body.len() <= 2800
        && !input.title.chars().any(char::is_control)
        && !input
            .body
            .chars()
            .any(|c| c.is_control() && !matches!(c, '\n' | '\t'))
}
#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{
        atomic::{AtomicUsize, Ordering},
        Arc,
    };
    struct Mock(Arc<AtomicUsize>);
    impl NotificationSink for Mock {
        fn show<'a>(
            &'a self,
            _: &'a NotificationCommand,
        ) -> Pin<Box<dyn Future<Output = NotificationResult> + Send + 'a>> {
            Box::pin(async move {
                self.0.fetch_add(1, Ordering::SeqCst);
                NotificationResult::new(NotificationStatus::Success)
            })
        }
    }
    fn command() -> NotificationCommand {
        NotificationCommand {
            command_id: uuid::Uuid::new_v4(),
            capability: "notification.show".into(),
            capability_version: 1,
            expires_at: "2026-10-03T12:01:00Z".into(),
            input: NotificationInput {
                title: "Aegis".into(),
                body: "Olá".into(),
            },
        }
    }
    #[tokio::test]
    async fn execution_dedupe_expiry_and_only_notification() {
        let calls = Arc::new(AtomicUsize::new(0));
        let executor = NotificationExecutor::new(Mock(calls.clone()));
        let mut c = command();
        let now = chrono::DateTime::parse_from_rfc3339("2026-10-03T12:00:00Z")
            .unwrap()
            .timestamp();
        assert_eq!(
            executor.execute(&c, now).await.status,
            NotificationStatus::Success
        );
        assert_eq!(
            executor.execute(&c, now).await.status,
            NotificationStatus::Duplicate
        );
        assert_eq!(
            executor.execute(&c, now + 61).await.status,
            NotificationStatus::Expired
        );
        assert_eq!(calls.load(Ordering::SeqCst), 1);
        c.capability = "shell.execute".into();
        assert_eq!(
            executor.execute(&c, now).await.status,
            NotificationStatus::Unsupported
        );
    }
    #[test]
    fn strict_payload_and_limits() {
        let mut c = command();
        c.input.title = "".into();
        assert!(!valid(&c.input));
        c.input.title = "a".repeat(121);
        assert!(!valid(&c.input));
        assert!(
            serde_json::from_str::<NotificationCommand>("{\"credential\":\"secret\"}").is_err()
        );
    }
    #[test]
    fn native_result_rejects_arbitrary_diagnostics_and_statuses() {
        let result: NotificationResult = serde_json::from_str(
            r#"{"status":"failed","diagnosticCode":"android_notification_build_failed"}"#,
        )
        .unwrap();
        assert_eq!(
            result.diagnostic_code,
            Some(AndroidDiagnosticCode::AndroidNotificationBuildFailed)
        );
        for invalid in [
            r#"{"status":"failed","diagnosticCode":"arbitrary-secret"}"#,
            r#"{"status":"arbitrary-secret"}"#,
            r#"{"status":"failed","stack":"arbitrary-secret"}"#,
        ] {
            assert!(serde_json::from_str::<NotificationResult>(invalid).is_err());
        }
        assert_eq!(
            serde_json::to_string(&NotificationResult::new(NotificationStatus::Success)).unwrap(),
            r#"{"status":"success"}"#
        );
    }
    #[tokio::test]
    async fn renderer_diagnostic_survives_executor_without_reserving_failed_id() {
        struct Failing;
        impl NotificationSink for Failing {
            fn show<'a>(
                &'a self,
                _: &'a NotificationCommand,
            ) -> Pin<Box<dyn Future<Output = NotificationResult> + Send + 'a>> {
                Box::pin(async {
                    NotificationResult::diagnostic(
                        NotificationStatus::Failed,
                        AndroidDiagnosticCode::AndroidNotificationBuildFailed,
                    )
                })
            }
        }
        let executor = NotificationExecutor::new(Failing);
        let now = chrono::DateTime::parse_from_rfc3339("2026-10-03T12:00:00Z")
            .unwrap()
            .timestamp();
        let c = command();
        for _ in 0..2 {
            let result = executor.execute(&c, now).await;
            assert_eq!(result.status, NotificationStatus::Failed);
            assert_eq!(
                result.diagnostic_code,
                Some(AndroidDiagnosticCode::AndroidNotificationBuildFailed)
            );
        }
    }
    #[tokio::test(start_paused = true)]
    async fn stalled_native_call_is_bounded_and_cancelled() {
        struct Guard(Arc<AtomicUsize>);
        impl Drop for Guard {
            fn drop(&mut self) {
                self.0.fetch_sub(1, Ordering::SeqCst);
            }
        }
        struct Stalled(Arc<AtomicUsize>);
        impl NotificationSink for Stalled {
            fn show<'a>(
                &'a self,
                _: &'a NotificationCommand,
            ) -> Pin<Box<dyn Future<Output = NotificationResult> + Send + 'a>> {
                Box::pin(async move {
                    self.0.fetch_add(1, Ordering::SeqCst);
                    let _guard = Guard(self.0.clone());
                    std::future::pending().await
                })
            }
        }
        let active = Arc::new(AtomicUsize::new(0));
        let executor = NotificationExecutor::new(Stalled(active.clone()));
        let now = chrono::DateTime::parse_from_rfc3339("2026-10-03T12:00:00Z")
            .unwrap()
            .timestamp();
        assert_eq!(
            executor.execute(&command(), now).await.status,
            NotificationStatus::Failed
        );
        assert_eq!(active.load(Ordering::SeqCst), 0);
    }
}
