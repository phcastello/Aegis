//! One opt-in listener per desktop installation. The worker owns the microphone,
//! detector, output, and private backend requests even when the chat is hidden.
use crate::{
    windows_voice_audio::{self, Input},
    windows_voice_backend::{Backend, Result},
    windows_voice_signal::{Background, Capture, Endpoint},
    windows_wakeword,
};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{
    fs,
    path::{Path, PathBuf},
    sync::{Arc, Condvar, Mutex},
    thread,
    time::{Duration, Instant},
};
use tauri::{AppHandle, Emitter, Manager};
use tokio_util::sync::CancellationToken;
use uuid::Uuid;
use windows::{
    Media::Control::{
        GlobalSystemMediaTransportControlsSession as MediaSession,
        GlobalSystemMediaTransportControlsSessionManager as MediaManager,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus as Playback,
    },
    Win32::System::WinRT::{RoInitialize, RoUninitialize, RO_INIT_MULTITHREADED},
};

#[derive(Default, Copy, Clone, Deserialize, Serialize)]
struct Settings {
    enabled: bool,
    diagnostic: bool,
}
/// Eligibility is pinned by detection time, not by network or capture latency.
#[derive(Clone, Copy)]
struct RecentConversation {
    scope: [u8; 32],
    conversation: Uuid,
    completed: Instant,
}
impl RecentConversation {
    fn at(self, scope: [u8; 32], detected: Instant) -> Option<Uuid> {
        (self.scope == scope
            && detected
                .checked_duration_since(self.completed)
                .is_some_and(|age| age < Duration::from_secs(10)))
        .then_some(self.conversation)
    }
}

struct State {
    settings: Settings,
    status: &'static str,
    lease: Option<Uuid>,
    importing: bool,
    recent: Option<RecentConversation>,
    worker: Option<CancellationToken>,
    exiting: bool,
}
struct Shared {
    app: AppHandle,
    dir: PathBuf,
    state: Mutex<State>,
    changed: Condvar,
}
pub(crate) struct WindowsHandsFree(Arc<Shared>);

fn message(status: &str) -> &'static str {
    match status {
        "disabled" => "Escuta desligada.",
        "starting" => "Preparando a escuta.",
        "listening" => "Aguardando a palavra Aegis.",
        "capturing" => "Escutando o pedido.",
        "processing" => "Preparando a resposta.",
        "speaking" => "Reproduzindo a resposta.",
        "continuation" => "Aguardando sua continuação.",
        "manual_audio" => "Áudio em uso pelo chat.",
        "pairing_required" => "Pareie e habilite este computador para usar a escuta.",
        "model_missing" => "Importe o modelo de ativação.",
        "microphone_unavailable" | "device_unavailable" | "microphone_disconnected" => {
            "Verifique o microfone padrão e o acesso ao microfone nas configurações do Windows."
        }
        "speaker_unavailable" => "Verifique a saída de áudio padrão do Windows.",
        "backend_unavailable" | "backend_response" | "election_timeout" => {
            "O servidor de voz não respondeu a tempo ou está indisponível."
        }
        "diagnostic_wake" => "Palavra de ativação detectada.",
        _ => "Falha ao preparar a escuta. Verifique o modelo e os dispositivos de áudio.",
    }
}

