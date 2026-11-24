# emendly-engine

Local Rust sidecar that speaks newline-delimited JSON over `stdin` and `stdout` according to [`docs/protocol.md`](../docs/protocol.md).

## Build and test

```sh
cargo test --manifest-path engine/Cargo.toml
cargo clippy --manifest-path engine/Cargo.toml -- -D warnings
cargo fmt --manifest-path engine/Cargo.toml -- --check
```

## Run by hand

Health check (single request):

```sh
printf '{"id":"a","type":"health"}\n' | ./engine/target/debug/emendly-engine --once
```

List available actions:

```sh
printf '{"id":"r2","type":"actions.list"}\n' | ./engine/target/debug/emendly-engine --once
```

Inspect effective config:

```sh
printf '{"id":"r3","type":"config.get"}\n' | ./engine/target/debug/emendly-engine --once
```

Run an action against an OpenAI-compatible endpoint (API key is read only from `EMENDLY_API_KEY`):

```sh
EMENDLY_API_KEY="sk-..." \
printf '{"id":"r5","type":"actions.run","action":"proofread","text":"their going too the store"}\n' \
  | ./engine/target/debug/emendly-engine --once
```

Use a custom config file (TOML or JSON, never containing the API key) via `EMENDLY_CONFIG`:

```sh
EMENDLY_CONFIG=/path/to/emendly.toml \
EMENDLY_API_KEY="sk-..." \
printf '{"id":"r6","type":"actions.run","action":"rewrite","text":"We need this by Friday.","options":{"tone":"professional"}}\n' \
  | ./engine/target/debug/emendly-engine --once
```

Send multiple requests in one session and exit cleanly:

```sh
printf '%s\n' \
  '{"id":"r1","type":"health"}' \
  '{"id":"r2","type":"actions.list"}' \
  '{"id":"r10","type":"shutdown"}' \
  | ./engine/target/debug/emendly-engine
```
