# Task 0018: Engine Extension Primitives (Glossary, System Prefix, Prompt Overrides, Extra Actions)

Status: ready
Depends on: task 0016

## Environment & Scope

Rust toolchain is available in WSL. Run `cargo test`, `cargo clippy -- -D warnings`, and `cargo fmt --check` before finishing.
Do NOT run any `git` commands; the parent reviews and commits.
Work ONLY inside:
- `engine/`
- `docs/protocol.md`

Do NOT touch `shell/`, `site/`, `packaging/`, `.github/`, or `README.md`.

## Goal

Add clean, backwards-compatible protocol v1 primitives to `emendly-engine` so both open-source power users and the closed `Emendly Pro` shell can inject terminology glossaries, custom tone prefixes, saved-prompt overrides, and extra user action directories — while keeping every hard rule in `CLAUDE.md` intact (on-demand only, zero text in logs, no API keys on disk, streaming preserved, no `unwrap()` outside tests, no new crates if avoidable).

## 1. Protocol v1 Optional Fields (`engine/src/protocol.rs`)

Keep `PROTOCOL_VERSION = 1` (strictly additive and backwards-compatible).

Add `GlossaryEntry` struct:
```rust
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct GlossaryEntry {
    pub source: String,
    pub target: Option<String>,
    pub do_not_translate: bool,
    pub note: Option<String>,
}
```

Extend `ActionOptions` with `#[serde(default)]` optional fields:
- `pub system_prefix: Option<String>` — optional instructions prepended to the action's system prompt (used for custom tone presets or caller-supplied instructions).
- `pub system_override: Option<String>` — optional full replacement for the action's system prompt (used for Saved Prompts running through `custom` or any action).
- `pub user_template_override: Option<String>` — optional replacement for `user_template` (supports `{{text}}`, `{{tone}}`, `{{target_lang}}`, `{{brief}}`, `{{instruction}}`, `{{context}}`).
- `pub glossary: Option<Vec<GlossaryEntry>>` — optional list of terminology rules.

Add a new optional request variant to `RequestKind`:
- `#[serde(rename = "actions.reload")] ActionsReload` — reloads the `ActionRegistry` from disk (built-in actions dir + `EMENDLY_EXTRA_ACTIONS_DIR`) and emits `{"id":"...","event":"actions","actions":[...]}` (same event shape as `actions.list`).

## 2. Prompt Assembly & Extra Actions (`engine/src/actions.rs`)

### Extra actions directory
- Support `EMENDLY_EXTRA_ACTIONS_DIR` environment variable (`pub const ENV_EXTRA_ACTIONS_DIR: &str = "EMENDLY_EXTRA_ACTIONS_DIR";`).
- When set and pointing to an existing directory, load all `*.toml` files in it after loading the built-in actions directory.
- Extra actions with new names are added to the registry; built-in action names (`proofread`, `rewrite`, `compose`, `translate`, `humanize`, `prompt_optimize`, `synonyms`, `antonyms`, `custom`) must NOT be silently broken if an extra dir has malformed files (return a clean typed `EngineError::Internal` if a file fails to parse, or validate with the same TOML schema). Allow extra files to add new action IDs cleanly.
- Expose `ActionRegistry::load_from_dirs(primary: &Path, extra: Option<&Path>) -> Result<Self, EngineError>` for clean unit testing without mutating env vars.

### Glossary & System Prompt Assembly
- In `render_prompt`:
  1. Base system prompt: if `non_empty(options.system_override.as_deref())` is present, use it; otherwise use `action.system_prompt`.
  2. If `options.glossary` has non-empty entries (skip entries where `source.trim().is_empty()`), format a clean deterministic block:
     ```
     Terminology rules (follow strictly):
     - "<source>" -> keep exactly as "<source>" (do not translate)
     - "<source>" -> "<target>" (note: <note>)
     ```
     (omit `(note: ...)` when `note` is empty/None).
  3. Combine in order: `[system_prefix (if non-empty), glossary_block (if non-empty), base_system_prompt]` joined by `\n\n`.
  4. User template: if `non_empty(options.user_template_override.as_deref())` is present, render that template with the same `vars` map; otherwise render `action.user_template`.
  5. For `custom` action: if `system_override` or `user_template_override` is provided, do NOT require `options.instruction` to be non-empty (since the saved prompt already supplies the instruction!). If neither override is set, preserve the existing rule requiring non-empty `options.instruction`.
  6. Privacy rule: NEVER log `system_prefix`, `system_override`, `user_template_override`, or `glossary` contents anywhere!

## 3. Main Loop Support (`engine/src/main.rs`)

- Handle `RequestKind::ActionsReload`: rebuild `ActionRegistry::load_default()`, swap `state.registry = Arc::new(new_registry)` on success, emit the updated `actions` event; on error emit `err.to_event(id)` and keep the previous registry intact.

## 4. Docs & Tests

- Update `docs/protocol.md` with concise documentation for:
  - `actions.reload` request
  - optional `options` keys: `system_prefix`, `system_override`, `user_template_override`, `glossary`
  - `EMENDLY_EXTRA_ACTIONS_DIR`
- Add unit tests in `engine/src/actions.rs` and `engine/src/protocol.rs` covering:
  - `system_prefix` + `glossary` (`do_not_translate` and mapped target with/without note) assembly order
  - `system_override` + `user_template_override` on `custom` action without `instruction`
  - `ActionRegistry::load_from_dirs` loading an extra `.toml` action alongside built-ins
  - Backwards compatibility: existing minimal JSON requests still parse identically
- Add an integration test in `engine/tests/stream.rs` verifying `actions.reload` and an `actions.run` with `glossary` + `system_prefix` sends the rendered terminology rules to the mock OpenAI server and streams back cleanly.

## Acceptance

1. `cargo test --manifest-path engine/Cargo.toml` — all unit & integration tests pass.
2. `cargo clippy --manifest-path engine/Cargo.toml -- -D warnings` — zero warnings.
3. `cargo fmt --manifest-path engine/Cargo.toml -- --check` — clean formatting.
