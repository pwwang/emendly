use std::collections::{BTreeMap, HashMap};
use std::env;
use std::fs::OpenOptions;
use std::io::{self, BufRead, BufWriter, Write};
use std::process::ExitCode;
use std::sync::{mpsc, Arc, Mutex};
use std::thread;
use std::time::Instant;

use emendly_engine::actions::ActionRegistry;
use emendly_engine::config::{read_api_key, Config, LoggingConfig};
use emendly_engine::protocol::{
    parse_request, EngineError, Event, EventKind, Request, RequestKind, Usage, ENGINE_VERSION,
    PROTOCOL_VERSION,
};
use emendly_engine::providers::openai::{stream_chat, CancelToken};

struct EngineState {
    config: Config,
    registry: Arc<ActionRegistry>,
    active: Arc<Mutex<HashMap<String, CancelToken>>>,
    workers: Vec<thread::JoinHandle<()>>,
    tx: mpsc::Sender<Event>,
}

struct RunInput {
    id: String,
    action: String,
    text: String,
    options: BTreeMap<String, serde_json::Value>,
    context: Option<String>,
    app: Option<String>,
    stream: bool,
}

fn main() -> ExitCode {
    let once = match parse_args() {
        Ok(v) => v,
        Err(msg) => {
            let _ = writeln!(io::stderr(), "{msg}");
            return ExitCode::from(2);
        }
    };
    let config = match Config::load_from_env() {
        Ok(c) => c,
        Err(e) => {
            let _ = writeln!(io::stderr(), "config error: {e}");
            return ExitCode::from(1);
        }
    };
    let registry = match ActionRegistry::load_default() {
        Ok(r) => Arc::new(r),
        Err(e) => {
            let _ = writeln!(io::stderr(), "actions error: {e}");
            return ExitCode::from(1);
        }
    };

    let (tx, rx) = mpsc::channel::<Event>();
    let writer = spawn_writer(rx);
    let mut state = EngineState {
        config,
        registry,
        active: Arc::new(Mutex::new(HashMap::new())),
        workers: Vec::new(),
        tx,
    };

    run_stdin_loop(&mut state, once);
    for h in state.workers {
        let _ = h.join();
    }
    drop(state.tx);

    match writer.join() {
        Ok(Ok(())) => ExitCode::SUCCESS,
        _ => ExitCode::from(1),
    }
}

fn parse_args() -> Result<bool, String> {
    let mut once = false;
    for arg in env::args().skip(1) {
        if arg == "--once" {
            once = true;
        } else {
            return Err(format!(
                "unknown argument: {arg}\nusage: emendly-engine [--once]"
            ));
        }
    }
    Ok(once)
}

fn spawn_writer(rx: mpsc::Receiver<Event>) -> thread::JoinHandle<io::Result<()>> {
    thread::spawn(move || {
        let stdout = io::stdout();
        let mut out = BufWriter::new(stdout.lock());
        for event in rx {
            serde_json::to_writer(&mut out, &event).map_err(io::Error::other)?;
            out.write_all(b"\n")?;
            out.flush()?;
        }
        Ok(())
    })
}

fn run_stdin_loop(state: &mut EngineState, once: bool) {
    let stdin = io::stdin();
    for line in stdin.lock().lines().map_while(Result::ok) {
        if line.trim().is_empty() {
            continue;
        }
        let stop = match parse_request(&line) {
            Ok(req) => dispatch_request(state, req),
            Err((id, err)) => {
                let _ = state.tx.send(err.to_event(id));
                false
            }
        };
        reap_finished(&mut state.workers);
        if stop || once {
            break;
        }
    }
}

