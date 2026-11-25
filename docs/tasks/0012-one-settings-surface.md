# Task 0012: one settings surface

Status: ready
Depends on: task 0011

## Environment

No .NET SDK in WSL. Do not build. Write code only; the parent builds on Windows. Work only inside `shell/windows/Emendly.App`.

## Problem

There are two settings interfaces:

- the **panel stage** added in task 0011 (Shortcut, Default language), opened by the gear in the panel header;
- the **SettingsWindow** opened from the tray menu (Base URL, Model, Global Hotkey typed into a text box, engine and API-key status).

Same concept, two UIs, and the tray one still asks the user to type a hotkey string.

## Change

Make **SettingsWindow the only settings surface**. Delete the panel's Settings stage.

1. Delete `PanelStage.Settings`, its XAML block (`SettingsPanel` and the `SettingsHeadingText`), the panel's shortcut-capture fields and handlers, and the `ModeLineText == "Settings"` branch. Remove the gear's stage-navigation handler; the gear keeps its place in the header.
2. The gear in the panel header now **closes the panel and opens `SettingsWindow`**. Restore focus to the target window first so it does not end up behind other windows; make the window centred on the screen that holds the caret, and give it `Topmost` only while the panel was topmost.
3. `SettingsWindow` gains the two rows the panel stage had, above the existing fields, in this order:
   1. **Shortcut** — a button showing the current combination (`F6`), press-to-set exactly as task 0011 specified: `Press the shortcut…`, capture on the next non-modifier key, `Escape` cancels, reject modifier-only, re-register on save, show `That shortcut is already in use.` on failure and keep the old one.
   2. **Default language** — a `ComboBox` with the same language list the panel picker uses, current value selected, editable false.
   3. Base URL (existing field)
   4. Model (existing field)
   5. Engine status and API-key status lines (existing)
4. Footer: `Cancel` and `Save`. `Save` applies the hotkey change, persists everything through `AppSettings.Save` to `%APPDATA%\Emendly\settings.json`, and closes. `Cancel` closes without applying; a captured-but-unsaved hotkey must not be registered.
5. The tray menu item keeps opening the same window; both entry points must show the identical window, same size, same rows, same order.
6. Add a `--settings` command-line flag: when present, start the app, open `SettingsWindow`, and exit when it closes (single-instance behaviour of the tray app must still hold).

## Constraints

- Window width 420, height sized to content; dark theme consistent with the panel (`#1E1E24` surface, `#2A2A34` inputs, `#3B82F6` focus border).
- No new packages. No engine changes.
- The F6 default and the `%APPDATA%\Emendly\settings.json` overlay from task 0011 stay as they are.

## Acceptance, run by the parent

1. Builds with zero errors.
2. `Emendly.App.exe --settings` shows one window with Shortcut, Default language, Base URL, Model and the two status lines.
3. Pressing the Shortcut button and pressing a key sets it; Save persists it; Cancel discards it and the old hotkey still works.
4. The gear in the panel opens that same window; the tray menu opens that same window.
5. No `SettingsPanel` or panel-stage settings code remains.

## When done

State what was deleted, how the gear hands over to the window, and how cancel avoids registering a captured hotkey.
