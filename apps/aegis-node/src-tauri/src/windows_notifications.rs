//! Real Windows toast actions use a registered URI activation and a native durable outbox.
//! URIs contain an opaque local ActionId only. Credentials and interaction tokens stay native.
use crate::node_notification::{
    NotificationCommand, NotificationResult, NotificationStatus, ReminderNotificationInput,
    ReminderWithdrawInput,
};
use rusqlite::{params, Connection, OptionalExtension};
use serde_json::json;
use std::sync::{Arc, OnceLock};
use tauri::{AppHandle, Emitter, Manager};
use windows::{
    core::HSTRING,
    Data::Xml::Dom::XmlDocument,
    UI::Notifications::{ToastNotification, ToastNotificationManager},
};
static PRESENTATION_GATE: std::sync::Mutex<()> = std::sync::Mutex::new(());
static OPEN: OnceLock<std::sync::Mutex<Option<String>>> = OnceLock::new();
static WAKE: OnceLock<Arc<tokio::sync::Notify>> = OnceLock::new();
fn db(app: &AppHandle) -> Result<Connection, ()> {
    let dir = app.path().app_local_data_dir().map_err(|_| ())?;
    std::fs::create_dir_all(&dir).map_err(|_| ())?;
    let db = Connection::open(dir.join("native-reminder-actions-v1.sqlite")).map_err(|_| ())?;
    db.busy_timeout(std::time::Duration::from_secs(2))
        .map_err(|_| ())?;
    db.execute_batch("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;
      CREATE TABLE IF NOT EXISTS withdrawals(id TEXT PRIMARY KEY,generation INTEGER NOT NULL,expires INTEGER NOT NULL);
      CREATE TABLE IF NOT EXISTS presentations(id TEXT PRIMARY KEY,revision INTEGER NOT NULL,generation INTEGER NOT NULL,token TEXT NOT NULL,expires INTEGER NOT NULL);
      CREATE TABLE IF NOT EXISTS actions(id TEXT PRIMARY KEY,reminderId TEXT NOT NULL,revision INTEGER NOT NULL,generation INTEGER NOT NULL,action TEXT NOT NULL,token TEXT NOT NULL,created INTEGER NOT NULL,queued INTEGER NOT NULL DEFAULT 0);
      CREATE TABLE IF NOT EXISTS commands(id TEXT PRIMARY KEY,expires INTEGER NOT NULL);").map_err(|_|())?;
    Ok(db)
}
fn xml(text: &str) -> String {
    text.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}
fn group(id: uuid::Uuid) -> String {
    id.simple().to_string()
}
fn register() -> Result<(), ()> {
    let exe = std::env::current_exe().map_err(|_| ())?;
    let root = windows_registry::CURRENT_USER
        .create("Software\\Classes\\aegis-reminder")
        .map_err(|_| ())?;
    root.set_string("", "URL:Aegis reminder").map_err(|_| ())?;
    root.set_string("URL Protocol", "").map_err(|_| ())?;
    let command = root.create("shell\\open\\command").map_err(|_| ())?;
    command
        .set_string(
            "",
            &format!("\"{}\" --reminder-action \"%1\"", exe.display()),
        )
        .map_err(|_| ())?;
    Ok(())
}
pub fn supported(app: &AppHandle) -> bool {
    register().is_ok() && db(app).is_ok()
}
pub fn show(app: &AppHandle, command: &NotificationCommand) -> NotificationResult {
    let result = (|| -> Result<NotificationStatus, ()> {
        if command.capability == "reminder.withdraw" {
            let input: ReminderWithdrawInput =
                serde_json::from_value(command.input.clone()).map_err(|_| ())?;
            withdraw(app, input.reminder_id, input.generation)?;
            return Ok(NotificationStatus::Success);
        }
        let _presentation = PRESENTATION_GATE.lock().map_err(|_| ())?;
        let mut database = db(app)?;
        let now = chrono::Utc::now().timestamp();
        database
            .execute("DELETE FROM commands WHERE expires<?", params![now])
            .map_err(|_| ())?;
        database
            .execute("DELETE FROM withdrawals WHERE expires<?", params![now])
            .map_err(|_| ())?;
        if database
            .query_row(
                "SELECT 1 FROM commands WHERE id=?",
                params![command.command_id.to_string()],
                |_| Ok(()),
            )
            .optional()
            .map_err(|_| ())?
            .is_some()
        {
            return Ok(NotificationStatus::Duplicate);
        }
        let (title, body, launch, buttons, tag, toast_group) = if command.capability
            == "reminder.notify"
        {
            let input: ReminderNotificationInput =
                serde_json::from_value(command.input.clone()).map_err(|_| ())?;
            let tx = database.transaction().map_err(|_| ())?;
            let withdrawn: Option<u64> = tx
                .query_row(
                    "SELECT generation FROM withdrawals WHERE id=?",
                    params![input.reminder_id.to_string()],
                    |r| r.get(0),
                )
                .optional()
                .map_err(|_| ())?;
            if withdrawn.is_some_and(|g| g >= input.generation) {
                return Ok(NotificationStatus::Expired);
            }
            let previous: Option<(u64, u64)> = tx
                .query_row(
                    "SELECT revision,generation FROM presentations WHERE id=?",
                    params![input.reminder_id.to_string()],
                    |r| Ok((r.get(0)?, r.get(1)?)),
                )
                .optional()
                .map_err(|_| ())?;
            if previous.is_some_and(|(r, g)| {
                g > input.generation || g == input.generation && r > input.revision
            }) {
                return Ok(NotificationStatus::Expired);
            }
            tx.execute("DELETE FROM actions WHERE created<?", params![now - 604800])
                .map_err(|_| ())?;
            tx.execute("DELETE FROM presentations WHERE expires<?", params![now])
                .map_err(|_| ())?;
            tx.execute("INSERT OR REPLACE INTO presentations(id,revision,generation,token,expires) VALUES(?,?,?,?,?)",params![input.reminder_id.to_string(),input.revision,input.generation,input.interaction_token,now+604800]).map_err(|_|())?;
            let mut ids = Vec::new();
            for action in ["open", "acknowledge", "snooze"] {
                let id = uuid::Uuid::new_v4().to_string();
                tx.execute("INSERT INTO actions(id,reminderId,revision,generation,action,token,created) VALUES(?,?,?,?,?,?,?)",params![id,input.reminder_id.to_string(),input.revision,input.generation,action,input.interaction_token,now]).map_err(|_|())?;
                ids.push(format!("aegis-reminder://action/{id}"));
            }
            tx.commit().map_err(|_| ())?;
            let buttons=format!("<actions><action content=\"OK\" activationType=\"protocol\" arguments=\"{}\"/><action content=\"Adiar 10 min\" activationType=\"protocol\" arguments=\"{}\"/></actions>",ids[1],ids[2]);
            (
                input.title,
                input.body,
                ids[0].clone(),
                buttons,
                input.generation.to_string(),
                group(input.reminder_id),
            )
        } else if command.capability == "notification.show" {
            (
                command.input["title"]
                    .as_str()
                    .unwrap_or("Aegis")
                    .to_string(),
                command.input["body"].as_str().unwrap_or("").to_string(),
                String::new(),
                String::new(),
                command.command_id.simple().to_string(),
                "notification".into(),
            )
        } else {
            return Ok(NotificationStatus::Unsupported);
        };
        let doc = XmlDocument::new().map_err(|_| ())?;
        let launch = if launch.is_empty() {
            String::new()
        } else {
            format!(" activationType=\"protocol\" launch=\"{launch}\"")
        };
        doc.LoadXml(&HSTRING::from(format!("<toast{launch}><visual><binding template=\"ToastGeneric\"><text>{}</text><text>{}</text></binding></visual>{buttons}</toast>",xml(&title),xml(&body)))).map_err(|_|())?;
        let toast = ToastNotification::CreateToastNotification(&doc).map_err(|_| ())?;
        toast.SetTag(&HSTRING::from(tag)).map_err(|_| ())?;
        toast
            .SetGroup(&HSTRING::from(toast_group))
            .map_err(|_| ())?;
        let notifier = ToastNotificationManager::CreateToastNotifierWithId(&HSTRING::from(
            app.config().identifier.as_str(),
        ))
        .map_err(|_| ())?;
        if notifier.Setting().map_err(|_| ())?
            != windows::UI::Notifications::NotificationSetting::Enabled
        {
            return Ok(NotificationStatus::PermissionDenied);
        }
        notifier.Show(&toast).map_err(|_| ())?;
        database
            .execute(
                "INSERT OR REPLACE INTO commands(id,expires) VALUES(?,?)",
                params![command.command_id.to_string(), now + 600],
            )
            .map_err(|_| ())?;
        Ok(NotificationStatus::Success)
    })();
    NotificationResult::new(result.unwrap_or(NotificationStatus::Failed))
}
fn withdraw(app: &AppHandle, id: uuid::Uuid, generation: u64) -> Result<(), ()> {
    let _presentation = PRESENTATION_GATE.lock().map_err(|_| ())?;
    let database = db(app)?;
    database.execute("INSERT INTO withdrawals(id,generation,expires) VALUES(?,?,?) ON CONFLICT(id) DO UPDATE SET generation=MAX(generation,excluded.generation),expires=MAX(expires,excluded.expires)",params![id.to_string(),generation,chrono::Utc::now().timestamp()+604800]).map_err(|_|())?;
    ToastNotificationManager::History()
        .map_err(|_| ())?
        .RemoveGroupedTagWithId(
            &HSTRING::from(generation.to_string()),
            &HSTRING::from(group(id)),
            &HSTRING::from(app.config().identifier.as_str()),
        )
        .map_err(|_| ())?;
    let previous: Option<u64> = database
        .query_row(
            "SELECT generation FROM presentations WHERE id=?",
            params![id.to_string()],
            |r| r.get(0),
        )
        .optional()
        .map_err(|_| ())?;
    if let Some(current) = previous.filter(|g| *g <= generation) {
        ToastNotificationManager::History()
            .map_err(|_| ())?
            .RemoveGroupedTagWithId(
                &HSTRING::from(current.to_string()),
                &HSTRING::from(group(id)),
                &HSTRING::from(app.config().identifier.as_str()),
            )
            .map_err(|_| ())?;
        database
            .execute(
                "DELETE FROM presentations WHERE id=? AND generation<=?",
                params![id.to_string(), generation],
            )
            .map_err(|_| ())?;
    }
    Ok(())
}
pub fn activate(app: &AppHandle, args: &[String]) {
    let uri = args
        .iter()
        .find(|v| v.starts_with("aegis-reminder://action/"));
    let Some(uri) = uri else { return };
    if uri.len() > 80 {
        return;
    }
    let Ok(id) = uuid::Uuid::parse_str(uri.trim_start_matches("aegis-reminder://action/")) else {
        return;
    };
    let Ok(mut database) = db(app) else { return };
    let Ok(tx) = database.transaction() else {
        return;
    };
    let action: Option<(String, u64, u64, String, i64)> = tx
        .query_row(
            "SELECT reminderId,revision,generation,action,created FROM actions WHERE id=?",
            params![id.to_string()],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?)),
        )
        .optional()
        .unwrap_or(None);
    let Some((reminder_id, revision, generation, action, created)) = action else {
        return;
    };
    if chrono::Utc::now().timestamp() - created > 604800 {
        return;
    }
    if action == "open" {
        *OPEN
            .get_or_init(|| std::sync::Mutex::new(None))
            .lock()
            .unwrap() = Some(reminder_id.clone());
        crate::native_notifications::open(app);
        let _ = app.emit("aegis-reminder-open", json!({"reminderId":reminder_id}));
        return;
    }
    let valid = tx
        .query_row(
            "SELECT 1 FROM presentations WHERE id=? AND revision=? AND generation=?",
            params![reminder_id, revision, generation],
            |_| Ok(()),
        )
        .optional()
        .ok()
        .flatten()
        .is_some();
    if !valid {
        return;
    }
    let count: i64 = tx
        .query_row("SELECT COUNT(*) FROM actions WHERE queued=1", [], |r| {
            r.get(0)
        })
        .unwrap_or(100);
    if count >= 100 {
        return;
    }
    let already = tx
        .query_row(
            "SELECT 1 FROM actions WHERE reminderId=? AND revision=? AND generation=? AND queued=1",
            params![reminder_id, revision, generation],
            |_| Ok(()),
        )
        .optional()
        .ok()
        .flatten()
        .is_some();
    if !already {
        if tx
            .execute(
                "UPDATE actions SET queued=1 WHERE id=?",
                params![id.to_string()],
            )
            .is_err()
        {
            return;
        }
    }
    if tx.commit().is_err() {
        return;
    }
    drop(database);
    let _ = withdraw(
        app,
        uuid::Uuid::parse_str(&reminder_id).unwrap(),
        generation,
    );
    WAKE.get_or_init(|| Arc::new(tokio::sync::Notify::new()))
        .notify_one();
}
async fn flush(app: &AppHandle) -> Result<bool, ()> {
    if !app
        .state::<crate::node_commands::NodeRuntime>()
        .has_identity()
        .await
    {
        db(app)?
            .execute("DELETE FROM actions WHERE queued=1", [])
            .map_err(|_| ())?;
        return Ok(false);
    }
    let rows = {
        let database = db(app)?;
        let now = chrono::Utc::now().timestamp();
        database
            .execute("DELETE FROM actions WHERE created<?", params![now - 604800])
            .map_err(|_| ())?;
        let mut query=database.prepare("SELECT id,reminderId,revision,generation,action FROM actions WHERE queued=1 ORDER BY created LIMIT 100").map_err(|_|())?;
        let rows = query
            .query_map([], |r| {
                Ok((
                    r.get::<_, String>(0)?,
                    r.get::<_, String>(1)?,
                    r.get::<_, u64>(2)?,
                    r.get::<_, u64>(3)?,
                    r.get::<_, String>(4)?,
                ))
            })
            .map_err(|_| ())?
            .collect::<Result<Vec<_>, _>>()
            .map_err(|_| ())?;
        rows
    };
    for (id, reminder, revision, generation, action) in rows {
        if !app
            .state::<crate::node_commands::NodeRuntime>()
            .has_identity()
            .await
        {
            db(app)?
                .execute("DELETE FROM actions WHERE queued=1", [])
                .map_err(|_| ())?;
            return Ok(false);
        }
        match app.state::<crate::node_commands::NodeRuntime>().domain_action_call(format!("/reminders/{reminder}/actions"),json!({"actionId":id,"expectedRevision":revision,"generation":generation,"action":action,"snoozeMinutes":10})).await {
            Ok(_) => {},
            Err((_, true)) => return Err(()),
            // An authoritative rejection is terminal for this immutable ActionId/revision.
            // Dropping it records no acknowledgement and never rewrites server state.
            Err((_, false)) => {},
        }
        db(app)?
            .execute("DELETE FROM actions WHERE id=?", params![id])
            .map_err(|_| ())?;
    }
    reconcile_presentations(app).await?;
    Ok(false)
}
async fn reconcile_presentations(app: &AppHandle) -> Result<(), ()> {
    let rows = {
        let database = db(app)?;
        let mut query = database
            .prepare("SELECT id,revision,generation FROM presentations WHERE expires>?")
            .map_err(|_| ())?;
        let rows=query.query_map(params![chrono::Utc::now().timestamp()],|r|Ok(json!({"reminderId":r.get::<_,String>(0)?,"revision":r.get::<_,u64>(1)?,"generation":r.get::<_,u64>(2)?}))).map_err(|_|())?.collect::<Result<Vec<_>,_>>().map_err(|_|())?;
        rows
    };
    for batch in rows.chunks(100) {
        let result = app
            .state::<crate::node_commands::NodeRuntime>()
            .domain_call(
                reqwest::Method::POST,
                "/reminders/reconcile".into(),
                json!({"items":batch}),
            )
            .await
            .map_err(|_| ())?;
        for item in result["withdraw"].as_array().ok_or(())? {
            let id =
                uuid::Uuid::parse_str(item["reminderId"].as_str().ok_or(())?).map_err(|_| ())?;
            withdraw(app, id, item["generation"].as_u64().ok_or(())?)?;
        }
    }
    Ok(())
}
pub fn initialize(app: &AppHandle) {
    if !supported(app) {
        return;
    }
    activate(app, &std::env::args().collect::<Vec<_>>());
    let app = app.clone();
    let wake = WAKE
        .get_or_init(|| Arc::new(tokio::sync::Notify::new()))
        .clone();
    tauri::async_runtime::spawn(async move {
        let mut retry = 30u64;
        loop {
            let mut pending = flush(&app).await.is_err();
            if pending
                && !app
                    .state::<crate::node_commands::NodeRuntime>()
                    .has_identity()
                    .await
            {
                if let Ok(database) = db(&app) {
                    let _ = database.execute("DELETE FROM actions WHERE queued=1", []);
                }
                pending = false;
            }
            if pending {
                tokio::select! {_=wake.notified()=>{},_=tokio::time::sleep(std::time::Duration::from_secs(retry))=>{}};
                retry = (retry * 2).min(300);
            } else {
                retry = 30;
                wake.notified().await;
            }
        }
    });
}

