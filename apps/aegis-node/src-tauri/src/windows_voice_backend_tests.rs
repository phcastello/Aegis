use super::*;
use std::{
    io::{BufRead, BufReader, Read, Write},
    net::{TcpListener, TcpStream},
    thread,
};

fn fixture() -> (Backend, TcpListener) {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let origin = format!("http://{}", listener.local_addr().unwrap());
    let backend = Backend {
        origin,
        current_credential: Arc::new(|| Some("fixture".into())),
        credential: "fixture".into(),
        client: Client::builder()
            .redirect(reqwest::redirect::Policy::none())
            .build()
            .unwrap(),
        cancel: CancellationToken::new(),
        turn: None,
        speech: None,
    };
    (backend, listener)
}

fn request(server: &TcpListener, path: &str) -> (TcpStream, Vec<u8>) {
    loop {
        let (mut socket, _) = server.accept().unwrap();
        socket
            .set_read_timeout(Some(Duration::from_secs(3)))
            .unwrap();
        let mut input = BufReader::new(&mut socket);
        let mut line = String::new();
        input.read_line(&mut line).unwrap();
        let request_line = line.trim_end().to_owned();
        let mut length = None;
        let mut authorized = false;
        loop {
            line.clear();
            input.read_line(&mut line).unwrap();
            if line == "\r\n" {
                break;
            }
            let (key, val) = line.split_once(':').unwrap();
            if key.eq_ignore_ascii_case("content-length") {
                length = Some(val.trim().parse::<usize>().unwrap());
            }
            if key.eq_ignore_ascii_case("authorization") {
                authorized = val.trim() == "AegisNode fixture";
            }
        }
        assert!(authorized);
        let mut payload = vec![0; length.unwrap_or(0)];
        input.read_exact(&mut payload).unwrap();
        if request_line.starts_with("DELETE /api/voice/speech/")
            || request_line.starts_with("DELETE /api/chat/turns/")
        {
            assert!(payload.is_empty());
            socket
                .write_all(b"HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n")
                .unwrap();
            continue;
        }
        assert_eq!(request_line, format!("POST {path} HTTP/1.1"));
        return (socket, payload);
    }
}

fn answer(mut socket: TcpStream, body: Value) {
    let bytes = serde_json::to_vec(&body).unwrap();
    write!(socket,"HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: {}\r\n\r\n",bytes.len()).unwrap();
    socket.write_all(&bytes).unwrap();
}

fn transcript(server: &TcpListener, value: &str) {
    let (s, p) = request(server, "/api/voice/transcriptions");
    assert!(p.windows(4).any(|s| s == b"RIFF"));
    let text = String::from_utf8_lossy(&p);
    let id = text
        .split("name=\"transcriptionRequestId\"\r\n\r\n")
        .nth(1)
        .unwrap()
        .split("\r\n")
        .next()
        .unwrap();
    answer(s, json!({"transcriptionRequestId":id,"text":value}));
}

fn stream(mut socket: TcpStream, events: &[Value]) {
    let body: String = events.iter().map(|event| format!("{event}\n")).collect();
    write!(socket,"HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: application/x-ndjson\r\nContent-Length: {}\r\n\r\n",body.len()).unwrap();
    socket.write_all(body.as_bytes()).unwrap();
}

fn segment_event(
    turn: &Value,
    convo: Uuid,
    message: Uuid,
    segment: Uuid,
    sequence: usize,
) -> Value {
    json!({"type":"final_segment","turnId":turn,"conversationId":convo,
        "assistantMessageId":message,"segmentId":segment,"sequence":sequence,"content":"Resposta."})
}
fn done_event(turn: &Value, convo: Uuid, message: Uuid, continuation: bool) -> Value {
    json!({"type":"done","turnId":turn,"conversationId":convo,"assistantMessageId":message,
        "voiceContinuation": if continuation {"continue"} else {"stop"}})
}

