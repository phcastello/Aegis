//! Opt-in queue for the private wake-word dataset. A clip belongs to the pairing
//! that heard it. Release the SQLite lock before any network request.
use crate::{
    windows_voice_backend::{Backend, Result},
    windows_voice_signal::{wav, RATE},
    windows_wakeword,
};
use base64::{engine::general_purpose::STANDARD, Engine};
use rusqlite::{params, Connection, OptionalExtension};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::{
    collections::VecDeque,
    fs,
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicU64, Ordering},
        mpsc::{self, Receiver, SyncSender},
        Arc, Mutex,
    },
    thread,
    time::Duration,
};
use tauri::AppHandle;
use tokio_util::sync::CancellationToken;
use uuid::Uuid;

const QUEUE_BYTES: usize = 100 * 1024 * 1024;

#[derive(Default)]
pub struct PreRoll {
    epoch: u64,
    samples: VecDeque<i16>,
}
impl PreRoll {
    pub fn push(&mut self, epoch: u64, block: &[i16]) {
        if self.epoch != epoch {
            self.samples.clear();
            self.epoch = epoch;
        }
        if epoch % 2 == 1 {
            self.samples.extend(block);
            self.samples
                .drain(..self.samples.len().saturating_sub(2 * RATE));
        }
    }
}
struct Data {
    db: Option<Connection>,
    token: CancellationToken,
    status: &'static str,
    stopped: bool,
}
pub struct Queue {
    path: PathBuf,
    epoch: AtomicU64,
    data: Mutex<Data>,
    signal: SyncSender<()>,
}
pub struct Clip {
    queue: Arc<Queue>,
    id: Uuid,
    epoch: u64,
}
fn storage<T>(_: T) -> &'static str {
    "storage_unavailable"
}

