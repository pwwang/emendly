# Emendly.Probe

Console harness for probing Windows UI Automation text capture capabilities (`TextPattern`, `ValuePattern`, selection, and caret state) against any window handle.

## Build (Windows host)

```powershell
dotnet build shell\windows\Emendly.sln
```

## Commands

```powershell
Emendly.Probe.exe --list
Emendly.Probe.exe --probe --hwnd 0x000A0B2C
Emendly.Probe.exe --probe --hwnd 0x000A0B2C --json
Emendly.Probe.exe --probe --title "Untitled - Notepad"
```

- `--list` prints one line per visible top-level window with a title: handle, PID, process name, class name, and title.
- `--probe` targets a window by `--hwnd` (hex `0x...` or decimal) or `--title` (first case-insensitive substring match).
- `--json` emits a single JSON object.

## JSON output

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
  "patterns": ["TextPattern", "ValuePattern"],
  "enabled": true,
  "read_only": false,
  "selection": { "length": 0, "preview": "" },
  "caret": { "supported": true, "offset": 0 },
  "field_text_length": 34,
  "verdict": "caret"
}
```

`verdict` is one of `selection`, `caret`, `read_only`, or `unsupported`. `selection.preview` strips newlines and never exceeds 80 characters. No user text is written to disk.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Probe or list succeeded (including `verdict: "unsupported"`) |
| `2` | No window matched `--hwnd` or `--title` |
| `3` | Internal error, invalid arguments, or 5-second UIA timeout (`{"verdict":"unsupported","error":"timeout"}`) |
