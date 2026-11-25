using System;
using System.Collections.Generic;
using Emendly.Capture;

namespace Emendly.App;

public enum ActionKind
{
    Replace,
    Insert,
    ReferenceList,
    Custom,
}

public enum ActionOptionKind
{
    None,
    Context,
    Language,
}

public sealed record MenuAction(
    string Id,
    string Label,
    string Description,
    ActionKind Kind,
    ActionOptionKind Options);

public sealed record MenuGroup(
    string Label,
    IReadOnlyList<MenuAction> Items);

public static class MenuModel
{
    public static readonly MenuAction Proofread = new(
        "proofread",
        "Proofread",
        "Fix spelling, grammar, and punctuation",
        ActionKind.Replace,
        ActionOptionKind.None);

    public static readonly MenuAction Rewrite = new(
        "rewrite",
        "Rewrite",
        "Improve clarity and flow",
        ActionKind.Replace,
        ActionOptionKind.Context);

    public static readonly MenuAction Humanize = new(
        "humanize",
        "Humanize",
        "Make phrasing read more naturally",
        ActionKind.Replace,
        ActionOptionKind.None);

    public static readonly MenuAction Translate = new(
        "translate",
        "Translate",
        "Translate to target language",
        ActionKind.Replace,
        ActionOptionKind.Language);

    public static readonly MenuAction PromptOptimize = new(
        "prompt_optimize",
        "Prompt Optimize",
        "Clarify a prompt for more reliable results",
        ActionKind.Replace,
        ActionOptionKind.None);

    public static readonly MenuAction Synonyms = new(
        "synonyms",
        "Synonyms",
        "List up to five concise synonyms",
        ActionKind.ReferenceList,
        ActionOptionKind.None);

    public static readonly MenuAction Antonyms = new(
        "antonyms",
        "Antonyms",
        "List up to five concise antonyms",
        ActionKind.ReferenceList,
        ActionOptionKind.None);

    public static readonly MenuAction Compose = new(
        "compose",
        "Compose",
        "Draft new text from a brief",
        ActionKind.Insert,
        ActionOptionKind.Context);

    public static readonly MenuAction Custom = new(
        "custom",
        "Custom Prompt…",
        "Run a free-text instruction",
        ActionKind.Custom,
        ActionOptionKind.None);

    private static readonly IReadOnlyList<MenuAction> EmptyDefaults = new[]
    {
        Compose,
        Custom,
    };

    private static readonly IReadOnlyList<MenuAction> WordDefaults = new[]
    {
        Proofread,
        Translate,
        Synonyms,
    };

    private static readonly IReadOnlyList<MenuAction> PassageDefaults = new[]
    {
        Proofread,
        Rewrite,
        Translate,
        Humanize,
        PromptOptimize,
    };

    private static readonly IReadOnlyList<MenuGroup> ExpandedGroups = new[]
    {
        new MenuGroup("Improve", new[] { Proofread, Rewrite, Humanize }),
        new MenuGroup("Change", new[] { Translate, PromptOptimize }),
        new MenuGroup("Words", new[] { Synonyms, Antonyms }),
        new MenuGroup("Create", new[] { Compose, Custom }),
    };

    private static readonly Dictionary<string, MenuAction> ActionsById = new(StringComparer.Ordinal)
    {
        [Proofread.Id] = Proofread,
        [Rewrite.Id] = Rewrite,
        [Humanize.Id] = Humanize,
        [Translate.Id] = Translate,
        [PromptOptimize.Id] = PromptOptimize,
        [Synonyms.Id] = Synonyms,
        [Antonyms.Id] = Antonyms,
        [Compose.Id] = Compose,
        [Custom.Id] = Custom,
    };