impl Shared {
    fn publish(&self, status: &'static str) {
        {
            let mut state = self.state.lock().unwrap();
            if state.status == status {
                return;
            }
            state.status = status;
        }
        let _ = self.app.emit(
            "aegis-windows-voice-state",
            json!({"status":status,"message":message(status)}),
        );
        crate::windows_voice_panel::update(&self.app, status);
    }
    fn view(&self) -> Value {
        let installed = windows_wakeword::installed(&self.dir);
        let state = self.state.lock().unwrap();
        json!({"enabled":state.settings.enabled,"diagnostic":state.settings.diagnostic,
            "assistantSelected":true,"permissionGranted":!matches!(state.status,"microphone_unavailable"|"microphone_disconnected"|"device_unavailable"),
            "captureConfirmed":matches!(state.status,"listening"|"capturing"|"continuation"),
            "modelReady":installed,"modelName":installed.then_some("Aegis Open v2"),
            "status":state.status,"message":message(state.status)})
    }
}
impl WindowsHandsFree {
    pub fn start(app: &AppHandle) -> std::result::Result<Self, Box<dyn std::error::Error>> {
        let dir = app.path().app_local_data_dir()?.join("hands-free");
        let settings = fs::read(dir.join("windows-listener.json"))
            .ok()
            .and_then(|b| serde_json::from_slice::<Settings>(&b).ok())
            .unwrap_or_default();
        let shared = Arc::new(Shared {
            app: app.clone(),
            dir,
            state: Mutex::new(State {
                settings,
                status: if settings.enabled {
                    "starting"
                } else {
                    "disabled"
                },
                lease: None,
                importing: false,
                recent: None,
                worker: None,
                exiting: false,
            }),
            changed: Condvar::new(),
        });
        let worker = shared.clone();
        thread::Builder::new()
            .name("AegisVoice".into())
            .spawn(move || worker_loop(worker))?;
        Ok(Self(shared))
    }
    pub fn shutdown(&self) {
        let mut s = self.0.state.lock().unwrap();
        s.exiting = true;
        if let Some(token) = &s.worker {
            token.cancel();
        }
        self.0.changed.notify_all();
    }
    pub fn operate(
        &self,
        method: &str,
        enabled: Option<bool>,
        diagnostic: Option<bool>,
        lease: Option<String>,
    ) -> Result<Value> {
        let core = &self.0;
        match method {
            "state" => {}
            "configure" => {
                let mut state = core.state.lock().unwrap();
                if state.importing {
                    return Err("audio_busy");
                }
                let settings = Settings {
                    enabled: enabled.ok_or("invalid_request")?,
                    diagnostic: diagnostic.unwrap_or(false),
                };
                if settings.enabled && !windows_wakeword::installed(&core.dir) {
                    return Err("invalid_request");
                }
                persist(&core.dir, settings)?;
                state.recent = None;
                state.settings = settings;
                if let Some(worker) = &state.worker {
                    worker.cancel();
                }
                core.changed.notify_all();
            }
            "import" => {
                {
                    let mut state = core.state.lock().unwrap();
                    if state.settings.enabled || state.importing || state.worker.is_some() {
                        return Err("audio_busy");
                    }
                    state.importing = true;
                }
                let result = windows_wakeword::import(&core.dir);
                core.state.lock().unwrap().importing = false;
                result?;
            }
            "cancel" => {
                if let Some(worker) = &core.state.lock().unwrap().worker {
                    worker.cancel();
                }
            }
            "manualAcquire" | "manualRelease" => {
                let lease = lease
                    .and_then(|s| Uuid::parse_str(&s).ok())
                    .filter(|id| !id.is_nil())
                    .ok_or("invalid_request")?;
                let mut state = core.state.lock().unwrap();
                if method == "manualRelease" {
                    if state.lease == Some(lease) {
                        state.lease = None;
                        core.changed.notify_all();
                    }
                    return Ok(json!({"released":true}));
                }
                if state.lease.is_some_and(|id| id != lease)
                    || matches!(
                        state.status,
                        "capturing" | "processing" | "speaking" | "continuation"
                    )
                {
                    return Ok(json!({"acquired":false}));
                }
                state.lease = Some(lease);
                if let Some(worker) = &state.worker {
                    worker.cancel();
                }
                core.changed.notify_all();
                let (mut state, _) = core
                    .changed
                    .wait_timeout_while(state, Duration::from_secs(3), |s| s.worker.is_some())
                    .unwrap();
                let acquired = state.worker.is_none() && state.lease == Some(lease);
                if !acquired && state.lease == Some(lease) {
                    state.lease = None;
                    core.changed.notify_all();
                }
                return Ok(json!({"acquired":acquired}));
            }
            "permission" => {
                use tauri_plugin_opener::OpenerExt;
                core.app
                    .opener()
                    .open_url("ms-settings:privacy-microphone", None::<&str>)
                    .map_err(|_| "native_failure")?;
            }
            _ => return Err("unsupported"),
        }
        Ok(core.view())
    }
}
fn persist(dir: &Path, settings: Settings) -> Result<()> {
    fs::create_dir_all(dir).map_err(|_| "native_failure")?;
    let tmp = dir.join("windows-listener.tmp");
    fs::write(
        &tmp,
        serde_json::to_vec(&settings).map_err(|_| "native_failure")?,
    )
    .map_err(|_| "native_failure")?;
    fs::rename(tmp, dir.join("windows-listener.json")).map_err(|_| "native_failure")
}

