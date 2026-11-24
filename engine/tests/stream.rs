use std::io::{BufRead, BufReader, Read, Write};
use std::net::{TcpListener, TcpStream};
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::mpsc;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use std::{env, fs, thread};

use serde_json::Value;

struct TempConfig(PathBuf);

impl TempConfig {
    fn new(base_url: &str, timeout_ms: u64) -> Self {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let path = env::temp_dir().join(format!("emendly-it-{}-{nanos}.toml", std::process::id()));
        fs::write(
            &path,
            format!("[provider]\nbase_url = \"{base_url}\"\nmodel = \"gpt-5-mini\"\ntimeout_ms = {timeout_ms}\n"),
        )
        .unwrap();
        Self(path)
    }
}

impl Drop for TempConfig {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.0);
    }
}

fn spawn_engine(cfg: Option<&TempConfig>, api_key: Option<&str>, args: &[&str]) -> Child {
    let mut cmd = Command::new(env!("CARGO_BIN_EXE_emendly-engine"));
    cmd.args(args).stdin(Stdio::piped()).stdout(Stdio::piped());
    match cfg {
        Some(c) => cmd.env("EMENDLY_CONFIG", &c.0),
        None => cmd.env_remove("EMENDLY_CONFIG"),
    };
    match api_key {
        Some(k) => cmd.env("EMENDLY_API_KEY", k),
        None => cmd.env_remove("EMENDLY_API_KEY"),
    };
    cmd.spawn().unwrap()
}

fn read_http_request(stream: &mut TcpStream) -> (String, Vec<u8>) {
    let mut reader = BufReader::new(stream.try_clone().unwrap());
    let mut headers = String::new();
    let mut content_length = 0usize;
    loop {
        let mut line = String::new();
        if reader.read_line(&mut line).unwrap() == 0 || line == "\r\n" || line == "\n" {
            break;
        }
        if let Some(v) = line.to_ascii_lowercase().strip_prefix("content-length:") {
            content_length = v.trim().parse().unwrap_or(0);
        }
        headers.push_str(&line);
    }
    let mut body = vec![0u8; content_length];
    if content_length > 0 {
        reader.read_exact(&mut body).unwrap();
    }
    (headers, body)
}

#[test]
fn rewrite_streams_started_deltas_and_done() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let addr = listener.local_addr().unwrap();
    let (req_tx, req_rx) = mpsc::channel();

    let server = thread::spawn(move || {
        let (mut stream, _) = listener.accept().unwrap();
        req_tx.send(read_http_request(&mut stream)).unwrap();
        let chunks = [
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Could we \"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"have this ready \"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"by Friday?\"}}]}\n\n",
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":21,\"completion_tokens\":7}}\n\n",
            "data: [DONE]\n\n",
        ];
        for chunk in chunks {
            stream.write_all(chunk.as_bytes()).unwrap();
            stream.flush().unwrap();
        }
    });

    let cfg = TempConfig::new(&format!("http://{addr}/v1"), 10_000);
    let mut child = spawn_engine(Some(&cfg), Some("test-mock-key"), &[]);
    let mut stdin = child.stdin.take().unwrap();
    let mut lines = BufReader::new(child.stdout.take().unwrap()).lines();

    let req = r#"{"id":"r6","type":"actions.run","action":"rewrite","text":"We need this by Friday.","options":{"tone":"professional"}}"#;
    writeln!(stdin, "{req}").unwrap();
    stdin.flush().unwrap();

    let mut events = Vec::new();
    for line in &mut lines {
        let ev: Value = serde_json::from_str(&line.unwrap()).unwrap();
        let terminal = matches!(ev["event"].as_str(), Some("done" | "error" | "cancelled"));
        events.push(ev);
        if terminal {
            break;
        }
    }
    writeln!(stdin, r#"{{"id":"r10","type":"shutdown"}}"#).unwrap();
    drop(stdin);

    assert!(child.wait().unwrap().success());
    server.join().unwrap();

    let (headers, body) = req_rx.recv().unwrap();
    assert!(headers.starts_with("POST /v1/chat/completions HTTP/1.1"));
    assert!(headers
        .to_ascii_lowercase()
        .contains("authorization: bearer test-mock-key"));
    let body_json: Value = serde_json::from_slice(&body).unwrap();
    assert_eq!(body_json["stream"], true);
    assert!(body_json["messages"][1]["content"]
        .as_str()
        .unwrap()
        .contains("Tone: professional"));

    assert!(events.len() >= 3);
    assert_eq!(
        (&events[0]["id"], &events[0]["event"], &events[0]["action"]),
        (
            &Value::from("r6"),
            &Value::from("started"),
            &Value::from("rewrite")
        )
    );
    let combined: String = events[1..events.len() - 1]
        .iter()
        .map(|e| {
            assert_eq!(e["event"], "delta");
            e["text"].as_str().unwrap()
        })
        .collect();
    let last = events.last().unwrap();
    assert_eq!(last["event"], "done");
    assert_eq!(last["text"], combined);
    assert_eq!(combined, "Could we have this ready by Friday?");
    assert_eq!(
        (
            &last["usage"]["input_tokens"],
            &last["usage"]["output_tokens"]
        ),
        (&Value::from(21), &Value::from(7))
    );
}