    public static readonly IReadOnlyList<string> DefaultLanguages = Array.AsReadOnly(new[]
    {
        "German",
        "English",
        "Spanish",
        "French",
        "Italian",
        "Portuguese",
        "Dutch",
        "Polish",
        "Russian",
        "Chinese (Simplified)",
        "Chinese (Traditional)",
        "Japanese",
        "Korean",
        "Arabic",
        "Hindi",
        "Turkish",
        "Swedish",
        "Norwegian",
        "Danish",
        "Finnish",
        "Czech",
        "Greek",
        "Hebrew",
        "Vietnamese",
        "Thai",
        "Indonesian",
        "Ukrainian",
    });

    private static readonly Dictionary<string, string> LanguageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["German"] = "DE",
        ["English"] = "EN",
        ["Spanish"] = "ES",
        ["French"] = "FR",
        ["Italian"] = "IT",
        ["Portuguese"] = "PT",
        ["Dutch"] = "NL",
        ["Polish"] = "PL",
        ["Russian"] = "RU",
        ["Chinese (Simplified)"] = "ZH",
        ["Chinese (Traditional)"] = "ZH",
        ["Japanese"] = "JA",
        ["Korean"] = "KO",
        ["Arabic"] = "AR",
        ["Hindi"] = "HI",
        ["Turkish"] = "TR",
        ["Swedish"] = "SV",
        ["Norwegian"] = "NO",
        ["Danish"] = "DA",
        ["Finnish"] = "FI",
        ["Czech"] = "CS",
        ["Greek"] = "EL",
        ["Hebrew"] = "HE",
        ["Vietnamese"] = "VI",
        ["Thai"] = "TH",
        ["Indonesian"] = "ID",
        ["Ukrainian"] = "UK",
    };

    public static string GetLanguageCode(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "??";
        }

        var trimmed = language.Trim();
        if (LanguageCodes.TryGetValue(trimmed, out var code))
        {
            return code;
        }

        var letters = new char[2];
        var count = 0;
        foreach (var ch in trimmed)
        {
            if (char.IsLetter(ch))
            {
                letters[count++] = char.ToUpperInvariant(ch);
                if (count == 2)
                {
                    return new string(letters);
                }
            }
        }

        return count == 1 ? new string(letters, 0, 1) : "??";
    }

    public static IReadOnlyList<MenuAction> GetDefault(SelectionState state) =>
        state switch
        {
            SelectionState.Empty => EmptyDefaults,
            SelectionState.Word => WordDefaults,
            SelectionState.Passage => PassageDefaults,
            _ => PassageDefaults,
        };

    public static IReadOnlyList<MenuGroup> GetExpanded() => ExpandedGroups;

    public static IReadOnlyList<MenuGroup> FilterExpanded(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return ExpandedGroups;
        }

        var trimmed = prefix.Trim();
        var filtered = new List<MenuGroup>();

        foreach (var group in ExpandedGroups)
        {
            var matches = new List<MenuAction>();
            foreach (var item in group.Items)
            {
                if (item.Label.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(item);
                }
            }

            if (matches.Count > 0)
            {
                filtered.Add(new MenuGroup(group.Label, matches));
            }
        }

        return filtered;
    }

    public static ActionOptionKind GetOptions(string actionId) =>
        actionId switch
        {
            "rewrite" or "compose" => ActionOptionKind.Context,
            "translate" => ActionOptionKind.Language,
            _ => ActionOptionKind.None,
        };

    public static MenuAction? FindById(string actionId) =>
        ActionsById.TryGetValue(actionId, out var action) ? action : null;

    public static IReadOnlyList<string> GetLanguages(string? currentLanguage)
    {
        if (string.IsNullOrWhiteSpace(currentLanguage))
        {
            return DefaultLanguages;
        }

        var trimmed = currentLanguage.Trim();
        foreach (var lang in DefaultLanguages)
        {
            if (string.Equals(lang, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return DefaultLanguages;
            }
        }

        var list = new List<string>(DefaultLanguages.Count + 1) { trimmed };
        list.AddRange(DefaultLanguages);
        return list;
    }

    public static int FindLanguageIndex(IReadOnlyList<string> languages, string? current)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return 0;
        }

        var trimmed = current.Trim();
        for (var i = 0; i < languages.Count; i++)
        {
            if (string.Equals(languages[i], trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }
}
