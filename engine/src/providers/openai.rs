use std::io::{self, BufRead, BufReader};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;

use serde::{Deserialize, Serialize};

use crate::actions::RenderedPrompt;
use crate::config::ProviderConfig;
use crate::protocol::{EngineError, Usage};

#[derive(Clone)]
pub struct CancelToken {
    cancelled: Arc<AtomicBool>,
    terminal_sent: Arc<AtomicBool>,
    agent: ureq::Agent,
}

impl CancelToken {
    pub fn new(timeout_ms: u64) -> Self {
        let cfg = ureq::Agent::config_builder()
            .timeout_global(Some(Duration::from_millis(timeout_ms)))
            .build();
        Self {
            cancelled: Arc::new(AtomicBool::new(false)),
            terminal_sent: Arc::new(AtomicBool::new(false)),
            agent: ureq::Agent::new_with_config(cfg),
        }
    }

    pub fn cancel(&self) {
        self.cancelled.store(true, Ordering::SeqCst);
        self.agent.clone().shutdown();
    }

    pub fn is_cancelled(&self) -> bool {
        self.cancelled.load(Ordering::SeqCst)
    }

    pub fn claim_terminal(&self) -> bool {
        !self.terminal_sent.swap(true, Ordering::SeqCst)
    }
}

#[derive(Serialize)]
struct ChatRequest<'a> {
    model: &'a str,
    messages: [ChatMessage<'a>; 2],
    stream: bool,
    stream_options: StreamOptions,
    #[serde(skip_serializing_if = "Option::is_none")]
    max_tokens: Option<u32>,
}

#[derive(Serialize)]
struct ChatMessage<'a> {
    role: &'a str,
    content: &'a str,
}

#[derive(Serialize)]
struct StreamOptions {
    include_usage: bool,
}

#[derive(Deserialize)]
struct SseChunk {
    #[serde(default)]
    choices: Vec<SseChoice>,
    #[serde(default)]
    usage: Option<SseUsage>,
    #[serde(default)]
    error: Option<serde_json::Value>,
}

#[derive(Deserialize)]
struct SseChoice {
    #[serde(default)]
    delta: SseDelta,
}

#[derive(Default, Deserialize)]
struct SseDelta {
    #[serde(default)]
    content: Option<String>,
}

