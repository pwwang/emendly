use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::{env, fs};

use serde::Deserialize;

use crate::config::Config;
use crate::protocol::{ActionInfo, EngineError};

pub const ENV_ACTIONS_DIR: &str = "EMENDLY_ACTIONS_DIR";

#[derive(Debug, Clone, Deserialize)]
pub struct Action {
    pub name: String,
    pub description: String,
    pub system_prompt: String,
    pub user_template: String,
    pub requires_text: bool,
    #[serde(default)]
    pub max_tokens: Option<u32>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RenderedPrompt {
    pub system: String,
    pub user: String,
    pub max_tokens: Option<u32>,
}

#[derive(Debug, Clone, Default)]
pub struct ActionRegistry {
    actions: BTreeMap<String, Action>,
}

impl ActionRegistry {
    pub fn load_default() -> Result<Self, EngineError> {
        Self::load_from_dir(&resolve_actions_dir()?)
    }

    pub fn load_from_dir(dir: &Path) -> Result<Self, EngineError> {
        let entries = fs::read_dir(dir)
            .map_err(|e| EngineError::Internal(format!("failed to read actions dir: {e}")))?;

        let mut actions = BTreeMap::new();
        for entry in entries {
            let path = entry
                .map_err(|e| EngineError::Internal(format!("failed to read action entry: {e}")))?
                .path();
            if path.extension().and_then(|e| e.to_str()) != Some("toml") {
                continue;
            }
            let raw = fs::read_to_string(&path)
                .map_err(|e| EngineError::Internal(format!("failed to read action file: {e}")))?;
            let action: Action = toml::from_str(&raw)
                .map_err(|e| EngineError::Internal(format!("invalid action TOML: {e}")))?;
            actions.insert(action.name.clone(), action);
        }

        if actions.is_empty() {
            return Err(EngineError::Internal("no action definitions found".into()));
        }
        Ok(Self { actions })
    }

    pub fn list(&self) -> Vec<ActionInfo> {
        self.actions
            .values()
            .map(|a| ActionInfo {
                name: a.name.clone(),
                description: a.description.clone(),
                requires_text: a.requires_text,
                max_tokens: a.max_tokens,
            })
            .collect()
    }

    pub fn render(
        &self,
        action_name: &str,
        text: &str,
        options: &BTreeMap<String, serde_json::Value>,
        top_context: Option<&str>,
        config: &Config,
    ) -> Result<RenderedPrompt, EngineError> {
        let action = self
            .actions
            .get(action_name)
            .ok_or_else(|| EngineError::UnknownAction(action_name.into()))?;

        if action.requires_text && text.trim().is_empty() {
            return Err(EngineError::BadRequest(format!(
                "action '{action_name}' requires non-empty text"
            )));
        }

        let vars = build_template_vars(text, options, top_context, config);
        Ok(RenderedPrompt {
            system: action.system_prompt.clone(),
            user: render_template(&action.user_template, &vars),
            max_tokens: action.max_tokens,
        })
    }
}

fn build_template_vars(
    text: &str,
    options: &BTreeMap<String, serde_json::Value>,
    top_context: Option<&str>,
    config: &Config,
) -> BTreeMap<String, String> {
    let mut vars = BTreeMap::from([
        ("tone".into(), "clear and natural".into()),
        ("target_lang".into(), config.language.default_target.clone()),
        ("brief".into(), String::new()),
        ("context".into(), top_context.unwrap_or("").into()),
    ]);

    for (key, val) in options {
        let rendered = match val {
            serde_json::Value::String(s) => Some(s.clone()),
            serde_json::Value::Number(n) => Some(n.to_string()),
            serde_json::Value::Bool(b) => Some(b.to_string()),
            _ => None,
        };
        if let Some(s) = rendered.filter(|s| !s.is_empty()) {
            vars.insert(key.clone(), s);
        }
    }

    vars.insert("text".into(), text.into());
    vars
}

fn render_template(template: &str, vars: &BTreeMap<String, String>) -> String {
    let mut out = String::with_capacity(template.len());
    let mut rest = template;
    while let Some(start) = rest.find("{{") {
        out.push_str(&rest[..start]);
        let after = &rest[start + 2..];
        let Some(end) = after.find("}}") else {
            out.push_str(&rest[start..]);
            return out;
        };
        if let Some(val) = vars.get(after[..end].trim()) {
            out.push_str(val);
        }
        rest = &after[end + 2..];
    }
    out.push_str(rest);
    out.trim().to_string()
}

pub fn resolve_actions_dir() -> Result<PathBuf, EngineError> {
    if let Ok(path) = env::var(ENV_ACTIONS_DIR) {
        let p = PathBuf::from(path);
        if p.is_dir() {
            return Ok(p);
        }
    }

    let mut candidates = vec![PathBuf::from("actions"), PathBuf::from("engine/actions")];
    if let Ok(exe) = env::current_exe() {
        if let Some(dir) = exe.parent() {
            candidates.push(dir.join("actions"));
            if let Some(crate_dir) = dir.parent().and_then(Path::parent) {
                candidates.push(crate_dir.join("actions"));
            }
        }
    }
    candidates.push(PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("actions"));

    candidates
        .into_iter()
        .find(|p| p.is_dir())
        .ok_or_else(|| EngineError::Internal("could not locate actions directory".into()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::protocol::ErrorCode;

    fn repo_registry() -> ActionRegistry {
        ActionRegistry::load_from_dir(&PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("actions"))
            .unwrap()
    }

    #[test]
    fn loads_five_actions_and_renders_prompts() {
        let reg = repo_registry();
        let cfg = Config::default();
        let names: Vec<_> = reg.list().into_iter().map(|a| a.name).collect();
        assert_eq!(
            names,
            ["compose", "proofread", "rewrite", "synonyms", "translate"]
        );

        let mut opts = BTreeMap::from([
            ("tone".into(), serde_json::json!("professional")),
            ("ignored_key".into(), serde_json::json!(42)),
        ]);
        let rw = reg
            .render("rewrite", "We need this.", &opts, None, &cfg)
            .unwrap();
        assert!(rw.user.contains("Tone: professional") && rw.user.contains("We need this."));

        let tr = reg
            .render("translate", "Hello", &BTreeMap::new(), None, &cfg)
            .unwrap();
        assert!(tr.user.contains("Target language: German"));

        opts.insert("brief".into(), serde_json::json!("Decline politely"));
        opts.insert("context".into(), serde_json::json!("Vendor email"));
        let cp = reg.render("compose", "", &opts, None, &cfg).unwrap();
        assert!(cp.user.contains("Brief: Decline politely") && cp.user.contains("Vendor email"));
    }

    #[test]
    fn enforces_required_text_and_unknown_action() {
        let reg = repo_registry();
        let cfg = Config::default();
        assert_eq!(
            reg.render("proofread", "  ", &BTreeMap::new(), None, &cfg)
                .unwrap_err()
                .code(),
            ErrorCode::BadRequest
        );
        assert_eq!(
            reg.render("summarize", "hi", &BTreeMap::new(), None, &cfg)
                .unwrap_err()
                .code(),
            ErrorCode::UnknownAction
        );
    }
}
