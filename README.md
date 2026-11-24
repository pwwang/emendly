# Emendly

A system-wide writing assistant that waits for you to ask.

Select text in any app, press one hotkey, and Emendly proofreads, rewrites, translates or composes in place. Nothing is read until you press the key. Your text goes to the model provider you configured, not to us.

Status: pre-alpha. The engine is being built before the shell.

## What it will do

- Proofread, rewrite with a chosen tone, compose from a brief, translate, synonyms and antonyms.
- Branch on the focused field: a selection shows text actions, an empty caret shows compose.
- Bring your own key: any OpenAI-compatible endpoint, or a local model through Ollama or LM Studio.
- Windows first, macOS second, Linux after that.

## What it will not do

- Read text you did not select and did not ask about.
- Log your writing. Logs carry action names, app names and latency, nothing else.
- Ship a managed model key in the free build.

## Layout

| Path | What lives there |
|---|---|
| `engine/` | Rust sidecar. Owns actions, prompt templates, providers, streaming, token accounting. |
| `shell/` | Native UI per platform. Windows in C# and WinUI, macOS in Swift. |
| `docs/` | Protocol, architecture, app coverage and decision records. |

## Build the engine

```sh
cargo test --manifest-path engine/Cargo.toml
cargo run --manifest-path engine/Cargo.toml -- --once
```

## Licence

MIT. See `LICENSE`.
