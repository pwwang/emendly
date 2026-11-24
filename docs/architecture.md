# Architecture

## Three parts

| Part | Runs where | Owns | Written in |
|---|---|---|---|
| Shell | one per OS | hotkey, focus capture, overlay UI, insert, clipboard safety | C# on Windows, Swift on macOS, GTK later |
| Adapter | inside the shell | reading the focused field, writing text back | UI Automation on Windows, AXAPI on macOS, AT-SPI2 on Linux |
| Engine | one process, every OS | actions, prompts, providers, streaming, token accounting | Rust |

The shell never talks to a model provider. The engine never touches the OS.

## Request path

1. The user presses the hotkey.
2. The shell asks the adapter what is focused: a selection, an empty editable caret, or neither.
3. The shell shows the matching panel and starts the engine request on the user's choice.
4. The engine streams `delta` events back.
5. On `done`, the shell writes the replacement through the adapter, then stores an undo buffer.
6. Any failure shows a visible notice. Silent failure is a bug.

## Why the capture layer is per platform

There is no portable API for "the text field that has focus". Each OS exposes a different tree, and each has different write-back rules.

| Capability | Windows | macOS | Linux |
|---|---|---|---|
| Focused element | UI Automation `GetFocusedElement` | `AXUIElement` on the system-wide element | AT-SPI2 over D-Bus |
| Selection | `TextPattern` selection range | `kAXSelectedText` | AT-SPI2 `Text` interface |
| Empty caret | `TextPattern` with a collapsed range, or `TextEditPattern` | `kAXSelectedTextRange` with zero length | usually unavailable |
| Write back | `TextEditPattern.InsertTextAtSelection`, else paste | `AXValue` set, else paste | XTest keys, else paste |
| Permission | none | Accessibility, granted in System Settings | none on X11, portal consent on Wayland |

## Order of work

1. Engine, with tests against a mock provider. This repository, now.
2. Windows adapter and shell, covering five apps in a coverage table.
3. macOS shell, reusing the engine unchanged.
4. Linux, X11 first.

## Non-goals for v1

Continuous checking, background scanning, browser extensions, team sync, accounts, and payments. Each one is a separate decision record.
