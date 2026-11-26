use crate::{
    node_commands::NodeRuntime,
    windows_voice_audio::{Output, PlaybackPause},
    windows_voice_signal::wav,
};
use reqwest::{Client, RequestBuilder, Response};
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
}

pub(crate) struct TurnReport {
    pub result: Result<TurnReply>,
    pub turn_id: Uuid,
    pub conversation: Option<Uuid>,
    pub accepted: bool,
    pub transcript: Option<String>,
    pub chat_started: bool,
    pub playback: Option<PlaybackInterruption>,
}

#[derive(Clone, Debug, PartialEq, Serialize)]
pub(crate) struct VoiceToolStatus {
    pub category: &'static str,
    pub state: String,
    pub message: String,
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
        }
    }
    fn web_search(turn_id: Uuid, conversation_id: Uuid, event: &Value) -> Option<Self> {
        if event["category"] != "web_search"
            || !matches!(
                event["state"].as_str(),
                Some("started" | "completed" | "failed")
            )
            || (!event["conversationId"].is_null()
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
                category: "web_search",
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
    /// Only a controllable player can be paused across its owner thread.
    fn pause_control(&self) -> Option<PlaybackPause> {
        None
    }
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
    fn pause_control(&self) -> Option<PlaybackPause> {
        Some(Output::pause_control(self))
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
    fn send(&self, request: RequestBuilder) -> Result<Response> {
        self.current()?;
        self.send_checked(request)
    }
    fn send_checked(&self, request: RequestBuilder) -> Result<Response> {
        tauri::async_runtime::block_on(async {
            tokio::select! {biased;
                _=self.cancel.cancelled()=>Err("cancelled"),
                response=request.send()=>match response { Ok(r) if r.status().is_success()=>Ok(r), _=>Err("backend_unavailable") }
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
        let id = Uuid::new_v4();
        let boundary = format!("aegis-{}", Uuid::new_v4());
        let mut body=format!("--{boundary}\r\nContent-Disposition: form-data; name=\"transcriptionRequestId\"\r\n\r\n{id}\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"clientDurationMilliseconds\"\r\n\r\n{}\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"audio\"; filename=\"command.wav\"\r\nContent-Type: audio/wav\r\n\r\n",samples.len()/16).into_bytes();
        body.extend(wav(samples));
        body.extend_from_slice(format!("\r\n--{boundary}--\r\n").as_bytes());
        let result = self.json(
            self.post("/api/voice/transcriptions", 90)
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
        let turn_id = Uuid::new_v4();
        let mut report = TurnReport {
            result: Err("backend_response"),
            turn_id,
            conversation: destination,
            accepted: false,
            transcript: None,
            chat_started: false,
            playback: None,
        };
        let text = match self.transcribe(samples) {
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
        if text.is_empty() {
            report.transcript = Some(text);
            report.result = Ok(TurnReply {
                ignored: true,
                continuation: false,
            });
            on_stream(VoiceChatStreamEvent::new("ignored", turn_id, None, None));
            return report;
        }
        report.transcript = Some(text.clone());
        // Keep the turn handle even if cancellation wins before HTTP writes; the
        // server settles a missing/unfinished prior generation idempotently.
        self.turn = Some(turn_id);
        report.chat_started = true;
        let mut payload = json!({
            "turnId": turn_id, "conversationId": destination, "voiceSessionId": session,
            "origin": "hands_free", "content": text, "voiceSegments": true, "allowSilentIgnore": true,
        });
        match interruption {
            Some(Interruption::Playback(position))
                if destination == Some(position.conversation) =>
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
        let progress = Arc::new(Mutex::new(ChatProgress {
            conversation: destination,
            accepted: false,
            assistant: None,
        }));
        let mut output: Option<A> = None;
        let mut playback = PlaybackProgress::default();
        let mut position = None;
        let reader = self.fork(&self.cancel);
        let reader_progress = progress.clone();
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
                    &reader_progress,
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
                scope.spawn(move || scheduler.schedule_speech(turn_id, receive, unit_send));
            let result = self.consume_segments(&mut units, &mut playback, &mut output, open);
            if result.is_err() {
                self.cancel.cancel();
            }
            // Snapshot the device's consumed head BEFORE waveOutReset/Drop.
            // Dropping now, before joining another worker or remote cleanup,
            // also discards buffered but unheard audio on an interruption.
            position = output
                .as_ref()
                .and_then(|device| device.played_frames())
                .and_then(|cursor| playback.at(turn_id, cursor));
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
                        // A requested destination alone does not establish acceptance. Search
                        // status is a transient observer, never a persistence or audio boundary.
                        if accepted {
                            if let Some(status) = conversation
                                .and_then(|id| VoiceChatStreamEvent::web_search(turn, id, &event))
                            {
                                on_stream(status);
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
                            if let Some(ready) = previous_ready.take() {
                                tauri::async_runtime::block_on(async {
                                    tokio::select! { biased;
                                        _=self.cancel.cancelled()=>Err("cancelled"),
                                        value=ready=>value.map_err(|_|"backend_response")
                                    }
                                })?;
                            }
                            let slot = tauri::async_runtime::block_on(async {
                                tokio::select! { biased;
                                    _=self.cancel.cancelled()=>Err("cancelled"),
                                    value=slots.clone().acquire_owned()=>value.map_err(|_|"backend_response")
                                }
                            })?;
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
        playback: &mut PlaybackProgress,
        output: &mut Option<A>,
        open: impl FnOnce() -> Result<A>,
    ) -> Result<TurnReply> {
        let cancel = self.cancel.clone();
        let mut open = Some(open);
        let mut submitted = 0u64;
        let mut last_identity = Instant::now();
        loop {
            self.current()?;
            match receive_event(receive, &cancel)? {
                SpeechEvent::Ignored => {
                    return Ok(TurnReply {
                        ignored: true,
                        continuation: false,
                    })
                }
                SpeechEvent::Done(continuation) => {
                    output.as_mut().ok_or("backend_response")?.finish(&cancel)?;
                    self.current()?;
                    return Ok(TurnReply {
                        ignored: false,
                        continuation,
                    });
                }
                SpeechEvent::Unit(mut unit) => {
                    playback.begin(unit.segment, submitted);
                    let start = submitted;
                    loop {
                        match receive_event(&mut unit.receive, &cancel)? {
                            AudioChunk::Pcm(pcm) => {
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
                                    *output = Some(open.take().ok_or("backend_response")?()?);
                                }
                                output.as_mut().unwrap().write(&pcm, &cancel)?;
                                submitted = next;
                            }
                            AudioChunk::Complete(frames) => {
                                if frames != submitted - start {
                                    return Err("backend_response");
                                }
                                playback.length(frames);
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
        mut deliver: impl FnMut(&[i16]) -> Result<()>,
    ) -> Result<u64> {
        let id = Uuid::new_v4();
        self.speech = Some(id);
        let mut response = self.send(self.post("/api/voice/speech/segments", 300).json(&json!({
            "turnId":turn,"speechRequestId":id,"assistantMessageId":segment.assistant,
            "conversationId":segment.conversation,"segmentId":segment.id,"sequence":segment.sequence,
        })))?;
        for (key, value) in [
            ("X-Aegis-Audio-Format", "pcm_s16le"),
            ("X-Aegis-Sample-Rate", "24000"),
            ("X-Aegis-Channels", "1"),
        ] {
            if response.headers().get(key).and_then(|v| v.to_str().ok()) != Some(value) {
                return Err("backend_response");
            }
        }
        if response
            .headers()
            .get("X-Aegis-Speech-Request-Id")
            .and_then(|v| v.to_str().ok())
            .and_then(|v| Uuid::parse_str(v).ok())
            != Some(id)
        {
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
            if total > MAX_PLAYBACK_FRAMES * 2 {
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
        self.speech = None;
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
fn parse_uuid(value: &Value) -> Result<Uuid> {
    Uuid::parse_str(value.as_str().unwrap_or(""))
        .ok()
        .filter(|v| !v.is_nil())
        .ok_or("backend_response")
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
