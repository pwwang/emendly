# Task 0011: header icons (settings, close), F6 default hotkey, press-to-set shortcut

Status: ready
Depends on: task 0010

## Environment

No .NET SDK in WSL. Do not build. Write code only; the parent builds on Windows. Work only inside `shell/windows/Emendly.App`. Preserve existing behaviour except where stated.

## 1. Header: replace the engine badge with two icons

- Remove the `engine` badge from the header's top-right corner. Keep the stage title and the character summary on the left exactly as they are.
- Add two 18 x 18 icon buttons at the top-right, using the existing `OptionIconButtonStyle` (same hover / focus / disabled treatment):
  - **Settings** — a gear drawn as a `Path` geometry in `Window.Resources` (eight-tooth outline, no font glyph, no package). Tooltip `Settings`.
  - **Close** — an X drawn as a `Path`. Tooltip `Close`.
- Close behaves exactly like `Esc` from the action menu: it closes the panel and restores focus to the target window. It is present in every stage.
- Settings opens a new stage (see 2).

## 2. Settings stage

New `PanelStage.Settings`, same 380 px width, shown inside the existing panel window (not a second window).

Contents, top to bottom:

1. Title `Settings`.
2. **Shortcut** row: label `Shortcut`, and a bordered button that shows the current combination (`F6`, `Ctrl+Alt+E`, …). Pressing it (click or `Enter`) enters capture mode.
3. **Default language** row: label `Default language`, showing the current value; clicking opens the existing language list stage and selecting a language returns to Settings with the value applied.
4. Footer hint: `Esc back` and a `Done` button that returns to the action menu.

`Esc` in Settings returns to the action menu (does not close the panel).

## 3. Press-to-set shortcut

- In capture mode the button text becomes `Press the shortcut…` and it takes keyboard focus.
- The next `PreviewKeyDown` with a non-modifier key captures the combination: modifiers `Ctrl`, `Alt`, `Shift`, `Win` plus the key, rendered as `Ctrl+Alt+E`, `F6`, `Shift+F9`.
- Reject and keep waiting when: only modifiers are held; the key is `Escape` (that cancels capture and restores the previous value); the combination is already registered by another application.
- On acceptance:
  1. Unregister the old hotkey (`UnregisterHotKey`), register the new one (`RegisterHotKey`).
  2. If registration fails, re-register the old one and show an inline error `That shortcut is already in use.` in red under the row.
  3. Persist the new value, and log `{"event":"hotkey","value":"<combo>"}`.
- The whole panel must keep working after the change: the same hotkey still toggles the panel open and closed.

## 4. F6 as the default

- Change the shipped default from `Ctrl+Alt+E` to `F6` in `appsettings.json`, and make F6 the fallback when no value is found. F6 is free in VS Code, Office and Explorer; the F-keys that are genuinely taken there are F1–F5 and F8–F12.
- A single F-key must be accepted as a complete shortcut (no modifier required).

## 5. Persistence

- Read settings in this order: `%APPDATA%\Emendly\settings.json`, then `appsettings.json` next to the executable, then built-in defaults.
- Write user changes only to `%APPDATA%\Emendly\settings.json` (`{"hotkey":"F6","language":{"default_target":"German"}}` shape). Never write into the install directory.
- A missing or malformed file must fall back silently to the next source.

## Acceptance, run by the parent

1. Builds with zero errors and zero new warnings.
2. Header shows the gear and the X only; the engine badge is gone.
3. Gear opens Settings; `Esc` returns to the menu; X closes the panel.
4. Clicking the shortcut button then pressing a key sets it; the old combination stops working and the new one opens the panel.
5. `F6` opens the panel on a fresh profile.
6. `settings.json` is created under `%APPDATA%\Emendly` and survives a restart.

## When done

State the geometry names for the two icons, how capture mode filters keystrokes, where the settings file is written, and what happens when registration fails.
