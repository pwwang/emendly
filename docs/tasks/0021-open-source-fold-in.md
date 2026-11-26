# Task 0021: Fold the Pro features into the open-source app, drop licensing

Status: ready
Depends on: task 0020

## Decision

The paid tier is cancelled. Every Pro feature ships in the MIT app. Revenue, when it comes, is donations. Nothing in the app may gate, nag, trial-expire or verify a licence again.

## Environment

No .NET SDK in WSL. Do NOT run `dotnet`. Do NOT run `git`. Work inside `shell/windows/` only.

Source of truth for the ported code:
- `~/github/emendly-pro/shell/windows/Emendly.Pro.Core/` — six stores, `ChainRunner`, `BatchRunner`, `ProEngineClient`, `ProPaths`
- `~/github/emendly-pro/shell/windows/Emendly.Pro.App/MainWindow.xaml` and `.xaml.cs` — the 7-view hub

Read those; do not modify anything under `emendly-pro`.

## 1. Move the core library into the public repo

Create `shell/windows/Emendly.Core/` from `Emendly.Pro.Core`, with these changes:

- Rename the assembly and namespace `Emendly.Pro.Core` -> `Emendly.Core`.
- Rename `ProPaths` -> `EmendlyPaths`. It must keep reading the **existing** locations so nothing a user already has is lost:
  - App profiles: `%LOCALAPPDATA%\Emendly\app-rules.json` (unchanged)
  - Everything else moves to `%APPDATA%\Emendly\`: `prompts.json`, `history.jsonl`, `glossary.json`, `tones.json`, `chains.json`, `settings.json`
  - On first load, if a file exists under the old `%APPDATA%\EmendlyPro\` path and the new one does not, copy it across once and leave the old file alone.
- Keep the constructor-injected data directory on every store so tests stay isolated.
- `ProEngineClient` becomes `EngineRunner`; keep its `IProEngineRunner` interface renamed to `IEngineRunner`.

Add the project to `Emendly.sln` and reference it from `Emendly.App`.

## 2. Port the hub into the app

The Pro Hub is a second window. Fold it in as a **Hub window** in the same app, opened from the tray menu as `Hub…` and from the panel's settings surface.

- Create `shell/windows/Emendly.App/HubWindow.xaml` and `.xaml.cs` from the Pro `MainWindow`, with:
  - Namespace `Emendly.App`, class `HubWindow`, title `Emendly Hub`.
  - The existing dark chrome, icon at `/Assets/emendly.ico`, and the dark title bar call.
  - **Delete the Licence view entirely** and every reference to it: no trial banner, no activation box, no expiry check, no `LicenceManager`, no `TrialManager`, no `Emendly.Keygen`.
  - Six views remain: Saved Prompts, History, Glossary, App Profiles, Chains & Batch, Tone Presets.
- Wire real behaviour instead of Pro's stubs where it is cheap:
  - Saved Prompts, Glossary and Tone Presets drive the same stores the panel uses.
  - App Profiles writes `app-rules.json`, which `AppCaptureRules` already reads — so a rule added in the hub takes effect on the next hotkey press.
  - History records from the panel: in `PanelWindow`, after a successful transform, call `HistoryStore.Record` unless paused.
- Saved-prompt hotkeys: register them through the existing `HotkeyWindow` on startup and re-register when the store changes. A prompt hotkey runs that prompt against the current selection exactly as the panel's Custom Prompt does.

## 3. Replace the extension seam with direct calls

`Extensions/IEmendlyExtension` and `ExtensionHost` existed to let a closed build attach. With nothing closed left, delete both and call the stores directly. Keep the code that applies filters and records history, just inline it.

## 4. Donations instead of a store

- `site/index.html`: remove the Free vs Pro pricing cards, the comparison table, and `PRO_CHECKOUT_URL`. Replace with one honest band:
  - Heading `Free and open source`
  - One sentence: MIT licensed, no account, no telemetry, bring your own key.
  - Buttons: `Sponsor on GitHub` (`https://github.com/sponsors/pwwang`) and `Buy me a coffee` (leave the URL as a single clearly-marked constant `DONATE_URL` in `site/script.js`, empty by default; the button hides when empty).
  - Keep the FAQ but rewrite the pricing entries to say there is no paid tier.
- `site/privacy.html`: drop the Pro licence and `%APPDATA%\EmendlyPro` sections; state that there is no licence check and no purchase path.
- `README.md`: replace the Free vs Pro section with a short `Support` section (sponsorship, no paid tier, everything MIT). Add the hub to the feature list.
- `packaging/winget/**` and `.github/**`: no changes needed; they never referenced Pro.

## 5. Delete the Pro repo's unique claims from docs

- `CLAUDE.md`: leave the hard rules. Change the scope line so it no longer says payments and settings windows are out of scope for v1.

## Acceptance

1. `dotnet build shell/windows/Emendly.sln -c Release` clean.
2. No string `Pro`, `licence`, `license`, `trial` or `EMPRO1` remains in `shell/windows/Emendly.App` or `Emendly.Core` except in unrelated words.
3. Hub opens from the tray and from the panel; six views, no Licence view.
4. Adding a capture rule in the hub changes behaviour on the next hotkey.
5. `site/index.html` has no price, no checkout constant, and a working sponsor link.

## When done

State how the stores' data paths moved, what replaced `ExtensionHost`, and the one place a saved-prompt hotkey is registered.