#[test]
fn once_health_and_missing_key_behave_cleanly() {
    let mut child = spawn_engine(None, None, &["--once"]);
    writeln!(
        child.stdin.as_mut().unwrap(),
        r#"{{"id":"a","type":"health"}}"#
    )
    .unwrap();
    let out = child.wait_with_output().unwrap();
    assert!(out.status.success());
    let lines: Vec<_> = std::str::from_utf8(&out.stdout).unwrap().lines().collect();
    assert_eq!(lines.len(), 1);
    let ev: Value = serde_json::from_str(lines[0]).unwrap();
    assert_eq!(
        (&ev["id"], &ev["event"], &ev["version"], &ev["protocol"]),
        (
            &Value::from("a"),
            &Value::from("health"),
            &Value::from("0.1.0"),
            &Value::from(1)
        )
    );

    let mut child = spawn_engine(None, None, &["--once"]);
    writeln!(
        child.stdin.as_mut().unwrap(),
        r#"{{"id":"k1","type":"actions.run","action":"proofread","text":"hi"}}"#
    )
    .unwrap();
    let out = child.wait_with_output().unwrap();
    assert!(out.status.success());
    let ev: Value = serde_json::from_str(std::str::from_utf8(&out.stdout).unwrap().trim()).unwrap();
    assert_eq!(
        (&ev["id"], &ev["event"], &ev["code"], &ev["retryable"]),
        (
            &Value::from("k1"),
            &Value::from("error"),
            &Value::from("missing_key"),
            &Value::from(false)
        )
    );
}

#[test]
fn cancel_aborts_in_flight_http_call() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let addr = listener.local_addr().unwrap();
    let (done_tx, done_rx) = mpsc::channel::<()>();

    let server = thread::spawn(move || {
        let (mut stream, _) = listener.accept().unwrap();
        let _ = read_http_request(&mut stream);
        let initial = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\ndata: {\"choices\":[{\"delta\":{\"content\":\"Partial \"}}]}\n\n";
        stream.write_all(initial.as_bytes()).unwrap();
        stream.flush().unwrap();
        let _ = done_rx.recv_timeout(Duration::from_secs(10));
    });

    let cfg = TempConfig::new(&format!("http://{addr}/v1"), 20_000);
    let mut child = spawn_engine(Some(&cfg), Some("mock-key"), &[]);
    let mut stdin = child.stdin.take().unwrap();
    let mut lines = BufReader::new(child.stdout.take().unwrap()).lines();

    writeln!(
        stdin,
        r#"{{"id":"r5","type":"actions.run","action":"proofread","text":"hi"}}"#
    )
    .unwrap();
    stdin.flush().unwrap();
    assert_eq!(
        serde_json::from_str::<Value>(&lines.next().unwrap().unwrap()).unwrap()["event"],
        "started"
    );
    assert_eq!(
        serde_json::from_str::<Value>(&lines.next().unwrap().unwrap()).unwrap()["event"],
        "delta"
    );

    let t0 = Instant::now();
    writeln!(stdin, r#"{{"id":"r9","type":"cancel","target":"r5"}}"#).unwrap();
    stdin.flush().unwrap();
    let cancelled: Value = serde_json::from_str(&lines.next().unwrap().unwrap()).unwrap();
    assert_eq!(
        (&cancelled["id"], &cancelled["event"]),
        (&Value::from("r5"), &Value::from("cancelled"))
    );
    assert!(t0.elapsed() < Duration::from_secs(3));

    writeln!(stdin, r#"{{"id":"r10","type":"shutdown"}}"#).unwrap();
    drop(stdin);
    let _ = done_tx.send(());
    assert!(child.wait().unwrap().success());
    server.join().unwrap();
}

#[test]
fn provider_auth_and_control_requests_work() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let addr = listener.local_addr().unwrap();
    let server = thread::spawn(move || {
        let (mut stream, _) = listener.accept().unwrap();
        let _ = read_http_request(&mut stream);
        stream
            .write_all(
                b"HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            )
            .unwrap();
    });

    let cfg = TempConfig::new(&format!("http://{addr}/v1"), 10_000);
    let mut child = spawn_engine(Some(&cfg), Some("bad-key"), &[]);
    let mut stdin = child.stdin.take().unwrap();
    let mut lines = BufReader::new(child.stdout.take().unwrap()).lines();

    for req in [
        r#"{"id":"r2","type":"actions.list"}"#,
        r#"{"id":"r3","type":"config.get"}"#,
        r#"{"id":"r4","type":"config.set","patch":{"model":"custom-model"}}"#,
        r#"{"id":"b1","type":"nope"}"#,
        "not json",
        r#"{"id":"u1","type":"actions.run","action":"missing","text":"hi"}"#,
        r#"{"id":"a1","type":"actions.run","action":"proofread","text":"hi"}"#,
    ] {
        writeln!(stdin, "{req}").unwrap();
    }
    stdin.flush().unwrap();

    let mut seen = Vec::new();
    for line in &mut lines {
        let ev: Value = serde_json::from_str(&line.unwrap()).unwrap();
        let done = ev["id"] == "a1" && ev["event"] == "error";
        seen.push(ev);
        if done {
            break;
        }
    }
    writeln!(stdin, r#"{{"id":"s1","type":"shutdown"}}"#).unwrap();
    drop(stdin);
    assert!(child.wait().unwrap().success());
    server.join().unwrap();

    assert!(seen
        .iter()
        .any(|e| e["id"] == "r2" && e["actions"].as_array().unwrap().len() == 5));
    assert!(seen
        .iter()
        .any(|e| e["id"] == "r3" && e["event"] == "config"));
    assert!(seen
        .iter()
        .any(|e| e["id"] == "r4" && e["config"]["provider"]["model"] == "custom-model"));
    assert!(seen
        .iter()
        .any(|e| e["id"] == "b1" && e["code"] == "bad_request"));
    assert!(seen
        .iter()
        .any(|e| e["id"] == "" && e["code"] == "bad_request"));
    assert!(seen
        .iter()
        .any(|e| e["id"] == "u1" && e["code"] == "unknown_action"));
    assert!(seen
        .iter()
        .any(|e| e["id"] == "a1" && e["code"] == "provider_auth" && e["retryable"] == false));
}
