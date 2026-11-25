use crate::{node_commands::NodeRuntime, windows_voice_audio::Output, windows_voice_signal::wav};
use reqwest::{Client, RequestBuilder, Response, StatusCode};
use serde_json::{json, Value};
use std::time::{Duration, Instant};
use tauri::{AppHandle, Manager};
use tokio_util::sync::CancellationToken;
use uuid::Uuid;

pub(crate) struct Backend {
    current_credential: Box<dyn Fn() -> Option<String>>,
    origin: String,
    credential: String,
    client: Client,
    cancel: CancellationToken,
    turn: Option<Uuid>,
    speech: Option<Uuid>,
}
pub(crate) struct Reply {
    pub conversation: Uuid,
    pub message: Uuid,
    pub continuation: bool,
}
pub(crate) type Result<T> = std::result::Result<T, &'static str>;

impl Backend {
    pub fn open(app: &AppHandle, client: &Client, cancel: &CancellationToken) -> Result<Self> {
        let origin = crate::config::configured_endpoint()
            .map_err(|_| "backend_unavailable")?
            .base_url();
        let app = app.clone();
        let current_credential = Box::new(move || {
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
    pub fn respond(
        &mut self,
        session: Uuid,
        destination: Option<Uuid>,
        text: String,
    ) -> Result<Option<Reply>> {
        let turn = Uuid::new_v4();
        self.turn = Some(turn);
        let response = self.send(self.post("/api/chat/messages", 180).json(&json!({
            "turnId": turn,
            "conversationId": destination,
            "voiceSessionId": session,
            "origin": "hands_free",
            "content": text,
            "allowSilentIgnore": true,
        })))?;
        if response.status() == StatusCode::NO_CONTENT {
            // The server deliberately did not create a turn or assistant message.
            // Clear the cleanup handle and finish without speech or a chat update.
            self.turn = None;
            self.current()?;
            return Ok(None);
        }
        let result = self.json_response(response)?;
        let conversation = parse_uuid(&result["conversationId"])?;
        let message = &result["message"];
        if destination.is_some_and(|id| id != conversation)
            || parse_uuid(&message["conversationId"])? != conversation
            || message["role"] != "assistant"
        {
            return Err("backend_response");
        }
        Ok(Some(Reply {
            conversation,
            message: parse_uuid(&message["id"])?,
            continuation: result["voiceContinuation"] == "continue",
        }))
    }
    pub fn speak(&mut self, reply: &Reply) -> Result<()> {
        let mut output = None;
        let cancel = self.cancel.clone();
        self.speech_pcm(reply, |pcm| {
            if output.is_none() {
                output = Some(Output::open()?);
            }
            output.as_mut().unwrap().write(pcm, &cancel)
        })?;
        output.ok_or("backend_response")?.finish(&cancel)?;
        self.current()?;
        self.turn = None;
        self.speech = None;
        Ok(())
    }
    fn speech_pcm(
        &mut self,
        reply: &Reply,
        mut deliver: impl FnMut(&[i16]) -> Result<()>,
    ) -> Result<()> {
        let id = Uuid::new_v4();
        self.speech = Some(id);
        let mut response = self.send(self.post("/api/voice/speech", 300).json(
            &json!({"turnId":self.turn,"assistantMessageId":reply.message,"speechRequestId":id}),
        ))?;
        for (key, value) in [
            ("X-Aegis-Audio-Format", "pcm_s16le"),
            ("X-Aegis-Sample-Rate", "24000"),
            ("X-Aegis-Channels", "1"),
        ] {
            if response.headers().get(key).and_then(|v| v.to_str().ok()) != Some(value) {
                return Err("backend_response");
            }
        }
        self.current()?;
        let mut odd = None;
        let mut total = 0u64;
        let mut last_identity = Instant::now();
        while let Some(mut chunk) = self.next(&mut response)? {
            if last_identity.elapsed() >= Duration::from_secs(1) {
                self.current()?;
                last_identity = Instant::now();
            }
            total += chunk.len() as u64;
            if total > 24_000 * 2 * 180 {
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
            // Revalidate authority even when the backend delivers one large chunk
            // which takes several seconds to drain on the output device.
            for part in pcm.chunks(12_000) {
                if last_identity.elapsed() >= Duration::from_secs(1) {
                    self.current()?;
                    last_identity = Instant::now();
                }
                deliver(part)?;
            }
        }
        if total == 0 || odd.is_some() {
            return Err("backend_response");
        }
        self.current()
    }
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