fn pcm(
    server: &TcpListener,
    turn: &Value,
    convo: Uuid,
    message: Uuid,
    segment: Uuid,
    frames: &[i16],
) {
    let (mut s, p) = request(server, "/api/voice/speech/segments");
    let body: Value = serde_json::from_slice(&p).unwrap();
    assert_eq!(body["turnId"], *turn);
    assert_eq!(body["conversationId"], json!(convo));
    assert_eq!(body["assistantMessageId"], json!(message));
    assert_eq!(body["segmentId"], json!(segment));
    let id = body["speechRequestId"].as_str().unwrap();
    let bytes: Vec<u8> = frames.iter().flat_map(|s| s.to_le_bytes()).collect();
    // Force an odd byte split to exercise stream framing.
    write!(s,"HTTP/1.1 200 OK\r\nConnection: close\r\nX-Aegis-Speech-Request-Id: {id}\r\nX-Aegis-Audio-Format: pcm_s16le\r\nX-Aegis-Sample-Rate: 24000\r\nX-Aegis-Channels: 1\r\nTransfer-Encoding: chunked\r\n\r\n1\r\n").unwrap();
    s.write_all(&bytes[..1]).unwrap();
    write!(s, "\r\n{:x}\r\n", bytes.len() - 1).unwrap();
    s.write_all(&bytes[1..]).unwrap();
    s.write_all(b"\r\n0\r\n\r\n").unwrap();
}

#[derive(Default)]
struct AudioFixture {
    played: Arc<Mutex<Vec<i16>>>,
    interrupt: bool,
    wait_for_prefetch: Option<std::sync::mpsc::Receiver<()>>,
    first_write: Option<std::sync::mpsc::Sender<()>>,
}
impl TurnAudio for AudioFixture {
    fn write(&mut self, samples: &[i16], cancel: &CancellationToken) -> Result<()> {
        assert!(
            !cancel.is_cancelled(),
            "stale audio written after cancellation"
        );
        self.played.lock().unwrap().extend_from_slice(samples);
        if let Some(sent) = self.first_write.take() {
            sent.send(()).unwrap();
        }
        if let Some(ready) = self.wait_for_prefetch.take() {
            ready
                .recv_timeout(Duration::from_secs(3))
                .expect("successor synthesis did not start while first unit played");
        }
        if self.interrupt {
            cancel.cancel();
            return Err("cancelled");
        }
        Ok(())
    }
    fn finish(&mut self, cancel: &CancellationToken) -> Result<()> {
        if cancel.is_cancelled() {
            Err("cancelled")
        } else {
            Ok(())
        }
    }
    fn played_frames(&self) -> Option<u64> {
        let n = self.played.lock().unwrap().len() as u64;
        Some(if self.interrupt { n.min(1) } else { n })
    }
}

fn chunk(socket: &mut TcpStream, bytes: &[u8]) {
    write!(socket, "{:x}\r\n", bytes.len()).unwrap();
    socket.write_all(bytes).unwrap();
    socket.write_all(b"\r\n").unwrap();
}

