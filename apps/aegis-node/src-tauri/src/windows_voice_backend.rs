use crate::{
    node_commands::NodeRuntime,
    windows_voice_audio::{Output, PlaybackControl},
    windows_voice_signal::wav,
};
use reqwest::{Client, RequestBuilder, Response, StatusCode};
use serde::Serialize;
use serde_json::{json, Value};
use std::{
    collections::HashSet,
    sync::{
        atomic::{AtomicU8, Ordering},
        Arc, Mutex,
    },
    thread,
    time::{Duration, Instant},
};
use tauri::{AppHandle, Manager};
use tokio_util::sync::CancellationToken;
use uuid::Uuid;

pub(crate) struct Backend {
    current_credential: Arc<dyn Fn() -> Option<String> + Send + Sync>,
    origin: String,
    credential: String,
    client: Client,
    cancel: CancellationToken,
    turn: Option<Uuid>,
    speech: Option<Uuid>,
}
pub(crate) struct TurnReply {
    pub ignored: bool,
    pub continuation: bool,
}
pub(crate) type Result<T> = std::result::Result<T, &'static str>;

#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct PlaybackInterruption {
    #[serde(skip)]
    pub conversation: Uuid,
    pub turn_id: Uuid,
    pub assistant_message_id: Uuid,
    pub segment_id: Uuid,
    pub sequence: usize,
    pub played_frames_in_segment: u64,
    pub segment_frames: Option<u64>,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) enum Interruption {
    Playback(PlaybackInterruption),
    Generation {
        turn_id: Uuid,
        previous_content: String,
    },
    ResearchPlayback,
}

#[derive(Clone)]
pub(crate) struct InterruptionContext {
    pub conversation: Option<Uuid>,
    pub predecessor: Option<Interruption>,
    pub research_job: Option<Uuid>,
}

pub(crate) struct AcceptedInterruption {
    pub context: InterruptionContext,
    pub content: String,
    pub receipt: Uuid,
}

pub(crate) enum InterruptionDecision {
    Resume,
    Stop,
    Replace(AcceptedInterruption),
}

pub(crate) struct VoiceTurnRequest<'a> {
    pub session: Uuid,
    pub destination: Option<Uuid>,
    pub samples: &'a [i16],
    pub legacy_interruption: Option<&'a Interruption>,
    pub replacement: Option<&'a AcceptedInterruption>,
    pub inspection: &'a TurnInspection,
    pub playback: &'a PlaybackControl,
}

pub(crate) struct TurnReport {
    pub result: Result<TurnReply>,
    pub turn_id: Uuid,
    pub conversation: Option<Uuid>,
    pub accepted: bool,
    pub transcript: Option<String>,
    pub chat_started: bool,
    pub playback: Option<PlaybackInterruption>,
    pub research_job: Option<Uuid>,
}

pub(crate) enum ResearchSpeechStatus {
    Pending,
    Ready,
    Unavailable,
}

#[derive(Clone, Debug, PartialEq, Serialize)]
pub(crate) struct VoiceToolStatus {
    pub category: &'static str,
    pub state: String,
    pub message: String,
}

#[derive(Clone, Debug, PartialEq, Serialize)]
pub(crate) struct VoiceResearchSourceCount {
    pub url: String,
    pub count: u64,
}

/// Local projection of validated, already committed chat events and observable web search. Speech
/// playback and server reconciliation remain independent of this observer.
#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct VoiceChatStreamEvent {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub turn_id: Uuid,
    pub conversation_id: Option<Uuid>,
    pub assistant_message_id: Option<Uuid>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub segment_id: Option<Uuid>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sequence: Option<usize>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub content: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tool_status: Option<VoiceToolStatus>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sources: Option<Vec<VoiceResearchSourceCount>>,
}

impl VoiceChatStreamEvent {
    fn new(
        kind: &'static str,
        turn_id: Uuid,
        conversation_id: Option<Uuid>,
        assistant_message_id: Option<Uuid>,
    ) -> Self {
        Self {
            kind,
            turn_id,
            conversation_id,
            assistant_message_id,
            segment_id: None,
            sequence: None,
            content: None,
            tool_status: None,
            sources: None,
        }
    }
    fn web_research_sources(turn_id: Uuid, conversation_id: Uuid, event: &Value) -> Option<Self> {
        if parse_uuid(&event["conversationId"]).ok() != Some(conversation_id) {
            return None;
        }
        let sources = event["sources"].as_array()?;
        if sources.is_empty() || sources.len() > 8 {
            return None;
        }
        let sources: Vec<_> = sources
            .iter()
            .filter_map(|source| {
                let url = source["url"].as_str()?;
                let count = source["count"].as_u64()?;
                if !(1..=i32::MAX as u64).contains(&count)
                    || url.encode_utf16().count() > 1024
                    || url.contains('\\')
                    || url.chars().any(|ch| ch.is_whitespace() || ch.is_control())
                {
                    return None;
                }
                let parsed = reqwest::Url::parse(url).ok()?;
                if !matches!(parsed.scheme(), "http" | "https")
                    || (!url.starts_with("https://") && !url.starts_with("http://"))
                    || parsed.host().is_none()
                    || !parsed.username().is_empty()
                    || parsed.password().is_some()
                {
                    return None;
                }
                Some(VoiceResearchSourceCount {
                    url: url.to_owned(),
                    count,
                })
            })
            .collect();
        (!sources.is_empty()).then(|| Self {
            sources: Some(sources),
            ..Self::new("web_research_sources", turn_id, Some(conversation_id), None)
        })
    }
    fn tool_status(turn_id: Uuid, conversation_id: Uuid, event: &Value) -> Option<Self> {
        let category = match event["category"].as_str() {
            Some("web_search") => "web_search",
            Some("python") => "python",
            _ => return None,
        };
        if !matches!(
            event["state"].as_str(),
            Some("started" | "completed" | "failed")
        ) || (!event["conversationId"].is_null()
            && parse_uuid(&event["conversationId"]).ok() != Some(conversation_id))
        {
            return None;
        }
        let message = event["message"].as_str()?;
        if message.trim().is_empty()
            || message.chars().count() > 256
            || message.chars().any(char::is_control)
        {
            return None;
        }
        Some(Self {
            tool_status: Some(VoiceToolStatus {
                category,
                state: event["state"].as_str()?.to_owned(),
                message: message.to_owned(),
            }),
            ..Self::new("tool_status", turn_id, Some(conversation_id), None)
        })
    }
    fn segment(turn_id: Uuid, segment: &Segment, content: String) -> Self {
        Self {
            segment_id: Some(segment.id),
            sequence: Some(segment.sequence),
            content: Some(content),
            ..Self::new(
                "final_segment",
                turn_id,
                Some(segment.conversation),
                Some(segment.assistant),
            )
        }
    }
}

