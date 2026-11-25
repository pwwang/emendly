# Task 0014: the shared language dropdown must accept a custom language

Status: ready
Depends on: task 0013

## Problem

Task 0013 replaced the panel's language stage with a shared `ComboBox` (`Themes/LanguageDropdown.xaml`) and set `IsEditable="False"`. The old stage let the user type a language that is not in the list and use it as the target (for example a rarer language name or a regional variant). That is a regression: the list is a convenience, not the whole space of valid targets, and the engine accepts any language name.

## Change

Make the shared dropdown editable, in both places, without breaking the shared style.

1. `Themes/LanguageDropdown.xaml`:
   - `IsEditable="True"`, `IsTextSearchEnabled="False"` (typing now goes into the field instead of jumping).
   - Add an editable template: `#2A2A34` field, `#3B3B48` border, `#3B82F6` when focused or open, `#F3F4F6` text and caret, `#9CA3AF` watermark, a compact chevron button on the right, dark popup with the same item template, hover and selected colours as today.
   - Watermark text: `Type or choose a language`.
2. Accepting a custom value:
   - `Enter` in the field with non-empty text that is not in the list accepts the typed text verbatim as the target language and closes the dropdown.
   - `Esc` reverts to the value the dropdown opened with.
   - Selecting a list item behaves as today.
   - Trim whitespace; ignore a blank entry (revert instead).
3. Settings (`SettingsWindow.xaml.cs`): a custom value is saved like any other and shown in the field afterwards.
4. Panel (`PanelWindow.xaml.cs`): a custom value applies to that Translate run only, exactly as a list pick does today; do not write it to `settings.json` from the panel.
5. Keep one style definition; do not fork a second editable style for either window.

## Acceptance, run by the parent

1. Builds with zero errors.
2. Settings: typing a language not in the list and pressing Enter saves it; reopening shows it.
3. Panel: the globe opens the same editable dropdown; a typed value applies to that run and the two-letter code under the globe reflects it (use the first two letters uppercased when there is no known code).
4. Esc reverts; blank reverts.

## When done

State how the editable template is shared, how a typed value is distinguished from a list pick, and what the code under the globe shows for a custom language.
