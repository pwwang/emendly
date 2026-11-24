# Task 0004: the panel must take the keyboard

Status: ready
Owner: coding agent
Depends on: task 0003

## The failing repro, observed on a real machine

1. Notepad holds `teh cat sat on teh mat`. Select all with `Ctrl+A`.
2. Press `Ctrl+Alt+E`. The log records `{"event":"probe","app":"Notepad","status":"selection"}` and a panel window appears.
3. Press `1` for Proofread. **The `1` is typed into the Notepad document.** The panel never receives the key.
4. Press `Enter`. The action log says `"action":"apply","status":"ok"` with 24 bytes in and 24 out, but the document text is unchanged apart from the stray `1`.

The engine itself is fine. A direct run against a mock provider streamed the full reply. The fault is keyboard focus and the apply path.

## What to fix

1. **The panel must own the keyboard while it is open.**
   - `ShowActivated = true`, `Topmost = true`, `Focusable = true`, and `Activate()` plus `Keyboard.Focus(...)` on the action list after `Show()`.
   - Log `{"event":"focus","target":<process>}` when the panel takes focus, so the next failure is visible in `%APPDATA%\Emendly\emendly.log`.
   - If `IsKeyboardFocusWithin` is false 150 ms after showing, retry activation twice before giving up and showing a notice.

2. **Never send keystrokes to the target app while the panel is open.** The panel's own key handling must cover digits 1 to 5, arrows, Enter and Esc, and it must swallow them (`e.Handled = true`).

3. **Return focus before pasting.** Capture the target `hwnd` when the panel opens, call `SetForegroundWindow(hwnd)` before the paste, and only then send `Ctrl+V`. Wait until `GetForegroundWindow()` equals the target, up to 300 ms, polling every 20 ms. If it never matches, abort the insert and show a notice rather than pasting into the wrong window.

4. **Apply the engine's `done` text, not the request text.** The 24-byte apply above equals the input length, which suggests the apply path used the original. Pass the accumulated `delta` text or the `done` payload through explicitly.

5. **The hotkey while the panel is open cancels the request and closes the panel.** Do not open a second panel.

## Files likely involved

- `Emendly.App/PanelWindow.xaml`, `PanelWindow.xaml.cs`
- `Emendly.App/App.xaml.cs` (hotkey dispatch, focus bookkeeping)
- `Emendly.Capture/ClipboardInserter.cs` (foreground checks around paste)

## Constraints

- No NuGet packages, no global keyboard hooks. Focus, not interception.
- Do not change the engine or the protocol.
- Keep the metadata-only logging rule: no user text in logs.

## When done

State exactly which mechanism makes the panel keep focus, what happens when activation fails three times, and how the apply path now obtains the text.
