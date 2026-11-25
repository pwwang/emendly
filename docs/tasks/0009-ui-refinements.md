# Task 0009: three UI refinements

Status: ready
Owner: coding agent
Depends on: task 0007

## Environment

No .NET SDK in WSL. Do not build. Write code only; the parent builds on Windows.

Preserve all existing behaviour except where these three changes require adjustment.

## 1. Options become icons, and the panel stops widening

Today the highlighted row shows a text chip (`Context: on`, `→ German`) and the panel widens from 380 to 500 px to fit it.

- Replace the text chips with **icon buttons**, 18 x 18, right-aligned in the row's second column.
- **Context toggle icon:** a pilcrow-with-brackets glyph drawn as a `Path` (no icon font, no package). On state: accent colour `#60A5FA`. Off state: muted `#6B7280` with a thin diagonal slash. Tooltip: `Include surrounding sentences (on)` / `(off)`.
- **Language icon:** a globe drawn as a `Path`, with the two-letter language code (`DE`, `FR`, `ES`) in 9 px text directly beneath it inside the same 18 px hit target. Tooltip: `Target language: German`.
- Keep the click and keyboard behaviour: click toggles or opens the language list, `Enter` on the language icon opens the list, `Space` toggles.
- **Remove the wide width entirely.** The panel stays at its current narrow width (380 px) for every row and every stage. Delete `WideMenuWidth` and any width switch; keep the fixed height.
- Rows without options keep the second column collapsed, so labels never shift.

## 2. Make the generating composer unmistakably different from the input composer

There are two different boxes and they currently look alike.

| State | Label | Look | Behaviour |
|---|---|---|---|
| **Generated, read-only** (streaming or preview) | `Generating…` with a small lock glyph while streaming, `Result` when finished | Muted surface `#141419`, 1 px border `#2A2A34`, text `#D1D5DB`, no caret, top-right status `streaming` / `done` | Read-only, selectable, never accepts typing |
| **Editable input** (custom prompt, compose brief) | `Your instruction` / `Brief` | Brighter surface `#1B1B22`, 1 px border `#3B82F6` when focused and `#2A2A34` otherwise, text `#F3F4F6`, visible caret | Typing, paste, `Enter` runs, `Shift+Enter` newline |

Also:

- The generated box shows a 2 px left accent bar `#2563EB` while streaming, and loses it when done.
- The editable box never shows that accent bar.
- No text may be typed into the generated box even if it has focus; keep it `IsReadOnly` and move focus to the action buttons instead.

## 3. Discarding the result returns to the menu

- In the result stage (streaming, preview ready, or error), `Esc` **returns to the action list in the same state and with the same highlighted row**, instead of closing the panel.
- If a request is streaming, `Esc` cancels it and returns to the menu.
- A second `Esc` from the menu closes the panel as today.
- The `Back` control in the result stage does the same as `Esc`.
- `Enter` on a result still applies or copies, unchanged.
- Discarding must clear the result text so a later reopen does not show stale output.

## Files

- `Emendly.App/PanelWindow.xaml` and `.xaml.cs` — icons, widths, stage styling, Esc/Back behaviour.
- `Emendly.App/App.axaml`-equivalent resources live in `PanelWindow.xaml`; add the two icon geometries there.
- No engine changes. No new packages.

## Acceptance, run by the parent

1. Builds with zero warnings.
2. Panel width is 380 px on every row, including Rewrite and Translate.
3. The context icon toggles and the next request reflects it; the language icon opens the picker.
4. Streaming text appears in the muted locked box; the input box is visually distinct.
5. `Esc` during streaming cancels and returns to the same menu; `Esc` again closes.

## When done

State the exact pixel width kept, how you draw the two icons, and how focus returns to the menu after a discard.
