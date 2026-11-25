# Task 0013: one language dropdown, clearer label

Status: ready
Depends on: task 0012

## Environment

No .NET SDK in WSL. Do not build. Write code only; the parent builds on Windows. Work only inside `shell/windows/Emendly.App`.

## 1. Label

In `SettingsWindow.xaml`, rename the row label `Default language` to `Default translation target language`. Nothing else about that row changes.

## 2. One dropdown for both places

Today the two language pickers look and behave differently:

- Settings uses a `ComboBox`.
- The panel's Translate row uses the globe icon, which opens a full-panel list stage (`OpenLanguagePicker` / `CloseLanguagePicker`).

Make both use the same dropdown UI and the same list source.

### Shared pieces

- One language list, defined once (the list the panel picker already derives from — reuse it, do not duplicate the names), exposed as a single static read-only sequence.
- One dark `ComboBox` style, defined once in a shared `ResourceDictionary` (e.g. `Themes/LanguageDropdown.xaml`) merged by both windows: `#2A2A34` field, `#3B3B48` border, `#3B82F6` border when focused or open, `#F3F4F6` text, `#32384A` item hover, `#263148` selected item, 12 px text, 4 px corner radius, 10 px dark scrollbar inside the popup, max popup height 260 px.

### Settings

- The existing row keeps the shared `ComboBox`, width 220, `IsEditable=false`, search-by-typing enabled (`IsTextSearchEnabled=true`).

### Panel

- The globe icon on the Translate row no longer switches stages. It opens the **same** `ComboBox` style as a dropdown anchored just under the icon, inside the panel's popup, with the current target language selected.
- Opening: click on the globe or `Enter`/`Space` on it sets `IsDropDownOpen = true`. The panel must stay open while the dropdown is open.
- Choosing an item sets the target language for that Translate run, updates the `ZH`-style code under the globe, closes the dropdown and returns keyboard focus to the Translate row. `Esc` closes the dropdown and returns to the row without changing anything.
- `Up`/`Down`/`Enter` must work inside the dropdown; typing letters jumps to a matching language.
- Remove the old language-picker stage and its stage-switching code (`PanelStage.LanguagePicker` or equivalent) if nothing else uses it. Check `HotkeyWindow`, `App.xaml.cs` and `SettingsWindow.xaml.cs` for callers before deleting.

## Acceptance, run by the parent

1. Builds with zero errors.
2. Settings shows `Default translation target language` with the shared dropdown.
3. The panel's globe opens a dropdown that looks identical to the Settings one — same field, same items, same highlight and scrollbar.
4. Choosing a language in the panel changes only that run's target; choosing one in Settings persists as the default.
5. No leftover picker stage code.

## When done

State where the shared dictionary lives, what the panel's old picker stage used to do that the dropdown now covers, and anything the dropdown cannot do that the old stage could.
