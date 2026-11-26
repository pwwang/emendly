# Task 0020: macOS Native Menu-Bar Shell (`shell/macos/`)

Status: ready
Depends on: task 0016

## Environment & Scope

No Xcode or macOS SDK exists in WSL Linux. Do NOT attempt to run `swift build` or `git` commands; write clean, idiomatic, compile-ready Swift 5.9+ code targeting macOS 13+ with ZERO external SwiftPM dependencies (pure Apple system frameworks: `Foundation`, `AppKit`, `SwiftUI`, `ApplicationServices`, `Carbon`, `Security`). The parent will review, wire macOS CI into GitHub Actions, and commit.

Work ONLY inside:
- `shell/macos/` (new directory)

Do NOT touch `engine/`, `shell/windows/`, `site/`, `packaging/`, `docs/`, `.github/`, or `README.md`.

## Architecture

Match the Windows shell's exact user experience and `docs/protocol.md` contract while using native macOS APIs (never sandboxed — Developer ID direct distribution, since the Mac App Sandbox forbids cross-app `AXUIElement` calls).

### Directory Layout (`shell/macos/`)
```
shell/macos/
  Package.swift
  README.md
  Resources/
    Info.plist
    app-rules.json
  Scripts/
    bundle-app.sh
    create-dmg.sh
  Sources/
    EmendlyCapture/
      CaptureTypes.swift
      SelectionClassifier.swift
      AppCaptureRules.swift
      AccessibilityCapture.swift
      ClipboardCapture.swift
      CaptureChain.swift
      GlobalHotkey.swift
    EmendlyApp/
      VersionInfo.swift
      AppSettings.swift
      KeychainHelper.swift
      ProtocolModels.swift
      EngineClient.swift
      MenuModel.swift
      UpdateChecker.swift
      FloatingPanel.swift
      PanelView.swift
      SettingsView.swift
      AppDelegate.swift
      main.swift
  Tests/
    EmendlyCaptureTests/
      SelectionClassifierTests.swift
```

## Detailed Component Requirements

### 1. `Package.swift`
- `// swift-tools-version: 5.9`
- Platform: `.macOS(.v13)`
- Products: `.executable(name: "EmendlyApp", targets: ["EmendlyApp"])`
- Targets:
  - `.target(name: "EmendlyCapture", path: "Sources/EmendlyCapture")`
  - `.executableTarget(name: "EmendlyApp", dependencies: ["EmendlyCapture"], path: "Sources/EmendlyApp")`
  - `.testTarget(name: "EmendlyCaptureTests", dependencies: ["EmendlyCapture"], path: "Tests/EmendlyCaptureTests")`
- Link system frameworks cleanly (no external package deps).

### 2. `Sources/EmendlyCapture/`
- `CaptureTypes.swift`:
  - `SelectionState`: `empty`, `word`, `passage`
  - `SelectionClassification`: `state`, `text`, `isReadOnly`, `wordCount`
  - `TargetAppInfo`: `pid: pid_t`, `bundleIdentifier: String`, `localizedName: String`
  - `CaptureVerdict`: `selection`, `caret`, `readOnly`, `unsupported`
  - `SurroundingContext`: `before: String`, `after: String`, `fieldText: String`
  - `CaptureSnapshot`: `targetApp`, `verdict`, `strategyName`, `selectedText`, `surroundingContext`, `anchorRect: CGRect?`, `isReadOnly: Bool`
- `SelectionClassifier.swift`:
  - Mirrors Windows classifier: empty/whitespace -> `.empty`; single word (no internal whitespace, <= 64 chars) -> `.word`; otherwise -> `.passage`.
- `AppCaptureRules.swift`:
  - Loads bundled `app-rules.json` + user overrides at `~/Library/Application Support/Emendly/app-rules.json`.
  - Default `Resources/app-rules.json` maps known Electron/terminal/PDF apps by bundle ID (`com.microsoft.VSCode`: `clipboard`, `com.apple.Terminal`: `clipboard-nokeys`, `com.googlecode.iterm2`: `clipboard-nokeys`, `com.apple.Preview`: `clipboard-ro`, `dev.warp.Warp-Stable`: `clipboard-nokeys`).
  - Supports strategies: `ax`, `clipboard`, `clipboard-ro`, `clipboard-nokeys`.
