use std::io::{BufRead, BufReader, Read, Write};
use std::net::{TcpListener, TcpStream};
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use std::{env, fs, thread};

use serde_json::Value;

struct TempConfig(PathBuf);

impl TempConfig {
    fn new(base_url: &str, timeout_ms: u64) -> Self {
        static NEXT_ID: AtomicU64 = AtomicU64::new(0);
        let seq = NEXT_ID.fetch_add(1, Ordering::Relaxed);
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let path = env::temp_dir().join(format!(
            "emendly-it-{}-{nanos}-{seq}.toml",
            std::process::id()
        ));
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
    spawn_engine_with_env(cfg, api_key, args, &[])
}

fn spawn_engine_with_env(
    cfg: Option<&TempConfig>,
    api_key: Option<&str>,
    args: &[&str],
    env: &[(&str, &str)],
) -> Child {
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
    cmd.env_remove("EMENDLY_ACTIONS_DIR");
    cmd.env_remove("EMENDLY_EXTRA_ACTIONS_DIR");
    for (k, v) in env {
        cmd.env(k, v);
    }
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
        .any(|e| e["id"] == "r2" && e["actions"].as_array().unwrap().len() == 9));
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

fn send_until_terminal(
    stdin: &mut std::process::ChildStdin,
    lines: &mut std::io::Lines<BufReader<std::process::ChildStdout>>,
    req: &str,
) -> Vec<Value> {
    writeln!(stdin, "{req}").unwrap();
    stdin.flush().unwrap();
    let mut events = Vec::new();
    for line in lines {
        let ev: Value = serde_json::from_str(&line.unwrap()).unwrap();
        let terminal = matches!(ev["event"].as_str(), Some("done" | "error" | "cancelled"));
        events.push(ev);
        if terminal {
            break;
        }
    }
    events
}

struct TempDir(PathBuf);

impl TempDir {
    fn new() -> Self {
        static NEXT_ID: AtomicU64 = AtomicU64::new(0);
        let seq = NEXT_ID.fetch_add(1, Ordering::Relaxed);
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let path = env::temp_dir().join(format!(
            "emendly-it-dir-{}-{nanos}-{seq}",
            std::process::id()
        ));
        fs::create_dir_all(&path).unwrap();
        Self(path)
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

#[test]
fn menu_actions_options_and_context_handling() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let addr = listener.local_addr().unwrap();
    let (req_tx, req_rx) = mpsc::channel();

    let server = thread::spawn(move || {
        for _ in 0..3 {
            let (mut stream, _) = listener.accept().unwrap();
            req_tx.send(read_http_request(&mut stream)).unwrap();
            let resp = concat!(
                "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n",
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n",
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}\n\n",
                "data: [DONE]\n\n"
            );
            stream.write_all(resp.as_bytes()).unwrap();
            stream.flush().unwrap();
        }
    });

    let cfg = TempConfig::new(&format!("http://{addr}/v1"), 10_000);
    let mut child = spawn_engine(Some(&cfg), Some("test-mock-key"), &[]);
    let mut stdin = child.stdin.take().unwrap();
    let mut lines = BufReader::new(child.stdout.take().unwrap()).lines();

    let missing_inst = send_until_terminal(
        &mut stdin,
        &mut lines,
        r#"{"id":"c0","type":"actions.run","action":"custom","text":"hi"}"#,
    );
    assert_eq!(missing_inst.len(), 1);
    assert_eq!(missing_inst[0]["event"], "error");
    assert_eq!(missing_inst[0]["code"], "bad_request");

    let custom_events = send_until_terminal(
        &mut stdin,
        &mut lines,
        r#"{"id":"c1","type":"actions.run","action":"custom","text":"Draft.","options":{"instruction":"Tighten this","include_context":true,"context_before":"Before line.","context_after":"After line.","ignored_extra":true}}"#,
    );
    assert_eq!(custom_events.last().unwrap()["event"], "done");
    let (_, body1) = req_rx.recv().unwrap();
    let json1: Value = serde_json::from_slice(&body1).unwrap();
    assert_eq!(
        json1["messages"][1]["content"].as_str().unwrap(),
        "Context before:\nBefore line.\n\nContext after:\nAfter line.\n\nInstruction: Tighten this\n\nDraft."
    );

    let no_ctx_events = send_until_terminal(
        &mut stdin,
        &mut lines,
        r#"{"id":"c2","type":"actions.run","action":"rewrite","text":"Ship it.","options":{"include_context":false,"context_before":"Secret before","context_after":"Secret after"}}"#,
    );
    assert_eq!(no_ctx_events.last().unwrap()["event"], "done");
    let (_, body2) = req_rx.recv().unwrap();
    let json2: Value = serde_json::from_slice(&body2).unwrap();
    let msg2 = json2["messages"][1]["content"].as_str().unwrap();
    assert!(!msg2.contains("Context before:"));
    assert!(!msg2.contains("Context after:"));
    assert!(!msg2.contains("Secret before"));
    assert!(!msg2.contains("Secret after"));
    assert!(msg2.contains("Ship it."));

