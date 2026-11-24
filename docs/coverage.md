# App coverage

Fill one row per app, per release. A platform ships when 80% of these rows pass both states.

Grading: `pass`, `partial`, `fail`. Note the mechanism that failed, not just the result.

## Windows

| App | Selection read | Caret read | Insert works | Notes |
|---|---|---|---|---|
| Notepad | | | | |
| Notepad++ | | | | |
| Microsoft Word | | | | |
| Microsoft Outlook (compose) | | | | |
| Microsoft Teams (compose) | | | | |
| Google Chrome (textarea) | | | | |
| Google Chrome (contenteditable) | | | | |
| Microsoft Edge (contenteditable) | | | | |
| Slack desktop | | | | |
| Visual Studio Code | | | | |
| Windows Explorer rename box | | | | |

## macOS

Empty until the macOS shell exists.

## How to test a row

1. Put the app in a normal window, not full screen.
2. Select a sentence, press the hotkey, run `proofread`. Check that the panel opens in selection mode.
3. Undo the insert, then check the text returned to the original.
4. Click into an empty field, press the hotkey. Check that the panel opens in compose mode, not selection mode.
5. Paste a sentence, put the caret mid-word, run `rewrite`. Check that only the selection changes, never the whole field.
6. Record the app version. Accessibility behaviour changes between releases.

## Measured on 2026-11-23, Windows 11

Probe: `shell/windows/Emendly.Probe`, UI Automation control view.

| App | Selection read | Caret read | Verdict from probe | Note |
|---|---|---|---|---|
| Notepad (classic) | pending | pass | `caret` | `TextPattern` on `Document`, caret offset 484 of 512 chars, not read-only |
| Google Chrome (page) | pass | pass | `read_only` | `Document` exposes `TextPattern`; selection length 43; the page is read-only until a field has focus |
| Notepad3 | fail | fail | `unsupported` | Scintilla exposes no text element to UIA. Expect the same for Notepad++ |
| Windows Settings | fail | fail | `unsupported` | No text element, as expected |

Consequence: Scintilla-based editors need the clipboard fallback path before they can be supported.