pub(crate) trait TurnAudio {
    fn write(&mut self, samples: &[i16], cancel: &CancellationToken) -> Result<()>;
    fn finish(&mut self, cancel: &CancellationToken) -> Result<()>;
    fn played_frames(&self) -> Option<u64>;
}

impl TurnAudio for Output {
    fn write(&mut self, samples: &[i16], cancel: &CancellationToken) -> Result<()> {
        Output::write(self, samples, cancel)
    }
    fn finish(&mut self, cancel: &CancellationToken) -> Result<()> {
        Output::finish(self, cancel)
    }
    fn played_frames(&self) -> Option<u64> {
        Output::played_frames(self)
    }
}

const MAX_PLAYBACK_FRAMES: u64 = 24_000 * 180;
const MAX_CHAT_BYTES: usize = 1024 * 1024;

#[derive(Clone, Debug, PartialEq)]
struct Segment {
    conversation: Uuid,
    assistant: Uuid,
    id: Uuid,
    sequence: usize,
}

#[derive(Default)]
struct PlaybackProgress {
    segments: Vec<(Segment, u64, Option<u64>)>,
}
impl PlaybackProgress {
    fn begin(&mut self, segment: Segment, start: u64) {
        self.segments.push((segment, start, None));
    }
    fn length(&mut self, frames: u64) {
        if let Some((_, _, length)) = self.segments.last_mut() {
            *length = Some(frames);
        }
    }
    fn at(&self, turn_id: Uuid, cursor: u64) -> Option<PlaybackInterruption> {
        if cursor > MAX_PLAYBACK_FRAMES {
            return None;
        }
        let (unit, start, length) = self
            .segments
            .iter()
            .rev()
            .find(|(_, start, _)| cursor >= *start)?;
        let relative = cursor - start;
        Some(PlaybackInterruption {
            conversation: unit.conversation,
            turn_id,
            assistant_message_id: unit.assistant,
            segment_id: unit.id,
            sequence: unit.sequence,
            played_frames_in_segment: length.map_or(relative, |n| relative.min(n)),
            segment_frames: *length,
        })
    }
}

#[derive(Default)]
struct ChatProgress {
    conversation: Option<Uuid>,
    accepted: bool,
    assistant: Option<Uuid>,
    research_job: Option<Uuid>,
}

pub(crate) struct TurnInspection {
    id: Uuid,
    progress: Mutex<ChatProgress>,
    transcript: Mutex<Option<String>>,
    playback: Mutex<PlaybackProgress>,
}

impl TurnInspection {
    pub fn new(conversation: Option<Uuid>) -> Self {
        Self {
            id: Uuid::new_v4(),
            progress: Mutex::new(ChatProgress {
                conversation,
                ..Default::default()
            }),
            transcript: Mutex::new(None),
            playback: Mutex::new(PlaybackProgress::default()),
        }
    }

    pub fn interruption(&self, played_frames: Option<u64>) -> InterruptionContext {
        let conversation = self.progress.lock().unwrap().conversation;
        let position = played_frames
            .and_then(|frames| self.playback.lock().unwrap().at(self.id, frames))
            .filter(|position| Some(position.conversation) == conversation);
        let predecessor = position.map(Interruption::Playback).or_else(|| {
            self.transcript
                .lock()
                .unwrap()
                .clone()
                .map(|previous_content| Interruption::Generation {
                    turn_id: self.id,
                    previous_content,
                })
        });
        InterruptionContext {
            conversation,
            predecessor,
            research_job: None,
        }
    }
}

fn write_interruption(payload: &mut Value, context: &InterruptionContext) {
    match context.predecessor.as_ref() {
        Some(Interruption::Playback(position))
            if context.conversation == Some(position.conversation) =>
        {
            payload["interruptedPlayback"] = json!(position);
        }
        Some(Interruption::Generation {
            turn_id,
            previous_content,
        }) => {
            payload["interruptedGeneration"] =
                json!({"turnId":turn_id,"previousContent":previous_content});
        }
        _ => {}
    }
    if let Some(job) = context.research_job {
        payload["researchJobId"] = json!(job);
    }
}

enum ChatEvent {
    Segment(Segment),
    Done(bool),
    Ignored,
}

enum AudioChunk {
    Pcm(Vec<i16>),
    Complete(u64),
}
struct SpeechUnit {
    segment: Segment,
    receive: tokio::sync::mpsc::Receiver<AudioChunk>,
    // Release only when this unit has drained into the native player; at most
    // current plus one prepared successor can retain a PCM pipe.
    _slot: tokio::sync::OwnedSemaphorePermit,
}
enum SpeechEvent {
    Unit(SpeechUnit),
    Done(bool),
    Ignored,
}