    let translate_events = send_until_terminal(
        &mut stdin,
        &mut lines,
        r#"{"id":"c3","type":"actions.run","action":"translate","text":"Hello","options":{"target_lang":"Japanese"}}"#,
    );
    assert_eq!(translate_events.last().unwrap()["event"], "done");
    let (_, body3) = req_rx.recv().unwrap();
    let json3: Value = serde_json::from_slice(&body3).unwrap();
    let msg3 = json3["messages"][1]["content"].as_str().unwrap();
    assert!(msg3.contains("Target language: Japanese"));
    assert!(msg3.contains("Hello"));

    writeln!(stdin, r#"{{"id":"s2","type":"shutdown"}}"#).unwrap();
    drop(stdin);
    assert!(child.wait().unwrap().success());
    server.join().unwrap();
}

#[test]
fn actions_reload_and_glossary_system_prefix_reach_the_provider() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let addr = listener.local_addr().unwrap();
    let (req_tx, req_rx) = mpsc::channel();

    let server = thread::spawn(move || {
        let (mut stream, _) = listener.accept().unwrap();
        req_tx.send(read_http_request(&mut stream)).unwrap();
        let resp = concat!(
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Guten \"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Tag\"}}]}\n\n",
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":40,\"completion_tokens\":4}}\n\n",
            "data: [DONE]\n\n"
        );
        stream.write_all(resp.as_bytes()).unwrap();
        stream.flush().unwrap();
    });

    let extra = TempDir::new();
    fs::write(
        extra.0.join("summarize.toml"),
        "name = \"summarize\"\ndescription = \"Condense text.\"\nrequires_text = true\nsystem_prompt = \"Summarize concisely.\"\nuser_template = \"Text:\\n{{text}}\"\n",
    )
    .unwrap();

    let cfg = TempConfig::new(&format!("http://{addr}/v1"), 10_000);
    let extra_path = extra.0.to_str().unwrap().to_string();
    let mut child = spawn_engine_with_env(
        Some(&cfg),
        Some("test-mock-key"),
        &[],
        &[("EMENDLY_EXTRA_ACTIONS_DIR", &extra_path)],
    );
    let mut stdin = child.stdin.take().unwrap();
    let mut lines = BufReader::new(child.stdout.take().unwrap()).lines();

    writeln!(stdin, r#"{{"id":"l1","type":"actions.list"}}"#).unwrap();
    stdin.flush().unwrap();
    let initial: Value = serde_json::from_str(&lines.next().unwrap().unwrap()).unwrap();
    assert_eq!(initial["event"], "actions");
    assert_eq!(initial["actions"].as_array().unwrap().len(), 10);

    writeln!(stdin, r#"{{"id":"l2","type":"actions.reload"}}"#).unwrap();
    stdin.flush().unwrap();
    let reloaded: Value = serde_json::from_str(&lines.next().unwrap().unwrap()).unwrap();
    assert_eq!(
        (&reloaded["id"], &reloaded["event"]),
        (&Value::from("l2"), &Value::from("actions"))
    );
    let names: Vec<_> = reloaded["actions"]
        .as_array()
        .unwrap()
        .iter()
        .map(|a| a["name"].as_str().unwrap())
        .collect();
    assert_eq!(names.len(), 10);
    assert!(names.contains(&"summarize"));
    assert!(names.contains(&"proofread"));

    let events = send_until_terminal(
        &mut stdin,
        &mut lines,
        r#"{"id":"g1","type":"actions.run","action":"translate","text":"Good morning","options":{"target_lang":"German","system_prefix":"Honor the product glossary exactly.","glossary":[{"source":"Emendly","do_not_translate":true},{"source":"engine","target":"Motor","note":"product term"}]}}"#,
    );
    writeln!(stdin, r#"{{"id":"s3","type":"shutdown"}}"#).unwrap();
    drop(stdin);
    assert!(child.wait().unwrap().success());
    server.join().unwrap();

    let (_, body) = req_rx.recv().unwrap();
    let json: Value = serde_json::from_slice(&body).unwrap();
    let system = json["messages"][0]["content"].as_str().unwrap();
    let prefix_at = system.find("Honor the product glossary exactly.").unwrap();
    let glossary_at = system.find("Terminology rules (follow strictly):").unwrap();
    let base_at = system.find("Translate the user's text").unwrap();
    assert!(prefix_at < glossary_at && glossary_at < base_at);
    assert!(system.contains("- \"Emendly\" -> keep exactly as \"Emendly\" (do not translate)"));
    assert!(system.contains("- \"engine\" -> \"Motor\" (note: product term)"));
    assert_eq!(
        json["messages"][1]["content"].as_str().unwrap(),
        "Target language: German\n\nText:\nGood morning"
    );

    assert_eq!(events[0]["event"], "started");
    let combined: String = events[1..events.len() - 1]
        .iter()
        .map(|e| e["text"].as_str().unwrap())
        .collect();
    let last = events.last().unwrap();
    assert_eq!(last["event"], "done");
    assert_eq!(last["text"], "Guten Tag");
    assert_eq!(combined, "Guten Tag");
}