#[derive(Deserialize)]
struct SseUsage {
    prompt_tokens: Option<u64>,
    completion_tokens: Option<u64>,
    input_tokens: Option<u64>,
    output_tokens: Option<u64>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StreamResult {
    pub text: String,
    pub usage: Usage,
}

pub fn stream_chat<F: FnMut(String)>(
    provider: &ProviderConfig,
    api_key: &str,
    prompt: &RenderedPrompt,
    cancel: &CancelToken,
    mut on_delta: F,
) -> Result<StreamResult, EngineError> {
    if cancel.is_cancelled() {
        return Err(EngineError::Cancelled);
    }

    let endpoint = format!(
        "{}/chat/completions",
        provider.base_url.trim_end_matches('/')
    );
    let payload = ChatRequest {
        model: &provider.model,
        messages: [
            ChatMessage {
                role: "system",
                content: &prompt.system,
            },
            ChatMessage {
                role: "user",
                content: &prompt.user,
            },
        ],
        stream: true,
        stream_options: StreamOptions {
            include_usage: true,
        },
        max_tokens: prompt.max_tokens,
    };
    let body = serde_json::to_vec(&payload)
        .map_err(|e| EngineError::Internal(format!("failed to encode request: {e}")))?;

    let response = cancel
        .agent
        .post(&endpoint)
        .header("Authorization", &format!("Bearer {api_key}"))
        .header("Content-Type", "application/json")
        .header("Accept", "text/event-stream")
        .send(&body)
        .map_err(|e| map_ureq_error(e, cancel))?;

    if cancel.is_cancelled() {
        return Err(EngineError::Cancelled);
    }

    read_sse_stream(
        BufReader::new(response.into_body().into_reader()),
        cancel,
        &mut on_delta,
    )
}

fn read_sse_stream<R: BufRead, F: FnMut(String)>(
    mut reader: R,
    cancel: &CancelToken,
    on_delta: &mut F,
) -> Result<StreamResult, EngineError> {
    let mut full_text = String::new();
    let mut usage = Usage::default();
    let mut saw_data = false;
    let mut line = String::new();

    loop {
        line.clear();
        match reader.read_line(&mut line) {
            Ok(0) if cancel.is_cancelled() => return Err(EngineError::Cancelled),
            Ok(0) => break,
            Ok(_) if cancel.is_cancelled() => return Err(EngineError::Cancelled),
            Ok(_) => {
                let trimmed = line.trim_end_matches(&['\r', '\n'][..]);
                let Some(rest) = trimmed.strip_prefix("data:") else {
                    continue;
                };
                let data = rest.strip_prefix(' ').unwrap_or(rest).trim_end();
                saw_data = true;
                if data == "[DONE]" {
                    break;
                }
                process_chunk(data, &mut full_text, &mut usage, on_delta)?;
            }
            Err(_) if cancel.is_cancelled() => return Err(EngineError::Cancelled),
            Err(err) => return Err(map_io_error(err)),
        }
    }

    if !saw_data {
        return Err(EngineError::ProviderError(
            "provider returned an empty stream".into(),
        ));
    }
    Ok(StreamResult {
        text: full_text,
        usage,
    })
}

fn process_chunk<F: FnMut(String)>(
    data: &str,
    full_text: &mut String,
    usage: &mut Usage,
    on_delta: &mut F,
) -> Result<(), EngineError> {
    let chunk: SseChunk = serde_json::from_str(data)
        .map_err(|_| EngineError::ProviderError("invalid SSE JSON from provider".into()))?;
    if chunk.error.is_some() {
        return Err(EngineError::ProviderError(
            "provider returned an error chunk".into(),
        ));
    }
    if let Some(u) = chunk.usage {
        if let Some(n) = u.prompt_tokens.or(u.input_tokens) {
            usage.input_tokens = n;
        }
        if let Some(n) = u.completion_tokens.or(u.output_tokens) {
            usage.output_tokens = n;
        }
    }
    if let Some(content) = chunk
        .choices
        .into_iter()
        .next()
        .and_then(|c| c.delta.content)
    {
        if !content.is_empty() {
            full_text.push_str(&content);
            on_delta(content);
        }
    }
    Ok(())
}

fn map_ureq_error(err: ureq::Error, cancel: &CancelToken) -> EngineError {
    if cancel.is_cancelled() {
        return EngineError::Cancelled;
    }
    match err {
        ureq::Error::StatusCode(401 | 403) => {
            EngineError::ProviderAuth("the configured key was rejected".into())
        }
        ureq::Error::StatusCode(429) => {
            EngineError::ProviderRateLimit("provider rate limit exceeded".into())
        }
        ureq::Error::StatusCode(code) => {
            EngineError::ProviderError(format!("provider returned HTTP {code}"))
        }
        ureq::Error::Timeout(_) => EngineError::Timeout,
        ureq::Error::HostNotFound
        | ureq::Error::ConnectionFailed
        | ureq::Error::InvalidProxyUrl
        | ureq::Error::Tls(_)
        | ureq::Error::Rustls(_)
        | ureq::Error::Pem(_)
        | ureq::Error::Io(_) => EngineError::Network("failed to reach provider".into()),
        ureq::Error::BadUri(_) | ureq::Error::Http(_) => {
            EngineError::BadRequest("invalid provider base_url".into())
        }
        ureq::Error::Protocol(_)
        | ureq::Error::RedirectFailed
        | ureq::Error::TooManyRedirects
        | ureq::Error::BodyExceedsLimit(_) => {
            EngineError::ProviderError("invalid response from provider".into())
        }
        _ => EngineError::Internal("unexpected HTTP client error".into()),
    }
}

fn map_io_error(err: io::Error) -> EngineError {
    let timed_out = err.kind() == io::ErrorKind::TimedOut
        || err
            .get_ref()
            .and_then(|e| e.downcast_ref::<ureq::Error>())
            .is_some_and(|e| matches!(e, ureq::Error::Timeout(_)));
    if timed_out {
        EngineError::Timeout
    } else {
        EngineError::Network("stream interrupted".into())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    #[test]
    fn parses_sse_deltas_usage_and_errors() {
        let sse = concat!(
            ": keep-alive\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hello \"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"world\"}}]}\n\n",
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":4}}\n\n",
            "data: [DONE]\n\n"
        );
        let cancel = CancelToken::new(5_000);
        let mut deltas = Vec::new();
        let res = read_sse_stream(Cursor::new(sse), &cancel, &mut |d| deltas.push(d)).unwrap();
        assert_eq!(deltas, ["Hello ", "world"]);
        assert_eq!(res.text, "Hello world");
        assert_eq!(
            res.usage,
            Usage {
                input_tokens: 12,
                output_tokens: 4
            }
        );

        let bad = read_sse_stream(Cursor::new("data: {bad}\n\n"), &cancel, &mut |_| {});
        assert!(matches!(bad, Err(EngineError::ProviderError(_))));
    }
}