impl Backend {
    pub fn open(app: &AppHandle, client: &Client, cancel: &CancellationToken) -> Result<Self> {
        let origin = crate::config::configured_endpoint()
            .map_err(|_| "backend_unavailable")?
            .base_url();
        let app = app.clone();
        let current_credential = Arc::new(move || {
            tauri::async_runtime::block_on(app.state::<NodeRuntime>().voice_credential())
        });
        let credential = current_credential().ok_or("pairing_required")?;
        Ok(Self {
            current_credential,
            origin,
            credential,
            client: client.clone(),
            cancel: cancel.clone(),
            turn: None,
            speech: None,
        })
    }
    pub fn fork(&self, cancel: &CancellationToken) -> Self {
        Self {
            current_credential: self.current_credential.clone(),
            origin: self.origin.clone(),
            credential: self.credential.clone(),
            client: self.client.clone(),
            cancel: cancel.clone(),
            turn: None,
            speech: None,
        }
    }
    pub fn scope_key(&self) -> [u8; 32] {
        use sha2::{Digest, Sha256};
        let mut hash = Sha256::new();
        hash.update(self.origin.as_bytes());
        hash.update([0]);
        hash.update(self.credential.as_bytes());
        hash.finalize().into()
    }
    fn current(&self) -> Result<()> {
        if self.cancel.is_cancelled() {
            return Err("cancelled");
        }
        let current = (self.current_credential)();
        if current.as_deref() != Some(self.credential.as_str()) {
            return Err("pairing_required");
        }
        Ok(())
    }
    fn post(&self, path: &str, seconds: u64) -> RequestBuilder {
        self.client
            .post(format!("{}{path}", self.origin))
            .header("Authorization", format!("AegisNode {}", self.credential))
            .header("Cache-Control", "no-store")
            .timeout(Duration::from_secs(seconds))
    }
    pub fn upload_wake_sample(&self, id: Uuid, body: String) -> Result<()> {
        self.current()?;
        let request = self
            .client
            .put(format!("{}/api/nodes/me/wake-samples/{id}", self.origin))
            .header("Authorization", format!("AegisNode {}", self.credential))
            .header("Content-Type", "application/json")
            .timeout(Duration::from_secs(20))
            .body(body);
        match self.send_response(request)?.status() {
            StatusCode::NO_CONTENT => Ok(()),
            StatusCode::BAD_REQUEST
            | StatusCode::UNPROCESSABLE_ENTITY
            | StatusCode::CONFLICT
            | StatusCode::PAYLOAD_TOO_LARGE => Err("sample_rejected"),
            _ => Err("backend_unavailable"),
        }
    }
    fn send(&self, request: RequestBuilder) -> Result<Response> {
        self.current()?;
        self.send_checked(request)
    }
    fn send_checked(&self, request: RequestBuilder) -> Result<Response> {
        let response = self.send_response(request)?;
        response
            .status()
            .is_success()
            .then_some(response)
            .ok_or("backend_unavailable")
    }
    fn send_response(&self, request: RequestBuilder) -> Result<Response> {
        tauri::async_runtime::block_on(async {
            tokio::select! {biased;
                _=self.cancel.cancelled()=>Err("cancelled"),
                response=request.send()=>response.map_err(|_|"backend_unavailable")
            }
        })
    }
    fn next(&self, response: &mut Response) -> Result<Option<Vec<u8>>> {
        tauri::async_runtime::block_on(async {
            tokio::select! {biased;
                _=self.cancel.cancelled()=>Err("cancelled"),
                part=response.chunk()=>part.map(|p|p.map(|p|p.to_vec())).map_err(|_|"backend_unavailable")
            }
        })
    }
    fn json(&self, request: RequestBuilder) -> Result<Value> {
        self.json_response(self.send(request)?)
    }
    fn json_response(&self, mut response: Response) -> Result<Value> {
        let mut buf = Vec::new();
        while let Some(chunk) = self.next(&mut response)? {
            if buf.len() + chunk.len() > 256 * 1024 {
                return Err("backend_response");
            }
            buf.extend_from_slice(&chunk);
        }
        serde_json::from_slice(&buf).map_err(|_| "backend_response")
    }

    pub fn research_speech_status(
        &self,
        job: Uuid,
        conversation: Uuid,
    ) -> Result<ResearchSpeechStatus> {
        self.current()?;
        let response = self.send_response(
            self.client
                .get(format!("{}/api/chat/research-jobs/{job}", self.origin))
                .header("Authorization", format!("AegisNode {}", self.credential))
                .header("Cache-Control", "no-store")
                .timeout(Duration::from_secs(6)),
        )?;
        match response.status() {
            StatusCode::NOT_FOUND | StatusCode::GONE => {
                return Ok(ResearchSpeechStatus::Unavailable)
            }
            StatusCode::OK => {}
            _ => return Err("backend_unavailable"),
        }
        let snapshot = self.json_response(response)?;
        if parse_uuid(&snapshot["id"])? != job
            || parse_uuid(&snapshot["conversationId"])? != conversation
        {
            return Err("backend_response");
        }
        match snapshot["state"].as_str() {
            Some("queued" | "running") => Ok(ResearchSpeechStatus::Pending),
            Some("completed" | "failed") => Ok(ResearchSpeechStatus::Ready),
            Some("cancelled") => Ok(ResearchSpeechStatus::Unavailable),
            _ => Err("backend_response"),
        }
    }

