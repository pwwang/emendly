using System;
using System.Collections.Generic;

namespace Emendly.App;

public sealed record RewriteStyle(
    string Id,
    string Label,
    string Description,
    string Instructions);

public static class RewriteStyles
{
    public static IReadOnlyList<RewriteStyle> All { get; } = Array.AsReadOnly(new[]
    {
        new RewriteStyle(
            "improve",
            "Improve",
            "Clearer and more natural, same meaning",
            "Rewrite the text so it is clearer, smoother, and more natural while preserving the original meaning."),
        new RewriteStyle(
            "humanize",
            "Humanize",
            "Natural human voice, varied rhythm, no stock phrasing",
            "Rewrite the text in a natural human voice with varied sentence rhythm and no stock or overly formal phrasing."),
        new RewriteStyle(
            "professional",
            "Professional",
            "Workplace register, no slang, no exclamation marks",
            "Rewrite the text in a polished professional workplace register without slang or exclamation marks."),
        new RewriteStyle(
            "academic",
            "Academic",
            "Formal scholarly register, precise and hedged",
            "Rewrite the text in a formal scholarly register with precise terminology and measured academic hedging."),
        new RewriteStyle(
            "concise",
            "Streamline",
            "Shorter, tighter, same information",
            "Rewrite the text to be shorter and tighter while keeping all of the original information."),
        new RewriteStyle(
            "creative",
            "Creative",
            "More vivid phrasing while staying truthful",
            "Rewrite the text with more vivid, engaging phrasing while staying truthful to the original meaning."),
        new RewriteStyle(
            "simple",
            "Simplify",
            "Plain language, short sentences, common words",
            "Rewrite the text in plain language using short sentences and common everyday words."),
        new RewriteStyle(
            "friendly",
            "Friendly",
            "Warm and approachable",
            "Rewrite the text in a warm, friendly, and approachable tone."),
        new RewriteStyle(
            "confident",
            "Confident",
            "Direct and assertive, no hedging",
            "Rewrite the text in a direct, confident, and assertive tone without hedging."),
        new RewriteStyle(
            "diplomatic",
            "Diplomatic",
            "Tactful, softens disagreement",
            "Rewrite the text in a tactful, diplomatic tone that softens disagreement while preserving the core message."),
        new RewriteStyle(
            "prompt_optimize",
            "Prompt Optimize",
            "Improve a prompt instead of prose; do not answer it",
            "Treat the user's text as a prompt for an AI model and rewrite it to clarify the task, constraints, and expected output format without answering the prompt."),
    });

    public static RewriteStyle Default => All[0];

    public static IReadOnlyList<string> Labels { get; } = BuildLabels();

    private static readonly Dictionary<string, RewriteStyle> StylesById = BuildById();

    public static RewriteStyle? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return StylesById.TryGetValue(id.Trim(), out var style) ? style : null;
    }

    public static RewriteStyle? FindByIdOrLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (StylesById.TryGetValue(trimmed, out var byId))
        {
            return byId;
        }

        foreach (var style in All)
        {
            if (string.Equals(style.Label, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return style;
            }
        }

        foreach (var style in All)
        {
            if (style.Label.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return style;
            }
        }

        return null;
    }

    public static string LabelOf(string? id) => (FindById(id) ?? Default).Label;

    public static string ShortLabelOf(string? id)
    {
        var label = LabelOf(id).Trim();
        return (label.Length >= 3 ? label[..3] : label).ToUpperInvariant();
    }

    public static int FindIndex(string? id)
    {
        var resolved = FindById(id) ?? Default;
        for (var i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i].Id, resolved.Id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 0;
    }

    private static IReadOnlyList<string> BuildLabels()
    {
        var labels = new string[All.Count];
        for (var i = 0; i < All.Count; i++)
        {
            labels[i] = All[i].Label;
        }

        return Array.AsReadOnly(labels);
    }

    private static Dictionary<string, RewriteStyle> BuildById()
    {
        var map = new Dictionary<string, RewriteStyle>(All.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var style in All)
        {
            map[style.Id] = style;
        }

        return map;
    }
}
