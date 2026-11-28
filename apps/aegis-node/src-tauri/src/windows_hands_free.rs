//! One opt-in listener per desktop installation. The worker owns the microphone,
//! detector, output, and private backend requests even when the chat is hidden.
use crate::{
    windows_barge_in_signal::{BargeInSignal, RenderReference},
    windows_voice_audio::{self, Input, Output, PlaybackControl},
    windows_voice_backend::{
        AcceptedInterruption, Backend, InterruptionContext, InterruptionDecision,
        ResearchSpeechStatus, Result, TurnInspection, TurnReport, VoiceTurnRequest,
    },
    windows_voice_signal::{Background, Capture, Endpoint, Waveform},
    windows_wake_dataset::{Clip, PreRoll, Queue},
    windows_wakeword,
};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{
    fs,
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicIsize, AtomicU64, Ordering},
        Arc, Condvar, Mutex,
    },
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
    Win32::{
        Foundation::HWND,
        System::WinRT::{RoInitialize, RoUninitialize, RO_INIT_MULTITHREADED},
        UI::WindowsAndMessaging::{IsIconic, IsWindowVisible},
    },
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
    generation: u64,
    lease: Option<Uuid>,
    importing: bool,
    recent: Option<RecentConversation>,
    research: Vec<PendingResearch>,
    worker: Option<CancellationToken>,
    exiting: bool,
}

#[derive(Clone, Copy)]
struct ResearchDelivery {
    job: Uuid,
    session: Uuid,
    conversation: Uuid,
    scope: [u8; 32],
    expires: Instant,
}

struct PendingResearch {
    delivery: ResearchDelivery,
    poll: CancellationToken,
    ready: bool,
}

impl State {
    fn clear_research(&mut self) {
        for pending in self.research.drain(..) {
            pending.poll.cancel();
        }
    }
}
struct Shared {
    app: AppHandle,
    dir: PathBuf,
    wake_queue: Arc<Queue>,
    state: Mutex<State>,
    changed: Condvar,
    window_handle: Arc<AtomicIsize>,
    speaking_generation: Arc<AtomicU64>,
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
    fn watch_research(
        self: &Arc<Self>,
        backend: &Backend,
        session: Uuid,
        conversation: Uuid,
        job: Uuid,
    ) {
        let poll = CancellationToken::new();
        let delivery = ResearchDelivery {
            job,
            session,
            conversation,
            scope: backend.scope_key(),
            expires: Instant::now() + Duration::from_secs(600),
        };
        {
            let mut state = self.state.lock().unwrap();
            if state.exiting
                || !state.settings.enabled
                || state.settings.diagnostic
                || state.research.len() >= 8
                || state
                    .research
                    .iter()
                    .any(|pending| pending.delivery.job == job)
            {
                return;
            }
            state.research.push(PendingResearch {
                delivery,
                poll: poll.clone(),
                ready: false,
            });
        }
        let listener = Arc::downgrade(self);
        let watcher = backend.fork(&poll);
        let spawned = thread::Builder::new()
            .name("AegisResearchWatch".into())
            .spawn(move || {
                let ready = loop {
                    if poll.is_cancelled() || Instant::now() >= delivery.expires {
                        break false;
                    }
                    match watcher.research_speech_status(job, conversation) {
                        Ok(ResearchSpeechStatus::Ready) => break true,
                        Ok(ResearchSpeechStatus::Unavailable)
                        | Err("pairing_required" | "cancelled" | "backend_response") => {
                            break false
                        }
                        _ => {}
                    }
                    tauri::async_runtime::block_on(async {
                        tokio::select! {
                            _ = poll.cancelled() => {},
                            _ = tokio::time::sleep(Duration::from_secs(3)) => {}
                        }
                    });
                };
                if let Some(listener) = listener.upgrade() {
                    let mut state = listener.state.lock().unwrap();
                    if ready && !poll.is_cancelled() {
                        if let Some(pending) = state
                            .research
                            .iter_mut()
                            .find(|pending| pending.delivery.job == job)
                        {
                            pending.ready = true;
                        }
                    } else {
                        state.research.retain(|pending| pending.delivery.job != job);
                    }
                }
            });
        if spawned.is_err() {
            self.state
                .lock()
                .unwrap()
                .research
                .retain(|pending| pending.delivery.job != job);
        }
    }

