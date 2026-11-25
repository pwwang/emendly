use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::{env, fs};

use serde::Deserialize;

use crate::config::Config;
use crate::protocol::{ActionInfo, ActionOptions, EngineError};

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
        options: &ActionOptions,
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

        if action_name == "custom" && non_empty(options.instruction.as_deref()).is_none() {
            return Err(EngineError::BadRequest(
                "action 'custom' requires non-empty instruction".into(),
            ));
        }

        let vars = build_template_vars(text, options, top_context, config);
        let body = render_template(&action.user_template, &vars);
        let user = assemble_user_message(body, options);
        Ok(RenderedPrompt {
            system: action.system_prompt.clone(),
            user,
            max_tokens: action.max_tokens,
        })
    }
}

fn non_empty(opt: Option<&str>) -> Option<&str> {
    opt.map(str::trim).filter(|s| !s.is_empty())
}

fn format_context_blocks(options: &ActionOptions) -> Option<String> {
    if !options.include_context {
        return None;
    }

    let mut blocks = Vec::new();
    if let Some(before) = non_empty(options.context_before.as_deref()) {
        blocks.push(format!("Context before:\n{before}"));
    }
    if let Some(after) = non_empty(options.context_after.as_deref()) {
        blocks.push(format!("Context after:\n{after}"));
    }

    if blocks.is_empty() {
        None
    } else {
        Some(blocks.join("\n\n"))
    }
}

fn assemble_user_message(body: String, options: &ActionOptions) -> String {
    match format_context_blocks(options) {
        Some(ctx) if body.is_empty() => ctx,
        Some(ctx) => format!("{ctx}\n\n{body}"),
        None => body,
    }
}

