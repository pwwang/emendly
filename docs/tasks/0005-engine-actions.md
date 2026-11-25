# Task 0005: engine support for the new menu

Status: ready
Owner: coding agent
Depends on: task 0001 (engine)

## Environment

No .NET or Rust SDK in WSL. **Do not build or test.** Write code and TOML only; the parent compiles on Windows.

## Goal

Extend the engine to serve nine actions and the five option fields in `docs/spec-menu.md`.

## Deliverables

1. **Four new action files** in `engine/actions/`:
   - `prompt_optimize.toml` — improve the user's prompt so a model follows it reliably; keep intent; output only the improved prompt.
   - `humanize.toml` — rewrite so it reads as natural human writing; vary rhythm, drop stock phrasing, keep meaning.
   - `antonyms.toml` — up to five antonyms, one per line, no definitions.
   - `custom.toml` — template with an `{instruction}` placeholder; the user's free text is the instruction, the selection is optional input.
2. **`engine/src/actions.rs`**:
   - Register the new actions.
   - `custom` requires a non-empty `instruction`; return `bad_request` when it is missing or blank.
   - Build the user message from the action template, then append context blocks only when `include_context` is true and the corresponding field is non-empty.
   - Context block format, exactly: `Context before:\n<text>\n\nContext after:\n<text>` before the instruction line.
   - Reject any context field when `include_context` is false: strip it, do not send it.
3. **`engine/src/protocol.rs`**: parse the new option fields into a typed struct:
   `include_context: bool` (default false), `context_before`, `context_after`, `target_lang`, `instruction`.
   Unknown fields stay ignored.
4. **Tests** in `engine/src/actions.rs` and `engine/tests/`:
   - Custom without `instruction` returns `bad_request`.
   - Custom with `instruction` produces a user message that contains it.
   - `include_context: true` with both context fields produces both blocks, in order.
   - `include_context: false` with context fields produces a message with neither block.
   - `translate` with `target_lang` puts the language into the user message.
   - Each of the nine actions loads from disk.

## Constraints

- Protocol stays at 1. New fields are additive, and unknown options stay ignored.
- Prompts stay in TOML, not Rust.
- No new dependencies.
- Never log prompt text or user text.

## When done

List the files changed, the exact context block format you emit, and any action whose prompt you consider weak.
