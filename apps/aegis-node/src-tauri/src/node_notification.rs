//! The only remotely executable operation. No credential or arbitrary OS error is public.
use serde::{Deserialize, Serialize};
use std::{collections::VecDeque, sync::Mutex};
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
pub trait NotificationSink: Send + Sync {
    fn show(&self, command: &NotificationCommand) -> &'static str;
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
    pub fn execute(&self, c: &NotificationCommand, now: i64) -> &'static str {
        if c.capability != "notification.show" || c.capability_version != 1 {
            return "unsupported";
        }
        let expiry = match chrono::DateTime::parse_from_rfc3339(&c.expires_at) {
            Ok(d) => d.timestamp(),
            Err(_) => return "failed",
        };
        if expiry <= now {
            return "expired";
        }
        if expiry > now + 300 || c.command_id.is_nil() || !valid(&c.input) {
            return "failed";
        }
        // Serialize the short native API invocation with dedupe reservation; never hold the socket writer.
        let mut recent = self.recent.lock().unwrap();
        recent.retain(|(_, at)| now.saturating_sub(*at) < 600);
        if recent.iter().any(|(id, _)| *id == c.command_id) {
            return "duplicate";
        }
        let result = self.sink.show(c);
        if result == "success" || result == "duplicate" {
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
        fn show(&self, _: &NotificationCommand) -> &'static str {
            self.0.fetch_add(1, Ordering::SeqCst);
            "success"
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
    #[test]
    fn execution_dedupe_expiry_and_only_notification() {
        let calls = Arc::new(AtomicUsize::new(0));
        let executor = NotificationExecutor::new(Mock(calls.clone()));
        let mut c = command();
        let now = chrono::DateTime::parse_from_rfc3339("2026-10-03T12:00:00Z")
            .unwrap()
            .timestamp();
        assert_eq!(executor.execute(&c, now), "success");
        assert_eq!(executor.execute(&c, now), "duplicate");
        assert_eq!(executor.execute(&c, now + 61), "expired");
        assert_eq!(calls.load(Ordering::SeqCst), 1);
        c.capability = "shell.execute".into();
        assert_eq!(executor.execute(&c, now), "unsupported");
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
}