#[test]
fn publishes_committed_text_and_plays_first_pcm_before_either_http_stream_finishes() {
    let (mut backend, server) = fixture();
    let conversation = Uuid::new_v4();
    let message = Uuid::new_v4();
    let first = Uuid::new_v4();
    let second = Uuid::new_v4();
    let (sent, heard) = std::sync::mpsc::channel();
    let observed = Arc::new(Mutex::new(Vec::<VoiceChatStreamEvent>::new()));
    let server_observed = observed.clone();
    let worker = thread::spawn(move || {
        transcript(&server, "Conte uma história");
        let (mut chat, payload) = request(&server, "/api/chat/messages/stream");
        let request_body: Value = serde_json::from_slice(&payload).unwrap();
        let turn = &request_body["turnId"];
        assert_eq!(request_body["includeWebResearchSources"], true);
        let sources_event = |id| {
            json!({"type":"web_research_sources","turnId":turn,"conversationId":id,"sources":[
                {"url":"https://example.org/source","count":2},
                {"url":"https://user:secret@example.org/source","count":1},
                {"url":"https://example.org/unobserved","count":0}
            ]})
        };
        chat.write_all(b"HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: application/x-ndjson\r\nTransfer-Encoding: chunked\r\n\r\n").unwrap();
        for event in [
            json!({"type":"tool_status","turnId":turn,"category":"web_search","state":"started","message":"Unaccepted progress"}),
            sources_event(conversation),
            json!({"type":"conversation","turnId":turn,"conversationId":conversation}),
            json!({"type":"tool_status","turnId":turn,"category":"web_search","state":"started","message":"Pesquisando na web…"}),
            json!({"type":"tool_status","turnId":turn,"conversationId":Uuid::new_v4(),"category":"web_search","state":"started","message":"Wrong destination"}),
            json!({"type":"tool_status","turnId":turn,"category":"web_search","state":"arbitrary","message":"Invalid state"}),
            json!({"type":"tool_status","turnId":turn,"category":"memory","state":"started","message":"Unrelated operation"}),
            sources_event(Uuid::new_v4()),
            sources_event(conversation),
            json!({"type":"tool_status","turnId":turn,"category":"web_search","state":"completed","message":"Busca concluída"}),
            json!({"type":"token","turnId":turn,"content":"Never display uncommitted text."}),
            segment_event(turn, conversation, message, first, 0),
        ] {
            chunk(&mut chat, format!("{event}\n").as_bytes());
        }
        let (mut speech, payload) = request(&server, "/api/voice/speech/segments");
        let request_body: Value = serde_json::from_slice(&payload).unwrap();
        assert_eq!(request_body["segmentId"], json!(first));
        let speech_id = request_body["speechRequestId"].as_str().unwrap();
        write!(speech,"HTTP/1.1 200 OK\r\nConnection: close\r\nX-Aegis-Speech-Request-Id: {speech_id}\r\nX-Aegis-Audio-Format: pcm_s16le\r\nX-Aegis-Sample-Rate: 24000\r\nX-Aegis-Channels: 1\r\nTransfer-Encoding: chunked\r\n\r\n").unwrap();
        chunk(&mut speech, &[17, 0, 17, 0]);
        // Neither server stream can terminate before real PCM has been
        // submitted to the player. This fails for response-level buffering.
        heard
            .recv_timeout(Duration::from_secs(3))
            .expect("audio waited for HTTP EOF");
        {
            let events = server_observed.lock().unwrap();
            assert_eq!(events.len(), 5);
            assert_eq!(events[0].kind, "conversation");
            assert_eq!(
                json!(events[1]),
                json!({
                    "type":"tool_status","turnId":turn,"conversationId":conversation,
                    "assistantMessageId":null,
                    "toolStatus":{"category":"web_search","state":"started","message":"Pesquisando na web…"}
                })
            );
            assert_eq!(
                json!(events[2]),
                json!({
                    "type":"web_research_sources","turnId":turn,"conversationId":conversation,
                    "assistantMessageId":null,"sources":[{"url":"https://example.org/source","count":2}]
                })
            );
            assert_eq!(events[3].tool_status.as_ref().unwrap().state, "completed");
            assert_eq!(
                json!(events[4]),
                json!({
                    "type":"final_segment","turnId":turn,"conversationId":conversation,
                    "assistantMessageId":message,"segmentId":first,"sequence":0,"content":"Resposta."
                })
            );
        }
        chunk(&mut speech, &[18, 0]);
        speech.write_all(b"0\r\n\r\n").unwrap();
        for event in [
            segment_event(turn, conversation, message, second, 1),
            done_event(turn, conversation, message, false),
        ] {
            chunk(&mut chat, format!("{event}\n").as_bytes());
        }
        chat.write_all(b"0\r\n\r\n").unwrap();
        pcm(&server, turn, conversation, message, second, &[99]);
        let (mut s, _) = request(
            &server,
            &format!("/api/chat/turns/{}/complete", turn.as_str().unwrap()),
        );
        s.write_all(b"HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n")
            .unwrap();
    });
    let output = AudioFixture {
        first_write: Some(sent),
        ..Default::default()
    };
    let audio = output.played.clone();
    let report = backend.run_turn(
        Uuid::new_v4(),
        Some(conversation),
        &[0; 64],
        None,
        |_| panic!("existing conversation must not navigate again"),
        |event| observed.lock().unwrap().push(event),
        || Ok(output),
    );
    assert!(report.result.is_ok());
    assert_eq!(*audio.lock().unwrap(), [17, 17, 18, 99]);
    let events = observed.lock().unwrap();
    assert_eq!(
        events.iter().map(|e| e.kind).collect::<Vec<_>>(),
        [
            "conversation",
            "tool_status",
            "web_research_sources",
            "tool_status",
            "final_segment",
            "final_segment",
            "done"
        ]
    );
    assert_eq!(events[5].segment_id, Some(second));
    assert_eq!(events[5].sequence, Some(1));
    assert_eq!(events[6].assistant_message_id, Some(message));
    assert!(events
        .iter()
        .all(|e| e.conversation_id == Some(conversation) && e.turn_id == report.turn_id));
    worker.join().unwrap();
}

