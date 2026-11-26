use serde::{Deserialize, Serialize};
use thiserror::Error;

use crate::config::{Config, ConfigPatch};

pub const PROTOCOL_VERSION: u32 = 1;
pub const ENGINE_VERSION: &str = env!("CARGO_PKG_VERSION");

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ErrorCode {
    BadRequest,
    UnknownAction,
    MissingKey,
    ProviderAuth,
    ProviderRateLimit,
    ProviderError,
    Network,
    Timeout,
    Cancelled,
    Internal,
}

impl ErrorCode {
    pub fn is_retryable(self) -> bool {
        matches!(
            self,
            Self::ProviderRateLimit | Self::ProviderError | Self::Network | Self::Timeout
        )
    }
}

#[derive(Debug, Error)]
pub enum EngineError {
    #[error("{0}")]
    BadRequest(String),
    #[error("unknown action: {0}")]
    UnknownAction(String),
    #[error("EMENDLY_API_KEY is not set")]
    MissingKey,
    #[error("{0}")]
    ProviderAuth(String),
    #[error("{0}")]
    ProviderRateLimit(String),
    #[error("{0}")]
    ProviderError(String),
    #[error("{0}")]
    Network(String),
    #[error("request timed out")]
    Timeout,
    #[error("request cancelled")]
    Cancelled,
    #[error("{0}")]
    Internal(String),
}

impl EngineError {
    pub fn code(&self) -> ErrorCode {
        match self {
            Self::BadRequest(_) => ErrorCode::BadRequest,
            Self::UnknownAction(_) => ErrorCode::UnknownAction,
            Self::MissingKey => ErrorCode::MissingKey,
            Self::ProviderAuth(_) => ErrorCode::ProviderAuth,
            Self::ProviderRateLimit(_) => ErrorCode::ProviderRateLimit,
            Self::ProviderError(_) => ErrorCode::ProviderError,
            Self::Network(_) => ErrorCode::Network,
            Self::Timeout => ErrorCode::Timeout,
            Self::Cancelled => ErrorCode::Cancelled,
            Self::Internal(_) => ErrorCode::Internal,
        }
    }

