# Menu specification

Status: proposed
Applies to: `Emendly.App` panel, engine action registry
Date: 2026-11-24

## 1. State model

The panel chooses its default items from one state plus one flag.

| State | Test | Notes |
|---|---|---|
| `empty` | No selection, caret in an editable field | Compose target |
| `word` | Selection with no whitespace, and either 40 or fewer ASCII letters or 20 or fewer characters in a script without spaces | Punctuation and digits allowed |
| `passage` | Any other selection | Includes multi-line selections |

`read_only` is a separate flag. It never changes which items appear; it changes what the result offers (see section 6).

Classification runs on the trimmed selection. The replacement still uses the untrimmed range.

## 2. Action inventory

| Id | Label | Kind | Default in |
|---|---|---|---|
| `proofread` | Proofread | Replace | word, passage |
| `rewrite` | Rewrite | Replace, has options | passage |
| `compose` | Compose | Insert, has options | empty |
| `translate` | Translate | Replace, has options | word, passage |
| `custom` | Custom Prompt | Custom | anywhere |
| `synonyms` | Synonyms | Reference list | word |
| `antonyms` | Antonyms | Reference list | none |
| `custom` | Custom Prompt… | Replace or insert, free text | empty |

`Replace` writes into the field. `Insert` writes at the caret. `Reference list` shows text in the panel and never writes until the user asks.

## 3. Default and expanded items

Default view, in this order:

| State | Items | Numbers |
|---|---|---|
| `empty` | Compose, Custom Prompt… | 1, 2 |
| `word` | Proofread, Translate, Synonyms | 1, 2, 3 |
| `passage` | Proofread, Rewrite, Translate | 1 to 3 |

Everything else sits under **More actions**, opened with `Tab` or the `More…` row, and keeps the grouping below:

```
Improve      Proofread · Rewrite
Change       Translate
Words        Synonyms · Antonyms
Create       Compose · Custom Prompt…
```

Consequences to accept:

- **Antonyms is never a default.** It appears only in the expanded list, directly after Synonyms. Converting a displayed synonym list into antonyms is one keystroke from the result view.
- **Compose and Custom Prompt are never defaults outside the empty state.** A selection is assumed to be something to act on, not a place to write.
- **Custom Prompt is hidden for word and passage** to keep the default list short. It is one `Tab` away.

## 4. Options

Options sit on one row under the highlighted item. `Tab` moves focus into the row, `Space` toggles, `Esc` returns to the list.

| Action | Option | Control | Default | Stored |
|---|---|---|---|---|
| Rewrite | Writing style | Dropdown anchored to the row: 11 styles | Improve | Global, persists |
| Rewrite | Use context | Toggle chip `Context: on/off` | On | Per action, persists |
| Compose | Use context | Toggle chip `Context: on/off` | On | Per action, persists |
| Translate | Target language | Chip showing the language, `Space` cycles, `Enter` opens a searchable list | `language.default_target` from config | Global, persists |

### Context definition

- **Rewrite:** the sentence before and the sentence after the selection, from the same field.
- **Compose:** the sentence before and the sentence after the caret, from the same field.
- Each side is capped at two sentences and 400 characters. Newlines collapse to spaces.
- Context is read through the same UI Automation text range as the selection. When the range cannot be read, the chip is disabled and shows `Context unavailable in this app`.
- The panel shows a collapsed `Context preview` line so the user sees exactly what will be sent. Context never leaves the machine unless the toggle is on and the request is sent.

## 5. Keyboard and layout

| Key | Effect |
|---|---|
| `1` to `9` | Run the item at that position in the current list |
| `Down` / `Up` | Move the highlight |
| `Enter` | Run the highlighted item, or confirm the option in focus |
| `Tab` | Enter the options row; `Shift+Tab` returns |
| `Space` | Toggle the focused option, or cycle a language chip |
| `Right` / `More…` | Open the expanded list |
| `Left` / `Back` | Return to the default list |
| `Esc` | Close the panel, cancel a running request, restore focus to the target app |
| Typing letters | Filter the expanded list by label prefix |
| `Ctrl+Enter` | Run with options forced off |

Layout, top to bottom: mode line (`Rewrite · 128 characters · Word`), item list, options row, context preview, result area.

The default list keeps a fixed height so the panel does not jump between states.

## 6. Read-only fields

A read-only selection keeps every default item. Output changes:

- The primary action becomes **Copy**, with the same shortcut (`Enter`).
- **Insert at caret** is offered only when a writable field also has a caret, and is disabled otherwise with the reason on the row.
- No undo entry is created, because nothing was written.

## 7. Edge cases

- **Trailing whitespace** is trimmed for classification, kept for replacement.
- **CJK and other scripts without spaces:** a selection of 20 or fewer characters with no sentence-final punctuation counts as `word`.
- **Single token that is a URL, email or path:** counts as `word`. The default list stays the same; the expanded list is the escape hatch.
- **Selection longer than 4,000 characters:** the request is trimmed to 4,000 and the panel shows `Long selection: first 4,000 characters used`.
- **Selection spanning paragraphs:** allowed. Context still takes only the adjacent sentences.
- **No editable field:** panel opens with an `Unsupported` notice and no items.
- **Empty field, no context available:** Compose runs with empty context.
- **Second hotkey press:** cancels and closes, never opens a second panel.

## 8. Engine contract additions

New actions: `antonyms`, `custom`. `humanize` and `prompt_optimize` are not top-level actions:
they are two of the eleven rewrite styles, applied through `system_suffix`.

New request fields under `options`:

```json
{
  "include_context": true,
  "context_before": "…",
  "context_after": "…",
  "target_lang": "German",
  "instruction": "free text, custom only"
}
```

Rules:

- Unknown options stay ignored, so protocol stays at 1.
- `include_context: false` must send no context fields at all.
- `custom` requires `instruction`; without it the engine returns `bad_request`.
- `antonyms` returns up to five items, one per line, with no definitions.
- `synonyms` keeps that shape.

Prompt intent, one line each:

| Action | Intent |
|---|---|
| `prompt_optimize` | Rewrite the user's prompt so a model follows it more reliably. Keep the intent. Output only the improved prompt. |
| `humanize` | Rewrite so it reads as natural human writing. Vary rhythm, drop stock phrasing, keep the meaning. |

## 9. Acceptance tests

1. Caret in an empty Notepad field: exactly `Compose`, `Custom Prompt…`.
2. Select `cat`: exactly `Proofread`, `Translate`, `Synonyms`.
3. Select `the cat sat on the mat`: exactly the five passage items in the order above.
4. Same sentence selected on a read-only Chrome page: same five items, primary action `Copy`, no undo entry.
5. `Tab` opens all nine items in the four groups, with Antonyms directly after Synonyms.
6. Rewrite with context on sends `context_before` and `context_after`; switching it off sends neither field.
7. Changing the Translate language persists across panel openings and appears in the next request as `target_lang`.
8. `Esc` closes and returns focus; a second hotkey press reopens in the same state with the previous language and context settings.
