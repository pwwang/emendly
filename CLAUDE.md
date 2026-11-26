# Emendly — project context for coding agents

Read this before touching code. It is the operating manual for this repository.

## What this is

A system-wide writing assistant. A native shell captures text from the focused field, hands it to a local engine process, and writes the result back. The engine is a Rust binary that speaks newline-delimited JSON over stdin and stdout.

The full contract lives in `docs/protocol.md`. Read it before changing anything that crosses the boundary.

## Hard rules

1. **On-demand only.** No timer, no polling, no watcher, no keystroke listener inside the engine. Work starts when a request arrives.
2. **No text in logs.** Log the action name, the app name, latency and token counts. Never log prompt text, user text or model output.
3. **Keys never touch disk in this repo.** Read the provider key from the `EMENDLY_API_KEY` environment variable or the OS keychain. A config file may hold the base URL and model name, never the key.
4. **Never write the user's clipboard from the engine.** Clipboard handling belongs to the shell, which must save and restore it.
5. **The engine owns no UI.** No window, no tray icon, no notification. It writes JSON and exits when stdin closes.
6. **Streaming is required.** Emit `delta` events as tokens arrive. Do not buffer the full answer unless the caller asked for it.
7. **Every request carries an id.** Responses and errors echo that id, so the shell can match them.

## Layout

```
engine/            Rust crate, binary name emendly-engine
engine/src/main.rs          stdin loop, request dispatch
engine/src/protocol.rs      request and event types, serde
engine/src/actions.rs       action registry, prompt assembly
engine/src/providers/       OpenAI-compatible client, streaming
engine/src/config.rs        config load, env var handling
engine/actions/*.toml       one file per action, prompt templates
shell/windows/              C# WinUI app (not started)
docs/                       protocol, architecture, coverage, decisions
```

## Commands

```sh
cargo test --manifest-path engine/Cargo.toml
cargo run  --manifest-path engine/Cargo.toml -- --once
cargo clippy --manifest-path engine/Cargo.toml -- -D warnings
cargo fmt  --manifest-path engine/Cargo.toml
```

All three must pass before a commit is called done.

## Style

- Rust 2021, no `unwrap()` outside tests, no `panic!` on user input.
- Errors are typed with `thiserror`, and map to the error codes in `docs/protocol.md`.
- Dependencies stay small. Justify any crate that pulls in a large tree.
- Prompts live in TOML, not in Rust source.
- Keep functions short. If a function needs a comment to explain its flow, split it.

## Scope for v1

Supported actions: `proofread`, `rewrite`, `compose`, `translate`, `synonyms`.

In scope for v1: the Windows shell (panel, tray, settings, hub), saved prompts, history, glossary, tone presets, per-app capture rules, chains and batch runs. There is no paid tier and no licensing — every feature ships in the MIT build.

Out of scope for v1: team features, macOS and Linux shells.

## Tests

- Unit tests for prompt assembly and config parsing.
- One integration test that runs the binary with a mock HTTP server on localhost and checks the streamed events for a rewrite.
- No test may reach the public internet.
