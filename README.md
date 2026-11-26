<p align="center">
  <img src="docs/media/menu.png" width="300" alt="Emendly action menu over the focused text field" />
</p>

<h1 align="center">Emendly</h1>

<p align="center">
  A writing assistant that lives in every app — and only wakes when you ask.
</p>

<p align="center">
  <img alt="platform Windows" src="https://img.shields.io/badge/platform-Windows-2563EB?style=flat-square" />
  <img alt="status pre-alpha" src="https://img.shields.io/badge/status-pre--alpha-C9762B?style=flat-square" />
  <img alt="engine Rust" src="https://img.shields.io/badge/engine-Rust-B7410E?style=flat-square" />
  <img alt="licence MIT" src="https://img.shields.io/badge/licence-MIT-3F8F5A?style=flat-square" />
</p>

<p align="center">
  <a href="site/index.html">Website</a> ·
  <a href="https://github.com/pwwang/emendly/releases/latest">Download</a> ·
  <a href="site/privacy.html">Privacy</a> ·
  <a href="#free-vs-pro">Pro</a>
</p>

---

Select a sentence in any app, press **Ctrl+Alt+E**, and Emendly offers the actions that fit what it sees. Nothing is read until you press the key, and nothing is sent anywhere except the model provider you configured.

## Why another writing assistant

- **System-wide, not browser-only.** Emendly reads the focused field through UI Automation, Scintilla messages or the clipboard, with per-app rules for the apps that hide their text from the accessibility tree.
- **On demand by design.** No background scanning, no keystroke logging, no suggestions popping up while you type.
- **The menu fits the selection.** One word gets proofreading and synonyms; a passage gets rewrite and translation; an empty field gets compose. Nine actions in total.
- **Bring your own model.** Any OpenAI-compatible endpoint, including a local Ollama or LM Studio server, with your own key.

## What it looks like

<table>
  <tr>
    <td align="center" width="50%">
      <img src="docs/media/menu.png" alt="Menu with five passage actions" /><br />
      <sub><b>The menu fits the selection.</b><br />A passage gets five actions; a single word gets three.</sub>
    </td>
    <td align="center" width="50%">
      <img src="docs/media/streaming.png" alt="Streaming a proofread result" /><br />
      <sub><b>Results stream in place.</b><br />Read-only while generating, then Replace, Copy or Back.</sub>
    </td>
  </tr>
  <tr>
    <td align="center" width="50%">
      <img src="docs/media/result.png" alt="Finished proofread result with Replace and Back" /><br />
      <sub><b>Enter replaces the selection</b> through the clipboard, then restores focus to the app you came from.</sub>
    </td>
    <td align="center" width="50%">
      <img src="docs/media/settings.png" alt="Settings window" /><br />
      <sub><b>Shortcut, language, endpoint, model.</b><br />Set the hotkey by pressing it, not by typing its name.</sub>
    </td>
  </tr>
</table>

## The nine actions

| Action | What it does | Needs |
|---|---|---|
| **Proofread** | Spelling, grammar and punctuation | a selection |
| **Rewrite** | Clearer wording, with an optional surrounding-context toggle | a selection |
| **Humanize** | Makes model-flavoured prose read naturally | a selection |
| **Prompt Optimize** | Turns a rough prompt into a precise one | a selection |
| **Translate** | Into the target language, chosen from a dropdown or typed freehand | a selection |
| **Synonyms** / **Antonyms** | Up to five concise alternatives | a single word |
| **Compose** | Drafts new text from a brief, optionally using the surrounding sentences | an empty caret |
| **Custom Prompt** | Free-text instruction against the current field | anywhere |

Actions are also reachable with the number keys, and `Tab` expands the full grouped list with a filter box.

## How it decides what to offer

| What Emendly sees | What you get |
|---|---|
| Empty caret | Compose, Custom Prompt |
| One word selected | Proofread, Translate, Synonyms |
| A sentence or passage | Proofread, Rewrite, Translate, Humanize, Prompt Optimize |
| Read-only text (PDF viewers, terminals) | the same menu, except results can only be copied, not inserted |

Read-only windows are detected rather than guessed: Emendly never pastes into a field that cannot take text.

## How it reads text without a browser

Windows apps hide their text in different ways, so the shell tries strategies in order and remembers what worked per application:

