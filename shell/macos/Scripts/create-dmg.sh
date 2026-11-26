#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MACOS_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
DIST_DIR="$MACOS_DIR/dist"
APP_BUNDLE="$DIST_DIR/Emendly.app"

TAG="${1:-${EMENDLY_VERSION:-0.1.0}}"
DMG_PATH="$DIST_DIR/Emendly-${TAG}-macos.dmg"

if [[ ! -d "$APP_BUNDLE" ]]; then
    echo "==> Emendly.app not found; running bundle-app.sh first..."
    EMENDLY_VERSION="$TAG" "$SCRIPT_DIR/bundle-app.sh"
fi

STAGE_DIR="$(mktemp -d "${TMPDIR:-/tmp}/emendly-dmg-XXXXXX")"
trap 'rm -rf "$STAGE_DIR"' EXIT

cp -R "$APP_BUNDLE" "$STAGE_DIR/Emendly.app"
ln -s /Applications "$STAGE_DIR/Applications"

rm -f "$DMG_PATH"
echo "==> Creating DMG at $DMG_PATH..."
hdiutil create \
    -volname "Emendly" \
    -srcfolder "$STAGE_DIR" \
    -ov \
    -format UDZO \
    "$DMG_PATH"

if [[ -n "${APPLE_SIGN_IDENTITY:-}" ]]; then
    echo "==> Signing DMG with identity: $APPLE_SIGN_IDENTITY"
    codesign --force --timestamp --sign "$APPLE_SIGN_IDENTITY" "$DMG_PATH"
fi

if [[ -n "${APPLE_NOTARY_KEYCHAIN_PROFILE:-}" ]]; then
    echo "==> Submitting DMG for notarization (keychain profile: $APPLE_NOTARY_KEYCHAIN_PROFILE)..."
    xcrun notarytool submit "$DMG_PATH" \
        --keychain-profile "$APPLE_NOTARY_KEYCHAIN_PROFILE" \
        --wait
    echo "==> Stapling notarization ticket..."
    xcrun stapler staple "$DMG_PATH"
elif [[ -n "${APPLE_ID:-}" && -n "${APPLE_TEAM_ID:-}" && -n "${APPLE_APP_PASSWORD:-}" ]]; then
    echo "==> Submitting DMG for notarization (Apple ID credentials)..."
    xcrun notarytool submit "$DMG_PATH" \
        --apple-id "$APPLE_ID" \
        --team-id "$APPLE_TEAM_ID" \
        --password "$APPLE_APP_PASSWORD" \
        --wait
    echo "==> Stapling notarization ticket..."
    xcrun stapler staple "$DMG_PATH"
else
    echo "==> Notarization credentials not set; skipping xcrun notarytool submit and staple."
    echo "    Set APPLE_NOTARY_KEYCHAIN_PROFILE or APPLE_ID + APPLE_TEAM_ID + APPLE_APP_PASSWORD to notarize."
fi

echo "==> DMG ready: $DMG_PATH"
