# Task 0006: panel menu per the specification

Status: ready
Owner: coding agent
Depends on: task 0003, task 0004, `docs/spec-menu.md`

## Environment

No .NET SDK in WSL. **Do not build or run.** Write code only; the parent builds on Windows.

## Goal

Implement `docs/spec-menu.md` in the WPF panel: state classification, default and expanded lists, the options row, context extraction, and read-only behaviour.

## 1. Classification

New file `Emendly.Capture/SelectionClassifier.cs`:

- `SelectionState.Empty`, `Word`, `Passage`, plus a `bool ReadOnly`.
- `Word`: trimmed text contains no whitespace and either (a) 40 or fewer ASCII letters/digits, or (b) 20 or fewer characters when the text has no ASCII letters at all (CJK and similar).
- Everything else with text is `Passage`.
- Return the trimmed text, the untrimmed range, and the character count.

## 2. Menu model

New file `Emendly.App/MenuModel.cs`:

- Action ids exactly: `proofread`, `rewrite`, `compose`, `translate`, `prompt_optimize`, `humanize`, `synonyms`, `antonyms`, `custom`.
- `GetDefault(state)` returns the ordered list from the spec: empty → compose, custom; word → proofread, translate, synonyms; passage → proofread, rewrite, translate, humanize, prompt_optimize.
- `GetExpanded()` returns all nine in the four groups, with group labels: Improve (proofread, rewrite, humanize), Change (translate, prompt_optimize), Words (synonyms, antonyms), Create (compose, custom).
- `GetOptions(actionId)` returns which options apply: context for rewrite and compose, language for translate, none otherwise.

## 3. Options row

In `PanelWindow`:

- One row under the highlighted item, shown only when the action has options.
- `Tab` enters it, `Shift+Tab` leaves, `Space` toggles context or cycles language, `Enter` on the language chip opens a searchable list.
- Context toggle persists per action in settings; language persists globally and seeds from `language.default_target`.
- When context cannot be read, the chip is disabled with the text `Context unavailable in this app`.
- A collapsed `Context preview` line shows the exact text that will be sent.

## 4. Context extraction

New file `Emendly.Capture/ContextReader.cs`:

- `Read(string before, string after)` around a selection or caret, from the same UI Automation text range.
- Take at most two sentences and 400 characters per side; collapse newlines to spaces; sentence ends are `.`, `!`, `?` and paragraph breaks.
- Return null when the range is unavailable; never guess from the clipboard.

## 5. Request construction

- `rewrite` and `compose` send `include_context` plus `context_before` and `context_after` only when the toggle is on and the text exists.
- `translate` sends `target_lang`.
- `custom` sends `instruction` from a text box; an empty instruction is refused by the panel before the request.
- Read-only selection: run the same request, but the result view offers `Copy` as the primary action and no `Insert at caret`, and no undo entry is created.

## 6. Keys and layout

- Digits run the item at that index in the current list.
- `Right` or the `More…` row expands; `Left` collapses; letters filter the expanded list.
- `Esc` closes, cancels, restores focus.
- Fixed panel height across states; mode line shows `Label · N characters · State`.

## 7. Logging

Metadata only: `{"event":"menu","state":"word","items":3}`, `{"event":"action","options":"context=on,lang=German"}`, plus the existing probe and apply records. Never text.

## Acceptance, run by the parent

1. Builds with zero warnings.
2. Caret in an empty Notepad shows exactly the two `empty` items.
3. A single word shows exactly three items; a sentence shows exactly five.
4. `Tab` shows nine items in four groups, Antonyms after Synonyms.
5. Translate language change persists across panel openings.
6. Read-only selection on a web page offers `Copy`, not `Insert`.

## When done

State how classification handles CJK, where context text is held in memory, and anything in the spec you had to interpret.