    pub fn speak_research_result<A: TurnAudio>(
        &self,
        job: Uuid,
        session: Uuid,
        open: impl FnOnce() -> Result<A>,
    ) -> Result<()> {
        let response = self.send(
            self.client
                .post(format!(
                    "{}/api/chat/research-jobs/{job}/speech",
                    self.origin
                ))
                .header("Authorization", format!("AegisNode {}", self.credential))
                .header("Cache-Control", "no-store")
                .json(&json!({ "voiceSessionId": session })),
        )?;
        let mut open = Some(open);
        let mut output: Option<A> = None;
        self.read_pcm(response, None, None, |frames| {
            let device = match output.as_mut() {
                Some(device) => device,
                None => output.insert(open.take().ok_or("backend_response")?()?),
            };
            device.write(frames, &self.cancel)
        })?;
        output.ok_or("backend_response")?.finish(&self.cancel)
    }
    pub fn claim(
        &self,
        id: Uuid,
        detected: Instant,
        confidence: f32,
        snr: Option<f64>,
    ) -> Result<bool> {
        let mut payload = json!({"activationId":id,"confidence":confidence});
        if let Some(value) = snr {
            payload["snrDb"] = json!(value);
        }
        self.current()?;
        let budget = Duration::from_secs(2)
            .checked_sub(detected.elapsed())
            .ok_or("election_timeout")?;
        let request = self
            .post("/api/nodes/me/voice-activations/claim", 2)
            .timeout(budget)
            .json(&payload);
        let response = self
            .send_checked(request)
            .and_then(|r| self.json_response(r));
        if response.is_err() || self.cancel.is_cancelled() {
            self.cleanup(format!("/api/nodes/me/voice-activations/{id}"));
        }
        let response = response?;
        if detected.elapsed() > Duration::from_secs(2) {
            return Err("election_timeout");
        }
        self.current()?;
        if parse_uuid(&response["activationId"])? != id {
            return Err("backend_response");
        }
        match response["decision"].as_str() {
            Some("granted") => Ok(true),
            Some("suppressed") => Ok(false),
            _ => Err("backend_response"),
        }
    }
    fn cleanup(&self, path: String) {
        let request = self
            .client
            .delete(format!("{}{path}", self.origin))
            .header("Authorization", format!("AegisNode {}", self.credential))
            .timeout(Duration::from_secs(2));
        tauri::async_runtime::spawn(async move {
            let _ = request.send().await;
        });
    }
    pub fn transcribe(&self, samples: &[i16]) -> Result<String> {
        self.transcribe_with_timeout(samples, 90)
    }
    pub fn transcribe_candidate(&self, samples: &[i16]) -> Result<String> {
        self.transcribe_with_timeout(samples, 12)
    }
    fn transcribe_with_timeout(&self, samples: &[i16], seconds: u64) -> Result<String> {
        let id = Uuid::new_v4();
        let boundary = format!("aegis-{}", Uuid::new_v4());
        let mut body=format!("--{boundary}\r\nContent-Disposition: form-data; name=\"transcriptionRequestId\"\r\n\r\n{id}\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"clientDurationMilliseconds\"\r\n\r\n{}\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"audio\"; filename=\"command.wav\"\r\nContent-Type: audio/wav\r\n\r\n",samples.len()/16).into_bytes();
        body.extend(wav(samples));
        body.extend_from_slice(format!("\r\n--{boundary}--\r\n").as_bytes());
        let result = self.json(
            self.post("/api/voice/transcriptions", seconds)
                .header(
                    "Content-Type",
                    format!("multipart/form-data; boundary={boundary}"),
                )
                .body(body),
        )?;
        if parse_uuid(&result["transcriptionRequestId"])? != id {
            return Err("backend_response");
        }
        let text = result["text"].as_str().ok_or("backend_response")?.trim();
        let end = text
            .find(|c: char| !c.is_alphanumeric())
            .unwrap_or(text.len());
        Ok(if text[..end].eq_ignore_ascii_case("aegis") {
            text[end..].trim_start_matches(|c: char| c.is_whitespace() || c.is_ascii_punctuation())
        } else {
            text
        }
        .to_owned())
    }

    pub fn classify_interruption(
        &self,
        session: Uuid,
        content: String,
        context: InterruptionContext,
    ) -> Result<InterruptionDecision> {
        if content.trim().is_empty() {
            return Ok(InterruptionDecision::Resume);
        }
        let mut payload = json!({
            "voiceSessionId": session,
            "conversationId": context.conversation,
            "content": content,
        });
        write_interruption(&mut payload, &context);
        let reply = self.json(
            self.post("/api/voice/interruptions/classify", 15)
                .json(&payload),
        )?;
        self.current()?;
        match reply["disposition"].as_str() {
            Some("resume") => Ok(InterruptionDecision::Resume),
            Some("stop") => Ok(InterruptionDecision::Stop),
            Some("replace") => Ok(InterruptionDecision::Replace(AcceptedInterruption {
                context,
                content,
                receipt: parse_uuid(&reply["receipt"])?,
            })),
            _ => Err("backend_response"),
        }
    }

    #[cfg(test)]
    /// One cancellable backend worker per automatic turn. The microphone owner
    /// keeps reading while this worker performs STT, generation and playback.
    pub fn run_turn<A: TurnAudio>(
        &mut self,
        session: Uuid,
        destination: Option<Uuid>,
        samples: &[i16],
        interruption: Option<&Interruption>,
        on_conversation: impl Fn(Uuid) + Send,
        on_stream: impl Fn(VoiceChatStreamEvent) + Sync,
        open: impl FnOnce() -> Result<A>,
    ) -> TurnReport {
        self.run_controlled_turn(
            VoiceTurnRequest {
                session,
                destination,
                samples,
                legacy_interruption: interruption,
                replacement: None,
                inspection: &TurnInspection::new(destination),
                playback: &PlaybackControl::default(),
            },
            on_conversation,
            on_stream,
            open,
        )
    }

