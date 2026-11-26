use std::path::Path;
use std::{env, fs};

use serde::{Deserialize, Serialize};

use crate::protocol::EngineError;

pub const ENV_CONFIG_PATH: &str = "EMENDLY_CONFIG";
pub const ENV_API_KEY: &str = "EMENDLY_API_KEY";

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Config {
    pub provider: ProviderConfig,
    pub language: LanguageConfig,
    pub logging: LoggingConfig,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ProviderConfig {
    pub base_url: String,
    pub model: String,
    pub timeout_ms: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct LanguageConfig {
    pub ui: String,
    pub default_target: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct LoggingConfig {
    pub level: String,
    pub path: String,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct ConfigPatch {
    pub provider: Option<ProviderPatch>,
    pub language: Option<LanguagePatch>,
    pub logging: Option<LoggingPatch>,
    pub base_url: Option<String>,
    pub model: Option<String>,
    pub timeout_ms: Option<u64>,
    pub ui: Option<String>,
    pub default_target: Option<String>,
    pub level: Option<String>,
    pub path: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct ProviderPatch {
    pub base_url: Option<String>,
    pub model: Option<String>,
    pub timeout_ms: Option<u64>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct LanguagePatch {
    pub ui: Option<String>,
    pub default_target: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct LoggingPatch {
    pub level: Option<String>,
    pub path: Option<String>,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            provider: ProviderConfig {
                base_url: "https://api.openai.com/v1".into(),
                model: "gpt-5-mini".into(),
                timeout_ms: 120_000,
            },
            language: LanguageConfig {
                ui: "en".into(),
                default_target: "German".into(),
            },
            logging: LoggingConfig {
                level: "warn".into(),
                path: String::new(),
            },
        }
    }
}

impl Config {
    pub fn load_from_env() -> Result<Self, EngineError> {
        let mut config = Self::default();
        if let Ok(path) = env::var(ENV_CONFIG_PATH) {
            let trimmed = path.trim();
            if !trimmed.is_empty() {
                config.apply_patch(&Self::read_patch_file(Path::new(trimmed))?)?;
            }
        }
        Ok(config)
    }

    pub fn load_from_file(path: &Path) -> Result<Self, EngineError> {
        let mut config = Self::default();
        config.apply_patch(&Self::read_patch_file(path)?)?;
        Ok(config)
    }

    fn read_patch_file(path: &Path) -> Result<ConfigPatch, EngineError> {
        let raw = fs::read_to_string(path)
            .map_err(|e| EngineError::Internal(format!("failed to read config file: {e}")))?;
        let is_json = path
            .extension()
            .and_then(|e| e.to_str())
            .is_some_and(|e| e.eq_ignore_ascii_case("json"));

        if is_json || raw.trim_start().starts_with('{') {
            serde_json::from_str(&raw)
                .map_err(|e| EngineError::Internal(format!("invalid JSON config: {e}")))
        } else {
            toml::from_str(&raw)
                .map_err(|e| EngineError::Internal(format!("invalid TOML config: {e}")))
        }
    }

    pub fn apply_patch(&mut self, patch: &ConfigPatch) -> Result<(), EngineError> {
        let mut next = self.clone();
        let p = patch.provider.as_ref();
        let l = patch.language.as_ref();
        let g = patch.logging.as_ref();

        if let Some(url) = patch
            .base_url
            .as_deref()
            .or(p.and_then(|x| x.base_url.as_deref()))
        {
            let url = url.trim();
            if url.is_empty() {
                return Err(EngineError::BadRequest(
                    "provider.base_url must not be empty".into(),
                ));
            }
            next.provider.base_url = url.into();
        }
        if let Some(model) = patch
            .model
            .as_deref()
            .or(p.and_then(|x| x.model.as_deref()))
        {
            let model = model.trim();
            if model.is_empty() {
                return Err(EngineError::BadRequest(
                    "provider.model must not be empty".into(),
                ));
            }
            next.provider.model = model.into();
        }
        if let Some(ms) = patch.timeout_ms.or(p.and_then(|x| x.timeout_ms)) {
            if ms == 0 {
                return Err(EngineError::BadRequest(
                    "provider.timeout_ms must be > 0".into(),
                ));
            }
            next.provider.timeout_ms = ms;
        }
        if let Some(ui) = patch.ui.as_ref().or(l.and_then(|x| x.ui.as_ref())) {
            next.language.ui = ui.clone();
        }
        if let Some(t) = patch
            .default_target
            .as_ref()
            .or(l.and_then(|x| x.default_target.as_ref()))
        {
            next.language.default_target = t.clone();
        }
        if let Some(lvl) = patch.level.as_ref().or(g.and_then(|x| x.level.as_ref())) {
            next.logging.level = lvl.clone();
        }
        if let Some(path) = patch.path.as_ref().or(g.and_then(|x| x.path.as_ref())) {
            next.logging.path = path.clone();
        }

        *self = next;
        Ok(())
    }
}

pub fn read_api_key() -> Result<String, EngineError> {
    read_api_key_from(|name| env::var(name).ok())
}

pub fn read_api_key_from<F: FnOnce(&str) -> Option<String>>(
    lookup: F,
) -> Result<String, EngineError> {
    match lookup(ENV_API_KEY) {
        Some(v) if !v.trim().is_empty() => Ok(v.trim().to_string()),
        _ => Err(EngineError::MissingKey),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn temp_path(ext: &str) -> std::path::PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        env::temp_dir().join(format!("emendly-cfg-{}-{nanos}.{ext}", std::process::id()))
    }

    #[test]
    fn loads_defaults_toml_and_json() {
        let def = Config::default();
        assert_eq!(def.provider.model, "gpt-5-mini");
        assert_eq!(def.provider.timeout_ms, 120_000);
        assert_eq!(def.language.default_target, "German");

        let toml_path = temp_path("toml");
        fs::write(&toml_path, "[provider]\nbase_url = \"http://127.0.0.1:8080/v1\"\n[language]\ndefault_target = \"French\"\n").unwrap();
        let cfg = Config::load_from_file(&toml_path).unwrap();
        let _ = fs::remove_file(&toml_path);
        assert_eq!(cfg.provider.base_url, "http://127.0.0.1:8080/v1");
        assert_eq!(cfg.language.default_target, "French");

        let json_path = temp_path("json");
        fs::write(
            &json_path,
            r#"{"provider":{"timeout_ms":5000},"logging":{"level":"info"}}"#,
        )
        .unwrap();
        let cfg = Config::load_from_file(&json_path).unwrap();
        let _ = fs::remove_file(&json_path);
        assert_eq!(cfg.provider.timeout_ms, 5000);
        assert_eq!(cfg.logging.level, "info");
    }

    #[test]
    fn applies_patches_and_rejects_invalid_values() {
        let mut cfg = Config::default();
        let flat: ConfigPatch = serde_json::from_str(r#"{"model":"gpt-4.1-mini"}"#).unwrap();
        cfg.apply_patch(&flat).unwrap();
        assert_eq!(cfg.provider.model, "gpt-4.1-mini");

        let bad: ConfigPatch =
            serde_json::from_str(r#"{"provider":{"model":"x","timeout_ms":0}}"#).unwrap();
        assert!(matches!(
            cfg.apply_patch(&bad),
            Err(EngineError::BadRequest(_))
        ));
        assert_eq!(cfg.provider.model, "gpt-4.1-mini");
    }

    #[test]
    fn api_key_reads_only_from_env() {
        assert!(matches!(
            read_api_key_from(|_| None),
            Err(EngineError::MissingKey)
        ));
        assert!(matches!(
            read_api_key_from(|_| Some("  ".into())),
            Err(EngineError::MissingKey)
        ));
        let key = read_api_key_from(|k| {
            assert_eq!(k, ENV_API_KEY);
            Some("secret".into())
        })
        .unwrap();
        assert_eq!(key, "secret");
    }
}