#[test]
fn winner_transcribes_one_command_streams_committed_segments_and_uses_native_played_cursor() {
    let (mut backend, server) = fixture();
    let convo = Uuid::new_v4();
    let session = Uuid::new_v4();
    let msg = Uuid::new_v4();
    let a = Uuid::new_v4();
    let b = Uuid::new_v4();
    let (prefetched, prefetch_ready) = std::sync::mpsc::channel();
    let worker = thread::spawn(move || {
        let (s, p) = request(&server, "/api/nodes/me/voice-activations/claim");
        let body: Value = serde_json::from_slice(&p).unwrap();
        assert_eq!(body["activationId"], json!(session));
        assert_eq!(body["snrDb"], 12.);
        answer(s, json!({"activationId":session,"decision":"granted"}));
        transcript(&server, "Aegis, continue de onde parou");
        let (s, p) = request(&server, "/api/chat/messages/stream");
        let body: Value = serde_json::from_slice(&p).unwrap();
        assert_eq!(body["content"], "continue de onde parou");
        assert_eq!(body["origin"], "hands_free");
        assert_eq!(body["voiceSegments"], true);
        assert_eq!(body["allowSilentIgnore"], true);
        assert_eq!(body["voiceSessionId"], json!(session));
        assert_eq!(body["conversationId"], json!(convo));
        let turn = &body["turnId"];
        stream(
            s,
            &[
                json!({"type":"conversation","turnId":turn,"conversationId":convo}),
                segment_event(turn, convo, msg, a, 0),
                segment_event(turn, convo, msg, b, 1),
                done_event(turn, convo, msg, true),
            ],
        );
        pcm(&server, turn, convo, msg, a, &[0x1234, -1]);
        pcm(&server, turn, convo, msg, b, &[7, 8, 9]);
        prefetched.send(()).unwrap();
        let (mut s, _) = request(
            &server,
            &format!("/api/chat/turns/{}/complete", turn.as_str().unwrap()),
        );
        s.write_all(b"HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n")
            .unwrap();
    });
    assert!(backend
        .claim(session, Instant::now(), 0.9, Some(12.))
        .unwrap());
    let audio = AudioFixture {
        wait_for_prefetch: Some(prefetch_ready),
        ..Default::default()
    };
    let heard = audio.played.clone();
    let report = backend.run_turn(
        session,
        Some(convo),
        &[0; 64],
        None,
        |_| panic!("destination did not change"),
        |_| {},
        || Ok(audio),
    );
    let reply = report.result.unwrap();
    assert!(reply.continuation && !reply.ignored);
    assert_eq!(report.conversation, Some(convo));
    assert!(report.accepted);
    assert_eq!(*heard.lock().unwrap(), [0x1234, -1, 7, 8, 9]);
    let position = report.playback.unwrap();
    assert_eq!(position.segment_id, b);
    assert_eq!(position.sequence, 1);
    assert_eq!(position.played_frames_in_segment, 3);
    assert_eq!(position.segment_frames, Some(3));
    assert!(backend.turn.is_none());
    worker.join().unwrap();
}