    pub fn run_controlled_turn<A: TurnAudio>(
        &mut self,
        request: VoiceTurnRequest<'_>,
        on_conversation: impl Fn(Uuid) + Send,
        on_stream: impl Fn(VoiceChatStreamEvent) + Sync,
        open: impl FnOnce() -> Result<A>,
    ) -> TurnReport {
        let VoiceTurnRequest {
            session,
            destination,
            samples,
            legacy_interruption,
            replacement,
            inspection,
            playback: audio,
        } = request;
        let turn_id = inspection.id;
        let mut report = TurnReport {
            result: Err("backend_response"),
            turn_id,
            conversation: destination,
            accepted: false,
            transcript: None,
            chat_started: false,
            playback: None,
            research_job: None,
        };
        let text = match replacement
            .map(|candidate| Ok(candidate.content.clone()))
            .unwrap_or_else(|| self.transcribe(samples))
        {
            Ok(text) => text,
            Err(error) => {
                report.result = Err(error);
                on_stream(VoiceChatStreamEvent::new(
                    if self.cancel.is_cancelled() {
                        "interrupted"
                    } else {
                        "error"
                    },
                    turn_id,
                    None,
                    None,
                ));
                return report;
            }
        };
        if text.is_empty()
            || matches!(legacy_interruption, Some(Interruption::ResearchPlayback))
                && closes_research_reply(&text)
        {
            report.transcript = Some(text);
            report.result = Ok(TurnReply {
                ignored: true,
                continuation: false,
            });
            on_stream(VoiceChatStreamEvent::new("ignored", turn_id, None, None));
            return report;
        }
        report.transcript = Some(text.clone());
        *inspection.transcript.lock().unwrap() = Some(text.clone());
        // Keep the turn handle even if cancellation wins before HTTP writes; the
        // server settles a missing/unfinished prior generation idempotently.
        self.turn = Some(turn_id);
        report.chat_started = true;
        let mut payload = json!({
            "turnId": turn_id, "conversationId": destination, "voiceSessionId": session,
            "origin": "hands_free", "content": text, "voiceSegments": true, "allowSilentIgnore": true,
            "includeWebResearchSources": true,
        });
        if let Some(candidate) = replacement {
            write_interruption(&mut payload, &candidate.context);
            payload["interruptionReceipt"] = json!(candidate.receipt);
        } else {
            write_interruption(
                &mut payload,
                &InterruptionContext {
                    conversation: destination,
                    predecessor: legacy_interruption.cloned(),
                    research_job: None,
                },
            );
        }
        let progress = &inspection.progress;
        let mut output: Option<A> = None;
        let playback = &inspection.playback;
        let mut position = None;
        let reader = self.fork(&self.cancel);
        let (send, receive) = tokio::sync::mpsc::channel(2);
        let (unit_send, mut units) = tokio::sync::mpsc::channel(2);
        let scheduler = self.fork(&self.cancel);
        // Bound the whole segmented generation/synthesis/playback pipeline, not
        // just each individual HTTP request. No timer runs during idle listening.
        let deadline_done = CancellationToken::new();
        let timer_done = deadline_done.clone();
        // 0 = armed, 1 = completed, 2 = deadline claimed; the timer cannot
        // cancel a completed turn and completion cannot hide an elapsed deadline.
        let deadline_state = Arc::new(AtomicU8::new(0));
        let timer_fired = deadline_state.clone();
        let timer_cancel = self.cancel.clone();
        tauri::async_runtime::spawn(async move {
            tokio::select! { biased;
                _=timer_done.cancelled()=>{},
                _=timer_cancel.cancelled()=>{},
                _=tokio::time::sleep(Duration::from_secs(300))=>{
                    if timer_fired.compare_exchange(0,2,Ordering::AcqRel,Ordering::Acquire).is_ok() {
                        timer_cancel.cancel();
                    }
                }
            }
        });
        report.result = thread::scope(|scope| {
            let on_stream = &on_stream;
            let read = scope.spawn(move || {
                let result = reader.read_chat(
                    turn_id,
                    destination,
                    payload,
                    progress,
                    send,
                    on_conversation,
                    on_stream,
                );
                if result.is_err() {
                    reader.cancel.cancel();
                }
                result
            });
            let schedule =
                scope.spawn(move || scheduler.schedule_speech(turn_id, receive, unit_send, audio));
            let result = self.consume_segments(&mut units, playback, &mut output, open, audio);
            if result.is_err() {
                self.cancel.cancel();
            }
            // Snapshot the device's consumed head BEFORE waveOutReset/Drop.
            // Dropping now, before joining another worker or remote cleanup,
            // also discards buffered but unheard audio on an interruption.
            position = output
                .as_ref()
                .and_then(|device| device.played_frames())
                .and_then(|cursor| playback.lock().unwrap().at(turn_id, cursor));
            drop(output.take());
            drop(units);
            let schedule = schedule.join().unwrap_or(Err("backend_response"));
            let read = read.join().unwrap_or(Err("backend_response"));
            match (result, schedule, read) {
                (Err("cancelled"), _, Err(error)) if error != "cancelled" => Err(error),
                (Err("cancelled"), Err(error), _) if error != "cancelled" => Err(error),
                (Err(error), _, _) | (_, Err(error), _) | (_, _, Err(error)) => Err(error),
                (Ok(reply), Ok(()), Ok(())) => Ok(reply),
            }
        });
        let finished_before_deadline = deadline_state
            .compare_exchange(0, 1, Ordering::AcqRel, Ordering::Acquire)
            .is_ok();
        deadline_done.cancel();
        if !finished_before_deadline {
            self.cancel.cancel();
            report.result = Err("backend_unavailable");
        }
        let confirmed = progress.lock().unwrap();
        report.conversation = confirmed.conversation;
        report.accepted = confirmed.accepted;
        report.playback = position;
        report.research_job = confirmed.research_job;
        let terminal = match &report.result {
            Ok(reply) if reply.ignored => Some("ignored"),
            Ok(_) => None, // validated done was emitted at the stream's EOF
            Err("cancelled") => Some("interrupted"),
            Err(_) => Some("error"),
        };
        if let Some(kind) = terminal {
            on_stream(VoiceChatStreamEvent::new(
                kind,
                turn_id,
                confirmed.conversation.filter(|_| confirmed.accepted),
                confirmed.assistant,
            ));
        }
        if report.result.is_ok() && !self.cancel.is_cancelled() {
            if !report.result.as_ref().unwrap().ignored {
                // Completion is a best-effort acknowledgement after device drain.
                self.complete_turn(turn_id);
            }
            self.turn = None;
            self.speech = None;
        }
        report
    }

    fn complete_turn(&self, id: Uuid) {
        let request = self
            .post(&format!("/api/chat/turns/{id}/complete"), 3)
            .body(Vec::new());
        tauri::async_runtime::spawn(async move {
            let _ = request.send().await;
        });
    }