fn dispatch_request(state: &mut EngineState, req: Request) -> bool {
    match req.kind {
        RequestKind::Health => {
            let _ = state.tx.send(Event::new(
                req.id,
                EventKind::Health {
                    version: ENGINE_VERSION.into(),
                    protocol: PROTOCOL_VERSION,
                },
            ));
        }
        RequestKind::ActionsList => {
            let _ = state.tx.send(Event::new(
                req.id,
                EventKind::Actions {
                    actions: state.registry.list(),
                },
            ));
        }
        RequestKind::ConfigGet => {
            let _ = state.tx.send(Event::new(
                req.id,
                EventKind::Config {
                    config: state.config.clone(),
                },
            ));
        }
        RequestKind::ConfigSet { patch } => {
            let ev = match state.config.apply_patch(&patch) {
                Ok(()) => Event::new(
                    req.id,
                    EventKind::Config {
                        config: state.config.clone(),
                    },
                ),
                Err(e) => e.to_event(req.id),
            };
            let _ = state.tx.send(ev);
        }
        RequestKind::Cancel { target } => {
            let found = state
                .active
                .lock()
                .ok()
                .and_then(|mut map| map.remove(&target))
                .map(|t| t.cancel())
                .is_some();
            if !found {
                let _ = state.tx.send(Event::new(req.id, EventKind::Cancelled));
            }
        }
        RequestKind::Shutdown => {
            if let Ok(mut map) = state.active.lock() {
                for (_, token) in map.drain() {
                    token.cancel();
                }
            }
            return true;
        }
        RequestKind::ActionsRun {
            action,
            text,
            options,
            context,
            app,
            stream,
        } => {
            spawn_worker(
                state,
                RunInput {
                    id: req.id,
                    action,
                    text,
                    options,
                    context,
                    app,
                    stream: stream.unwrap_or(true),
                },
            );
        }
    }
    false
}

fn spawn_worker(state: &mut EngineState, input: RunInput) {
    let cancel = CancelToken::new(state.config.provider.timeout_ms);
    if let Ok(mut map) = state.active.lock() {
        map.insert(input.id.clone(), cancel.clone());
    }
    let config = state.config.clone();
    let registry = Arc::clone(&state.registry);
    let active = Arc::clone(&state.active);
    let tx = state.tx.clone();

    state.workers.push(thread::spawn(move || {
        let started_at = Instant::now();
        let outcome = run_action(&input, &config, &registry, &cancel, &tx);
        if let Ok(mut map) = active.lock() {
            map.remove(&input.id);
        }
        let latency_ms = started_at.elapsed().as_millis() as u64;
        match outcome {
            Ok((text, usage)) => {
                let _ = tx.send(Event::new(
                    input.id,
                    EventKind::Done {
                        text,
                        usage: usage.clone(),
                        latency_ms,
                    },
                ));
                log_meta(
                    &config.logging,
                    &input.action,
                    input.app.as_deref(),
                    latency_ms,
                    &usage,
                    "ok",
                );
            }
            Err(err) => {
                let status = format!("{:?}", err.code());
                let _ = tx.send(err.to_event(input.id));
                log_meta(
                    &config.logging,
                    &input.action,
                    input.app.as_deref(),
                    latency_ms,
                    &Usage::default(),
                    &status,
                );
            }
        }
    }));
}

fn run_action(
    input: &RunInput,
    config: &Config,
    registry: &ActionRegistry,
    cancel: &CancelToken,
    tx: &mpsc::Sender<Event>,
) -> Result<(String, Usage), EngineError> {
    let prompt = registry.render(
        &input.action,
        &input.text,
        &input.options,
        input.context.as_deref(),
        config,
    )?;
    let api_key = read_api_key()?;
    if cancel.is_cancelled() {
        return Err(EngineError::Cancelled);
    }

    let _ = tx.send(Event::new(
        input.id.clone(),
        EventKind::Started {
            action: input.action.clone(),
            model: config.provider.model.clone(),
        },
    ));

    let res = stream_chat(&config.provider, &api_key, &prompt, cancel, |text| {
        if input.stream {
            let _ = tx.send(Event::new(input.id.clone(), EventKind::Delta { text }));
        }
    })?;
    Ok((res.text, res.usage))
}

fn log_meta(
    logging: &LoggingConfig,
    action: &str,
    app: Option<&str>,
    latency_ms: u64,
    usage: &Usage,
    status: &str,
) {
    let enabled = match logging.level.as_str() {
        "off" => false,
        "debug" | "info" => true,
        _ => status != "ok" && !logging.path.is_empty(),
    };
    if !enabled {
        return;
    }
    let rec = serde_json::json!({
        "action": action,
        "app": app.unwrap_or(""),
        "latency_ms": latency_ms,
        "input_tokens": usage.input_tokens,
        "output_tokens": usage.output_tokens,
        "status": status,
    });
    if !logging.path.is_empty() {
        if let Ok(mut f) = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&logging.path)
        {
            let _ = writeln!(f, "{rec}");
        }
    } else {
        let _ = writeln!(io::stderr(), "{rec}");
    }
}

fn reap_finished(workers: &mut Vec<thread::JoinHandle<()>>) {
    let mut i = 0;
    while i < workers.len() {
        if workers[i].is_finished() {
            let _ = workers.swap_remove(i).join();
        } else {
            i += 1;
        }
    }
}
