# 0002: WPF for the Windows shell, not WinUI

Date: 2026-11-23
Status: accepted

## Context

The shell needs a tray icon, a global hotkey, and a small always-on-top panel near the caret. WinUI 3 is the modern choice, but it brings the Windows App SDK runtime, a packaging story, and a restore that fails without elevation on a clean machine.

## Decision

Build the first shell in WPF on `net9.0-windows`. Add `UseWindowsForms` for the tray icon only.

## Consequences

- `dotnet build` and `dotnet run` work unpackaged, with no SDK runtime install and no elevation.
- The panel is a few hundred lines rather than a page of XAML plumbing.
- No Fluent visuals out of the box. Acceptable: the panel is a command palette, not a document window.
- The capture layer stays in its own assembly, so a WinUI shell can replace this one later without touching it.

## Alternatives rejected

- **WinUI 3.** Better look, heavier install, and a runtime dependency that breaks the first-run experience for a side project.
- **Electron.** 150 MB minimum, weak UI Automation access, and antivirus friction.
