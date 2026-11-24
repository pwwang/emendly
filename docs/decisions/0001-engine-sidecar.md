# 0001: Engine as a separate process

Date: 2026-11-23
Status: accepted

## Context

The product has to run on Windows, then macOS, then Linux. Each platform needs its own capture layer, because no portable API exposes the focused text field. The actions, prompts, provider calls and streaming are identical everywhere.

## Decision

Put actions, prompts, providers and streaming in one Rust binary. Talk to it with newline-delimited JSON over stdin and stdout. Each platform shell stays native and small.

## Consequences

- Prompt and provider work is written once, and tested without a UI.
- The shell can be rewritten or replaced without touching the engine.
- Two languages in the repository, so the build has two entry points.
- The contract needs versioning from day one. See `docs/protocol.md`.
- A shell crash cannot corrupt provider state, and an engine crash cannot freeze the UI.

## Alternatives rejected

- **One Electron app for all platforms.** Fastest first release, weakest capture layer on Windows and a heavy install.
- **One Tauri app for all platforms.** Good fit for macOS, but the Windows capture layer still needs C#, so the savings are small.
- **Native code duplicated per platform.** Three copies of the prompt and provider logic, three sets of bugs.