- `AccessibilityCapture.swift`:
  - `isTrusted(prompt: Bool) -> Bool` using `AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: prompt] as CFDictionary)`.
  - Uses `AXUIElementCreateSystemWide()` -> `kAXFocusedUIElementAttribute`.
  - Reads `kAXSelectedTextAttribute`, `kAXSelectedTextRangeAttribute`, `kAXValueAttribute`, `kAXRoleAttribute`.
  - Checks writability via `AXUIElementIsAttributeSettable(element, kAXValueAttribute as CFString, &settable)` and `kAXSelectedTextAttribute` to detect read-only fields cleanly!
  - Extracts caret/selection screen bounds via `kAXBoundsForRangeParameterizedAttribute` (converting AX top-left coordinates to Cocoa bottom-left screen coordinates cleanly).
  - Extracts surrounding context (~400 chars before/after caret/selection) from `AXValue` when available.
  - Supports AX direct write-back (`AXUIElementSetAttributeValue` on `kAXSelectedTextAttribute`) when writable, falling back to `ClipboardCapture.paste`.
- `ClipboardCapture.swift`:
  - Saves ALL `NSPasteboard.general` items/types before probing (`Data` per `NSPasteboard.PasteboardType`) and restores them after!
  - Synthesizes `Cmd+C` via `CGEvent` (`CGKeyCode(8)` + `.maskCommand`), waits briefly (polling `changeCount` up to ~180ms), reads copied string, restores previous pasteboard.
  - `clipboard-nokeys` mode never sends `Cmd+C` (safe for terminals).
  - `pasteText(_:restoreDelayMs:)` puts result on pasteboard, activates target `NSRunningApplication`, synthesizes `Cmd+V` (`CGKeyCode(9)` + `.maskCommand`), restores original pasteboard, and saves undo state (previous selected text + target app) for "Undo last insert".
- `CaptureChain.swift`:
  - Orchestrates per-app rule -> AX probe -> Clipboard fallback (just like Windows `CaptureChain.cs`).
- `GlobalHotkey.swift`:
  - Registers system-wide hotkey via Carbon `InstallEventHandler` + `RegisterEventHotKey` (works without Accessibility input-monitoring overhead and survives app switching).
  - Parses human-readable shortcuts like `Cmd+Option+E`, `Ctrl+Alt+E`, `F6` into Carbon keyCode + modifier flags, with default `Cmd+Option+E`.
  - Clean unregister/re-register on Settings change.

### 3. `Sources/EmendlyApp/`
- `ProtocolModels.swift` + `EngineClient.swift`:
  - Exact implementation of `docs/protocol.md` NDJSON-over-stdio protocol (`health`, `actions.list`, `config.set`, `actions.run`, `cancel`, `shutdown`).
  - Resolves `emendly-engine` in order: `EMENDLY_ENGINE` env var -> `Bundle.main.resourcePath/engine/emendly-engine` -> `Bundle.main.executableURL/../Resources/engine/emendly-engine` -> repo-relative `engine/target/release/emendly-engine` and `debug`.
  - Passes `EMENDLY_API_KEY` from environment or macOS Keychain (`KeychainHelper.swift`, service `dev.emendly.api-key`) into child process environment — NEVER writes key to disk!
  - Captures `engineVersion` from `health` event and streams `delta` callbacks on `@MainActor`.
- `AppSettings.swift`:
  - JSON persistence at `~/Library/Application Support/Emendly/settings.json`: `base_url`, `model`, `hotkey` (default `Cmd+Option+E`), `language.default_target` (default `German`), `context_enabled`, `check_updates_on_startup`, `last_update_check_utc`.
- `MenuModel.swift`:
  - Exact 9 actions (`proofread`, `rewrite`, `humanize`, `prompt_optimize`, `translate`, `synonyms`, `antonyms`, `compose`, `custom`), selection-state defaults (Empty / Word / Passage), grouped expandable menu + filter, 24 languages + 2-letter codes matching Windows `MenuModel.cs`.
