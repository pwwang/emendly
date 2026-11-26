#!/usr/bin/env bash
# Build the Windows engine and place it where the app looks for it.
#
# Why this exists: `cargo build` inside WSL produces a LINUX ELF binary, but the
# WPF app launches `emendly-engine.exe`. A plain `cargo build` therefore rebuilds a
# file the app never runs and leaves a stale .exe behind — which is exactly how a
# two-day-old engine kept serving requests after an engine fix had landed.
#
# The app resolves, in order:
#   <app dir>/emendly-engine.exe, <app dir>/engine/…, ../engine/…,
#   ../../..//emendly/engine/target/release/emendly-engine.exe   <-- what we fill here
#   …/target/debug/emendly-engine.exe
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENGINE_DIR="$REPO_ROOT/engine"
TARGET=x86_64-pc-windows-gnu

# The Windows toolchain lives outside WSL: rustup-init + mingw were installed to
# %LOCALAPPDATA%\emendly-tools. Fall back to a Windows cargo already on PATH.
WIN_CARGO="${WIN_CARGO:-/mnt/c/Users/$USER/AppData/Local/emendly-tools/cargo/bin/cargo.exe}"
if [[ ! -x "$WIN_CARGO" ]]; then
    WIN_CARGO="$(ls /mnt/c/Users/*/.cargo/bin/cargo.exe 2>/dev/null | head -1 || true)"
fi
WIN_RUSTUP="$(dirname "${WIN_CARGO:-/x}")/rustup.exe"

MINGW_BIN="$(ls -d /mnt/c/Users/*/AppData/Local/emendly-tools/mingw/mingw64/bin 2>/dev/null | head -1 || true)"

if [[ -z "${WIN_CARGO:-}" || ! -x "$WIN_CARGO" ]]; then
    echo "error: no Windows cargo found. Install rustup on Windows, or set WIN_CARGO." >&2
    exit 1
fi

if ! "$WIN_RUSTUP" target list --installed 2>/dev/null | grep -qx "$TARGET"; then
    echo "error: Windows rustup is missing the $TARGET target. Run: rustup target add $TARGET" >&2
    exit 1
fi

if [[ -n "$MINGW_BIN" ]]; then
    # Fresh MSVC-style PATH entry; mingw must win over any WSL gcc.
    export PATH="$MINGW_BIN:$PATH"
fi

# Cargo cannot create its session lock over the 9p/UNC share used to reach this
# checkout from Windows, so incremental compilation has to be off.
export CARGO_INCREMENTAL=0

echo "building $TARGET (windows cargo: $WIN_CARGO)"
"$WIN_CARGO" build --release --target "$TARGET" --manifest-path "$ENGINE_DIR/Cargo.toml"

SRC="$ENGINE_DIR/target/$TARGET/release/emendly-engine.exe"
DST="$ENGINE_DIR/target/release/emendly-engine.exe"
[[ -f "$SRC" ]] || { echo "error: expected $SRC to exist" >&2; exit 1; }

install -m 0755 "$SRC" "$DST"
echo "installed: $DST"
"$DST" --version 2>/dev/null || true
printf '%s\n' '{"id":"health","type":"health"}' | EMENDLY_API_KEY=probe "$DST" --once \
    | sed 's/^/health: /'