    fn read_chat(
        &self,
        turn: Uuid,
        destination: Option<Uuid>,
        payload: Value,
        progress: &Mutex<ChatProgress>,
        send: tokio::sync::mpsc::Sender<ChatEvent>,
        on_conversation: impl Fn(Uuid),
        on_stream: &impl Fn(VoiceChatStreamEvent),
    ) -> Result<()> {
        let mut response = self.send(self.post("/api/chat/messages/stream", 180).json(&payload))?;
        let mut pending = Vec::new();
        let mut total = 0usize;
        let mut sequence = 0usize;
        let mut conversation = destination;
        let mut reported = destination;
        let mut assistant = None;
        let mut seen = HashSet::new();
        let mut any = false;
        let mut accepted = false;
        let mut terminal = None;
        let mut research_job = None;
        while let Some(chunk) = self.next(&mut response)? {
            total = total.checked_add(chunk.len()).ok_or("backend_response")?;
            if total > MAX_CHAT_BYTES {
                return Err("backend_response");
            }
            pending.extend_from_slice(&chunk);
            while let Some(index) = pending.iter().position(|b| *b == b'\n') {
                if index > 64 * 1024 {
                    return Err("backend_response");
                }
                let line: Vec<u8> = pending.drain(..=index).collect();
                let event: Value = serde_json::from_slice(&line).map_err(|_| "backend_response")?;
                self.current()?;
                if terminal.is_some() {
                    return Err("backend_response");
                }
                let kind = event["type"].as_str().ok_or("backend_response")?;
                if kind == "error" {
                    return Err("backend_unavailable");
                }
                if parse_uuid(&event["turnId"])? != turn {
                    return Err("backend_response");
                }
                let first = !any;
                any = true;
                match kind {
                    "ignored"
                        if first && event.as_object().is_some_and(|value| value.len() == 2) =>
                    {
                        terminal = Some(ChatEvent::Ignored);
                    }
                    "conversation" => {
                        let next = parse_uuid(&event["conversationId"])?;
                        if conversation.is_some_and(|id| id != next) {
                            return Err("backend_response");
                        }
                        conversation = Some(next);
                        *progress.lock().unwrap() = ChatProgress {
                            conversation,
                            accepted: true,
                            assistant,
                            research_job: None,
                        };
                        if reported != conversation {
                            on_conversation(next);
                            reported = conversation;
                        }
                        if !accepted {
                            accepted = true;
                            on_stream(VoiceChatStreamEvent::new(
                                "conversation",
                                turn,
                                conversation,
                                None,
                            ));
                        }
                    }
                    "final_segment" | "done" => {
                        let next = parse_uuid(&event["conversationId"])?;
                        let message = parse_uuid(&event["assistantMessageId"])?;
                        if conversation.is_some_and(|id| id != next)
                            || assistant.is_some_and(|id| id != message)
                        {
                            return Err("backend_response");
                        }
                        let validated = if kind == "done" {
                            if sequence == 0 {
                                return Err("backend_response");
                            }
                            research_job = parse_uuid(&event["researchJobId"]).ok();
                            ChatEvent::Done(event["voiceContinuation"] == "continue")
                        } else {
                            let id = parse_uuid(&event["segmentId"])?;
                            let content = event["content"].as_str().ok_or("backend_response")?;
                            if sequence >= 128
                                || event["sequence"].as_u64() != Some(sequence as u64)
                                || !seen.insert(id)
                                || content.trim().is_empty()
                                || content.chars().count() > 8000
                            {
                                return Err("backend_response");
                            }
                            ChatEvent::Segment(Segment {
                                conversation: next,
                                assistant: message,
                                id,
                                sequence,
                            })
                        };
                        // A malformed segment or terminal must not navigate the
                        // UI or claim an accepted destination as a side effect.
                        conversation = Some(next);
                        assistant = Some(message);
                        *progress.lock().unwrap() = ChatProgress {
                            conversation,
                            accepted: true,
                            assistant,
                            research_job: None,
                        };
                        if reported != conversation {
                            on_conversation(next);
                            reported = conversation;
                        }
                        if !accepted {
                            accepted = true;
                            on_stream(VoiceChatStreamEvent::new(
                                "conversation",
                                turn,
                                conversation,
                                None,
                            ));
                        }
                        if kind == "done" {
                            terminal = Some(validated);
                        } else {
                            if let ChatEvent::Segment(segment) = &validated {
                                // Only text belonging to a validated committed
                                // segment is visible. Never expose speculative
                                // tokens or unaccepted false activations.
                                on_stream(VoiceChatStreamEvent::segment(
                                    turn,
                                    segment,
                                    event["content"].as_str().unwrap().to_owned(),
                                ));
                            }
                            send_event(&send, validated, &self.cancel)?;
                            sequence += 1;
                        }
                    }
                    "tool_status" => {
                        // A requested destination alone does not establish acceptance. Tool
                        // status is a transient observer, never a persistence or audio boundary.
                        if accepted {
                            if let Some(status) = conversation
                                .and_then(|id| VoiceChatStreamEvent::tool_status(turn, id, &event))
                            {
                                on_stream(status);
                            }
                        }
                    }
                    "web_research_sources" => {
                        if accepted {
                            if let Some(sources) = conversation.and_then(|id| {
                                VoiceChatStreamEvent::web_research_sources(turn, id, &event)
                            }) {
                                on_stream(sources);
                            }
                        }
                    }
                    "memory_activity" | "token" => {}
                    _ => return Err("backend_response"),
                }
            }
            if pending.len() > 64 * 1024 {
                return Err("backend_response");
            }
        }
        if !pending.is_empty() {
            return Err("backend_response");
        }
        self.current()?;
        let terminal = terminal.ok_or("backend_response")?;
        if matches!(terminal, ChatEvent::Done(_)) {
            progress.lock().unwrap().research_job = research_job;
            on_stream(VoiceChatStreamEvent::new(
                "done",
                turn,
                conversation,
                assistant,
            ));
        }
        send_event(&send, terminal, &self.cancel)
    }

