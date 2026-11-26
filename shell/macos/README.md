# Emendly macOS Shell

Native macOS 13+ menu-bar shell for Emendly, written in Swift 5.9+ with zero external SwiftPM dependencies (pure `Foundation`, `AppKit`, `SwiftUI`, `ApplicationServices`, `Carbon`, and `Security`).

> **Direct distribution only (no Mac App Sandbox):** Emendly relies on cross-application Accessibility (`AXUIElement`) and `CGEvent` keyboard synthesis to read selections, inspect caret bounds, and replace text in any focused app. Per Apple DTS, the Mac App Sandbox forbids cross-app `AXUIElement` control, so Emendly is distributed outside the Mac App Store signed with Developer ID and notarized.

## Prerequisites

- macOS 13 (Ventura) or later
- Xcode 15+ (Swift 5.9+)
- Rust toolchain (`cargo`) to build `emendly-engine`

## Build & Run (Development)

1. Build the Rust engine:

```bash
cargo build --release --manifest-path ../../engine/Cargo.toml
```

2. Run tests for the capture module:

```bash
swift test
```

3. Run the menu-bar app directly:

```bash
export EMENDLY_API_KEY="sk-..."   # or save in macOS Keychain via Settings
swift run EmendlyApp
```

The app runs as a menu-bar accessory (`LSUIElement = true`, no Dock icon). Default global shortcut: **`Cmd+Option+E`**.

## Granting Accessibility Permission

On first activation, macOS prompts for Accessibility access. You can also enable it manually:

1. Open **System Settings -> Privacy & Security -> Accessibility**.
2. Toggle **Emendly** (or your terminal when running via `swift run`) on.
3. Click **Settings -> Refresh** inside Emendly to verify the status changes to **Granted**.

Without Accessibility permission, Emendly falls back to clipboard probing where possible, but caret positioning, surrounding context, and in-place AX replacement require Accessibility trust.

## Configuration & Storage

- Settings file: `~/Library/Application Support/Emendly/settings.json`
- Per-app capture rule overrides: `~/Library/Application Support/Emendly/app-rules.json`
- Metadata-only log (never contains user text or model output): `~/Library/Application Support/Emendly/emendly.log`
- API key: read from `EMENDLY_API_KEY` environment variable or macOS Keychain (`service: dev.emendly.api-key`) — never written to disk.

## Packaging, Signing & Notarization

Build the `.app` bundle (includes `EmendlyApp`, `emendly-engine`, `engine/actions/*.toml`, `Info.plist`, and `app-rules.json`):

```bash
chmod +x Scripts/bundle-app.sh Scripts/create-dmg.sh
./Scripts/bundle-app.sh
```

Create a signed and notarized DMG for Developer ID distribution:

```bash
export APPLE_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)"
export APPLE_NOTARY_KEYCHAIN_PROFILE="emendly-notary"
# Alternatively set APPLE_ID, APPLE_TEAM_ID, and APPLE_APP_PASSWORD

./Scripts/bundle-app.sh
./Scripts/create-dmg.sh 0.1.0
```

Outputs:
- `dist/Emendly.app`
- `dist/Emendly-<tag>-macos.dmg`
