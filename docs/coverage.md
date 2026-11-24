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