| Strategy | Used for | Example |
|---|---|---|
| **UI Automation** | accessible Win32 and Chromium surfaces | Chrome, Edge, most native apps |
| **Scintilla messages** | `WM_COPY` where the control answers it | Notepad3, Notepad++ |
| **Clipboard probe** | surfaces that publish nothing to UIA | VS Code, Tauri/WebView2 apps, terminals |

`app-rules.json` pins a strategy per executable when the automatic chain guesses wrong, and a `clipboard-nokeys` mode exists so a terminal is never sent a `Ctrl+C` that would read as SIGINT.

## Keyboard

| Key | Action |
|---|---|
| `Ctrl+Alt+E` | open or close the panel (configurable) |
| `↑` `↓` | move between actions |
| `1`–`9` | run an action directly |
| `Tab` / `←` | expand or collapse the full list |
| `Enter` | run, replace, or retry after a failure |
| `Esc` | back one step, then close |

The panel stays open when you click elsewhere: only an explicit close dismisses it.

## Install

Grab the latest build from [GitHub Releases](https://github.com/pwwang/emendly/releases/latest):

- **Windows installer** — `Emendly-vX.Y.Z-win-x64-setup.exe`. Installs per-user to `%LOCALAPPDATA%\Programs\Emendly`, no admin prompt, and adds a Start Menu shortcut and uninstaller.
- **Portable zip** — `Emendly-vX.Y.Z-win-x64.zip`. Unpack anywhere; the engine and its prompt templates sit beside `Emendly.App.exe`.

Or install with winget (manifest lives in `packaging/winget/`):

```sh
winget install pwwang.Emendly
```

After installing, set your endpoint and model in **Settings** and point `EMENDLY_API_KEY` at your provider key (see [Build it](#build-it) for the details).

Installed builds can check GitHub Releases for a newer version on startup, at most once every 24 hours. The check sends only a versioned User-Agent; turn it off in **Settings**.

## Build it

Requires the .NET 9 SDK, the Rust toolchain, and a Windows toolchain for the sidecar.

```sh
# engine
cargo build --release --manifest-path engine/Cargo.toml

# shell
dotnet build shell/windows/Emendly.sln -c Release
```

Run the app, set your endpoint and model in **Settings**, and export a key:

```sh
set EMENDLY_API_KEY=sk-...
```

The key is read from the environment and never written to disk. Settings live in `%APPDATA%\Emendly\settings.json`.

## Free vs Pro

The core engine and Windows shell in this repository are **MIT, forever**: all nine actions, all three capture strategies, selection and caret branching, read-only detection, and bring-your-own-key against any OpenAI-compatible endpoint or a local Ollama / LM Studio server.

An optional closed-source **Emendly Pro** build ($24 one-time, 14-day free trial) adds workflow features on top of the same engine, under the extension hooks in `shell/windows/Emendly.App/Extensions/` and `docs/tasks/0018-engine-extension-hooks.md`:

- Saved Prompt Library with per-prompt hotkeys
- Visual per-app capture profile editor
- Searchable local run history
- Custom terminology glossary
- Custom tone presets
- Action chaining and multi-paragraph batch runs
- `emendly-cli` for scripting and editor integration
- 12 months of priority email support

Pro is **also BYOK**: the $24 buys the software, not API usage, so there is no subscription and your text never passes through an Emendly server in either tier.

## Privacy

- Text is read only after the hotkey, and only from the focused field.
- Logs record action names, app names, latency and byte counts — never your text.
- Requests go to the endpoint you configure. There is no Emendly service in the path.

The full policy, including every local file Emendly writes, is in [`site/privacy.html`](site/privacy.html).

## Status

Windows shell and Rust engine are working end to end: capture, menu, streaming results, replace, settings, tray, and a release pipeline that ships an installer, a portable zip and a winget manifest. A native Swift menu-bar shell for macOS lives in `shell/macos/`; the Rust engine builds on Windows, macOS and Linux, and the protocol is platform-neutral by design.

## Layout

| Path | What lives there |
|---|---|
| `engine/` | Rust sidecar: actions, prompt templates, providers, streaming, token accounting |
| `shell/windows/` | WPF shell: capture strategies, panel, settings, tray |
| `shell/macos/` | Swift menu-bar shell for macOS |
| `packaging/winget/` | winget manifest for `winget install pwwang.Emendly` |
| `site/` | Static landing page and privacy policy, deployed to GitHub Pages |
| `docs/` | protocol, architecture, app coverage, decision records, signing guide |

## Licence

MIT. See [`LICENSE`](LICENSE).