#[test]
fn suppressed_wrong_id_stale_and_revoked_candidates_never_proceed() {
    let (backend, server) = fixture();
    let id = Uuid::new_v4();
    let worker = thread::spawn(move || {
        let (s, _) = request(&server, "/api/nodes/me/voice-activations/claim");
        answer(s, json!({"activationId":id,"decision":"suppressed"}));
        let (s, _) = request(&server, "/api/nodes/me/voice-activations/claim");
        answer(
            s,
            json!({"activationId":Uuid::new_v4(),"decision":"granted"}),
        );
    });
    assert!(!backend.claim(id, Instant::now(), 0.8, None).unwrap());
    assert_eq!(
        backend.claim(id, Instant::now(), 0.8, None),
        Err("backend_response")
    );
    assert_eq!(
        backend.claim(id, Instant::now() - Duration::from_secs(3), 0.8, None),
        Err("election_timeout")
    );
    worker.join().unwrap();
    let mut revoked = backend;
    revoked.current_credential = Arc::new(|| None);
    assert_eq!(
        revoked.claim(id, Instant::now(), 0.8, None),
        Err("pairing_required")
    );
}

#[test]
fn cancellation_interrupts_pending_election_before_response() {
    let (backend, server) = fixture();
    let id = Uuid::new_v4();
    let cancel = backend.cancel.clone();
    let worker = thread::spawn(move || {
        let (_s, _) = request(&server, "/api/nodes/me/voice-activations/claim");
        cancel.cancel();
        thread::sleep(Duration::from_millis(100));
    });
    assert_eq!(
        backend.claim(id, Instant::now(), 0.8, None),
        Err("cancelled")
    );
    worker.join().unwrap();
}

#[test]
fn ignored_transcript_has_no_output_or_cleanup_turn() {
    let (mut backend, server) = fixture();
    let session = Uuid::new_v4();
    let convo = Uuid::new_v4();
    let worker = thread::spawn(move || {
        transcript(&server, "Conversa ao fundo.");
        let (s, p) = request(&server, "/api/chat/messages/stream");
        let body: Value = serde_json::from_slice(&p).unwrap();
        assert_eq!(body["conversationId"], json!(convo));
        stream(s, &[json!({"type":"ignored","turnId":body["turnId"]})]);
    });
    let events = Mutex::new(Vec::new());
    let report = backend.run_turn::<AudioFixture>(
        session,
        Some(convo),
        &[0; 64],
        None,
        |_| panic!(),
        |event| events.lock().unwrap().push(event),
        || panic!("ignored must never open output"),
    );
    assert!(report.result.unwrap().ignored);
    assert!(!report.accepted);
    assert_eq!(
        events.into_inner().unwrap(),
        [VoiceChatStreamEvent::new(
            "ignored",
            report.turn_id,
            None,
            None
        )]
    );
    assert!(backend.turn.is_none() && backend.speech.is_none());
    worker.join().unwrap();
}

