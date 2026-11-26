# Task 0023: Rewrite styles, modelled on Grammarly's paraphraser

Status: ready
Depends on: task 0022

## Why

`rewrite` today takes a free-text `tone` option but the panel never sends one — `Rewrite` is declared
`ActionOptionKind.Context`, which only toggles surrounding-sentence context. So "Rewrite" behaves almost
exactly like "Proofread": no way to say *how* to rewrite. And `Humanize` and `Prompt Optimize` are separate
top-level menu items that are really two more ways of rewriting.

Grammarly's paraphraser solves this with a horizontal row of **writing styles** above the result
(Humanize, Academic, Professional, Streamlined, Creative, Simplify), plus tone filters. Emendly should
copy that shape: one `Rewrite` entry with a style picker, where Humanize and Prompt Optimize are two of
the styles rather than separate top-level actions.

## Environment

- No .NET SDK in WSL. Do NOT run `dotnet`, and do NOT run `git`. Build is the parent's job.
- Work ONLY inside `shell/windows/`. Do not touch `engine/`, `site/` or docs.

## 1. Styles as data, in one place

Add `shell/windows/Emendly.App/RewriteStyles.cs`:

```csharp
public sealed record RewriteStyle(string Id, string Label, string Description, string Instructions);
public static class RewriteStyles
{
    public static IReadOnlyList<RewriteStyle> All { get; }
    public static RewriteStyle Default { get; }            // "improve"
    public static RewriteStyle? FindById(string? id);
    public static string LabelOf(string? id);              // for hints/logs
}
```

Styles, each with an `Instructions` fragment appended to the engine's own rewrite system prompt:

| Id | Label | Intent |
|---|---|---|
| `improve` | Improve | Clearer and more natural, same meaning (**default**) |
| `humanize` | Humanize | Natural human voice, varied rhythm, no stock phrasing |
| `professional` | Professional | Workplace register, no slang, no exclamation marks |
| `academic` | Academic | Formal scholarly register, precise and hedged |
| `concise` | Streamline | Shorter, tighter, same information |
| `creative` | Creative | More vivid phrasing while staying truthful |
| `simple` | Simplify | Plain language, short sentences, common words |
| `friendly` | Friendly | Warm and approachable |
| `confident` | Confident | Direct and assertive, no hedging |
| `diplomatic` | Diplomatic | Tactful, softens disagreement |
| `prompt_optimize` | Prompt Optimize | Improve a *prompt* instead of prose; do not answer it |

`Instructions` must be a full sentence telling the model how to rewrite. Keep them one line each.
`prompt_optimize` is the odd one: its instruction must say the user's text is a prompt for an AI model,
to clarify task/constraints/expected output, and not to answer it.

## 2. Presentation

Change `MenuModel.Rewrite` to use a new option kind:

- Add `ActionOptionKind.Style` next to `Context` and `Language`.
- `Rewrite` becomes `ActionOptionKind.Style` with description "Rewrite in a chosen style".
- **Remove the top-level `Humanize` and `PromptOptimize` menu items** and their `ActionsById` entries.
  They live in the style list now. Delete their `MenuAction` fields.
- `ExpandedGroups` "Improve" becomes `new[] { Proofread, Rewrite }`.
- `PassageDefaults` becomes `{ Proofread, Rewrite, Translate }` — dropping two entries keeps the
  default 5-item menu the same height it is now.

Do not renumber or reorder anything else.

## 3. The style picker in the panel

Mirror the existing language dropdown exactly; do not invent a new look.

- `PanelWindow.xaml`: an overlay `Canvas x:Name="StyleDropdownOverlay"` + `ComboBox x:Name="StyleDropdown"`
  beside the language one, reusing `LanguageDropdownStyle` from `Themes/LanguageDropdown.xaml`.
  Its watermark should read `Pick a style`.
- Row option button for `Style`: reuse the globe-style icon slot but with a different glyph. Add a
  `StyleIconGeometry` to `PanelWindow.xaml` resources — a simple sparkle/pen nib path, 16x16, stroked
  with `IconAccentBrush`. Where `Language` shows a 2-letter code under the globe, `Style` shows the
  selected style's short label (first 3 chars uppercase, e.g. `HUM`, `PRO`, `SIM`).
- `PanelWindow.xaml.cs`: `OpenStyleDropdown` / `PositionStyleDropdown` / `CloseStyleDropdown` /
  `OnStyleDropdownClosed`, copied from the language equivalents, anchored to the style row's button.
  `ActivateOption` gains a `case ActionOptionKind.Style: OpenStyleDropdown(); break;`.
  `ConfigureRowOption` gains a Style branch (always enabled; tooltip `Rewrite style: <Label>`).
  `UpdateMenuHints`: for Style, `"Enter/Space picks · Esc back"` like Language.

## 4. Wiring the choice

- `AppSettings`: add `[JsonPropertyName("rewrite_style")] public string? RewriteStyle { get; set; }`
  and `GetRewriteStyle()` returning the stored id if valid else `RewriteStyles.Default.Id`.
  Follow the same read/write pattern as `TargetLanguage`.
- `PanelWindow`: keep a `_rewriteStyle` field initialised from settings on `ApplySavedSettings`, so a
  change in Settings is reflected. Persist on pick, like the language does.

## 5. Build the request

In `BuildOptions`, add the Style branch:

```csharp
else if (action.Options == ActionOptionKind.Style)
{
    var style = RewriteStyles.FindById(_rewriteStyle) ?? RewriteStyles.Default;
    if (!forceOptionsOff)
    {
        options["system_suffix"] = style.Instructions;
        if (style.Id == "prompt_optimize") options["user_template_override"] = "Prompt to improve:\n{{text}}";
        logParts.Add($"style={style.Id}");
    }
    else logParts.Add("style=default");
}
```

`system_suffix` is new; see §6. Keep the rest of `BuildOptions` untouched.

## 6. Engine: accept the style instructions

`engine/src/protocol.rs` `ActionOptions` and `shell/windows/Emendly.Core/EngineRunner.cs`
`EngineRequest` / `BuildActionRunJson`:

- Add an optional `system_suffix` string, serialized as `system_suffix`, read on the Rust side and
  appended AFTER the action's base system prompt (that ordering matters: the style adjusts the base
  instruction rather than replacing it).
- In `engine/src/actions.rs` `assemble_system_prompt`, push `system_suffix` last, after `base`.
- In `EngineTransformer.cs`, map the request option through.

Keep it backward compatible: absent means no change in behaviour.

## 7. Hub

`HubWindow.xaml.cs`: `ChainStepActionCombo` and `BatchActionCombo` currently list `humanize` and
`prompt_optimize` as actions. Drop `prompt_optimize` from both — it is a rewrite style now. Leave
`humanize` in place so existing saved chains keep working.

Do NOT add a style picker to the chain step editor in this task; that is a separate change and would
bloat this one. Note in your report that chain steps do not yet expose a style.

## Acceptance

1. `dotnet build shell/windows/Emendly.sln -c Release` clean (parent runs it; you cannot).
2. No `Humanize` or `PromptOptimize` top-level menu entries remain in `MenuModel`.
3. `grep -ri "prompt_optimize" shell/windows/Emendly.App` finds it only in `RewriteStyles.cs` and combos.
4. Nine or more styles in `RewriteStyles.All`, `improve` is `Default`.

## When done

Report: the style list you shipped, where the dropdown anchors, what you chose for §7, and any file you
had to touch outside `shell/windows/`.
