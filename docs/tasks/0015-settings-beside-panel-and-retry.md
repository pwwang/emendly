# Task 0015: settings beside the panel, retry after a failed generation

Status: ready
Depends on: task 0014

## Environment

No .NET SDK in WSL. Do not build. Write code only; the parent builds on Windows. Work only inside `shell/windows/Emendly.App`. Preserve existing behaviour except where stated.

## 1. The panel stays open while settings are open

Today the gear in the panel header closes the panel and then opens `SettingsWindow` (`App.OpenSettings` calls `_activePanel.CloseAndRestoreFocus()`).

- **Do not close the panel.** Show `SettingsWindow` modeless (`Show()`, not `ShowDialog()`), with `Topmost` matching the panel's, so it appears above the panel rather than behind it.
- Position it beside the panel: to the right if the monitor has room for the 420 px width plus a 12 px gap, otherwise to the left, otherwise below; never overlapping the panel and never off-monitor.
- Reopening the gear while the window is open activates the existing window (the single-instance guard already does this).
- Closing the panel by any intentional path (`Esc`, the X, the hotkey toggle, Apply) also closes an open settings window, so no orphaned dialog is left behind.
- The panel must stay visible and usable underneath while settings are open.

### Reflect saved settings in the panel

- `SettingsWindow` raises a `Saved` event after a successful save.
- The panel handles it and refreshes, without changing stage, highlighted row, filter or any typed prompt text:
  - **Default target language**: if the user has not picked a language for the current run, the target becomes the saved default, the field text and the two-letter code under the globe update, and the tooltip follows.
  - **Hotkey**: nothing to redraw inside the panel; the registration was already replaced by the save.
  - **Base URL / model**: nothing to redraw; the next request uses them.
- A per-run language pick in the panel keeps winning over the default until the panel closes.

## 2. Retry after a failed generation

The error stage currently offers `Back` and, when partial text exists, `Copy`.

- Add a **Retry** button, primary, in `ErrorOrCancelled` when the failure was a request error (not when the user cancelled with `Esc`).
- `Retry` re-issues the same action with the same selection, options and context, stays in the panel, and shows the streaming stage again. Cancel any existing request first; never stack two.
- Keyboard: `Enter` retries while the error stage is showing; `R` also retries. `Esc` still discards to the menu. `Back` still returns to the menu.
- Footer hint in that stage: `Enter retry · R retry · Esc back`.
- A second failure returns to the same error stage with the new message; a success shows the preview as usual.
- Log a new `{"event":"action",...}` line per retry, as a normal attempt does.

## Acceptance, run by the parent

1. Builds with zero errors.
2. Gear: panel stays visible, settings window appears beside it, no overlap.
3. Saving a new default language updates the panel's globe code while the panel is open.
4. Closing the panel closes the settings window.
5. With a provider that fails, the error stage shows Retry; pressing Enter starts a second attempt with the same action and the log shows two attempts.
6. Cancelling with `Esc` shows no Retry button.

## When done

State how the settings window is positioned, how the panel refreshes on save, and how retry reuses the original request state.
