# Task 0007: move options beside the item

Status: ready
Owner: coding agent
Depends on: task 0006

## Environment

No .NET SDK in WSL. Do not build. Write code only; the parent builds on Windows.

## Goal

The options for the highlighted action must sit to the **right of that menu row**, not in a row under the list.

## Layout

Each action row is a two-column grid:

```
 1  Proofread
 2  Rewrite            [Context: on]
 3  Translate          [→ German]
 4  Humanize
 5  Prompt Optimize
```

- Column one: the number and label, as today.
- Column two: the option chips, right-aligned, rendered **only on the highlighted row**.
- Rows without options keep the second column collapsed, so labels do not shift horizontally between rows.
- The panel widens when the highlighted action has options and narrows when it does not. Keep the height fixed. Do not animate.

## Chips

- Context chip: text `Context: on` or `Context: off`. Click toggles. Keyboard focusable.
- Language chip: text `→ German` (the current target). Click or `Enter` opens the existing searchable language list. Keyboard focusable.
- Chips use the same visual weight as the mode line, not as the action labels.
- The chip column is not a separate panel: it has no background, border or header of its own.

## Behaviour that must not regress

- `1` to `9` still run the item at that position.
- `Tab` still opens the expanded list. From the expanded list, `Tab` moves into the chips of the highlighted row when it has any.
- `Space` toggles the focused chip; `Esc` returns focus to the list, then closes on a second press.
- Changing the language still persists globally; the context toggle still persists per action.
- Removing the bottom options row must not change the context preview line or the result area; both stay below the list.

## Files

- `shell/windows/Emendly.App/PanelWindow.xaml` — item template becomes two columns; delete the bottom options row.
- `shell/windows/Emendly.App/PanelWindow.xaml.cs` — chip visibility per row, focus handling, width adjustment on selection change.
- `AppSettings.cs` — only if a persisted width or chip state needs a new field.

## Acceptance, run by the parent

1. Builds with zero warnings.
2. `Rewrite` highlighted shows `Context: on` at the right edge of that row.
3. `Proofread` highlighted shows no chip and the panel is narrower.
4. Clicking the context chip flips the text and the next request sends `include_context` accordingly.
5. `Translate` highlighted shows `→ German`, and changing it updates the chip text.

## When done

Describe how the chip column avoids shifting labels, and what happens to panel width when the selection moves between rows with and without options.
