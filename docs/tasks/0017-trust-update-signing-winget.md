# Task 0017: Trust, Auto-Update, Code Signing Pipeline & Winget Manifest

Status: ready
Depends on: task 0016

## Environment & Scope

No .NET SDK in WSL. Do NOT run `dotnet` or `git` commands; the parent builds, tests, and commits on Windows.
Work ONLY inside:
- `shell/windows/Emendly.App/`
- `packaging/winget/` (new directory)
- `docs/signing.md` (new file)
- `.github/workflows/release.yml`

Do NOT touch `engine/`, `site/`, `shell/macos/`, `README.md`, `docs/protocol.md`, or `.github/workflows/ci.yml`.

## 1. In-app Auto-Update Checker (`shell/windows/Emendly.App/`)

Zero external NuGet packages — use .NET 9 BCL (`System.Net.Http.HttpClient`, `System.Text.Json`, `System.Diagnostics.Process`).

### `UpdateChecker.cs` (new)
- Queries GitHub Releases API: `https://api.github.com/repos/pwwang/emendly/releases/latest` (overrideable via `EMENDLY_UPDATE_URL` env var for offline/localhost testing!).
- Sends `User-Agent: Emendly/<AppVersion>` and `Accept: application/vnd.github+json` ONLY — no machine ID, no telemetry, no user text.
- Parses `tag_name` (strip leading `v`), `html_url`, `name`, `body` (release notes summary), and finds the Windows installer asset (`*-win-x64-setup.exe`) or fallback zip (`*-win-x64.zip`) `browser_download_url`.
- Compares semantic versions (`TryParseSemVer`, comparing `Major.Minor.Patch`, ignoring pre-release suffixes cleanly).
- Returns a typed `UpdateCheckResult`:
  - `Status`: `UpToDate`, `UpdateAvailable`, `Error`
  - `CurrentVersion`, `LatestVersion`, `ReleaseUrl`, `DownloadUrl`, `ReleaseNotes`, `ErrorMessage`
