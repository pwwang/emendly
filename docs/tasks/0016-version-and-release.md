# Task 0016: version in the UI, release-ready packaging, GitHub Actions

Status: ready
Depends on: task 0015

## Environment

No .NET SDK in WSL. Do not build here. Write code and workflow files only; the parent builds and runs on Windows. Work only inside the repo root, `shell/windows/Emendly.App` and `.github/workflows`.

## 1. Show the version

- `Emendly.App.csproj`: add `<Version>0.1.0</Version>` and `<FileVersion>`/`<InformationalVersion>` derived from it.
- New `VersionInfo.cs` in the App project:
  - `AppVersion` from the executing assembly's informational version, trimmed at `+` (drop the git hash suffix if present).
  - `EngineVersion`: set from the engine's `health` reply.
  - `ProtocolVersion`: the existing `SupportedProtocolVersion` constant.
- `EngineTransformer` already receives `{"event":"health","version":"…","protocol":1}`. Capture `version` into a `public string? EngineVersion` property next to `StatusSummary`; default `null` until the handshake completes.
- `SettingsWindow`: add a small footer line above the buttons, `#6B7280`, 11 px: `Emendly 0.1.0 · engine 0.1.0 · protocol 1`. When the engine version is unknown show `engine —`. Do not let it wrap; widen the window to 440 if needed and keep the layout otherwise identical.
- Tray: add a disabled first item to the context menu reading `Emendly 0.1.0` (no click handler, `Enabled = false`). The existing tooltip stays as is.

## 2. Make a bundled build actually run

`AppSettings.ResolveEnginePath()` currently looks at `EMENDLY_ENGINE`, then a repo-relative `engine/target/debug`, then ancestors. A shipped folder has the engine beside the exe. Add, in this order:

1. `EMENDLY_ENGINE` (existing)
2. `<AppContext.BaseDirectory>\engine\emendly-engine.exe`
3. `<AppContext.BaseDirectory>\emendly-engine.exe`
4. the existing repo-relative and ancestor probes (debug, then release)

Keep `ResolveBinaryCandidate`'s rule that `.exe` wins on Windows, and add the same treatment for `target/release`.

## 3. GitHub Actions

No remote is configured yet; create the files anyway.

### `.github/workflows/ci.yml`

- Trigger: `push` to any branch, `pull_request`.
- Job `engine` — matrix `ubuntu-latest`, `windows-latest`, `macos-latest`: `cargo test --manifest-path engine/Cargo.toml --locked`, then `cargo build --release` and upload the binary as an artifact.
- Job `shell` — `windows-latest`: `dotnet build shell/windows/Emendly.sln -c Release` on .NET 9. Use `actions/setup-dotnet@v4` with `dotnet-version: 9.0.x`.
- Cache cargo with `actions/cache@v4` keyed on `engine/Cargo.lock`.

### `.github/workflows/release.yml`

- Trigger: `push` of tags matching `v*`.
- `permissions: contents: write`.
- Job `engine`: matrix `windows-latest` (`x86_64-pc-windows-gnu` target is not needed; build natively with the MSVC toolchain), `ubuntu-latest`, `macos-latest`; `cargo build --release`; rename to `emendly-engine.exe` / `emendly-engine`; upload artifacts.
- Job `shell`: `windows-latest`, `dotnet publish shell/windows/Emendly.App/Emendly.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:Version=${{ github.ref_name }}` with the leading `v` stripped for the version property.
- Job `package`: needs both; downloads the Windows engine and the published shell; assembles:

```
Emendly/
  Emendly.App.exe
  Assets/
  engine/emendly-engine.exe
  appsettings.json
  app-rules.json
  *.dll, runtime files
```

Zips it as `Emendly-<tag>-win-x64.zip`, and (optional but preferred) builds an Inno Setup installer `Emendly-<tag>-win-x64-setup.exe` installed with `choco install innosetup`, using a generated `.iss` that installs to `{localappdata}\Programs\Emendly`, creates a Start Menu shortcut and an uninstaller, and does **not** require admin.
- Create the GitHub Release with `softprops/action-gh-release@v2`, attaching the zip and the installer, with `generate_release_notes: true`.

### Correctness requirements

- Every action pinned to a major version tag (`@v4`, `@v2`).
- No secrets required: the app takes `EMENDLY_API_KEY` from the environment at runtime.
- YAML must parse; no tabs.
- The workflow must not assume a git remote.

## Acceptance, run by the parent

1. Builds with zero errors; the settings window shows the version line.
2. Tray menu has the disabled version item.
3. `dotnet publish -r win-x64 --self-contained` succeeds locally and the bundle launches with the engine beside the exe.
4. Both workflow files parse as YAML and their shell steps match commands that work locally.

## When done

State the version string shown, where the engine version comes from, the exact publish command used, and what the release assets are named.