impl Queue {
    fn new(dir: &Path) -> (Arc<Self>, Receiver<()>) {
        let (signal, rx) = mpsc::sync_channel(1);
        let path = dir.join("wake-upload.sqlite3");
        let queue = Arc::new(Self {
            path,
            epoch: AtomicU64::new(0),
            signal,
            data: Mutex::new(Data {
                db: None,
                token: CancellationToken::new(),
                status: "ready",
                stopped: false,
            }),
        });
        if queue.path.exists() {
            let mut data = queue.data.lock().unwrap();
            let restore = queue.connection(&mut data).and_then(|db| {
                db.execute("UPDATE clips SET ready=1 WHERE ready=0", [])
                    .map_err(storage)?;
                db.query_row("SELECT enabled FROM consent WHERE id=1", [], |r| {
                    r.get::<_, bool>(0)
                })
                .map_err(storage)
            });
            match restore {
                Ok(true) => {
                    queue.epoch.store(1, Ordering::Release);
                }
                Err(_) => data.status = "rejected",
                _ => {}
            }
        }
        (queue, rx)
    }
    pub fn start(dir: &Path, app: AppHandle) -> std::io::Result<Arc<Self>> {
        let (queue, rx) = Self::new(dir);
        let worker = queue.clone();
        thread::Builder::new()
            .name("AegisWakeUpload".into())
            .spawn(move || worker.upload_loop(app, rx))?;
        Ok(queue)
    }
    fn connection<'a>(&self, data: &'a mut Data) -> Result<&'a mut Connection> {
        if data.db.is_none() {
            fs::create_dir_all(self.path.parent().ok_or("storage_unavailable")?)
                .map_err(storage)?;
            let db = Connection::open(&self.path).map_err(storage)?;
            db.execute_batch("PRAGMA secure_delete=ON;
                CREATE TABLE IF NOT EXISTS consent (id INTEGER PRIMARY KEY CHECK(id=1), enabled INTEGER NOT NULL, scope BLOB);
                INSERT OR IGNORE INTO consent VALUES(1, 0, NULL);
                CREATE TABLE IF NOT EXISTS clips (id TEXT PRIMARY KEY, scope BLOB NOT NULL,
                    body TEXT NOT NULL, size INTEGER NOT NULL, ready INTEGER NOT NULL, rejected INTEGER NOT NULL DEFAULT 0);").map_err(storage)?;
            data.db = Some(db);
        }
        Ok(data.db.as_mut().unwrap())
    }
    pub fn epoch(&self) -> u64 {
        self.epoch.load(Ordering::Acquire)
    }
    pub fn state(&self) -> (bool, &'static str) {
        let data = self.data.lock().unwrap();
        (self.epoch() % 2 == 1, data.status)
    }
    pub fn configure(&self, scope: Option<[u8; 32]>) -> Result<()> {
        let enabled = scope.is_some();
        let mut data = self.data.lock().unwrap();
        if (self.epoch() % 2 == 1) == enabled {
            return Ok(());
        }
        let tx = self.connection(&mut data)?.transaction().map_err(storage)?;
        tx.execute(
            "UPDATE consent SET enabled=?1,scope=?2 WHERE id=1",
            params![enabled, scope.as_ref().map(|v| v.as_slice())],
        )
        .map_err(storage)?;
        if !enabled {
            tx.execute("DELETE FROM clips", []).map_err(storage)?;
        }
        tx.commit().map_err(storage)?;
        data.token.cancel();
        data.token = CancellationToken::new();
        data.status = "ready";
        self.epoch.fetch_add(1, Ordering::AcqRel);
        let _ = self.signal.try_send(());
        Ok(())
    }
    pub fn stop(&self) {
        let mut data = self.data.lock().unwrap();
        data.stopped = true;
        data.token.cancel();
        let _ = self.signal.try_send(());
    }
    pub fn capture(
        self: &Arc<Self>,
        id: Uuid,
        scope: [u8; 32],
        ring: &mut PreRoll,
        score: f32,
        diagnostic: bool,
    ) -> Option<Clip> {
        let mut data = self.data.lock().unwrap();
        let epoch = self.epoch();
        if epoch % 2 == 0 || ring.epoch != epoch || ring.samples.is_empty() {
            return None;
        }
        let paired = self.connection(&mut data).and_then(|db| {
            db.query_row(
                "SELECT enabled=1 AND scope=?1 FROM consent WHERE id=1",
                [scope.as_slice()],
                |row| row.get::<_, bool>(0),
            )
            .map_err(storage)
        });
        if paired != Ok(true) {
            data.status = "rejected";
            return None;
        }
        let audio = wav(ring.samples.make_contiguous());
        let body = json!({"schemaVersion":1, "groupId":id, "activatedAt":chrono::Utc::now().to_rfc3339(),
            "origin": if diagnostic {"diagnostic"} else {"wake_word"}, "disposition":"candidate",
            "appVersion":option_env!("AEGIS_SOURCE_VERSION").unwrap_or(env!("CARGO_PKG_VERSION")),
            "build":env!("AEGIS_INSTALLATION_CODE").parse::<u32>().ok(), "modelVersion":"Aegis Open v2",
            "modelSha256":windows_wakeword::BUNDLE_DIGEST, "decisionScore":score,
            "sha256":format!("{:x}", Sha256::digest(&audio)), "wav":STANDARD.encode(audio)}).to_string();
        let stored = self.connection(&mut data).and_then(|db| {
            db.execute(
                "INSERT INTO clips(id,scope,body,size,ready) SELECT ?1,?2,?3,?4,0
                WHERE (SELECT coalesce(sum(size),0) FROM clips) + ?4 <= ?5",
                params![
                    id.to_string(),
                    scope.as_slice(),
                    body,
                    body.len(),
                    QUEUE_BYTES
                ],
            )
            .map_err(storage)
        });
        if stored != Ok(1) {
            data.status = if stored == Ok(0) {
                "queue_full"
            } else {
                "rejected"
            };
            return None;
        }
        data.status = "waiting";
        Some(Clip {
            queue: self.clone(),
            id,
            epoch,
        })
    }
    fn upload_loop(&self, app: AppHandle, rx: Receiver<()>) {
        let Ok(client) = reqwest::Client::builder()
            .redirect(reqwest::redirect::Policy::none())
            .build()
        else {
            return;
        };
        loop {
            let (token, epoch) = {
                let data = self.data.lock().unwrap();
                if data.stopped {
                    return;
                }
                (data.token.clone(), self.epoch())
            };
            if epoch % 2 == 0 {
                if rx.recv().is_err() {
                    return;
                }
                continue;
            }
            if let Ok(backend) = Backend::open(&app, &client, &token) {
                let pending = {
                    let mut data = self.data.lock().unwrap();
                    self.connection(&mut data).and_then(|db| db.query_row(
                        "SELECT id,body FROM clips WHERE scope=?1 AND ready=1 AND rejected=0 ORDER BY rowid LIMIT 1",
                        [backend.scope_key().as_slice()], |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?)))
                        .optional().map_err(storage))
                };
                if let Ok(Some((id, body))) = pending {
                    let result = Uuid::parse_str(&id)
                        .map_err(|_| "sample_rejected")
                        .and_then(|id| backend.upload_wake_sample(id, body));
                    let mut data = self.data.lock().unwrap();
                    if self.epoch() != epoch {
                        continue;
                    }
                    let db = data.db.as_ref().unwrap();
                    match result {
                        Ok(()) if db.execute("DELETE FROM clips WHERE id=?1", [&id]).is_ok() => {
                            data.status = "sent";
                            continue;
                        }
                        Err("sample_rejected")
                            if db
                                .execute("UPDATE clips SET rejected=1 WHERE id=?1", [&id])
                                .is_ok() =>
                        {
                            data.status = "rejected";
                            continue;
                        }
                        _ => data.status = "waiting",
                    }
                }
            }
            let _ = rx.recv_timeout(Duration::from_secs(45));
        }
    }
}
impl Clip {
    pub fn update(&self, change: Value) {
        let mut data = self.queue.data.lock().unwrap();
        if self.queue.epoch() != self.epoch {
            return;
        }
        if let Some(db) = &data.db {
            let patch = change.to_string();
            let result = db.execute(
                "UPDATE clips SET body=json_patch(body, ?2), size=length(json_patch(body, ?2))
                WHERE id=?1 AND ready=0 AND (SELECT coalesce(sum(size),0) FROM clips) + ?3 <= ?4",
                params![self.id.to_string(), patch, patch.len(), QUEUE_BYTES],
            );
            if !matches!(result, Ok(1)) {
                data.status = "rejected";
            }
        }
    }
    pub fn add_command(&self, samples: &[i16]) {
        if !samples.is_empty() {
            let audio = wav(&samples[..samples.len().min(20 * RATE)]);
            self.update(json!({"postSha256":format!("{:x}",Sha256::digest(&audio)), "postWav":STANDARD.encode(audio)}));
        }
    }
}
impl Drop for Clip {
    fn drop(&mut self) {
        let mut data = self.queue.data.lock().unwrap();
        if self.queue.epoch() != self.epoch {
            return;
        }
        if let Some(db) = &data.db {
            if db
                .execute(
                    "UPDATE clips SET ready=1 WHERE id=?1",
                    [self.id.to_string()],
                )
                .is_err()
            {
                data.status = "rejected";
            }
        }
        let _ = self.queue.signal.try_send(());
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn offline_loser_survives_restart_and_opt_out_discards_pending_audio() {
        let dir = tempfile::tempdir().unwrap();
        let (queue, _) = Queue::new(dir.path());
        let mut ring = PreRoll::default();
        ring.push(queue.epoch(), &[1; 320]);
        let id = Uuid::new_v4();
        assert!(queue.capture(id, [5; 32], &mut ring, 0.7, false).is_none());
        queue.configure(Some([5; 32])).unwrap();
        ring.push(queue.epoch(), &[2; 320]);
        assert_eq!(ring.samples.len(), 320);
        let clip = queue.capture(id, [5; 32], &mut ring, 0.7, false).unwrap();
        clip.update(json!({"disposition":"election_lost"}));
        drop(clip);
        drop(queue);
        let (queue, _) = Queue::new(dir.path());
        assert!(queue.state().0);
        let read = || {
            let mut data = queue.data.lock().unwrap();
            queue
                .connection(&mut data)
                .unwrap()
                .query_row(
                    "SELECT body FROM clips WHERE ready=1 AND scope=?1",
                    [[5u8; 32].as_slice()],
                    |row| row.get::<_, String>(0),
                )
                .optional()
                .unwrap()
        };
        let body: Value = serde_json::from_str(&read().unwrap()).unwrap();
        assert_eq!(body["disposition"], "election_lost");
        assert!(body.get("postWav").is_none());
        queue.configure(None).unwrap();
        assert!(read().is_none());
        queue.configure(Some([5; 32])).unwrap();
        assert!(queue.capture(id, [5; 32], &mut ring, 0.7, false).is_none());
    }
}
