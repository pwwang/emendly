# Task 0019: Distribution — Landing Page (`site/`), Privacy Policy & README Polish

Status: ready
Depends on: task 0016

## Environment & Scope

Do NOT run any `git` commands; the parent reviews and commits.
You MAY use `Bash` only for file inspection / copying screenshots from `docs/media/` into `site/assets/` and `shell/windows/Emendly.App/Assets/emendly-256.png` into `site/assets/`.
Work ONLY inside:
- `site/` (new directory)
- `.github/workflows/pages.yml` (new file)
- `README.md`

Do NOT touch `engine/`, `shell/`, `packaging/`, `docs/`, `.github/workflows/ci.yml`, or `.github/workflows/release.yml`.

## 1. Static Landing Page (`site/`)

Build a crisp, fast, zero-build-step static site ready for GitHub Pages and `emendly.dev`.

### Design Direction
- Match Emendly's actual desktop chrome: deep obsidian surface (`#0D0E12`, `#141419`, `#1C1D25`), hairline borders (`#2A2C38`), crisp typography (system geometric sans + monospace for shortcuts/commands), electric blue primary accent (`#2563EB` / `#60A5FA`), subtle emerald accents (`#34D399`) for privacy/checks.
- High-craft editorial density — NOT generic AI slop, NOT rainbow neon gradients, NOT fake testimonials or invented metrics. Every claim on the page must be true about Emendly as shipped!
- Copy `docs/media/menu.png`, `docs/media/streaming.png`, `docs/media/result.png`, `docs/media/settings.png`, and `shell/windows/Emendly.App/Assets/emendly-256.png` into `site/assets/`.
- Responsive down to mobile (375px) and clean on 1440px desktop.

### `site/index.html` Sections
1. **Top Navigation**: Brand icon (`assets/emendly-256.png`) + `Emendly` wordmark, anchor links (`How it works`, `Actions`, `Free vs Pro`, `Privacy`), `GitHub` link, and a `Download` button.
2. **Hero**:
   - Eyebrow pill: `Windows 10/11 · macOS shell in repo · Bring Your Own Key`
   - Headline: `A writing assistant that lives in every app — and only wakes when you ask.`
   - Subheadline: `Select text anywhere, press Ctrl+Alt+E, and choose an action. Zero background scanning. Your text goes straight to your own OpenAI-compatible provider or local Ollama server.`
   - Primary CTA: `Download for Windows` (`https://github.com/pwwang/emendly/releases/latest`)
   - Secondary CTA: `View on GitHub` (`https://github.com/pwwang/emendly`)
   - Copyable command bar: `winget install pwwang.Emendly` with working Copy button (`Copied!`).
   - Hero visual: crisp framed showcase with interactive tab pills switching between the 4 real screenshots (`Action menu`, `Live streaming`, `Replace in place`, `Settings`) with accurate captions.
3. **Why Emendly (4 crisp cards)**:
   - System-wide capture (UI Automation, Scintilla messages, clipboard probe + per-app rules)
   - On-demand by design (zero keylogging, zero idle polling)
   - Selection-aware menu (one word, passage, empty caret, and read-only detection)
   - Bring your own model (OpenAI, Anthropic proxy, Ollama, LM Studio — your key stays in env/keychain)
4. **The 9 Built-in Actions**: Compact table/grid matching `README.md` (`Proofread`, `Rewrite`, `Humanize`, `Prompt Optimize`, `Translate`, `Synonyms`, `Antonyms`, `Compose`, `Custom Prompt`) plus the selection-state branching summary.
5. **Free (MIT) vs Pro ($24 one-time, BYOK)**:
   - Side-by-side pricing/comparison cards + feature comparison table matching the exact Free/Pro split:
     - **Free — $0 forever (MIT)**: All 9 actions, all 3 capture strategies, selection vs caret branching, read-only detection, BYOK (any OpenAI-compatible endpoint / Ollama), global hotkey & settings, open source on GitHub.
     - **Pro — $24 one-time (BYOK, 14-day free trial)**: Everything in Free plus Saved Prompt Library with per-prompt hotkeys, Visual Per-App Capture Profiles editor, Searchable Local Run History, Custom Terminology Glossary, Custom Tone Presets, Action Chaining & Multi-Paragraph Batch Runs, `emendly-cli` for scripting & editors, 12 months of priority email support.
   - Include a clear honest note right under the cards: *"BYOK for both tiers: the $24 one-time licence buys the Pro desktop software — you bring your own API key (or local model) so there is no subscription and your text never routes through our servers."*
   - Pro CTA button links to `#pro-checkout` / configurable `const PRO_CHECKOUT_URL` in `site/script.js` (defaulting to GitHub Releases / Polar.sh placeholder with a clean modal or notice if not yet configured so there are no broken 404 clicks!).
