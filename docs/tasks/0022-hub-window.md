# Task 0022: Port the Hub window into the app

Status: ready
Depends on: task 0021 (partially complete)

## What task 0021 got done

`shell/windows/Emendly.Core/` exists and builds (`AppProfilesStore`, `BatchRunner`,
`ChainRunner`, `EmendlyPaths`, `EngineRunner`, `GlossaryStore`, `HistoryStore`,
`SavedPromptStore`, `TonePresetStore`). Website and README are done.

## What is missing, and is your whole job

1. `Emendly.Core` is **not** in `Emendly.sln` and **not** referenced by
   `Emendly.App.csproj`. Add both.
2. There is no `HubWindow`. Create it.
3. `Extensions/ExtensionHost.cs` and `IEmendlyExtension.cs` still exist. Delete them.

## Environment

No .NET SDK in WSL. Do NOT run `dotnet`. Do NOT run `git`.

Source of truth, read only — do not modify anything under `emendly-pro`:
- `~/github/emendly-pro/shell/windows/Emendly.Pro.App/MainWindow.xaml` (1050 lines)
- `~/github/emendly-pro/shell/windows/Emendly.Pro.App/MainWindow.xaml.cs` (1211 lines)

## 1. Wire the project references

- `dotnet sln`-equivalent: add `Emendly.Core\Emendly.Core.csproj` to `Emendly.sln`
  with a new project GUID, in the same solution folder style as the other projects.
- `Emendly.App.csproj`: add `<ProjectReference Include="..\Emendly.Core\Emendly.Core.csproj" />`.

## 2. Create `Emendly.App/HubWindow.xaml` and `.xaml.cs`

Copy the Pro `MainWindow` and adapt:

- `x:Class="Emendly.App.HubWindow"`, `Title="Emendly Hub"`, `Icon="/Assets/emendly.ico"`.
- Keep the existing dark palette, the sidebar, and the dark title-bar `DwmSetWindowAttribute` call.
- **Delete every Licence element and all its code**: `NavLicence`, `SidebarLicenceBadge`,
  `SidebarLicenceTitle`, `SidebarLicenceSubtitle`, `ExpiredBanner`, the licence view,
  the activation box, and all `LicenceManager`/`TrialManager`/`EntitlementState` usage.
- Keep six nav items and six views: `NavPrompts`/`ViewPrompts`, `NavHistory`/`ViewHistory`,
  `NavGlossary`/`ViewGlossary`, `NavProfiles`/`ViewProfiles`, `NavChains`/`ViewChains`,
  `NavTones`/`ViewTones`.
- Namespace `Emendly.Core` for the stores; `Emendly.Core.EngineRunner` for the engine client.
- Sidebar status lines: keep `SidebarEngineStatus` and `SidebarApiKeyStatus`; they read from
  `EngineRunner` and `AppSettings.ReadApiKey()` exactly as the Pro version did.

Behaviour that must be real, not stubbed:

- **Saved Prompts** writes through `SavedPromptStore`; its list, editor and save/delete work.
- **Glossary** and **Tone Presets** write through their stores.
- **App Profiles** writes `app-rules.json` via `AppProfilesStore`, which `AppCaptureRules`
  already reads — so a rule added here applies on the next hotkey.
- **History** reads `HistoryStore`; the search box, action filter, pause toggle and clear work.

## 3. Open the hub

- `App.xaml.cs`: add a tray menu item `Hub…` immediately after `Settings`, and open one
  `HubWindow` instance (activate the existing one if open).
- `PanelWindow`: the gear already raises `SettingsRequested`. Leave it alone; do not add a
  second entry point in this task.

## 4. Replace the extension seam

- Delete `Extensions/ExtensionHost.cs` and `Extensions/IEmendlyExtension.cs`.
- In `App.xaml.cs`, drop the `ExtensionHost.ApplyTrayMenu(menu)` call.
- In `PanelWindow.xaml.cs`, replace `ExtensionHost.ApplyRequestFilters(state.Request)` with
  the request unchanged, and replace `ExtensionHost.NotifyCompleted(...)` with a direct
  `HistoryStore.Record(...)` call guarded by `!IsPaused`, matching the fields the Pro
  `HistoryEntry` used (action, app name, input text, output text, model, latency, tokens).
  Construct the store once with its default data directory.

## Acceptance

1. `dotnet build shell/windows/Emendly.sln -c Release` clean, zero errors.
2. `grep -riE "licence|license|trial|EMPRO1|ExtensionHost"` over `Emendly.App/` and
   `Emendly.Core/` finds nothing.
3. Six views in the XAML, no Licence view.
4. `HubWindow` reachable from the tray menu item.

## When done

State where `HubWindow` is opened from, which store backs each of the six views, and how the
history record is written from the panel.
