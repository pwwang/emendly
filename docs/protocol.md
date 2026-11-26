# Protocol: engine and shell

The engine is a long-lived child process. The shell starts it once and keeps it open. They exchange newline-delimited JSON: one object per line, no pretty printing, UTF-8.

## Why a separate process

- One engine serves every platform shell, so prompts, providers and streaming are written once.
- The shell holds the OS permissions; the engine holds no privileges.
- A crashed engine cannot take the UI down, and a shell update never touches provider logic.
- The contract is inspectable. A test can drive the engine with plain text on stdin.

## Requests

Every request is one line with an `id` and a `type`.

```json
{"id":"r1","type":"health"}
{"id":"r2","type":"actions.list"}
{"id":"r2b","type":"actions.reload"}
{"id":"r3","type":"config.get"}
{"id":"r4","type":"config.set","patch":{"model":"gpt-5-mini"}}
{"id":"r5","type":"actions.run","action":"proofread","text":"their going too the store"}
{"id":"r6","type":"actions.run","action":"rewrite","text":"We need this by Friday.","options":{"system_suffix":"Rewrite the text in a polished professional workplace register without slang or exclamation marks."}}
{"id":"r7","type":"actions.run","action":"translate","text":"Good morning","options":{"target_lang":"German"}}
{"id":"r8","type":"actions.run","action":"compose","text":"","options":{"brief":"Decline the meeting politely, offer Tuesday","context":"Email to a vendor"}}
{"id":"r9","type":"cancel","target":"r5"}
{"id":"r10","type":"shutdown"}
```

Rules:

- `id` is caller-chosen, unique per session, and echoed in every event for that request.
- `text` is the selected text, or empty for `compose`.
- `options` is free-form per action; unknown keys are ignored, not rejected.
- `context` is optional surrounding text the shell read from the same field. The engine never reads it from anywhere else.

### Extension options

`actions.run` accepts five optional keys inside `options`. All are additive; a request that omits them behaves exactly as before.

| Key | Type | Effect |
|---|---|---|
| `system_prefix` | string | Prepended to the action's system prompt, separated by a blank line. Use it for tone presets or caller-supplied instructions. |
| `system_suffix` | string | Appended after the action's system prompt, separated by a blank line. Use it to adjust the instruction without replacing it — this is how rewrite styles are applied. |
| `system_override` | string | Replaces the action's system prompt entirely. Blank strings are ignored. |
| `user_template_override` | string | Replaces the action's `user_template`. Supports the same placeholders: `{{text}}`, `{{tone}}`, `{{target_lang}}`, `{{brief}}`, `{{instruction}}`, `{{context}}`. |
| `glossary` | array | Terminology rules, rendered into the system prompt. |

Each glossary entry is an object; every field is optional:

```json
{"source":"Emendly","do_not_translate":true}
{"source":"engine","target":"Motor","note":"product term"}
```

The engine renders a deterministic block, skipping entries whose `source` is empty:

```
Terminology rules (follow strictly):
- "Emendly" -> keep exactly as "Emendly" (do not translate)
- "engine" -> "Motor" (note: product term)
```

Final system prompt order: `system_prefix`, glossary block, then the base system prompt (`system_override` if present, otherwise the action's own). Blocks are joined with a blank line. When both overrides are supplied to the `custom` action, `instruction` may be omitted, because the saved prompt already carries it.

The engine never logs the contents of `system_prefix`, `system_override`, `user_template_override` or `glossary`.

### Reloading actions

`actions.reload` re-reads the action directory from disk and replies with the same `actions` event as `actions.list`. If the reload fails, the engine emits an `error` event and keeps serving the previous registry.

Extra action files are read from the directory named by `EMENDLY_EXTRA_ACTIONS_DIR`, when that variable is set and points to an existing directory. Files are `*.toml` with the same schema as the built-in actions, and new names are added alongside the built-ins. A built-in id can never be replaced from the extra directory. A malformed file fails the reload with `internal`; the engine never silently drops a broken action.

## Events

```json
{"id":"r5","event":"started","action":"proofread","model":"gpt-5-mini"}
{"id":"r5","event":"delta","text":"their "}
{"id":"r5","event":"delta","text":"going to the store"}
{"id":"r5","event":"done","text":"they're going to the store","usage":{"input_tokens":18,"output_tokens":9},"latency_ms":740}
{"id":"r5","event":"error","code":"provider_auth","message":"the configured key was rejected","retryable":false}
```

Rules:

- `started` comes once, before the first `delta`.
- `delta` events are ordered and additive. The shell appends them.
- `done` carries the full replacement text, so a shell that missed a delta can still apply the result.
- `error` ends the request. No `done` follows it.
- `cancel` ends a request with `{"event":"cancelled"}`, and must abort the HTTP call.
- Unknown request types produce `error` with code `bad_request`, never a panic.

## Error codes

| Code | Meaning | Retryable |
|---|---|---|
| `bad_request` | Malformed line, unknown type, missing text | no |
| `unknown_action` | `action` is not in the registry | no |
| `missing_key` | No API key in the environment or keychain | no |
| `provider_auth` | Provider rejected the key | no |
| `provider_rate_limit` | Provider asked us to slow down | yes |
| `provider_error` | Provider returned 5xx or an invalid body | yes |
| `network` | DNS, TLS or connection failure | yes |
| `timeout` | Request exceeded the action's deadline | yes |
| `cancelled` | Caller cancelled | no |
| `internal` | Anything else, with no user text in the message | no |

## Config

`config.get` returns the effective config. `config.set` applies a patch and returns the new config.

```json
{"provider":{"base_url":"https://api.openai.com/v1","model":"gpt-5-mini","timeout_ms":20000},
 "language":{"ui":"en","default_target":"German"},
 "logging":{"level":"warn","path":""}}
```

The API key is never part of this object. It is read from `EMENDLY_API_KEY` at call time, or from the OS keychain through the shell.

## Versioning

`health` returns `{"event":"health","version":"0.1.0","protocol":1}`. The shell refuses to run against a protocol it does not know.