fn build_template_vars(
    text: &str,
    options: &ActionOptions,
    top_context: Option<&str>,
    config: &Config,
) -> BTreeMap<String, String> {
    let mut vars = BTreeMap::from([
        ("tone".into(), "clear and natural".into()),
        ("target_lang".into(), config.language.default_target.clone()),
        ("brief".into(), String::new()),
        ("context".into(), top_context.unwrap_or("").into()),
        ("instruction".into(), String::new()),
    ]);

    if let Some(v) = non_empty(options.tone.as_deref()) {
        vars.insert("tone".into(), v.into());
    }
    if let Some(v) = non_empty(options.target_lang.as_deref()) {
        vars.insert("target_lang".into(), v.into());
    }
    if let Some(v) = non_empty(options.brief.as_deref()) {
        vars.insert("brief".into(), v.into());
    }
    if let Some(v) = non_empty(options.context.as_deref()) {
        vars.insert("context".into(), v.into());
    }
    if let Some(v) = non_empty(options.instruction.as_deref()) {
        vars.insert("instruction".into(), v.into());
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
            // engine/actions (shipped layout) and engine/../actions (portable layout)
            candidates.push(dir.join("actions"));
            if let Some(parent) = dir.parent() {
                candidates.push(parent.join("actions"));
                if let Some(grandparent) = parent.parent() {
                    candidates.push(grandparent.join("actions"));
                }
            }
        }
    }
    candidates.push(PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("actions"));

    candidates
        .into_iter()
        .find(|p| p.is_dir())
        .ok_or_else(|| {
            EngineError::Internal(
                "could not locate actions directory: expected an actions/ folder beside the                  emendly-engine binary (set EMENDLY_ACTIONS_DIR to override)"
                    .into(),
            )
        })
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
    fn loads_all_nine_actions_from_disk() {
        let reg = repo_registry();
        let cfg = Config::default();
        let names: Vec<_> = reg.list().into_iter().map(|a| a.name).collect();
        assert_eq!(
            names,
            [
                "antonyms",
                "compose",
                "custom",
                "humanize",
                "prompt_optimize",
                "proofread",
                "rewrite",
                "synonyms",
                "translate",
            ]
        );

        let opts = ActionOptions {
            instruction: Some("Do the thing".into()),
            ..Default::default()
        };
        for name in names {
            let prompt = reg.render(&name, "sample text", &opts, None, &cfg).unwrap();
            assert!(!prompt.system.trim().is_empty(), "{name} system prompt");
            assert!(!prompt.user.trim().is_empty(), "{name} user prompt");
        }
    }

    #[test]
    fn renders_prompts_with_options() {
        let reg = repo_registry();
        let cfg = Config::default();

        let rw_opts = ActionOptions {
            tone: Some("professional".into()),
            ..Default::default()
        };
        let rw = reg
            .render("rewrite", "We need this.", &rw_opts, None, &cfg)
            .unwrap();
        assert!(rw.user.contains("Tone: professional") && rw.user.contains("We need this."));

        let tr_default = reg
            .render("translate", "Hello", &ActionOptions::default(), None, &cfg)
            .unwrap();
        assert!(tr_default.user.contains("Target language: German"));

        let tr_opts = ActionOptions {
            target_lang: Some("Japanese".into()),
            ..Default::default()
        };
        let tr = reg
            .render("translate", "Hello", &tr_opts, None, &cfg)
            .unwrap();
        assert!(tr.user.contains("Target language: Japanese") && tr.user.contains("Hello"));

        let cp_opts = ActionOptions {
            brief: Some("Decline politely".into()),
            context: Some("Vendor email".into()),
            ..Default::default()
        };
        let cp = reg.render("compose", "", &cp_opts, None, &cfg).unwrap();
        assert!(cp.user.contains("Brief: Decline politely") && cp.user.contains("Vendor email"));
    }

    #[test]
    fn custom_requires_non_empty_instruction() {
        let reg = repo_registry();
        let cfg = Config::default();

        assert_eq!(
            reg.render("custom", "hi", &ActionOptions::default(), None, &cfg)
                .unwrap_err()
                .code(),
            ErrorCode::BadRequest
        );
        let blank_opts = ActionOptions {
            instruction: Some("   \t\n ".into()),
            ..Default::default()
        };
        assert_eq!(
            reg.render("custom", "hi", &blank_opts, None, &cfg)
                .unwrap_err()
                .code(),
            ErrorCode::BadRequest
        );

        let opts = ActionOptions {
            instruction: Some("Summarize in one sentence".into()),
            ..Default::default()
        };
        let no_text = reg.render("custom", "", &opts, None, &cfg).unwrap();
        assert_eq!(no_text.user, "Instruction: Summarize in one sentence");

        let with_text = reg
            .render("custom", "Long paragraph here.", &opts, None, &cfg)
            .unwrap();
        assert!(with_text.user.contains("Summarize in one sentence"));
        assert!(with_text.user.contains("Long paragraph here."));
    }

    #[test]
    fn context_blocks_respect_include_context_flag() {
        let reg = repo_registry();
        let cfg = Config::default();

        let both = ActionOptions {
            include_context: true,
            context_before: Some("First sentence.".into()),
            context_after: Some("Last sentence.".into()),
            instruction: Some("Write a transition".into()),
            ..Default::default()
        };
        let rendered = reg.render("custom", "", &both, None, &cfg).unwrap();
        assert_eq!(
            rendered.user,
            "Context before:\nFirst sentence.\n\nContext after:\nLast sentence.\n\nInstruction: Write a transition"
        );

        let before_only = ActionOptions {
            include_context: true,
            context_before: Some("Only before.".into()),
            ..Default::default()
        };
        let rw_before = reg
            .render("rewrite", "Target.", &before_only, None, &cfg)
            .unwrap();
        assert!(rw_before.user.starts_with("Context before:\nOnly before.\n\n"));
        assert!(!rw_before.user.contains("Context after:"));

        let after_only = ActionOptions {
            include_context: true,
            context_after: Some("Only after.".into()),
            ..Default::default()
        };
        let rw_after = reg
            .render("rewrite", "Target.", &after_only, None, &cfg)
            .unwrap();
        assert!(rw_after.user.starts_with("Context after:\nOnly after.\n\n"));
        assert!(!rw_after.user.contains("Context before:"));

        let disabled = ActionOptions {
            include_context: false,
            context_before: Some("Secret before.".into()),
            context_after: Some("Secret after.".into()),
            ..Default::default()
        };
        let rw_disabled = reg
            .render("rewrite", "Target.", &disabled, None, &cfg)
            .unwrap();
        assert!(!rw_disabled.user.contains("Context before:"));
        assert!(!rw_disabled.user.contains("Context after:"));
        assert!(!rw_disabled.user.contains("Secret before."));
        assert!(!rw_disabled.user.contains("Secret after."));
        assert!(rw_disabled.user.contains("Target."));
    }

    #[test]
    fn enforces_required_text_and_unknown_action() {
        let reg = repo_registry();
        let cfg = Config::default();
        let empty = ActionOptions::default();

        for name in ["proofread", "prompt_optimize", "humanize", "antonyms"] {
            assert_eq!(
                reg.render(name, "  ", &empty, None, &cfg)
                    .unwrap_err()
                    .code(),
                ErrorCode::BadRequest
            );
        }
        assert_eq!(
            reg.render("summarize", "hi", &empty, None, &cfg)
                .unwrap_err()
                .code(),
            ErrorCode::UnknownAction
        );
    }
}