    fn next_research(&self) -> Option<ResearchDelivery> {
        let mut state = self.state.lock().unwrap();
        if state.exiting
            || !state.settings.enabled
            || state.settings.diagnostic
            || state.lease.is_some()
        {
            return None;
        }
        state.research.retain(|pending| {
            let live = pending.ready || Instant::now() < pending.delivery.expires;
            if !live {
                pending.poll.cancel();
            }
            live
        });
        let index = state.research.iter().position(|pending| pending.ready)?;
        Some(state.research.remove(index).delivery)
    }

    fn publish(&self, status: &'static str) -> u64 {
        let generation = {
            let mut state = self.state.lock().unwrap();
            if state.status == status {
                return state.generation;
            }
            state.status = status;
            state.generation += 1;
            self.speaking_generation.store(
                if status == "speaking" {
                    state.generation
                } else {
                    0
                },
                Ordering::Release,
            );
            state.generation
        };
        let _ = self.app.emit(
            "aegis-windows-voice-state",
            json!({"status":status,"message":message(status),"generation":generation}),
        );
        crate::native_notifications::set_voice_tooltip(
            &self.app,
            (status != "disabled").then(|| message(status)),
        );
        generation
    }
    fn waveform_visible(&self) -> bool {
        // Use the existing window's native handle without a synchronous round trip
        // to the UI thread. No signal work or IPC while it is hidden/minimized.
        waveform_visible(&self.window_handle)
    }
    fn publish_waveform(
        &self,
        waveform: &mut Waveform,
        samples: &[i16],
        status: &'static str,
        generation: u64,
        queued_blocks: usize,
    ) {
        if let Some(levels) = waveform.push(
            samples,
            self.waveform_visible(),
            Instant::now(),
            queued_blocks,
        ) {
            let _ = self.app.emit_to(
                "main",
                "aegis-windows-voice-waveform",
                json!({"status":status,"generation":generation,"levels":levels.as_slice()}),
            );
        }
    }
    fn view(&self) -> Value {
        let installed = windows_wakeword::installed(&self.dir);
        let wake_sync = self.wake_queue.state();
        let state = self.state.lock().unwrap();
        json!({"enabled":state.settings.enabled,"diagnostic":state.settings.diagnostic,
            "assistantSelected":true,"permissionGranted":!matches!(state.status,"microphone_unavailable"|"microphone_disconnected"|"device_unavailable"),
            "captureConfirmed":matches!(state.status,"listening"|"capturing"|"processing"|"speaking"|"continuation"),
            "modelReady":installed,"modelName":installed.then_some("Aegis Open v2"),
            "status":state.status,"message":message(state.status),"generation":state.generation,
            "wakeSync":wake_sync})
    }
}
fn waveform_visible(window_handle: &AtomicIsize) -> bool {
    let handle = window_handle.load(Ordering::Acquire);
    if handle == 0 {
        return false;
    }
    let window = HWND(handle as *mut _);
    unsafe { IsWindowVisible(window).as_bool() && !IsIconic(window).as_bool() }
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
            wake_queue: Queue::start(&dir, app.clone())?,
            dir,
            state: Mutex::new(State {
                settings,
                status: if settings.enabled {
                    "starting"
                } else {
                    "disabled"
                },
                generation: 0,
                lease: None,
                importing: false,
                recent: None,
                research: Vec::new(),
                worker: None,
                exiting: false,
            }),
            changed: Condvar::new(),
            window_handle: Arc::new(AtomicIsize::new(0)),
            speaking_generation: Arc::new(AtomicU64::new(0)),
        });
        let worker = shared.clone();
        thread::Builder::new()
            .name("AegisVoice".into())
            .spawn(move || worker_loop(worker))?;
        Ok(Self(shared))
    }
    pub fn set_window_handle(&self, handle: isize) {
        self.0.window_handle.store(handle, Ordering::Release);
    }
    pub fn shutdown(&self) {
        self.0.wake_queue.stop();
        let mut s = self.0.state.lock().unwrap();
        s.exiting = true;
        s.clear_research();
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
            "collectionSync" => {
                let pairing = if enabled.ok_or("invalid_request")? {
                    Some(
                        Backend::open(
                            &core.app,
                            &reqwest::Client::new(),
                            &CancellationToken::new(),
                        )?
                        .scope_key(),
                    )
                } else {
                    None
                };
                core.wake_queue.configure(pairing)?;
            }
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
                state.clear_research();
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
                let mut state = core.state.lock().unwrap();
                state.clear_research();
                if let Some(worker) = &state.worker {
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
        .read_timeout(Duration::from_secs(120))
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
    shared: &Arc<Shared>,
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
    let mut ring = PreRoll::new(shared.wake_queue.epoch());
    let mut mic = Input::open()?;
    let mut history = Background::default();
    shared.publish("listening");
    let confidence = loop {
        ring.before_read(shared.wake_queue.epoch(), || mic.pending());
        let samples = mic.read(cancel)?;
        history.push(&samples);
        ring.push(shared.wake_queue.epoch(), &samples);
        if let Some(score) = detector.accept(&samples)? {
            break score;
        }
        if let Some(result) = shared.next_research() {
            let backend = Backend::open(&shared.app, http, cancel)?;
            if backend.scope_key() == result.scope {
                let levels = CaptureLevels::from(&history);
                if let Some(replacement) =
                    play_research(shared, &mut mic, &backend, cancel, result, levels)?
                {
                    return converse(
                        shared,
                        &mut mic,
                        &backend,
                        cancel,
                        result.session,
                        Some(result.conversation),
                        levels,
                        Some(replacement),
                        None,
                    );
                }
            }
            return Ok(());
        }
    };
    let detected = Instant::now() - Duration::from_millis(20 * mic.pending() as u64);
    let session = Uuid::new_v4();
    let backend = if !diagnostic || shared.wake_queue.epoch() % 2 == 1 {
        Some(Backend::open(&shared.app, http, cancel))
    } else {
        None
    };
    let sample = backend
        .as_ref()
        .and_then(|value| value.as_ref().ok())
        .and_then(|backend| {
            shared.wake_queue.capture(
                session,
                backend.scope_key(),
                &mut ring,
                confidence,
                diagnostic,
            )
        });
    if diagnostic {
        shared.publish("diagnostic_wake");
        return Ok(());
    }
    let backend = backend.ok_or("backend_unavailable")??;
    // The read-only manager request is asynchronous. Start it during the
    // election, not after it; only the winning node may inspect/pause media.
    let lookup_started = Instant::now();
    let lookup = MediaManager::RequestAsync().ok();
    let grant = backend.claim(session, detected, confidence, history.snr());
    if let Some(sample) = &sample {
        sample.record_election(grant);
    }
    if !grant.is_ok_and(|claim| claim.granted) || cancel.is_cancelled() {
        if let Some(operation) = lookup {
            let _ = operation.Cancel();
        }
        if let Some(sample) = sample {
            // Capture only what arrived during the election. Do not keep a losing
            // device busy waiting for the winner's command.
            let mut after = Vec::new();
            for _ in 0..mic.pending().min(150) {
                let Ok(block) = mic.read(cancel) else { break };
                after.extend_from_slice(&block);
            }
            sample.add_command(&after);
        }
        grant?;
        return if cancel.is_cancelled() {
            Err("cancelled")
        } else {
            Ok(())
        };
    }
    let granted_at = Instant::now();
    drop(detector);
    // A completed lookup returns immediately, even when the remaining timeout
    // is zero. An unfinished lookup must not add another full wait to the cue.
    let manager = tauri::async_runtime::block_on(async {
        let lookup = lookup?;
        let budget = media_lookup_remaining(lookup_started.elapsed());
        match tokio::time::timeout(budget, lookup.clone()).await {
            Ok(Ok(manager)) => Some(manager),
            Err(_) => {
                let _ = lookup.Cancel();
                None
            }
            _ => None,
        }
    });
    if cancel.is_cancelled() {
        return Err("cancelled");
    }
    let _paused = PausedMedia::new(manager);
    if cancel.is_cancelled() {
        return Err("cancelled");
    }
    let scope = backend.scope_key();
    let destination = shared
        .state
        .lock()
        .unwrap()
        .recent
        .and_then(|recent| recent.at(scope, detected));
    // A granted session can navigate the existing webview, never activate or
    // surface its window. Emit the intended destination before the voice state.
    let _ = shared.app.emit_to(
        "main",
        "aegis-windows-voice-conversation",
        json!({"conversationId": destination}),
    );
    shared.publish("capturing");
    let cue_started = Instant::now();
    windows_voice_audio::cue(cancel)?;
    // Local diagnostics only: monotonic durations, no recording, words, device
    // identity or backend credentials. Playback time includes driver startup.
    eprintln!(
        "AegisWindowsVoiceCue claimMs={} preparationMs={} playbackMs={} detectionToCueEndMs={}",
        granted_at.duration_since(detected).as_millis(),
        cue_started.duration_since(granted_at).as_millis(),
        cue_started.elapsed().as_millis(),
        detected.elapsed().as_millis()
    );
    converse(
        shared,
        &mut mic,
        &backend,
        cancel,
        session,
        destination,
        CaptureLevels::from(&history),
        None,
        sample,
    )
}

#[derive(Clone, Copy)]
struct CaptureLevels {
    noise: f64,
    sustained_speech_floor: Option<f64>,
}
impl From<&Background> for CaptureLevels {
    fn from(background: &Background) -> Self {
        Self {
            noise: background.noise(),
            sustained_speech_floor: background.sustained_speech_floor(),
        }
    }
}

fn converse(
    shared: &Arc<Shared>,
    mic: &mut Input,
    backend: &Backend,
    cancel: &CancellationToken,
    session: Uuid,
    mut destination: Option<Uuid>,
    levels: CaptureLevels,
    first_replacement: Option<AcceptedInterruption>,
    mut wake_sample: Option<Clip>,
) -> Result<()> {
    let announced = Mutex::new(destination);
    let scope = backend.scope_key();
    let mut generation = shared.publish("capturing");
    let mut first = true;
    let mut replacement = first_replacement;
    let mut tail: Option<(BargeInSignal, RenderReference)> = None;
    loop {
        let samples = if replacement.is_some() {
            Vec::new()
        } else {
            let mut endpoint = Endpoint::new(levels.noise, if first { 6 } else { 2 })
                .prefer_near_speech(levels.sustained_speech_floor);
            // Preserve the inline command buffered during election and allow a
            // full wait after the initial cue. No cue or wakeword on continuation.
            if first {
                endpoint.after_cue(mic.pending());
            }
            let status = if first { "capturing" } else { "continuation" };
            let mut waveform = Waveform::default();
            loop {
                let mut block = mic.read(cancel)?;
                if let Some((signal, reference)) = tail.as_mut() {
                    signal.clean_tail(&mut block, reference, mic.pending());
                }
                shared.publish_waveform(&mut waveform, &block, status, generation, mic.pending());
                match endpoint.push(&block) {
                    Capture::More => {}
                    Capture::Empty => {
                        if let Some(sample) = wake_sample.take() {
                            sample.update(json!({"commandDisposition":"no_speech"}));
                        }
                        return Ok(());
                    }
                    Capture::Complete(audio) => break audio,
                }
            }
        };
        first = false;
        if let Some(sample) = &wake_sample {
            sample.add_command(&samples);
        }
        tail = None;
        shared.publish("processing");
        let monitored = monitor_turn(
            shared,
            mic,
            backend,
            cancel,
            session,
            destination,
            &samples,
            replacement.as_ref(),
            levels,
            &announced,
        )?;
        let report = monitored.report;
        if let Some(sample) = wake_sample.take() {
            let outcome = match &report.result {
                Ok(reply) if reply.ignored => "ignored",
                Ok(_) if report.accepted => "accepted",
                Err("cancelled") => "cancelled",
                _ => "unknown",
            };
            sample.update(json!({"commandDisposition": outcome}));
        }
        destination = report.conversation.or(destination);
        if let (Some(job), Some(conversation)) = (report.research_job, destination) {
            shared.watch_research(backend, session, conversation, job);
        }
        // The accepted marker can arrive just as a candidate is interrupted.
        // Recover its destination after the old worker is joined, exactly once.
        if report.accepted {
            if let Some(conversation) = destination {
                announce_conversation(shared, &announced, conversation);
            }
        }
        if let Some(candidate) = monitored.replacement {
            // Send exactly what the backend adjudicated. Preserve the
            // provisional destination even if the predecessor just committed
            // a conversation; the receipt resolves that race server side.
            destination = candidate.context.conversation;
            replacement = Some(candidate);
            continue;
        }
        let reply = report.result?;
        if reply.ignored {
            return Ok(());
        }
        let conversation = destination.ok_or("backend_response")?;
        {
            let mut state = shared.state.lock().unwrap();
            if cancel.is_cancelled() {
                return Err("cancelled");
            }
            state.recent = Some(RecentConversation {
                scope,
                conversation,
                completed: Instant::now(),
            });
        }
        replacement = None;
        if !reply.continuation {
            return Ok(());
        }
        tail = Some((monitored.signal, monitored.reference));
        generation = shared.publish("continuation");
    }
}

fn play_research(
    shared: &Shared,
    mic: &mut Input,
    backend: &Backend,
    cancel: &CancellationToken,
    result: ResearchDelivery,
    levels: CaptureLevels,
) -> Result<Option<AcceptedInterruption>> {
    let playback = cancel.child_token();
    let reference = RenderReference::default();
    let mut signal =
        BargeInSignal::new(levels.noise).prefer_near_speech(levels.sustained_speech_floor);
    let audio = PlaybackControl::default();
    let (spoken, replacement) = thread::scope(|scope| {
        let worker = backend.fork(&playback);
        let worker_audio = &audio;
        let worker_reference = &reference;
        let worker_playback = &playback;
        let work = scope.spawn(move || {
            worker.speak_research_result(result.job, result.session, || {
                let mut output = Output::controlled(worker_audio.clone())?;
                output.observe_reference(worker_reference.clone());
                if worker_playback.is_cancelled() {
                    return Err("cancelled");
                }
                if !worker_audio.is_paused() {
                    shared.publish("speaking");
                }
                Ok(output)
            })
        });
        let replacement = listen_for_interruption(
            shared,
            mic,
            backend,
            cancel,
            result.session,
            levels,
            &mut signal,
            &reference,
            &audio,
            || work.is_finished(),
            || InterruptionContext {
                conversation: Some(result.conversation),
                predecessor: None,
                research_job: Some(result.job),
            },
            || playback.cancel(),
        );
        if !matches!(replacement, Ok(None)) {
            playback.cancel();
        }
        let outcome = work.join().map_err(|_| "backend_response")?;
        let replacement = replacement?;
        if replacement.is_some() || audio.stopped().is_cancelled() {
            Ok((false, replacement))
        } else {
            outcome.map(|()| (true, None))
        }
    })?;
    if spoken {
        shared.state.lock().unwrap().recent = Some(RecentConversation {
            scope: result.scope,
            conversation: result.conversation,
            completed: Instant::now(),
        });
    }
    Ok(replacement)
}

struct MonitoredTurn {
    report: TurnReport,
    replacement: Option<AcceptedInterruption>,
    signal: BargeInSignal,
    reference: RenderReference,
}

/// Mic remains on its original worker and never crosses a thread boundary.
/// Each model/TTS turn has a child token; its exclusive Output is joined before
/// handing off to a new backend so a stale turn cannot publish or play audio.
fn monitor_turn(
    shared: &Shared,
    mic: &mut Input,
    backend: &Backend,
    cancel: &CancellationToken,
    session: Uuid,
    destination: Option<Uuid>,
    samples: &[i16],
    replacement: Option<&AcceptedInterruption>,
    levels: CaptureLevels,
    announced: &Mutex<Option<Uuid>>,
) -> Result<MonitoredTurn> {
    let turn_cancel = cancel.child_token();
    let reference = RenderReference::default();
    let mut signal =
        BargeInSignal::new(levels.noise).prefer_near_speech(levels.sustained_speech_floor);
    let phase = Mutex::new(());
    let audio = PlaybackControl::default();
    let inspection = TurnInspection::new(destination);
    let (report, replacement) = thread::scope(|scope| {
        let mut worker_backend = backend.fork(&turn_cancel);
        let worker_cancel = turn_cancel.clone();
        let worker_reference = reference.clone();
        let phase = &phase;
        let worker_audio = &audio;
        let inspection = &inspection;
        let work = scope.spawn(move || {
            worker_backend.run_controlled_turn(
                VoiceTurnRequest {
                    session,
                    destination,
                    samples,
                    legacy_interruption: None,
                    replacement,
                    inspection,
                    playback: worker_audio,
                },
                |conversation| {
                    let _guard = phase.lock().unwrap();
                    if !worker_cancel.is_cancelled() {
                        announce_conversation(shared, announced, conversation);
                    }
                },
                |event| {
                    let _guard = phase.lock().unwrap();
                    // Cancellation and new capture already share the same
                    // gate with state/navigation. Only cleanup for this old
                    // turn may follow that boundary.
                    if !worker_cancel.is_cancelled()
                        || matches!(event.kind, "interrupted" | "error" | "ignored")
                    {
                        let _ =
                            shared
                                .app
                                .emit_to("main", "aegis-windows-voice-chat-stream", event);
                    }
                },
                || {
                    let mut output = Output::controlled(worker_audio.clone())?;
                    output.observe_reference(worker_reference);
                    let _guard = phase.lock().unwrap();
                    if worker_cancel.is_cancelled() {
                        return Err("cancelled");
                    }
                    if !worker_audio.is_paused() {
                        shared.publish("speaking");
                    }
                    let app = shared.app.clone();
                    let window = shared.window_handle.clone();
                    let speaking = shared.speaking_generation.clone();
                    let speaking_visible = speaking.clone();
                    output.observe_spectrum(
                        move || speaking_visible.load(Ordering::Acquire) != 0 && waveform_visible(&window),
                        move |levels| {
                            let generation = speaking.load(Ordering::Acquire);
                            if generation != 0 {
                                let _ = app.emit_to("main", "aegis-windows-voice-waveform",
                                json!({"status":"speaking","generation":generation,"levels":levels}));
                            }
                        },
                    );
                    Ok(output)
                },
            )
        });
        let replacement = listen_for_interruption(
            shared,
            mic,
            backend,
            cancel,
            session,
            levels,
            &mut signal,
            &reference,
            &audio,
            || work.is_finished(),
            || inspection.interruption(audio.played_frames()),
            || {},
        );
        if !matches!(replacement, Ok(None)) {
            // A failed local device or an authorized replacement settles the
            // predecessor. Resume and stop-only never cancel its text work.
            let _guard = phase.lock().unwrap();
            turn_cancel.cancel();
        }
        let report = work.join().map_err(|_| "backend_response")?;
        Ok::<_, &'static str>((report, replacement?))
    })?;
    if cancel.is_cancelled() {
        return Err("cancelled");
    }
    Ok(MonitoredTurn {
        report,
        replacement,
        signal,
        reference,
    })
}

fn listen_for_interruption(
    shared: &Shared,
    mic: &mut Input,
    backend: &Backend,
    cancel: &CancellationToken,
    session: Uuid,
    levels: CaptureLevels,
    signal: &mut BargeInSignal,
    reference: &RenderReference,
    audio: &PlaybackControl,
    work_finished: impl Fn() -> bool,
    context: impl Fn() -> InterruptionContext + Sync,
    stop_playback: impl Fn(),
) -> Result<Option<AcceptedInterruption>> {
    let mut capture = None::<(Endpoint, Waveform, u64)>;
    loop {
        if capture.is_none() && work_finished() {
            return Ok(None);
        }
        let mut block = mic.read(cancel)?;
        if audio.stopped().is_cancelled() {
            // Stop ended the spoken interaction. Let its authorized text
            // reach history before another, independent wake.
            continue;
        }
        if let Some((endpoint, waveform, generation)) = capture.as_mut() {
            signal.clean_tail(&mut block, reference, mic.pending());
            shared.publish_waveform(waveform, &block, "capturing", *generation, mic.pending());
            let decision = match endpoint.push(&block) {
                Capture::More => continue,
                Capture::Empty => InterruptionDecision::Resume,
                Capture::Complete(samples) => {
                    shared.publish("processing");
                    adjudicate_interruption(mic, backend, cancel, session, samples, &context)?
                }
            };
            match decision {
                InterruptionDecision::Resume => audio.resume()?,
                InterruptionDecision::Stop => {
                    audio.stop();
                    stop_playback();
                }
                InterruptionDecision::Replace(candidate) => return Ok(Some(candidate)),
            }
            signal.rearm();
            capture = None;
            shared.publish(if audio.has_output() {
                "speaking"
            } else {
                "processing"
            });
        } else if let Some(prefix) = signal.push(&block, reference, mic.pending()) {
            audio.pause()?;
            let generation = shared.publish("capturing");
            capture = Some((
                Endpoint::interrupted(levels.noise, prefix)
                    .prefer_near_speech(levels.sustained_speech_floor),
                Waveform::default(),
                generation,
            ));
        }
    }
}

fn adjudicate_interruption(
    mic: &mut Input,
    backend: &Backend,
    cancel: &CancellationToken,
    session: Uuid,
    samples: Vec<i16>,
    context: &(impl Fn() -> InterruptionContext + Sync),
) -> Result<InterruptionDecision> {
    thread::scope(|scope| {
        let candidate = cancel.child_token();
        let probe = backend.fork(&candidate);
        let probe_cancel = candidate.clone();
        let work = scope.spawn(move || {
            let content = probe.transcribe_candidate(&samples)?;
            if content.is_empty() {
                return Ok(InterruptionDecision::Resume);
            }
            let mut prior = context();
            // The first request may still be in STT. Give its transcript a
            // short chance to arrive while the new candidate is provisional.
            if prior.predecessor.is_none() && prior.research_job.is_none() {
                let start = Instant::now();
                while prior.predecessor.is_none()
                    && !probe_cancel.is_cancelled()
                    && start.elapsed() < Duration::from_secs(12)
                {
                    thread::sleep(Duration::from_millis(25));
                    prior = context();
                }
                if prior.predecessor.is_none() {
                    // Keep the original audio under its original request.
                    // Accepting without its transcript would lose the first
                    // question if this was the first turn of a conversation.
                    return Ok(InterruptionDecision::Resume);
                }
            }
            probe.classify_interruption(session, content, prior)
        });
        while !work.is_finished() {
            // Keep the microphone ring current while waiting for STT/model;
            // these frames are discarded and cannot retrigger a stale cut.
            if let Err(error) = mic.read(cancel) {
                candidate.cancel();
                return Err(error);
            }
        }
        let result = work.join().map_err(|_| "backend_response")?;
        if cancel.is_cancelled() {
            return Err("cancelled");
        }
        // A failed or timed-out provisional decision must never interrupt a response.
        Ok(result.unwrap_or(InterruptionDecision::Resume))
    })
}

fn announce_conversation(shared: &Shared, announced: &Mutex<Option<Uuid>>, conversation: Uuid) {
    let mut previous = announced.lock().unwrap();
    if *previous != Some(conversation) {
        *previous = Some(conversation);
        let _ = shared.app.emit_to(
            "main",
            "aegis-windows-voice-conversation",
            json!({"conversationId":conversation}),
        );
    }
}

const MEDIA_OPERATION_BUDGET: Duration = Duration::from_millis(160);

fn media_lookup_remaining(elapsed: Duration) -> Duration {
    MEDIA_OPERATION_BUDGET.saturating_sub(elapsed)
}

/// Only resume the exact session the listener successfully paused.
struct PausedMedia(Option<MediaSession>);
impl PausedMedia {
    fn new(manager: Option<MediaManager>) -> Self {
        // A media player can stall a remote WinRT operation. Keep this best-effort
        // interaction within the local capture queue and the audible cue budget.
        let paused = tauri::async_runtime::block_on(async {
            let session = manager?.GetCurrentSession().ok()?;
            if session.GetPlaybackInfo().ok()?.PlaybackStatus().ok()? != Playback::Playing {
                return None;
            }
            let operation = session.TryPauseAsync().ok()?;
            match tokio::time::timeout(MEDIA_OPERATION_BUDGET, operation.clone()).await {
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
    fn media_lookup_is_charged_to_the_election_not_the_cue() {
        assert_eq!(
            media_lookup_remaining(Duration::ZERO),
            MEDIA_OPERATION_BUDGET
        );
        assert_eq!(
            media_lookup_remaining(Duration::from_millis(80)),
            Duration::from_millis(80)
        );
        assert_eq!(
            media_lookup_remaining(Duration::from_millis(160)),
            Duration::ZERO
        );
        assert_eq!(
            media_lookup_remaining(Duration::from_millis(650)),
            Duration::ZERO
        );
    }
    #[tokio::test(start_paused = true)]
    async fn zero_remaining_lookup_budget_still_accepts_a_ready_manager() {
        // Tokio's timeout polls the lookup first. A completed RequestAsync
        // can therefore be consumed after the election even at zero budget.
        let ready = tokio::time::timeout(Duration::ZERO, std::future::ready(42)).await;
        assert_eq!(ready.unwrap(), 42);
        let stalled = tokio::time::timeout(Duration::ZERO, std::future::pending::<()>()).await;
        assert!(stalled.is_err());
    }
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