    /// Match the Android segmented pipeline: no more than two active synthesis
    /// slots, and bounded half-second PCM pipes. A successor can start as soon
    /// as the preceding server request returned real PCM, preserving order.
    fn schedule_speech(
        &self,
        turn: Uuid,
        mut receive: tokio::sync::mpsc::Receiver<ChatEvent>,
        send: tokio::sync::mpsc::Sender<SpeechEvent>,
        audio: &PlaybackControl,
    ) -> Result<()> {
        let slots = Arc::new(tokio::sync::Semaphore::new(2));
        let failure = Arc::new(Mutex::new(None));
        let result = thread::scope(|scope| {
            let mut previous_ready: Option<tokio::sync::oneshot::Receiver<()>> = None;
            let outcome = (|| -> Result<()> {
                loop {
                    self.current()?;
                    match receive_event(&mut receive, &self.cancel)? {
                        ChatEvent::Ignored => {
                            return send_event(&send, SpeechEvent::Ignored, &self.cancel);
                        }
                        ChatEvent::Done(continuation) => {
                            return send_event(
                                &send,
                                SpeechEvent::Done(continuation),
                                &self.cancel,
                            );
                        }
                        ChatEvent::Segment(segment) => {
                            if audio.stopped().is_cancelled() {
                                continue;
                            }
                            if let Some(ready) = previous_ready.take() {
                                let started = tauri::async_runtime::block_on(async {
                                    tokio::select! { biased;
                                        _=self.cancel.cancelled()=>Err("cancelled"),
                                        _=audio.stopped().cancelled()=>Ok(false),
                                        value=ready=>value.map(|()|true).map_err(|_|"backend_response")
                                    }
                                })?;
                                if !started {
                                    continue;
                                }
                            }
                            let slot = tauri::async_runtime::block_on(async {
                                tokio::select! { biased;
                                    _=self.cancel.cancelled()=>Err("cancelled"),
                                    _=audio.stopped().cancelled()=>Ok(None),
                                    value=slots.clone().acquire_owned()=>value.map(Some).map_err(|_|"backend_response")
                                }
                            })?;
                            let Some(slot) = slot else {
                                continue;
                            };
                            let (pcm_send, pcm_receive) = tokio::sync::mpsc::channel(5);
                            let (started, ready) = tokio::sync::oneshot::channel();
                            previous_ready = Some(ready);
                            let mut downloader = self.fork(&self.cancel);
                            let cancel = self.cancel.clone();
                            let speech_segment = segment.clone();
                            let download_failure = failure.clone();
                            scope.spawn(move || {
                                let mut started = Some(started);
                                let result = downloader
                                    .segment_pcm(turn, &speech_segment, |frames| {
                                        if let Some(signal) = started.take() {
                                            let _ = signal.send(());
                                        }
                                        send_event(
                                            &pcm_send,
                                            AudioChunk::Pcm(frames.to_vec()),
                                            &cancel,
                                        )
                                    })
                                    .and_then(|frames| {
                                        send_event(&pcm_send, AudioChunk::Complete(frames), &cancel)
                                    });
                                if let Err(error) = result {
                                    if audio.stopped().is_cancelled() && !cancel.is_cancelled() {
                                        // End the local mailbox without issuing the
                                        // legacy speech DELETE (it also ends text).
                                        downloader.speech = None;
                                        let _ =
                                            send_event(&pcm_send, AudioChunk::Complete(0), &cancel);
                                        return;
                                    }
                                    if error != "cancelled" {
                                        let mut failure = download_failure.lock().unwrap();
                                        if failure.is_none() {
                                            *failure = Some(error);
                                        }
                                    }
                                    cancel.cancel();
                                }
                                // The downloader owns and cancels only its own speech handle.
                            });
                            send_event(
                                &send,
                                SpeechEvent::Unit(SpeechUnit {
                                    segment,
                                    receive: pcm_receive,
                                    _slot: slot,
                                }),
                                &self.cancel,
                            )?;
                        }
                    }
                }
            })();
            if outcome.is_err() {
                self.cancel.cancel();
            }
            outcome
        });
        let error = *failure.lock().unwrap();
        error.map_or(result, Err)
    }

    fn consume_segments<A: TurnAudio>(
        &self,
        receive: &mut tokio::sync::mpsc::Receiver<SpeechEvent>,
        playback: &Mutex<PlaybackProgress>,
        output: &mut Option<A>,
        open: impl FnOnce() -> Result<A>,
        audio: &PlaybackControl,
    ) -> Result<TurnReply> {
        let cancel = self.cancel.clone();
        let mut open = Some(open);
        let mut submitted = 0u64;
        let mut last_identity = Instant::now();
        loop {
            self.current()?;
            match receive_while_playing(receive, &cancel, audio, output)? {
                SpeechEvent::Ignored => {
                    return Ok(TurnReply {
                        ignored: true,
                        continuation: false,
                    })
                }
                SpeechEvent::Done(continuation) => {
                    if !audio.stopped().is_cancelled() {
                        match output.as_mut().ok_or("backend_response")?.finish(&cancel) {
                            Ok(()) | Err("playback_stopped") => {}
                            Err(error) => return Err(error),
                        }
                    }
                    self.current()?;
                    return Ok(TurnReply {
                        ignored: false,
                        continuation: continuation && !audio.stopped().is_cancelled(),
                    });
                }
                SpeechEvent::Unit(mut unit) => {
                    playback.lock().unwrap().begin(unit.segment, submitted);
                    let start = submitted;
                    loop {
                        match receive_while_playing(&mut unit.receive, &cancel, audio, output)? {
                            AudioChunk::Pcm(pcm) => {
                                if audio.stopped().is_cancelled() {
                                    continue;
                                }
                                if last_identity.elapsed() >= Duration::from_secs(1) {
                                    self.current()?;
                                    last_identity = Instant::now();
                                }
                                let next = submitted
                                    .checked_add(pcm.len() as u64)
                                    .ok_or("backend_response")?;
                                if next > MAX_PLAYBACK_FRAMES {
                                    return Err("backend_response");
                                }
                                if output.is_none() {
                                    if cancel.is_cancelled() {
                                        return Err("cancelled");
                                    }
                                    match open.take().ok_or("backend_response")?() {
                                        Ok(device) => *output = Some(device),
                                        Err("playback_stopped") => continue,
                                        Err(error) => return Err(error),
                                    }
                                }
                                match output.as_mut().unwrap().write(&pcm, &cancel) {
                                    Ok(()) => submitted = next,
                                    Err("playback_stopped") => {
                                        output.take();
                                    }
                                    Err(error) => return Err(error),
                                }
                            }
                            AudioChunk::Complete(frames) => {
                                if !audio.stopped().is_cancelled() && frames != submitted - start {
                                    return Err("backend_response");
                                }
                                playback.lock().unwrap().length(frames);
                                break;
                            }
                        }
                    }
                    // Dropping this unit releases its slot. Prefetched frames
                    // never count as heard and the device cursor is authoritative.
                }
            }
        }
    }
    fn segment_pcm(
        &mut self,
        turn: Uuid,
        segment: &Segment,
        deliver: impl FnMut(&[i16]) -> Result<()>,
    ) -> Result<u64> {
        let id = Uuid::new_v4();
        self.speech = Some(id);
        let response = self.send(self.post("/api/voice/speech/segments", 300).json(&json!({
            "turnId":turn,"speechRequestId":id,"assistantMessageId":segment.assistant,
            "conversationId":segment.conversation,"segmentId":segment.id,"sequence":segment.sequence,
        })))?;
        let frames = self.read_pcm(response, Some(id), Some(MAX_PLAYBACK_FRAMES), deliver)?;
        self.speech = None;
        Ok(frames)
    }

