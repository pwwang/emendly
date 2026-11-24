# Task 0002: Windows capture adapter, console harness

Status: ready
Owner: coding agent
Depends on: .NET SDK 9 on the Windows host, not in WSL

## Goal

Build `shell/windows/Emendly.Probe`, a console app that reports, for any window handle, whether the focused text element offers a selection, an empty caret, or neither. No UI. No hotkey. This is the instrument the coverage table depends on.

## Environment facts

- The repository lives in WSL, but **this project builds and runs on Windows** with `dotnet`.
- There is **no .NET SDK inside WSL**. Do not run `dotnet build`, `dotnet test` or `dotnet run`. Write the code only; the parent agent builds it.
- Target framework: `net9.0-windows` with `<UseWPF>true</UseWPF>`, which brings in `System.Windows.Automation` (`UIAutomationClient`, `UIAutomationTypes`).
- Do not add NuGet packages. The base class library and the UIA assemblies are enough.

## Commands

```
Emendly.Probe.exe --list
Emendly.Probe.exe --probe --hwnd 0x000A0B2C
Emendly.Probe.exe --probe --hwnd 0x000A0B2C --json
Emendly.Probe.exe --probe --title "Untitled - Notepad"
```

- `--list` prints one line per top-level window that has a title: handle, pid, process name, class name, title.
- `--probe` resolves a window by `--hwnd` or by `--title` (case-insensitive substring, first match), then reports.
- `--json` prints a single JSON object instead of the human summary. JSON is the contract used by tests and by me.
- Exit code 0 on a successful probe, 2 when no window matches, 3 on an internal error.

## JSON shape

```json
{
  "hwnd": "0x000A0B2C",
  "process": "notepad",
  "title": "Untitled - Notepad",
  "window_class": "Notepad",
  "element": {
    "name": "Text editor",
    "control_type": "Document",
    "class_name": "RichEditD2DPT",
    "automation_id": "15",
    "is_keyboard_focusable": true
  },
  "patterns": ["TextPattern", "TextEditPattern", "ValuePattern"],
  "enabled": true,
  "read_only": false,
  "selection": { "length": 0, "preview": "" },
  "caret": { "supported": true, "offset": 0 },
  "field_text_length": 34,
  "verdict": "caret"
}
```

`verdict` is exactly one of:

| Value | Meaning |
|---|---|
| `selection` | A text element is present and the selection is non-empty |
| `caret` | A text element is present and the selection is empty |
| `read_only` | A text element is present but not editable |
| `unsupported` | No text element found in the window |

## Rules

1. **Find the element carefully.** Start from the window's focused element. If it has no `TextPattern`, search descendants breadth-first for control types `Document` or `Edit` that do expose `TextPattern`. Stop at the first match.
2. **Never print more than 80 characters of user text.** `preview` is truncated and stripped of newlines. Nothing is written to disk.
3. **Survive hostile apps.** Every UIA call can hang. Wrap the probe body in a worker thread with a 5 second deadline; on expiry print `{"verdict":"unsupported","error":"timeout"}` and exit 3.
4. **No exceptions escape.** Catch `ElementNotAvailableException`, `COMException` and `InvalidOperationException` per call, and degrade the field rather than the whole probe.
5. **`read_only`** comes from `ValuePattern.Current.IsReadOnly` when available, otherwise `TextPattern` plus the element's `IsEnabled`.
6. **No `Console.WriteLine` of field text** anywhere except the truncated `preview`.

## Deliverables

- `shell/windows/Emendly.Probe/Emendly.Probe.csproj`
- `shell/windows/Emendly.Probe/Program.cs` (argument parsing, dispatch, exit codes)
- `shell/windows/Emendly.Probe/WindowEnumerator.cs` (`--list`, title matching)
- `shell/windows/Emendly.Probe/UiaProbe.cs` (element resolution, pattern detection, verdict)
- `shell/windows/Emendly.Probe/ProbeReport.cs` (record type and JSON serialisation via `System.Text.Json`)
- `shell/windows/Emendly.Probe/README.md` (the four commands, the JSON example, the exit codes)
- `shell/windows/Emendly.sln` linking the project, optional but preferred

## Acceptance

The parent agent runs these and reports back:

1. `dotnet build` succeeds with no warnings.
2. `--list` prints at least five windows on a normal desktop.
3. `--probe --title "Notepad"` against a freshly opened Notepad returns `verdict` `caret` and lists `TextPattern`.
4. Probing a window with no text, such as the taskbar or Settings, returns `unsupported` and exits 0.
5. No file in the project writes user text to disk.

## When done

Report the files created, the element-search strategy you chose, and any app class you expect to fail.