    pub fn to_event(&self, id: String) -> Event {
        if matches!(self, Self::Cancelled) {
            return Event::new(id, EventKind::Cancelled);
        }
        let code = self.code();
        Event::new(
            id,
            EventKind::Error {
                code,
                message: self.to_string(),
                retryable: code.is_retryable(),
            },
        )
    }
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct GlossaryEntry {
    pub source: String,
    pub target: Option<String>,
    pub do_not_translate: bool,
    pub note: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct ActionOptions {
    pub include_context: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub context_before: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub context_after: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub target_lang: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub instruction: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tone: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub brief: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub context: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub system_prefix: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub system_override: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub user_template_override: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub glossary: Option<Vec<GlossaryEntry>>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Request {
    pub id: String,
    #[serde(flatten)]
    pub kind: RequestKind,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type")]
pub enum RequestKind {
    #[serde(rename = "health")]
    Health,
    #[serde(rename = "actions.list")]
    ActionsList,
    #[serde(rename = "actions.reload")]
    ActionsReload,
    #[serde(rename = "config.get")]
    ConfigGet,
    #[serde(rename = "config.set")]
    ConfigSet { patch: ConfigPatch },
    #[serde(rename = "actions.run")]
    ActionsRun {
        action: String,
        #[serde(default)]
        text: String,
        #[serde(default)]
        options: ActionOptions,
        #[serde(default)]
        context: Option<String>,
        #[serde(default)]
        app: Option<String>,
        #[serde(default)]
        stream: Option<bool>,
    },
    #[serde(rename = "cancel")]
    Cancel { target: String },
    #[serde(rename = "shutdown")]
    Shutdown,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct Usage {
    pub input_tokens: u64,
    pub output_tokens: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ActionInfo {
    pub name: String,
    pub description: String,
    pub requires_text: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_tokens: Option<u32>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Event {
    pub id: String,
    #[serde(flatten)]
    pub kind: EventKind,
}

impl Event {
    pub fn new(id: String, kind: EventKind) -> Self {
        Self { id, kind }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "event", rename_all = "snake_case")]
pub enum EventKind {
    Health {
        version: String,
        protocol: u32,
    },
    Actions {
        actions: Vec<ActionInfo>,
    },
    Config {
        config: Config,
    },
    Started {
        action: String,
        model: String,
    },
    Delta {
        text: String,
    },
    Done {
        text: String,
        usage: Usage,
        latency_ms: u64,
    },
    Cancelled,
    Error {
        code: ErrorCode,
        message: String,
        retryable: bool,
    },
}

pub fn parse_request(line: &str) -> Result<Request, (String, EngineError)> {
    let value: serde_json::Value = serde_json::from_str(line).map_err(|_| {
        (
            String::new(),
            EngineError::BadRequest("malformed JSON line".into()),
        )
    })?;

    let id = value
        .get("id")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_string();
    if id.is_empty() {
        return Err((
            String::new(),
            EngineError::BadRequest("missing or empty request id".into()),
        ));
    }

    let req_type = value
        .get("type")
        .and_then(|v| v.as_str())
        .map(str::to_owned);
    if req_type.is_none() {
        return Err((id, EngineError::BadRequest("missing request type".into())));
    }

    serde_json::from_value::<Request>(value).map_err(|_| {
        let msg = match req_type.as_deref() {
            Some(
                "health" | "actions.list" | "actions.reload" | "config.get" | "config.set"
                | "actions.run" | "cancel" | "shutdown",
            ) => "invalid request fields".into(),
            Some(t) => format!("unknown request type: {t}"),
            None => "missing request type".into(),
        };
        (id, EngineError::BadRequest(msg))
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trips_all_protocol_requests() {
        let cases = [
            r#"{"id":"r1","type":"health"}"#,
            r#"{"id":"r2","type":"actions.list"}"#,
            r#"{"id":"r2b","type":"actions.reload"}"#,
            r#"{"id":"r3","type":"config.get"}"#,
            r#"{"id":"r4","type":"config.set","patch":{"model":"gpt-5-mini"}}"#,
            r#"{"id":"r5","type":"actions.run","action":"proofread","text":"their going"}"#,
            r#"{"id":"r6","type":"actions.run","action":"rewrite","text":"hi","options":{"tone":"professional"}}"#,
            r#"{"id":"r7","type":"actions.run","action":"translate","text":"hi","options":{"target_lang":"German"}}"#,
            r#"{"id":"r8","type":"actions.run","action":"compose","text":"","options":{"brief":"Decline","context":"Email"}}"#,
            r#"{"id":"r9","type":"cancel","target":"r5"}"#,
            r#"{"id":"r10","type":"shutdown"}"#,
        ];
        for line in cases {
            let req = parse_request(line).unwrap();
            let round: Request =
                serde_json::from_str(&serde_json::to_string(&req).unwrap()).unwrap();
            assert_eq!(req, round);
        }
    }

    #[test]
    fn serializes_events_and_rejects_bad_lines() {
        let health = serde_json::to_value(Event::new(
            "a".into(),
            EventKind::Health {
                version: ENGINE_VERSION.into(),
                protocol: PROTOCOL_VERSION,
            },
        ))
        .unwrap();
        assert_eq!(health["event"], "health");
        assert_eq!(health["protocol"], 1);

        let err = serde_json::to_value(EngineError::MissingKey.to_event("r5".into())).unwrap();
        assert_eq!(err["code"], "missing_key");
        assert_eq!(err["retryable"], false);

        assert_eq!(
            parse_request("{bad").unwrap_err().1.code(),
            ErrorCode::BadRequest
        );
        let (id, e) = parse_request(r#"{"id":"x1","type":"nope"}"#).unwrap_err();
        assert_eq!((id.as_str(), e.code()), ("x1", ErrorCode::BadRequest));
    }

    #[test]
    fn parses_action_options_and_ignores_unknown_fields() {
        let req = parse_request(
            r#"{"id":"o1","type":"actions.run","action":"custom","text":"hi","options":{"include_context":true,"context_before":"before","context_after":"after","target_lang":"French","instruction":"tighten","unknown_num":42,"unknown_obj":{"a":1}}}"#,
        )
        .unwrap();
        let RequestKind::ActionsRun { options, .. } = req.kind else {
            panic!("expected ActionsRun");
        };
        assert!(options.include_context);
        assert_eq!(options.context_before.as_deref(), Some("before"));
        assert_eq!(options.context_after.as_deref(), Some("after"));
        assert_eq!(options.target_lang.as_deref(), Some("French"));
        assert_eq!(options.instruction.as_deref(), Some("tighten"));

        let default_req =
            parse_request(r#"{"id":"o2","type":"actions.run","action":"proofread","text":"hi"}"#)
                .unwrap();
        let RequestKind::ActionsRun { options, .. } = default_req.kind else {
            panic!("expected ActionsRun");
        };
        assert!(!options.include_context);
        assert_eq!(options.context_before, None);
        assert_eq!(options.context_after, None);
        assert_eq!(options.target_lang, None);
        assert_eq!(options.instruction, None);
        assert_eq!(options.system_prefix, None);
        assert_eq!(options.system_override, None);
        assert_eq!(options.user_template_override, None);
        assert_eq!(options.glossary, None);
    }

    #[test]
    fn parses_extension_options_and_reload_request() {
        let req = parse_request(
            r#"{"id":"g1","type":"actions.run","action":"translate","text":"hi","options":{"system_prefix":"Prefer plain English.","system_override":"You are terse.","user_template_override":"{{text}}","glossary":[{"source":"Emendly","do_not_translate":true},{"source":"engine","target":"motor","note":"product term"},{"source":"shell","target":"casca"}]}}"#,
        )
        .unwrap();
        let RequestKind::ActionsRun { options, .. } = req.kind else {
            panic!("expected ActionsRun");
        };
        assert_eq!(
            options.system_prefix.as_deref(),
            Some("Prefer plain English.")
        );
        assert_eq!(options.system_override.as_deref(), Some("You are terse."));
        assert_eq!(options.user_template_override.as_deref(), Some("{{text}}"));
        let glossary = options.glossary.unwrap();
        assert_eq!(
            glossary,
            vec![
                GlossaryEntry {
                    source: "Emendly".into(),
                    target: None,
                    do_not_translate: true,
                    note: None,
                },
                GlossaryEntry {
                    source: "engine".into(),
                    target: Some("motor".into()),
                    do_not_translate: false,
                    note: Some("product term".into()),
                },
                GlossaryEntry {
                    source: "shell".into(),
                    target: Some("casca".into()),
                    do_not_translate: false,
                    note: None,
                },
            ]
        );

        let reload = parse_request(r#"{"id":"g2","type":"actions.reload"}"#).unwrap();
        assert_eq!(reload.kind, RequestKind::ActionsReload);

        let empty_entry: GlossaryEntry = serde_json::from_str("{}").unwrap();
        assert_eq!(empty_entry, GlossaryEntry::default());
    }

    #[test]
    fn extension_options_are_skipped_when_absent() {
        let req =
            parse_request(r#"{"id":"m1","type":"actions.run","action":"proofread","text":"hi"}"#)
                .unwrap();
        let value = serde_json::to_value(&req).unwrap();
        let options = &value["options"];
        for key in [
            "system_prefix",
            "system_override",
            "user_template_override",
            "glossary",
        ] {
            assert!(options.get(key).is_none(), "{key} must be absent");
        }
        assert_eq!(
            req,
            Request {
                id: "m1".into(),
                kind: RequestKind::ActionsRun {
                    action: "proofread".into(),
                    text: "hi".into(),
                    options: ActionOptions::default(),
                    context: None,
                    app: None,
                    stream: None,
                },
            }
        );
    }
}