#[test]
fn thinking_interruption_retains_accepted_destination_and_prior_transcript() {
    let (mut backend, server) = fixture();
    let session = Uuid::new_v4();
    let convo = Uuid::new_v4();
    let cancel = backend.cancel.clone();
    let worker = thread::spawn(move || {
        transcript(&server, "Primeiro pedido");
        let (mut s, p) = request(&server, "/api/chat/messages/stream");
        let body: Value = serde_json::from_slice(&p).unwrap();
        assert!(body["conversationId"].is_null());
        let event = format!(
            "{}\n",
            json!({"type":"conversation","turnId":body["turnId"],"conversationId":convo})
        );
        write!(s,"HTTP/1.1 200 OK\r\nConnection: close\r\nTransfer-Encoding: chunked\r\n\r\n{:x}\r\n{}\r\n",event.len(),event).unwrap();
        thread::sleep(Duration::from_millis(100)); // no terminal/model response before cancellation
    });
    let events = Mutex::new(Vec::new());
    let report = backend.run_turn::<AudioFixture>(
        session,
        None,
        &[0; 64],
        None,
        move |id| {
            assert_eq!(id, convo);
            cancel.cancel();
        },
        |event| events.lock().unwrap().push(event),
        || panic!("no playback during reasoning"),
    );
    assert_eq!(report.result.err(), Some("cancelled"));
    assert_eq!(report.conversation, Some(convo));
    assert!(report.accepted && report.chat_started);
    assert_eq!(report.transcript.as_deref(), Some("Primeiro pedido"));
    assert!(report.playback.is_none());
    assert_eq!(
        events.into_inner().unwrap().last(),
        Some(&VoiceChatStreamEvent::new(
            "interrupted",
            report.turn_id,
            Some(convo),
            None
        ))
    );
    worker.join().unwrap();
}

#[test]
fn interruption_before_acceptance_retains_generation_id_and_text_without_creating_destination() {
    let (mut backend, server) = fixture();
    let session = Uuid::new_v4();
    let cancel = backend.cancel.clone();
    let (send, received) = std::sync::mpsc::channel();
    let worker = thread::spawn(move || {
        transcript(&server, "Pedido anterior");
        let (_s, p) = request(&server, "/api/chat/messages/stream");
        let body: Value = serde_json::from_slice(&p).unwrap();
        send.send(parse_uuid(&body["turnId"]).unwrap()).unwrap();
        cancel.cancel();
        thread::sleep(Duration::from_millis(100));
    });
    let events = Mutex::new(Vec::new());
    let report = backend.run_turn::<AudioFixture>(
        session,
        None,
        &[0; 64],
        None,
        |_| panic!("no accepted event"),
        |event| events.lock().unwrap().push(event),
        || panic!(),
    );
    assert_eq!(report.result.err(), Some("cancelled"));
    assert_eq!(
        report.turn_id,
        received.recv_timeout(Duration::from_secs(1)).unwrap()
    );
    assert!(report.chat_started && !report.accepted && report.conversation.is_none());
    assert_eq!(report.transcript.as_deref(), Some("Pedido anterior"));
    assert!(report.playback.is_none());
    assert_eq!(
        events.into_inner().unwrap(),
        [VoiceChatStreamEvent::new(
            "interrupted",
            report.turn_id,
            None,
            None
        )]
    );
    worker.join().unwrap();
}

#[test]
fn interruption_during_transcription_leaves_no_started_chat_and_signals_pcm_retention() {
    let (mut backend, server) = fixture();
    let cancel = backend.cancel.clone();
    let worker = thread::spawn(move || {
        let (_s, p) = request(&server, "/api/voice/transcriptions");
        assert!(p.windows(4).any(|b| b == b"RIFF"));
        cancel.cancel();
        thread::sleep(Duration::from_millis(100));
    });
    let command = vec![17i16; 320];
    let report = backend.run_turn::<AudioFixture>(
        Uuid::new_v4(),
        None,
        &command,
        None,
        |_| panic!(),
        |_| {},
        || panic!(),
    );
    assert_eq!(report.result.err(), Some("cancelled"));
    assert!(!report.chat_started && report.transcript.is_none() && report.conversation.is_none());
    assert!(backend.turn.is_none()); // no remote generation to cancel or invent
    assert_eq!(command, [17i16; 320]); // caller retains PCM for the next handoff
    worker.join().unwrap();
}

