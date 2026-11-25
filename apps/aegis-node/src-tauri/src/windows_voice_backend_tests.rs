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
        current_credential: Box::new(|| Some("fixture".into())),
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
    let (mut socket, _) = server.accept().unwrap();
    socket
        .set_read_timeout(Some(Duration::from_secs(3)))
        .unwrap();
    let mut input = BufReader::new(&mut socket);
    let mut line = String::new();
    input.read_line(&mut line).unwrap();
    assert_eq!(line.trim_end(), format!("POST {path} HTTP/1.1"));
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
            length = Some(val.trim().parse::<usize>().unwrap())
        }
        if key.eq_ignore_ascii_case("authorization") {
            authorized = val.trim() == "AegisNode fixture"
        }
    }
    assert!(authorized);
    let mut payload = vec![0; length.unwrap()];
    input.read_exact(&mut payload).unwrap();
    (socket, payload)
}

fn answer(mut socket: TcpStream, body: Value) {
    let bytes = serde_json::to_vec(&body).unwrap();
    write!(socket,"HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: {}\r\n\r\n",bytes.len()).unwrap();
    socket.write_all(&bytes).unwrap();
}

#[test]
fn winner_transcribes_one_command_sends_it_to_fixed_conversation_and_decodes_streamed_pcm() {
    let (mut backend, server) = fixture();
    let convo = Uuid::new_v4();
    let id = Uuid::new_v4();
    let msg = Uuid::new_v4();
    let worker = thread::spawn(move || {
        let (s, p) = request(&server, "/api/nodes/me/voice-activations/claim");
        let body: Value = serde_json::from_slice(&p).unwrap();
        assert_eq!(body["activationId"], id.to_string());
        assert_eq!(body["snrDb"], 12.);
        answer(s, json!({"activationId":id,"decision":"granted"}));
        let (s, p) = request(&server, "/api/voice/transcriptions");
        assert!(p.windows(4).any(|s| s == b"RIFF"));
        let text = String::from_utf8_lossy(&p);
        let id_text = text
            .split("name=\"transcriptionRequestId\"\r\n\r\n")
            .nth(1)
            .unwrap()
            .split("\r\n")
            .next()
            .unwrap();
        answer(
            s,
            json!({"transcriptionRequestId":id_text,"text":"Aegis, continue de onde parou"}),
        );
        let (s, p) = request(&server, "/api/chat/messages");
        let body: Value = serde_json::from_slice(&p).unwrap();
        assert_eq!(body["content"], "continue de onde parou");
        assert_eq!(body["origin"], "hands_free");
        assert_eq!(body["allowSilentIgnore"], true);
        assert_eq!(body["voiceSessionId"], id.to_string());
        assert_eq!(body["conversationId"], convo.to_string());
        let turn = body["turnId"].clone();
        answer(
            s,
            json!({"conversationId":convo,"message":{"id":msg,"role":"assistant","conversationId":convo},"voiceContinuation":"continue"}),
        );
        let (mut s, p) = request(&server, "/api/voice/speech");
        let body: Value = serde_json::from_slice(&p).unwrap();
        assert_eq!(body["turnId"], turn);
        assert_eq!(body["assistantMessageId"], msg.to_string());
        write!(s,"HTTP/1.1 200 OK\r\nConnection: close\r\nX-Aegis-Audio-Format: pcm_s16le\r\nX-Aegis-Sample-Rate: 24000\r\nX-Aegis-Channels: 1\r\nTransfer-Encoding: chunked\r\n\r\n1\r\n").unwrap();
        s.write_all(&[0x34]).unwrap();
        s.write_all(b"\r\n3\r\n").unwrap();
        s.write_all(&[0x12, 0xff, 0xff]).unwrap();
        s.write_all(b"\r\n0\r\n\r\n").unwrap();
    });
    assert!(backend.claim(id, Instant::now(), 0.9, Some(12.)).unwrap());
    let text = backend.transcribe(&[0i16; 64]).unwrap();
    let reply = backend.respond(id, Some(convo), text).unwrap().unwrap();
    assert!(reply.continuation);
    let mut played = Vec::new();
    backend
        .speech_pcm(&reply, |samples| {
            played.extend_from_slice(samples);
            Ok(())
        })
        .unwrap();
    assert_eq!(played, [0x1234, -1]);
    backend.turn = None;
    backend.speech = None;
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
    revoked.current_credential = Box::new(|| None);
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
fn ignored_transcript_has_no_reply_or_cleanup_turn_and_next_request_still_works() {
    let (mut backend, server) = fixture();
    let session = Uuid::new_v4();
    let convo = Uuid::new_v4();
    let msg = Uuid::new_v4();
    let worker = thread::spawn(move || {
        for destination in [None, Some(convo)] {
            let (mut s, p) = request(&server, "/api/chat/messages");
            let body: Value = serde_json::from_slice(&p).unwrap();
            assert_eq!(body["conversationId"], json!(destination));
            assert_eq!(body["voiceSessionId"], json!(session));
            assert_eq!(body["allowSilentIgnore"], true);
            assert_eq!(body["origin"], "hands_free");
            s.write_all(b"HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n")
                .unwrap();
        }
        // The next request is still allowed in the same conversation. The
        // legacy success JSON contains the existing assistant reply.
        let (s, p) = request(&server, "/api/chat/messages");
        let body: Value = serde_json::from_slice(&p).unwrap();
        assert_eq!(body["conversationId"], json!(convo));
        assert_eq!(body["content"], "sim");
        answer(
            s,
            json!({"conversationId":convo,"message":{"id":msg,"role":"assistant","conversationId":convo},"voiceContinuation":"continue"}),
        );
    });
    for destination in [None, Some(convo)] {
        assert!(backend
            .respond(session, destination, "Conversa ao fundo.".into())
            .unwrap()
            .is_none());
        assert!(backend.turn.is_none());
        assert!(backend.speech.is_none());
    }
    let reply = backend
        .respond(session, Some(convo), "sim".into())
        .unwrap()
        .unwrap();
    assert_eq!(reply.conversation, convo);
    assert_eq!(reply.message, msg);
    assert!(reply.continuation);
    backend.turn = None;
    worker.join().unwrap();
}