- Rate-limiting for background startup checks: persist `last_update_check_utc` in `AppSettings` (default `null`) and only run automatic startup checks when `check_updates_on_startup` is `true` (default `true`) AND at least 24 hours have passed since the last check. Manual checks always run immediately.
- Add `DownloadAndLaunchInstallerAsync(string downloadUrl, string latestVersion, CancellationToken ct)` that downloads `Emendly-v<latest>-win-x64-setup.exe` to `%LOCALAPPDATA%\Emendly\updates\`, verifies the downloaded file is non-empty, and opens it (or opens `ReleaseUrl` in the browser if no direct `.exe` asset is present). Never auto-executes silently without user confirmation.

### `AppSettings.cs`
- Add `[JsonPropertyName("check_updates_on_startup")] public bool CheckUpdatesOnStartup { get; set; } = true;`
- Add `[JsonPropertyName("last_update_check_utc")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? LastUpdateCheckUtc { get; set; }`
- Parse both cleanly in `ApplyFile` and persist on `Save()`.

### `UpdateDialog.xaml` + `UpdateDialog.xaml.cs` (new, dark chrome matching `SettingsWindow`)
- Clean dark dialog (`#141419` / `#1E1E26` / `#2563EB`, dark title bar via `DwmSetWindowAttribute` like `SettingsWindow`) shown when an update is available (or when manually checking):
  - Shows current version -> latest version
  - Scrollable release notes preview
  - Buttons: `Download & Install` (primary), `Open Release Page` (secondary), `Later` (close)
  - Progress state while downloading, with clear error message fallback.

### Tray & Settings integration
- `App.xaml.cs`:
  - Add tray menu item `Check for updates…` between `Undo last insert` and the separator before `Quit`.
  - On normal startup (not `--settings`), fire a quiet background update check (after a 4-second delay) if `CheckUpdatesOnStartup` is true and >24h since `LastUpdateCheckUtc`. If an update is available, show a tray balloon ("Emendly <ver> is available — click to update") whose balloon click opens `UpdateDialog`. Never show an error balloon on silent startup failure.
  - On manual `Check for updates…`: show `UpdateDialog` if available, or a brief `NoticeWindow`/MessageBox stating `Emendly <ver> is up to date.` (or the error if offline).
- `SettingsWindow.xaml` & `SettingsWindow.xaml.cs`:
  - Add a compact row below the Model box (styled in dark chrome): CheckBox `Check for updates on startup` bound to `_settings.CheckUpdatesOnStartup`, and a `Check now` secondary button on the right that runs a manual update check and displays inline status (`Up to date`, `vX.Y.Z available`, or `Could not check`) next to it / opens `UpdateDialog` on available update.

## 2. Clean Extension Seam for Emendly Pro (`shell/windows/Emendly.App/Extensions/`)

Keep the public MIT codebase 100% free of proprietary code or licence checks while making it trivial for the closed `Emendly.Pro` shell to attach cleanly:
- `shell/windows/Emendly.App/Extensions/IEmendlyExtension.cs`:
  - `string Name { get; }`
  - `void ConfigureTrayMenu(System.Windows.Forms.ContextMenuStrip menu)` (called before Quit separator so extensions can add items like "Emendly Pro Hub…")
  - `TransformRequest? TransformRequestFilter(TransformRequest request)` (optional hook before sending to engine, e.g. injecting glossary / tone / saved prompt overrides)
  - `void OnTransformCompleted(TransformRequest request, TransformResult result, string? appProcess)` (optional hook after a successful run, e.g. recording local history)
- `shell/windows/Emendly.App/Extensions/ExtensionHost.cs`:
  - Static thread-safe registry (`Register(IEmendlyExtension)`, `Extensions`, helper methods `ApplyRequestFilters`, `NotifyCompleted`, `ApplyTrayMenu`) that does nothing when empty.
- Wire `ExtensionHost.ApplyTrayMenu(menu)` in `App.xaml.cs` and `ExtensionHost.ApplyRequestFilters` / `ExtensionHost.NotifyCompleted` around `_transformer.TransformAsync` in `PanelWindow.xaml.cs`.
- Zero behaviour change when no extension is registered.

## 3. Windows Code Signing Pipeline (`.github/workflows/release.yml` + `docs/signing.md`)

### `.github/workflows/release.yml`
Update the `package` job cleanly without breaking unsigned releases:
- Add a conditional step **before creating the zip & installer** to sign `stage/Emendly/Emendly.App.exe` and `stage/Emendly/engine/emendly-engine.exe`, and a conditional step **after ISCC builds the setup exe** to sign `dist/Emendly-$tag-win-x64-setup.exe`.
- Support TWO signing methods automatically via GitHub Secrets:
  1. **Azure Artifact Signing** (preferred): active when `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, `AZURE_SIGNING_ENDPOINT`, `AZURE_SIGNING_ACCOUNT`, and `AZURE_SIGNING_PROFILE` are all non-empty. Uses `azure/trusted-signing-action@v0` (or official `SignTool` + `Azure.CodeSigning.Dlib` — use `azure/artifact-signing-action@v1` or `azure/trusted-signing-action@v0.5.7`? Actually prefer the clean PowerShell `signtool.exe` + `Microsoft.Trusted.Signing.Client` NuGet tool or `azure/trusted-signing-action@v0` — wait: check `release.yml` requirement "Every action pinned to a major version tag". Using `signtool` via `dotnet tool` / PowerShell or `azure/login@v2` + `azure/trusted-signing-action@v0` — actually using PowerShell with `signtool.exe` from the preinstalled Windows SDK on `windows-latest` + NuGet `Microsoft.Trusted.Signing.Client` for Azure Artifact Signing, AND `signtool.exe sign /f ... /p ... /fd SHA256 /tr http://timestamp.digicert.com /td SHA256` for PFX has ZERO fragile third-party action tags!).
  2. **Traditional PFX Certificate**: active when `WINDOWS_SIGN_PFX_B64` and `WINDOWS_SIGN_PFX_PASSWORD` are set. Decodes base64 to a temp `.pfx`, signs with `signtool.exe` + RFC 3161 SHA-256 timestamp (`http://timestamp.digicert.com`), deletes the temp `.pfx` in a `finally` block.
  3. **Fallback when no signing secrets are configured**: logs `::notice::No Windows code-signing secrets configured; shipping unsigned binaries.` and continues without failing!
- Add a step that generates `dist/SHA256SUMS.txt` covering the zip and setup `.exe`, and include `dist/SHA256SUMS.txt` in `softprops/action-gh-release@v2` `files:`.
- Keep all YAML valid (no tabs, valid syntax).

### `docs/signing.md` (new)
Concise, practical operator guide covering:
- Option A: Azure Artifact Signing (individual developer path + 3-year business eligibility note, required secrets table, ~$9.99/mo).
- Option B: Traditional EV/OV Authenticode certificate (PFX base64 encoding command, secrets table, SmartScreen reputation note).
- Verification command (`signtool verify /pa /v Emendly.App.exe`).

## 4. Winget Package Manifest (`packaging/winget/`)

Create WinGet 1.6 multi-file manifests ready to submit to `microsoft/winget-pkgs`:
- `packaging/winget/manifests/p/pwwang/Emendly/0.1.0/pwwang.Emendly.yaml` (version manifest, `ManifestVersion: 1.6.0`)
- `packaging/winget/manifests/p/pwwang/Emendly/0.1.0/pwwang.Emendly.installer.yaml` (installer manifest: `InstallerType: inno`, `Scope: user`, `Architecture: x64`, `InstallerUrl: https://github.com/pwwang/emendly/releases/download/v0.1.0/Emendly-v0.1.0-win-x64-setup.exe`, placeholder `InstallerSha256` clearly marked + silent switches `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`)
- `packaging/winget/manifests/p/pwwang/Emendly/0.1.0/pwwang.Emendly.locale.en-US.yaml` (default locale manifest: publisher, description, tags, MIT license, `https://emendly.dev`, `https://emendly.dev/privacy.html`, `https://github.com/pwwang/emendly`)
- `packaging/winget/update-manifest.py` — stdlib-only Python script: given `--version 0.2.0 --installer path/to/setup.exe` (or `--sha256 <hex>`), generates a complete new version directory under `packaging/winget/manifests/p/pwwang/Emendly/<version>/` with exact SHA256 and URL!
- `packaging/winget/README.md` — exact commands to validate (`winget validate --manifest ...`) and submit PR to `microsoft/winget-pkgs` (via `wingetcreate`).

## Acceptance (run by parent)

1. `dotnet build shell/windows/Emendly.sln -c Release` succeeds with 0 warnings and 0 errors.
2. `UpdateChecker` unit logic works against a local HTTP test server (mock GitHub releases JSON).
3. `packaging/winget/update-manifest.py --version 0.1.1 --sha256 ... --dry-run` works and produces valid YAML.
4. `.github/workflows/release.yml` parses as valid YAML with zero tabs.