6. **Privacy Guarantee**: Concise summary + link to `privacy.html`.
7. **FAQ (clean `<details>` accordion)**:
   - Is API usage included in the $24 Pro licence? (No — BYOK keeps it one-time and private.)
   - Does Emendly scan what I type in the background? (Never. Strictly on-demand after the hotkey.)
   - Which apps are supported? (Browser, Word/Office, Notepad3/Notepad++, VS Code, terminals, PDF viewers in read-only Copy mode.)
   - Does it work completely offline with Ollama / LM Studio? (Yes — point Base URL to `http://localhost:11434/v1`.)
   - What about macOS and Linux? (Shared Rust engine builds for Windows, macOS, and Linux; native Swift macOS menu-bar shell lives in `shell/macos/`.)
8. **Footer**: `Emendly`, links to `privacy.html`, GitHub repo, MIT licence, Releases.

### `site/privacy.html`
Plain-English, legally clear, EU/GDPR-transparent privacy policy:
- What Emendly does NOT collect (no keystrokes, no document text, no analytics, no telemetry, no accounts).
- Where your text goes (only after pressing the hotkey, directly from your machine to the `base_url` you configure).
- Local files stored on your device (`%APPDATA%\Emendly\settings.json`, `%LOCALAPPDATA%\Emendly\app-rules.json`, optional metadata-only logs with zero user text; and in Pro, local-only `%APPDATA%\EmendlyPro\` prompts/glossary/history/licence key).
- API keys (read from `EMENDLY_API_KEY` env var or OS keychain, never stored in settings JSON).
- Optional update check (standard HTTPS GET to GitHub Releases API sending only `User-Agent: Emendly/<version>`; can be disabled in Settings).
- Offline Pro licence verification (ECDSA signature verified locally; no phone-home).
- Contact & effective date (`November 2026`, `pwwang@pwwang.com`, `https://github.com/pwwang/emendly/issues`).

### `site/styles.css`, `site/script.js`, `site/CNAME.example`, `site/README.md`
- `site/CNAME.example` containing `emendly.dev`
- `site/README.md` explaining GitHub Pages setup and custom domain DNS (`emendly.dev`).

## 2. GitHub Pages Workflow (`.github/workflows/pages.yml`)

- Triggers on `push` to `main` touching `site/**` and `workflow_dispatch`.
- Uses `actions/checkout@v4`, `actions/configure-pages@v5`, `actions/upload-pages-artifact@v3` (`path: site`), and `actions/deploy-pages@v4`.
- Proper `permissions: contents: read, pages: write, id-token: write` and `concurrency`.
- Valid YAML, zero tabs.

## 3. Polish `README.md`

Keep `README.md` concise, factual, and clean (do not bloat it). Add/update:
- Top links bar right under badges: `[Website](site/index.html)` · `[Download](https://github.com/pwwang/emendly/releases/latest)` · `[Privacy](site/privacy.html)` · `[Pro](#free-vs-pro)`
- An **Install** section above **Build it**:
  - Windows installer (`Emendly-vX.Y.Z-win-x64-setup.exe`) and portable zip from GitHub Releases
  - `winget install pwwang.Emendly` (with note on `packaging/winget/`)
  - Mention built-in update check (opt-outable in Settings)
- A concise **Free vs Pro** section explaining that the core repo is MIT forever with all 9 actions and BYOK, while the optional closed Pro build ($24 one-time, BYOK) adds saved prompts, visual app-rules editor, local searchable history, glossary, tone presets, chaining/batch, and CLI mode.
- Update **Status** and **Layout** tables to include `shell/macos/` (Swift menu-bar shell), `packaging/winget/`, `site/`, and `docs/signing.md`.

## Acceptance (run by parent)

1. `site/index.html` and `site/privacy.html` render cleanly in headless Chrome with all images loaded (no broken asset links) and zero console errors.
2. `.github/workflows/pages.yml` parses as valid YAML with zero tabs.
3. `README.md` links resolve to existing files in the repo.
