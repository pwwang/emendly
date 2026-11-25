# Menu specification

Status: proposed
Revised: 2026-11-24

The panel shows a short list of actions for the current selection, plus a second list behind "More". Two actions carry options, one carries a language picker.

## 1. Inputs the menu branches on

| Input | Source | Values |
|---|---|---|
| Selection state | `ProbeReport.selection.length` | none, single word, multiple words |
| Read-only | `ProbeReport.read_only` | false, true |
| Word under caret | `TextPattern` range around the caret, only when the selection is empty | a word, or nothing |

Rules, applied in this order:

1. A selection that is empty or only whitespace counts as **no selection**.
2. A selection with no whitespace counts as **single word**. Length does not matter.
3. Any other selection counts as **multiple words**. This includes punctuation-only selections and multi-line selections.
4. Read-only never changes the default list. It changes what happens to the result, not what is offered.

## 2. Default items

Shown when the panel opens. Number keys follow the order below.

| State | Default items | Keys |
|---|---|---|
| No selection | Compose, Custom Prompt | 1, 2 |
| Single word | Proofread, Translate, Synonyms | 1, 2, 3 |
| Multiple words | Proofread, Rewrite, Translate, Prompt Optimize | 1, 2, 3, 4 |

## 3. Items behind "More"

`M` or `Tab` opens the list. `B` or `Esc` returns to the default list. Number keys restart at 1.

| State | More items, in order | Enabled | Disabled reason |
|---|---|---|---|
| No selection | Synonyms, Antonyms, Translate | when the caret sits inside a word | "place the caret inside a word" |
| No selection | Proofread, Rewrite, Prompt Optimize | never | "select text first" |
| Single word | Antonyms | always | — |
| Single word | Rewrite, Compose, Prompt Optimize, Custom Prompt | always | — |
| Multiple words | Synonyms, Antonyms | always, applied to the word under the caret, else the first word | "place the caret on a word" |
| Multiple words | Compose, Custom Prompt | always | — |

Every item stays visible in "More", even when disabled. Hiding six of eight actions leaves no trace that they exist, and the reason line teaches the rule in one glance.

## 4. Options

### Rewrite

- Checkbox: **Include context**, default **on**.
- Context is the previous sentence and the next sentence around the selection. Selection text is not counted.
- The panel shows a summary chip: `context: 2 sentences · 214 chars`.
- Context off sends the selection alone.

### Compose

- Checkbox: **Include context**, default **on**.
- Context is the previous sentence before the caret and the next sentence after it.
- Same chip as Rewrite.
- Context off sends the brief alone.

### Translate

- Dropdown: **Translate to**, with the last used language preselected.
- Shortlist: English, Spanish, French, German, Portuguese, Chinese (Simplified), Japanese, Hindi, Arabic.
- `More languages…` opens a search field over the full list.
- Source language is always detected from the text; there is no source picker.
- Changing the language starts the translation immediately, replacing any streamed result.

## 5. What happens to the result

| Field | Primary action | Secondary |
|---|---|---|
| Editable | Replace the selection, or insert at the caret | Copy |
| Read-only | Copy | Show in a panel without inserting |

The button label follows the field: `Replace` when editable, `Copy` when read-only. Read-only never implies a disabled action; it implies a different destination.

## 6. Edge cases

| Case | Behaviour |
|---|---|
| Selection over 5,000 characters | Proofread and Rewrite allowed after a confirmation; Compose and Prompt Optimize disabled with "selection too long" |
| Selection spans paragraphs | Treated as multiple words; context is taken from the first and last sentences only |
| Caret in a URL or code | Word under caret resolves as usual; no special casing in v1 |
| Non-text selection | All actions disabled, notice "no text selected" |
| IME composition active | Hotkey ignored until composition ends |
| Field changes between probe and apply | Apply aborts with "the field changed", nothing is pasted |

## 7. Implementation map

```json
{
  "defaults": {
    "none":        ["compose", "custom_prompt"],
    "single_word": ["proofread", "translate", "synonyms"],
    "multi_word":  ["proofread", "rewrite", "translate", "prompt_optimize"]
  },
  "more": {
    "none":        ["synonyms", "antonyms", "translate", "proofread", "rewrite", "prompt_optimize"],
    "single_word": ["antonyms", "rewrite", "compose", "prompt_optimize", "custom_prompt"],
    "multi_word":  ["synonyms", "antonyms", "compose", "custom_prompt"]
  },
  "options": {
    "rewrite":  { "include_context": true, "context_scope": "selection_surroundings" },
    "compose":  { "include_context": true, "context_scope": "caret_surroundings" },
    "translate": { "target_language": "last_used", "source_language": "auto" }
  }
}
```

The action ids must match the engine registry: `proofread`, `rewrite`, `compose`, `translate`, `synonyms`. `prompt_optimize`, `antonyms` and `custom_prompt` need either a new engine action or a composed prompt; that is a separate task.
