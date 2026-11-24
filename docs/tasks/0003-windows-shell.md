# Task 0003: the Windows shell

Status: ready
Owner: coding agent
Depends on: task 0002 (probe), engine binary in `engine/`

## Environment facts

- Repository is in WSL. **No .NET SDK inside WSL.** Do not run `dotnet build`, `dotnet test` or `dotnet run`. Write code only; the parent agent builds and runs it on Windows.
- Target `net9.0-windows`, `<UseWPF>true</UseWPF>`, `<UseWindowsForms>true</UseWindowsForms>` (tray icon only).
- **No NuGet packages.** No Windows App SDK. No third-party tray or hotkey libraries.
- The WPF decision is recorded in `docs/decisions/0002-wpf-shell.md`.

## Scope

Two new projects, plus a refactor of the existing probe:

1. `shell/windows/Emendly.Capture/` — class library. Move the UI Automation logic out of `Emendly.Probe` into it, unchanged in behaviour. The probe project keeps its CLI and references the library.
2. `shell/windows/Emendly.App/` — WPF tray application.

`Emendly.Probe` must keep working exactly as it does now after the move.

## The shell, in behaviour

1. Lives in the tray with a context menu: Settings, Undo last insert, Quit.
2. Registers one global hotkey, default `Ctrl+Alt+E`, via `RegisterHotKey`, with a tray balloon if registration fails.
3. On hotkey: identify the foreground window, probe it through `Emendly.Capture`.
   - `selection`: open the panel in selection mode with the selected text.
   - `caret`: open the panel in compose mode, with an empty prompt box and the surrounding field text as optional context.
   - `read_only` or `unsupported`: show a small notice that names the app and the reason, and do nothing else.
4. Panel: small, always on top, positioned near the caret rectangle when the app reports one, otherwise near the mouse. Actions: Proofread, Rewrite, Compose, Translate, Synonyms. Number keys 1 to 5, arrow keys and Enter, Esc closes.
5. On action: send a request to the engine, stream the result into a preview box, Enter applies, Esc discards.
6. Apply: replace the selection, or insert at the caret in compose mode. **Save the clipboard, set it, send Ctrl+V, restore the clipboard, always in a `finally`.** Keep the replaced text in a one-slot undo buffer, and expose it in the tray menu.
7. While a request runs, a second hotkey press cancels it.

## Engine wiring

- `ITransformer` with two implementations: `EngineTransformer` (spawns the sidecar, NDJSON on stdin and stdout, emits deltas) and `EchoTransformer` (returns an uppercased marker, for development).
- Engine path: `EMENDLY_ENGINE` environment variable, else `../../engine/target/debug/emendly-engine` relative to the app directory, else fall back to `EchoTransformer` and say so in the tray tooltip.
- Protocol is `docs/protocol.md`. One request per line, one event per line.
- Never write the API key to disk. Read `EMENDLY_API_KEY` at call time.

## Files

- `Emendly.Capture/UiaProbe.cs`, `WindowEnumerator.cs`, `ProbeReport.cs` (moved)
- `Emendly.Capture/ClipboardInserter.cs` (save, set, Ctrl+V, restore, undo buffer)
- `Emendly.Capture/CaretLocator.cs` (bounding rectangle from the text range, mouse fallback)
- `Emendly.App/App.xaml`, `App.xaml.cs` (tray, hotkey, lifetime)
- `Emendly.App/HotkeyWindow.cs` (a message-only window that receives `WM_HOTKEY`)
- `Emendly.App/PanelWindow.xaml`, `PanelWindow.xaml.cs`
- `Emendly.App/EngineTransformer.cs`, `EchoTransformer.cs`, `ITransformer.cs`
- `Emendly.App/appsettings.json` (base URL, model, hotkey; no key)
- Update `Emendly.sln` with all three projects

## Rules

1. **Never paste without restoring the clipboard.** A `try/finally` around every paste, and restore the previous clipboard contents including when the clipboard was empty.
2. **Never log user text.** Log action name, app process name, latency and byte counts only.
3. **No blocking UI thread.** The engine call runs on a background task; the panel updates through the dispatcher.
4. **One instance.** A named mutex; a second launch focuses the running one and exits.
5. **The panel never steals focus permanently.** It activates to take keys, and returns activation to the previous window before pasting.

## Acceptance, run by the parent agent

1. `dotnet build` on the solution: zero warnings.
2. Both binaries start; the tray icon appears and Quit exits cleanly.
3. `Emendly.Probe --list` still works after the refactor.
4. A manual pass: hotkey in Notepad opens the panel in compose mode; hotkey with text selected opens selection mode.

## When done

List the files created, state which parts you could not verify without a desktop session, and name the two riskiest assumptions in the paste path.