fn worker_loop(shared: Arc<Shared>) {
    // WinRT and Windows ML are apartment-bound. The audio worker never migrates.
    let apartment = unsafe { RoInitialize(RO_INIT_MULTITHREADED) }.is_ok();
    let http = reqwest::Client::builder()
        .redirect(reqwest::redirect::Policy::none())
        .connect_timeout(Duration::from_secs(5))
        .build();
    loop {
        let (token, diagnostic) = {
            let mut s = shared.state.lock().unwrap();
            while !s.exiting && (!s.settings.enabled || s.lease.is_some()) {
                let idle = if s.lease.is_some() {
                    "manual_audio"
                } else {
                    "disabled"
                };
                if s.status != idle {
                    drop(s);
                    shared.publish(idle);
                    s = shared.state.lock().unwrap();
                    continue;
                }
                s = shared.changed.wait(s).unwrap();
            }
            if s.exiting {
                break;
            }
            let token = CancellationToken::new();
            s.worker = Some(token.clone());
            (token, s.settings.diagnostic)
        };
        let outcome = match http.as_ref() {
            Ok(http) => cycle(&shared, http, &token, diagnostic),
            Err(_) => Err("backend_unavailable"),
        };
        let mut state = shared.state.lock().unwrap();
        state.worker = None;
        shared.changed.notify_all();
        if token.is_cancelled() || state.exiting {
            continue;
        }
        if matches!(outcome, Err("input_route_changed")) {
            continue;
        }
        if let Err(error) = outcome {
            drop(state);
            shared.publish(error);
            let state = shared.state.lock().unwrap();
            if state.settings.enabled && state.lease.is_none() && !state.exiting {
                drop(
                    shared
                        .changed
                        .wait_timeout(state, Duration::from_secs(3))
                        .unwrap(),
                );
            }
        }
    }
    if apartment {
        unsafe { RoUninitialize() }
    }
}
fn cycle(
    shared: &Shared,
    http: &reqwest::Client,
    cancel: &CancellationToken,
    diagnostic: bool,
) -> Result<()> {
    shared.publish("starting");
    if !diagnostic
        && !tauri::async_runtime::block_on(
            shared
                .app
                .state::<crate::node_commands::NodeRuntime>()
                .has_identity(),
        )
    {
        return Err("pairing_required");
    }
    let mut detector = windows_wakeword::Detector::open(&shared.dir)?;
    let mut mic = Input::open()?;
    let mut history = Background::default();
    shared.publish("listening");
    let confidence = loop {
        let samples = mic.read(cancel)?;
        history.push(&samples);
        if let Some(score) = detector.accept(&samples)? {
            break score;
        }
    };
    let detected = Instant::now() - Duration::from_millis(20 * mic.pending() as u64);
    if diagnostic {
        shared.publish("diagnostic_wake");
        return Ok(());
    }
    let mut backend = Backend::open(&shared.app, http, cancel)?;
    let session = Uuid::new_v4();
    if !backend.claim(session, detected, confidence, history.snr())? {
        return Ok(());
    }
    drop(detector);
    if cancel.is_cancelled() {
        return Err("cancelled");
    }
    let _paused = PausedMedia::new();
    if cancel.is_cancelled() {
        return Err("cancelled");
    }
    shared.publish("capturing");
    windows_voice_audio::cue(cancel)?;
    let scope = backend.scope_key();
    let mut destination = shared
        .state
        .lock()
        .unwrap()
        .recent
        .and_then(|recent| recent.at(scope, detected));
    let mut first = true;
    loop {
        let mut endpoint = Endpoint::new(history.noise(), if first { 6 } else { 2 });
        // Keep the inline command collected during the election and start
        // counting the full first-speech wait after the audible cue.
        if first {
            endpoint.after_cue(mic.pending());
        }
        let samples = loop {
            match endpoint.push(&mic.read(cancel)?) {
                Capture::More => {}
                Capture::Empty => return Ok(()),
                Capture::Complete(audio) => break audio,
            }
        };
        drop(mic);
        shared.publish("processing");
        let text = backend.transcribe(&samples)?;
        drop(samples);
        if text.is_empty() {
            return Ok(());
        }
        let reply = backend.respond(session, destination, text)?;
        shared.publish("speaking");
        backend.speak(&reply)?;
        {
            let mut state = shared.state.lock().unwrap();
            if cancel.is_cancelled() {
                return Err("cancelled");
            }
            state.recent = Some(RecentConversation {
                scope,
                conversation: reply.conversation,
                completed: Instant::now(),
            });
        }
        first = false;
        if !reply.continuation {
            return Ok(());
        }
        mic = Input::open()?;
        destination = Some(reply.conversation);
        shared.publish("continuation");
    }
}

