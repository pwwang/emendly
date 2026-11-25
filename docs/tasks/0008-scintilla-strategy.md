# Task 0008: a capture strategy for Scintilla apps

Status: ready
Owner: coding agent
Depends on: task 0006

## Why

Notepad3 reports `unsupported` because its editor is a **Scintilla** child window and Scintilla publishes no UIA `TextPattern`. It is not a permission problem.

Verified by hand on this machine against a live Notepad3 window:

```
EnumChildWindows  → hwnd 7869976, class = Scintilla
SCI_GETTEXTLENGTH (2006)      = 1634
SCI_GETLINECOUNT  (2154)      = 55
SCI_GETSELECTIONSTART (2143)  = 0
SCI_GETSELECTIONEND   (2144)  = 0
```

Those messages work cross-process with plain `SendMessage`, no hooks, no elevation. One rule therefore covers every Scintilla editor: Notepad3, Notepad++, SciTE, and others.

## Goal

Replace the single UIA probe with a strategy chain, and add a Scintilla strategy.

## 1. Strategy interface

New file `Emendly.Capture/ICaptureStrategy.cs`:

```
CaptureResult TryCapture(IntPtr topHwnd)
```

`CaptureResult` carries: `bool Handled`, `SelectionState State`, `string Text`, `int SelectionLength`, `bool ReadOnly`, `string StrategyName`, plus the existing caret and field data the panel needs.

Chain order, first `Handled = true` wins:

1. `UiaCaptureStrategy` — today's code, unchanged behaviour.
2. `ScintillaCaptureStrategy` — new.
3. `ClipboardCaptureStrategy` — optional, see section 4.

Log the winning strategy name with the probe record: `{"event":"probe","app":"Notepad3","status":"selection","strategy":"scintilla"}`.

## 2. Scintilla strategy

New file `Emendly.Capture/Scintilla/ScintillaCaptureStrategy.cs` plus `ScintillaMessages.cs`.

- Find the editor: `EnumChildWindows` from the top-level window, first child whose class name is `Scintilla`.
- Selection bounds: `SCI_GETSELECTIONSTART` (2143) and `SCI_GETSELECTIONEND` (2144). Equal means caret.
- Text length: `SCI_GETTEXTLENGTH` (2006). Line count: `SCI_GETLINECOUNT` (2154).
- Selection text: `SCI_COPYRANGE` (2423) with start and end, then read the clipboard, then restore the clipboard. Do not use `WM_COPY`; it depends on focus.
- Context: convert positions with `SCI_LINEFROMPOSITION` (2166), `SCI_POSITIONFROMLINE` (2135) and `SCI_GETLINEENDPOSITION` (2136), then `SCI_COPYRANGE` for the chosen span. Apply the same two-sentence, 400-character cap as `ContextReader`.
- Write back: put the result on the clipboard, send `SCI_PASTE` (2179), then restore the clipboard. `SCI_PASTE` replaces the current selection and respects undo.
- Never guess the caret: if the child window is missing, return `Handled = false` so the chain continues.

## 3. Multi-view apps

Notepad++ can host two Scintilla children. Choose the one that has keyboard focus:

- `GetGUIThreadInfo` for the app's thread, read `hwndFocus`, and use it when it is a Scintilla child.
- Otherwise fall back to the first Scintilla child, and note `ambiguous_view: true` in the log.

## 4. Clipboard fallback, opt-in

Keep a `ClipboardCaptureStrategy` that sends `Ctrl+C`, reads the clipboard, and restores it. It must be **off by default** and enabled per app by rule, because it is slow and it disturbs the clipboard. Rules live in `shell/windows/Emendly.App/app-rules.json`:

```json
{
  "notepad3.exe": "scintilla",
  "notepad++.exe": "scintilla",
  "windowsterminal.exe": "clipboard"
}
```

Unknown apps use the default chain: UIA, then Scintilla, then stop.

## 5. Probe CLI

`Emendly.Probe --probe` gains a `strategy` field in its JSON output so the parent can verify without the GUI.

## Acceptance, run by the parent

1. Builds with zero warnings.
2. `Emendly.Probe --probe --hwnd <Notepad3>` returns `verdict: selection` or `caret` and `strategy: scintilla` instead of `unsupported`.
3. The panel shows the `passage` menu for a selection in Notepad3.
4. Applying an action in Notepad3 replaces the selection and undo works in the app.
5. Notepad, Chrome and Settings behave exactly as before.

## Constraints

- No new NuGet packages. P/Invoke only.
- Clipboard save and restore on every path, in a `finally`.
- No user text in logs, only lengths, strategy names and statuses.
- Do not change the engine or the protocol.

## When done

State which Scintilla messages you used for context, how you pick the focused view, and any app you expect the strategy to misfire on.