#[test]
fn playback_interruption_uses_heard_position_excludes_queued_frames_and_next_turn_sends_pointer() {
    let (mut backend, server) = fixture();
    let session = Uuid::new_v4();
    let convo = Uuid::new_v4();
    let msg = Uuid::new_v4();
    let seg = Uuid::new_v4();
    let prefetched_segment = Uuid::new_v4();
    let (prefetched, prefetch_ready) = std::sync::mpsc::channel();
    let worker = thread::spawn(move || {
        transcript(&server, "Conta uma história");
        let (s, p) = request(&server, "/api/chat/messages/stream");
        let body: Value = serde_json::from_slice(&p).unwrap();
        let turn = body["turnId"].clone();
        stream(
            s,
            &[
                segment_event(&turn, convo, msg, seg, 0),
                segment_event(&turn, convo, msg, prefetched_segment, 1),
                done_event(&turn, convo, msg, false),
            ],
        );
        pcm(&server, &turn, convo, msg, seg, &[10, 20, 30, 40]);
        pcm(
            &server,
            &turn,
            convo,
            msg,
            prefetched_segment,
            &[99, 99, 99],
        );
        prefetched.send(()).unwrap();
        transcript(&server, "Pode continuar");
        let (s, p) = request(&server, "/api/chat/messages/stream");
        let next: Value = serde_json::from_slice(&p).unwrap();
        assert_eq!(next["conversationId"], json!(convo));
        assert_eq!(next["voiceSessionId"], json!(session));
        assert_eq!(next["interruptedPlayback"]["turnId"], turn);
        assert_eq!(next["interruptedPlayback"]["segmentId"], json!(seg));
        assert_eq!(next["interruptedPlayback"]["playedFramesInSegment"], 1);
        assert!(next.get("interruptedGeneration").is_none());
        stream(s, &[json!({"type":"ignored","turnId":next["turnId"]})]);
    });
    let output = AudioFixture {
        interrupt: true,
        wait_for_prefetch: Some(prefetch_ready),
        ..Default::default()
    };
    let heard = output.played.clone();
    let first = backend.run_turn(
        session,
        Some(convo),
        &[0; 64],
        None,
        |_| {},
        |_| {},
        || Ok(output),
    );
    assert_eq!(first.result.err(), Some("cancelled"));
    let pointer = first.playback.unwrap();
    assert_eq!(pointer.played_frames_in_segment, 1);
    assert_eq!(pointer.segment_frames, None);
    assert!(!heard.lock().unwrap().contains(&99));
    // The real turn worker's Drop also cancels the previous generation.
    backend.turn = None;
    backend.speech = None;
    let mut next = backend.fork(&CancellationToken::new());
    let report = next.run_turn::<AudioFixture>(
        session,
        Some(convo),
        &[0; 64],
        Some(&Interruption::Playback(pointer)),
        |_| {},
        |_| {},
        || panic!(),
    );
    assert!(report.result.unwrap().ignored);
    worker.join().unwrap();
}

#[test]
fn before_playback_handoff_sends_generation_id_and_text_exclusively() {
    let (mut backend, server) = fixture();
    let session = Uuid::new_v4();
    let prior = Uuid::new_v4();
    let worker = thread::spawn(move || {
        transcript(&server, "Segunda fala");
        let (s, p) = request(&server, "/api/chat/messages/stream");
        let body: Value = serde_json::from_slice(&p).unwrap();
        assert!(body["conversationId"].is_null());
        assert_eq!(
            body["interruptedGeneration"],
            json!({"turnId":prior,"previousContent":"Primeira fala"})
        );
        assert!(body.get("interruptedPlayback").is_none());
        stream(s, &[json!({"type":"ignored","turnId":body["turnId"]})]);
    });
    let report = backend.run_turn::<AudioFixture>(
        session,
        None,
        &[0; 64],
        Some(&Interruption::Generation {
            turn_id: prior,
            previous_content: "Primeira fala".into(),
        }),
        |_| {},
        |_| {},
        || panic!(),
    );
    assert!(report.result.unwrap().ignored);
    worker.join().unwrap();
}