pub fn pending(app: &AppHandle) -> u32 {
    db(app)
        .ok()
        .and_then(|db| {
            db.query_row("SELECT COUNT(*) FROM actions WHERE queued=1", [], |r| {
                r.get::<_, u32>(0)
            })
            .ok()
        })
        .unwrap_or(0)
}

pub fn consume_open() -> Option<String> {
    OPEN.get_or_init(|| std::sync::Mutex::new(None))
        .lock()
        .unwrap()
        .take()
}

pub fn granted(app: &AppHandle) -> bool {
    ToastNotificationManager::CreateToastNotifierWithId(&HSTRING::from(
        app.config().identifier.as_str(),
    ))
    .and_then(|n| n.Setting())
    .is_ok_and(|s| s == windows::UI::Notifications::NotificationSetting::Enabled)
}

pub fn enqueue_ui(app: &AppHandle, id: &str, body: &serde_json::Value) -> Result<(), String> {
    let invalid = || "Não foi possível guardar esta ação no aparelho.".to_string();
    let reminder = uuid::Uuid::parse_str(id).map_err(|_| invalid())?;
    let action_id = body["actionId"]
        .as_str()
        .and_then(|v| uuid::Uuid::parse_str(v).ok())
        .ok_or_else(invalid)?;
    let revision = body["expectedRevision"]
        .as_u64()
        .filter(|v| *v > 0)
        .ok_or_else(invalid)?;
    let generation = body["generation"].as_u64().ok_or_else(invalid)?;
    let action = body["action"]
        .as_str()
        .filter(|a| matches!(*a, "acknowledge" | "snooze"))
        .ok_or_else(invalid)?;
    if body
        .get("snoozeMinutes")
        .and_then(|v| v.as_u64())
        .unwrap_or(10)
        != 10
    {
        return Err(invalid());
    }
    let mut database = db(app).map_err(|_| invalid())?;
    let tx = database.transaction().map_err(|_| invalid())?;
    let now = chrono::Utc::now().timestamp();
    tx.execute("DELETE FROM actions WHERE created<?", params![now - 604800])
        .map_err(|_| invalid())?;
    let duplicate = tx
        .query_row(
            "SELECT 1 FROM actions WHERE reminderId=? AND revision=? AND generation=? AND queued=1",
            params![id, revision, generation],
            |_| Ok(()),
        )
        .optional()
        .map_err(|_| invalid())?
        .is_some();
    if !duplicate {
        let count: u32 = tx
            .query_row("SELECT COUNT(*) FROM actions WHERE queued=1", [], |r| {
                r.get(0)
            })
            .map_err(|_| invalid())?;
        if count >= 100 {
            return Err(invalid());
        }
        tx.execute("INSERT INTO actions(id,reminderId,revision,generation,action,token,created,queued) VALUES(?,?,?,?,?,'',?,1)",params![action_id.to_string(),id,revision,generation,action,now]).map_err(|_|invalid())?;
    }
    tx.commit().map_err(|_| invalid())?;
    drop(database);
    withdraw(app, reminder, generation).map_err(|_| invalid())?;
    WAKE.get_or_init(|| Arc::new(tokio::sync::Notify::new()))
        .notify_one();
    Ok(())
}

pub fn request_sync() {
    WAKE.get_or_init(|| Arc::new(tokio::sync::Notify::new()))
        .notify_one();
}
