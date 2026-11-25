# Task 0010: the panel is too tall for a five-item menu

Status: ready
Depends on: task 0009

## Environment

No .NET SDK in WSL. Do not build. Write code only; the parent builds on Windows. Work only inside `shell/windows/Emendly.App`. Preserve all existing behaviour except the height change described here.

## Problem

The window is a fixed 380 x 420. A default menu shows at most five rows (Compress: Compose, Custom Prompt; Passage: Proofread, Rewrite, Translate, Humanize, Prompt Optimize; Word: Proofread, Translate, Synonyms), so 420 px leaves a large empty band under the footer. The same height is used when the list is expanded to nine items.

## Change

- Remove the fixed `Height="420"` from `PanelWindow.xaml`.
- Let the window size itself to its content: `SizeToContent="Height"`, `MaxHeight="420"`, and a `MinHeight` that keeps the prompt and result stages from collapsing (they should keep roughly their current height).
- The action list scrolls only when the expanded list would exceed `MaxHeight`; the default five-row menu must have no scrollbar and no empty band.
- Width stays exactly 380. The `SizeToContent` height must not cause the width to change or the window to jump horizontally.
- Re-measure after a filter change, after Tab expansion (nine items), and when returning from a result with `Esc`, so the panel shrinks and grows with the row count instead of keeping the tallest height it has ever had.
- Anchor the window: it is positioned near the caret. Keep the top-left corner where it is when the height changes (do not re-centre vertically).

## Verify yourself by reading the code

1. Default Passage menu: five rows and the footer, bottom padding about 12 px, no scrollbar.
2. Tab-expanded menu: nine rows, height capped at 420 with scrolling beyond that.
3. Prompt stage and result stage: unchanged look, no clipping at the new height.
4. `Esc` from a result returns to the five-row menu at the smaller height.

## When done

State the new height rules and how you re-measure after filtering, expansion and discard.
