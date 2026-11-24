# Task 0001: scaffold the engine

Status: ready
Owner: coding agent
Depends on: nothing

## Goal

Create a working Rust crate at `engine/` that implements the contract in `docs/protocol.md`, builds on Linux, and passes its tests without network access.

## Deliverables

1. `engine/Cargo.toml` with binary name `emendly-engine`, edition 2021, and a small dependency set. Suggested: `serde`, `serde_json`, `thiserror`, `toml`, `ureq` with TLS. Justify anything larger.
2. `engine/src/main.rs`: read stdin line by line, dispatch, write one JSON event per line to stdout, flush after each line. Exit cleanly on `shutdown` or end of input.
3. `engine/src/protocol.rs`: request and event types, with serialise and deserialise tests.
4. `engine/src/actions.rs`: registry that loads `engine/actions/*.toml`, each with `name`, `description`, `system_prompt`, `user_template`, `requires_text`, and optional `max_tokens`.
5. `engine/actions/`: five files, `proofread.toml`, `rewrite.toml`, `compose.toml`, `translate.toml`, `synonyms.toml`. Prompts are short, imperative, and ask for text only, with no preamble and no explanation.
6. `engine/src/providers/openai.rs`: a client for any OpenAI-compatible chat completions endpoint that streams server-sent events. Parse `data:` lines, emit `delta` events, accumulate the full text for `done`.
7. `engine/src/config.rs`: load defaults, merge `EMENDLY_CONFIG` path if set, read the key from `EMENDLY_API_KEY` only.
8. `engine/tests/stream.rs`: start a mock HTTP server on 127.0.0.1 with `std::net::TcpListener`, point the client at it, run a `rewrite` request, and assert the event sequence `started`, one or more `delta`, `done` with the concatenated text.
9. `engine/README.md`: how to run it by hand with `printf` and a pipe.

## Acceptance

- `cargo test --manifest-path engine/Cargo.toml` passes, with no test touching the internet.
- `cargo clippy --manifest-path engine/Cargo.toml -- -D warnings` is clean.
- `cargo fmt --manifest-path engine/Cargo.toml -- --check` is clean.
- `printf '{"id":"a","type":"health"}\n' | ./engine/target/debug/emendly-engine --once` prints one JSON line with `version` and `protocol`.
- A missing key produces `{"event":"error","code":"missing_key"}` and no panic.
- No source file contains a hard-coded API key. No log statement prints request text or model output.

## Constraints

- No async runtime. One thread reads stdin, a worker thread runs the request, events go out through a channel.
- No `unwrap()` outside tests.
- Keep the whole crate under 1500 lines.
- Do not create a shell, a UI, or any OS-specific code.

## When done

Report which files you created, the test output, and anything in `docs/protocol.md` that turned out to be underspecified.