- `FloatingPanel.swift` + `PanelView.swift`:
  - Custom `NSPanel` subclass with `.nonactivatingPanel`, `.borderless`, `.floating` level, `canBecomeKey = true`, dark appearance (`#141419` / `#1E1E26` / `#2563EB`), stays open when focus moves unless explicitly closed (`Esc` or close button or second hotkey).
  - Anchored near `snapshot.anchorRect` (or mouse location fallback), clamped to visible screen bounds.
  - Stages: `ChooseAction` (number keys `1`–`9`, `↑`/`↓`, `Tab` expand + filter, inline context toggle / language picker), `PromptInput` (for Compose / Custom Prompt), `Streaming` (live read-only output + Cancel), `PreviewReady` (`Enter` Replace or Copy if read-only, `Copy`, `Back`), `Error` (`Retry`, `Back`).
  - Header shows `Emendly` + target app + strategy badge + gear (opens Settings beside panel) + close button.
- `SettingsView.swift`:
  - Dark SwiftUI settings window: Hotkey input, Default translation target language picker (editable/custom), Base URL, Model, API key status + Keychain save/clear button, Accessibility permission status + "Open Accessibility Settings" button (`x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility`), Check for updates toggle + button, version footer `Emendly 0.1.0 · engine <ver> · protocol 1`.
- `UpdateChecker.swift`:
  - Checks `https://api.github.com/repos/pwwang/emendly/releases/latest` for newer version (`User-Agent: Emendly/<ver>`), looks for `*-macos.dmg` asset, opens release/download URL.
- `AppDelegate.swift` + `main.swift`:
  - `NSApplication.shared.setActivationPolicy(.accessory)` (menu-bar only, no Dock icon).
  - `NSStatusItem` with SF Symbol (`wand.and.stars` or template E glyph) and menu: disabled `Emendly 0.1.0`, separator, `Settings…`, `Undo last insert`, `Check for updates…`, separator, `Quit`.
  - Warms engine on startup so version is ready immediately.
  - Prompts for Accessibility permission gracefully on first activation if untrusted.

### 4. Packaging Scripts & Docs (`shell/macos/Scripts/`, `Resources/Info.plist`, `README.md`)
- `Resources/Info.plist`: `LSUIElement = true`, `CFBundleIdentifier = dev.emendly.app`, `NSAppleEventsUsageDescription` / usage strings, version `0.1.0`.
- `Scripts/bundle-app.sh`:
  - Builds release `EmendlyApp` (`swift build -c release`), copies binary, `Info.plist`, `app-rules.json`, `emendly-engine`, and `engine/actions/*.toml` into `dist/Emendly.app/`.
  - If `APPLE_SIGN_IDENTITY` env var is set, signs `emendly-engine` and `Emendly.app` with `codesign --force --options runtime --timestamp`.
- `Scripts/create-dmg.sh`:
  - Builds `dist/Emendly-<tag>-macos.dmg` via `hdiutil create`, and if `APPLE_NOTARY_KEYCHAIN_PROFILE` (or `APPLE_ID` + `APPLE_TEAM_ID` + `APPLE_APP_PASSWORD`) is set, submits to `xcrun notarytool submit --wait` and staples with `xcrun stapler staple`; otherwise skips notarization with a clear notice.
- `shell/macos/README.md`:
  - Exact build, run, Accessibility permission, Developer ID signing, and notarization instructions (noting App Store sandbox incompatibility per Apple DTS).

### 5. Unit Tests (`Tests/EmendlyCaptureTests/SelectionClassifierTests.swift`)
- XCTest suite testing `SelectionClassifier` (`empty`, `word`, `passage`, read-only) and `AppCaptureRules` lookup/normalization.

## Correctness Checks (before finishing)
Carefully review every Swift file for:
- Missing imports (`AppKit`, `SwiftUI`, `Carbon`, `ApplicationServices`, `Security`, `Combine`)
- Strict Swift 5.9 concurrency / `@MainActor` / `Sendable` cleanliness
- Exact Carbon C-callback conventions (`@convention(c)` without capturing context, passing `Unmanaged.passUnretained(self).toOpaque()`)
- Exact `NSPasteboard` save/restore and `AXUIElement` memory/CFType casting (`as? String`, `AXValueGetValue`)
- Valid `Package.swift` and shell scripts (`chmod +x` notes / POSIX bash).
