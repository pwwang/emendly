#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MACOS_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$MACOS_DIR/../.." && pwd)"
DIST_DIR="$MACOS_DIR/dist"
APP_BUNDLE="$DIST_DIR/Emendly.app"

echo "==> Building EmendlyApp (release)..."
(
    cd "$MACOS_DIR"
    swift build -c release
)

BIN_DIR="$(cd "$MACOS_DIR" && swift build -c release --show-bin-path)"
APP_BIN="$BIN_DIR/EmendlyApp"
if [[ ! -x "$APP_BIN" ]]; then
    echo "error: expected built binary at $APP_BIN" >&2
    exit 1
fi

ENGINE_BIN="${EMENDLY_ENGINE_BIN:-$REPO_ROOT/engine/target/release/emendly-engine}"
if [[ ! -x "$ENGINE_BIN" ]]; then
    if command -v cargo >/dev/null 2>&1; then
        echo "==> Building emendly-engine (release)..."
        cargo build --release --manifest-path "$REPO_ROOT/engine/Cargo.toml"
        ENGINE_BIN="$REPO_ROOT/engine/target/release/emendly-engine"
    fi
fi

if [[ ! -x "$ENGINE_BIN" ]]; then
    echo "error: emendly-engine binary not found at $ENGINE_BIN" >&2
    echo "       Build engine/ first or set EMENDLY_ENGINE_BIN." >&2
    exit 1
fi

ACTIONS_DIR="$REPO_ROOT/engine/actions"
if [[ ! -d "$ACTIONS_DIR" ]]; then
    echo "error: engine actions directory not found at $ACTIONS_DIR" >&2
    exit 1
fi

echo "==> Assembling $APP_BUNDLE..."
rm -rf "$APP_BUNDLE"
mkdir -p \
    "$APP_BUNDLE/Contents/MacOS" \
    "$APP_BUNDLE/Contents/Resources/engine/actions"

cp "$APP_BIN" "$APP_BUNDLE/Contents/MacOS/EmendlyApp"
chmod +x "$APP_BUNDLE/Contents/MacOS/EmendlyApp"

cp "$MACOS_DIR/Resources/Info.plist" "$APP_BUNDLE/Contents/Info.plist"
cp "$MACOS_DIR/Resources/app-rules.json" "$APP_BUNDLE/Contents/Resources/app-rules.json"

if [[ -n "${EMENDLY_VERSION:-}" ]]; then
    CLEAN_VERSION="${EMENDLY_VERSION#v}"
    /usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $CLEAN_VERSION" "$APP_BUNDLE/Contents/Info.plist"
fi

cp "$ENGINE_BIN" "$APP_BUNDLE/Contents/Resources/engine/emendly-engine"
chmod +x "$APP_BUNDLE/Contents/Resources/engine/emendly-engine"
cp "$ACTIONS_DIR"/*.toml "$APP_BUNDLE/Contents/Resources/engine/actions/"

if [[ -n "${APPLE_SIGN_IDENTITY:-}" ]]; then
    echo "==> Signing emendly-engine and Emendly.app with identity: $APPLE_SIGN_IDENTITY"
    codesign --force --options runtime --timestamp \
        --sign "$APPLE_SIGN_IDENTITY" \
        "$APP_BUNDLE/Contents/Resources/engine/emendly-engine"
    codesign --force --options runtime --timestamp \
        --sign "$APPLE_SIGN_IDENTITY" \
        "$APP_BUNDLE"
    codesign --verify --deep --strict "$APP_BUNDLE"
else
    echo "==> APPLE_SIGN_IDENTITY not set; skipping Developer ID code signing."
fi

echo "==> Bundle ready: $APP_BUNDLE"