    fn read_pcm(
        &self,
        mut response: Response,
        expected_id: Option<Uuid>,
        max_frames: Option<u64>,
        mut deliver: impl FnMut(&[i16]) -> Result<()>,
    ) -> Result<u64> {
        for (key, value) in [
            ("X-Aegis-Audio-Format", "pcm_s16le"),
            ("X-Aegis-Sample-Rate", "24000"),
            ("X-Aegis-Channels", "1"),
        ] {
            if response.headers().get(key).and_then(|v| v.to_str().ok()) != Some(value) {
                return Err("backend_response");
            }
        }
        let response_id = response
            .headers()
            .get("X-Aegis-Speech-Request-Id")
            .and_then(|v| v.to_str().ok())
            .and_then(|v| Uuid::parse_str(v).ok())
            .filter(|id| !id.is_nil())
            .ok_or("backend_response")?;
        if expected_id.is_some_and(|id| id != response_id) {
            return Err("backend_response");
        }
        self.current()?;
        let mut odd = None;
        let mut total = 0u64;
        let mut last_identity = Instant::now();
        while let Some(mut chunk) = self.next(&mut response)? {
            total = total
                .checked_add(chunk.len() as u64)
                .ok_or("backend_response")?;
            if max_frames.is_some_and(|frames| total > frames * 2) {
                return Err("backend_response");
            }
            if let Some(byte) = odd.take() {
                chunk.insert(0, byte);
            }
            if chunk.len() % 2 == 1 {
                odd = chunk.pop();
            }
            let pcm: Vec<i16> = chunk
                .chunks_exact(2)
                .map(|pair| i16::from_le_bytes([pair[0], pair[1]]))
                .collect();
            for part in pcm.chunks(2400) {
                if last_identity.elapsed() >= Duration::from_secs(1) {
                    self.current()?;
                    last_identity = Instant::now();
                }
                if self.cancel.is_cancelled() {
                    return Err("cancelled");
                }
                deliver(part)?;
            }
        }
        if total == 0 || odd.is_some() {
            return Err("backend_response");
        }
        self.current()?;
        Ok(total / 2)
    }
}

fn send_event<T>(
    send: &tokio::sync::mpsc::Sender<T>,
    event: T,
    cancel: &CancellationToken,
) -> Result<()> {
    tauri::async_runtime::block_on(async {
        tokio::select! { biased;
            _=cancel.cancelled()=>Err("cancelled"),
            result=send.send(event)=>result.map_err(|_|"cancelled")
        }
    })
}
fn receive_event<T>(
    receive: &mut tokio::sync::mpsc::Receiver<T>,
    cancel: &CancellationToken,
) -> Result<T> {
    tauri::async_runtime::block_on(async {
        tokio::select! { biased;
            _=cancel.cancelled()=>Err("cancelled"),
            value=receive.recv()=>value.ok_or("backend_response")
        }
    })
}

fn receive_while_playing<T, A>(
    receive: &mut tokio::sync::mpsc::Receiver<T>,
    cancel: &CancellationToken,
    audio: &PlaybackControl,
    output: &mut Option<A>,
) -> Result<T> {
    if output.is_some() {
        let event = tauri::async_runtime::block_on(async {
            tokio::select! { biased;
                _=cancel.cancelled()=>Err("cancelled"),
                _=audio.stopped().cancelled()=>Ok(None),
                value=receive.recv()=>value.map(Some).ok_or("backend_response"),
            }
        })?;
        if let Some(event) = event {
            return Ok(event);
        }
        output.take();
    }
    // Keep draining already opened synthesis units, since cancelling a
    // segment request on a legacy backend also cancels its generating turn.
    receive_event(receive, cancel)
}
fn parse_uuid(value: &Value) -> Result<Uuid> {
    Uuid::parse_str(value.as_str().unwrap_or(""))
        .ok()
        .filter(|v| !v.is_nil())
        .ok_or("backend_response")
}

fn closes_research_reply(text: &str) -> bool {
    let spoken = text
        .split(|character: char| !character.is_alphanumeric())
        .filter(|word| !word.is_empty())
        .collect::<Vec<_>>()
        .join(" ")
        .to_lowercase();
    let acknowledgement = |text: &str| {
        matches!(
            text,
            "ok" | "okay"
                | "beleza"
                | "valeu"
                | "obrigado"
                | "obrigada"
                | "tá bom"
                | "ta bom"
                | "está bom"
                | "esta bom"
                | "ah não"
                | "ah nao"
                | "pode parar"
                | "já deu"
                | "ja deu"
                | "é só isso"
                | "e so isso"
        )
    };
    acknowledgement(&spoken)
        || ["ok ", "okay ", "beleza ", "ah não ", "ah nao "]
            .iter()
            .any(|prefix| spoken.strip_prefix(prefix).is_some_and(acknowledgement))
}
impl Drop for Backend {
    fn drop(&mut self) {
        let paths = [
            self.speech.map(|id| format!("/api/voice/speech/{id}")),
            self.turn.map(|id| format!("/api/chat/turns/{id}")),
        ];
        for path in paths.into_iter().flatten() {
            self.cleanup(path);
        }
    }
}

#[cfg(test)]
#[path = "windows_voice_backend_tests.rs"]
mod tests;