#[test]
fn invalid_stream_turn_id_and_extra_events_after_terminal_fail_without_audio() {
    for trailing in [false, true] {
        let (mut backend, server) = fixture();
        let session = Uuid::new_v4();
        let worker = thread::spawn(move || {
            transcript(&server, "Pedido");
            let (s, p) = request(&server, "/api/chat/messages/stream");
            let body: Value = serde_json::from_slice(&p).unwrap();
            let mut events = vec![
                json!({"type":"ignored","turnId":if trailing {body["turnId"].clone()} else {json!(Uuid::new_v4())}}),
            ];
            if trailing {
                events.push(json!({"type":"token","turnId":body["turnId"],"content":"atrasado"}));
            }
            stream(s, &events);
        });
        let report = backend.run_turn::<AudioFixture>(
            session,
            None,
            &[0; 64],
            None,
            |_| panic!(),
            |_| {},
            || panic!("invalid stream must not open output"),
        );
        assert!(report.result.is_err());
        worker.join().unwrap();
    }
}

#[test]
fn malformed_segment_cannot_announce_a_new_destination_or_open_output() {
    let (mut backend, server) = fixture();
    let worker = thread::spawn(move || {
        transcript(&server, "Pedido");
        let (s, p) = request(&server, "/api/chat/messages/stream");
        let body: Value = serde_json::from_slice(&p).unwrap();
        let invalid = segment_event(
            &body["turnId"],
            Uuid::new_v4(),
            Uuid::new_v4(),
            Uuid::new_v4(),
            1,
        );
        stream(s, &[invalid]);
    });
    let events = Mutex::new(Vec::new());
    let report = backend.run_turn::<AudioFixture>(
        Uuid::new_v4(),
        None,
        &[0; 64],
        None,
        |_| panic!("malformed segment must not announce conversation"),
        |event| events.lock().unwrap().push(event),
        || panic!("malformed segment must not speak"),
    );
    assert!(report.result.is_err() && !report.accepted && report.conversation.is_none());
    assert_eq!(
        events.into_inner().unwrap(),
        [VoiceChatStreamEvent::new(
            "error",
            report.turn_id,
            None,
            None
        )]
    );
    worker.join().unwrap();
}

#[test]
fn position_tracker_uses_actual_segment_boundary_and_unknown_length() {
    let mut tracker = PlaybackProgress::default();
    let convo = Uuid::new_v4();
    let turn = Uuid::new_v4();
    let msg = Uuid::new_v4();
    let first = Uuid::new_v4();
    let second = Uuid::new_v4();
    tracker.begin(
        Segment {
            conversation: convo,
            assistant: msg,
            id: first,
            sequence: 0,
        },
        0,
    );
    tracker.length(100);
    tracker.begin(
        Segment {
            conversation: convo,
            assistant: msg,
            id: second,
            sequence: 1,
        },
        100,
    );
    let a = tracker.at(turn, 99).unwrap();
    assert_eq!(a.segment_id, first);
    assert_eq!(a.played_frames_in_segment, 99);
    let b = tracker.at(turn, 100).unwrap();
    assert_eq!(b.segment_id, second);
    assert_eq!(b.played_frames_in_segment, 0);
    assert_eq!(b.segment_frames, None);
    assert!(tracker.at(turn, MAX_PLAYBACK_FRAMES + 1).is_none());
}