/// Only resume the exact session the listener successfully paused.
struct PausedMedia(Option<MediaSession>);
impl PausedMedia {
    fn new() -> Self {
        // A media player can stall a remote WinRT operation. Keep this best-effort
        // interaction within the local capture queue and the audible cue budget.
        let paused = tauri::async_runtime::block_on(async {
            let lookup = MediaManager::RequestAsync().ok()?;
            let manager = tokio::time::timeout(Duration::from_millis(160), lookup)
                .await
                .ok()?
                .ok()?;
            let session = manager.GetCurrentSession().ok()?;
            if session.GetPlaybackInfo().ok()?.PlaybackStatus().ok()? != Playback::Playing {
                return None;
            }
            let operation = session.TryPauseAsync().ok()?;
            match tokio::time::timeout(Duration::from_millis(160), operation.clone()).await {
                Ok(Ok(true)) => Some(session),
                Err(_) => {
                    let _ = operation.Cancel();
                    None
                }
                _ => None,
            }
        });
        Self(paused)
    }
}
impl Drop for PausedMedia {
    fn drop(&mut self) {
        if let Some(session) = &self.0 {
            if session
                .GetPlaybackInfo()
                .and_then(|v| v.PlaybackStatus())
                .ok()
                == Some(Playback::Paused)
            {
                let _ = session.TryPlayAsync();
            }
        }
    }
}

#[cfg(test)]
mod continuity_tests {
    use super::*;
    #[test]
    fn recent_destination_uses_detection_time_and_identity() {
        let completed = Instant::now();
        let conversation = Uuid::new_v4();
        let recent = RecentConversation {
            scope: [1; 32],
            conversation,
            completed,
        };
        assert_eq!(
            recent.at([1; 32], completed + Duration::from_millis(9999)),
            Some(conversation)
        );
        assert_eq!(
            recent.at([1; 32], completed + Duration::from_secs(10)),
            None
        );
        assert_eq!(recent.at([2; 32], completed), None);
        assert_eq!(
            recent.at([1; 32], completed - Duration::from_millis(1)),
            None
        );
    }
}
