using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Emendly.Capture;

public sealed record SurroundingContext(string Before, string After)
{
    public bool HasAny => Before.Length > 0 || After.Length > 0;
}

public static class ContextReader
{
    public const int MaxSentencesPerSide = 2;
    public const int MaxCharsPerSide = 400;
    private const int MaxRawScanChars = 2400;

    public static SurroundingContext? Read(string? before, string? after)
    {
        if (before is null && after is null)
        {
            return null;
        }

        var trimmedBefore = ExtractBefore(before ?? string.Empty);
        var trimmedAfter = ExtractAfter(after ?? string.Empty);
        return new SurroundingContext(trimmedBefore, trimmedAfter);
    }

    public static SurroundingContext? Read(TextPatternRange? selectionRange, TextPatternRange? documentRange)
    {
        if (selectionRange is null || documentRange is null)
        {
            return null;
        }

        var beforeOk = TryReadBefore(selectionRange, documentRange, out var rawBefore);
        var afterOk = TryReadAfter(selectionRange, documentRange, out var rawAfter);

        if (!beforeOk && !afterOk)
        {
            return null;
        }

        return Read(beforeOk ? rawBefore : string.Empty, afterOk ? rawAfter : string.Empty);
    }

    private static bool TryReadBefore(
        TextPatternRange selectionRange,
        TextPatternRange documentRange,
        out string rawBefore)
    {
        try
        {
            var probe = selectionRange.Clone();
            probe.MoveEndpointByRange(
                TextPatternRangeEndpoint.End,
                selectionRange,
                TextPatternRangeEndpoint.Start);

            var moved = probe.MoveEndpointByUnit(
                TextPatternRangeEndpoint.Start,
                TextUnit.Character,
                -MaxRawScanChars);

            if (moved < 0)
            {
                rawBefore = probe.GetText(-1) ?? string.Empty;
                return true;
            }

            if (probe.CompareEndpoints(
                    TextPatternRangeEndpoint.Start,
                    documentRange,
                    TextPatternRangeEndpoint.Start) == 0)
            {
                rawBefore = string.Empty;
                return true;
            }

            var prefix = documentRange.Clone();
            prefix.MoveEndpointByRange(
                TextPatternRangeEndpoint.End,
                selectionRange,
                TextPatternRangeEndpoint.Start);
            rawBefore = prefix.GetText(-1) ?? string.Empty;
            return true;
        }
        catch (Exception ex) when (IsUiaException(ex))
        {
            rawBefore = string.Empty;
            return false;
        }
    }

    private static bool TryReadAfter(
        TextPatternRange selectionRange,
        TextPatternRange documentRange,
        out string rawAfter)
    {
        try
        {
            var suffix = documentRange.Clone();
            suffix.MoveEndpointByRange(
                TextPatternRangeEndpoint.Start,
                selectionRange,
                TextPatternRangeEndpoint.End);
            rawAfter = suffix.GetText(MaxRawScanChars) ?? string.Empty;
            return true;
        }
        catch (Exception ex) when (IsUiaException(ex))
        {
            rawAfter = string.Empty;
            return false;
        }
    }

    private static string ExtractBefore(string rawBefore)
    {
        var segments = SplitSentences(rawBefore);
        if (segments.Count == 0)
        {
            return string.Empty;
        }

        var startIndex = Math.Max(0, segments.Count - MaxSentencesPerSide);
        var joined = string.Join(" ", segments.GetRange(startIndex, segments.Count - startIndex));
        if (joined.Length <= MaxCharsPerSide)
        {
            return joined;
        }

        var cutIndex = joined.Length - MaxCharsPerSide;
        var tail = joined[cutIndex..];
        if (cutIndex > 0 && !char.IsWhiteSpace(joined[cutIndex - 1]) && !char.IsWhiteSpace(tail[0]))
        {
            var firstSpace = tail.IndexOf(' ');
            if (firstSpace > 0 && firstSpace < tail.Length - 1)
            {
                tail = tail[(firstSpace + 1)..];
            }
        }

        return tail.TrimStart();
    }

    private static string ExtractAfter(string rawAfter)
    {
        var segments = SplitSentences(rawAfter);
        if (segments.Count == 0)
        {
            return string.Empty;
        }

        var count = Math.Min(MaxSentencesPerSide, segments.Count);
        var joined = string.Join(" ", segments.GetRange(0, count));
        if (joined.Length <= MaxCharsPerSide)
        {
            return joined;
        }

        var head = joined[..MaxCharsPerSide];
        if (MaxCharsPerSide < joined.Length
            && !char.IsWhiteSpace(joined[MaxCharsPerSide])
            && !char.IsWhiteSpace(head[^1]))
        {
            var lastSpace = head.LastIndexOf(' ');
            if (lastSpace > 0)
            {
                head = head[..lastSpace];
            }
        }

        return head.TrimEnd();
    }

    private static List<string> SplitSentences(string raw)
    {
        var segments = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return segments;
        }

        var normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var current = new StringBuilder();
        var i = 0;

        while (i < normalized.Length)
        {
            var ch = normalized[i];

            if (ch == '\n')
            {
                var lookahead = i + 1;
                while (lookahead < normalized.Length && (normalized[lookahead] == ' ' || normalized[lookahead] == '\t'))
                {
                    lookahead++;
                }

                if (lookahead < normalized.Length && normalized[lookahead] == '\n')
                {
                    FlushSegment(current, segments);
                    i = lookahead + 1;
                    while (i < normalized.Length && (normalized[i] == '\n' || normalized[i] == ' ' || normalized[i] == '\t'))
                    {
                        i++;
                    }
                    continue;
                }

                current.Append(' ');
                i++;
                continue;
            }

            if (IsSentenceTerminator(ch))
            {
                var isInlineDot = ch == '.'
                    && i + 1 < normalized.Length
                    && char.IsLetterOrDigit(normalized[i + 1]);

                current.Append(ch);
                i++;

                if (isInlineDot)
                {
                    continue;
                }

                while (i < normalized.Length && (IsSentenceTerminator(normalized[i]) || IsClosingWrapper(normalized[i])))
                {
                    current.Append(normalized[i]);
                    i++;
                }

                FlushSegment(current, segments);
                continue;
            }

            current.Append(char.IsControl(ch) ? ' ' : ch);
            i++;
        }

        FlushSegment(current, segments);
        return segments;
    }

    private static void FlushSegment(StringBuilder current, List<string> segments)
    {
        if (current.Length == 0)
        {
            return;
        }

        var collapsed = CollapseWhitespace(current);
        current.Clear();

        if (collapsed.Length > 0)
        {
            segments.Add(collapsed);
        }
    }

    private static string CollapseWhitespace(StringBuilder sb)
    {
        var result = new StringBuilder(sb.Length);
        var inSpace = true;

        for (var i = 0; i < sb.Length; i++)
        {
            var ch = sb[i];
            if (char.IsWhiteSpace(ch))
            {
                if (!inSpace)
                {
                    result.Append(' ');
                    inSpace = true;
                }
            }
            else
            {
                result.Append(ch);
                inSpace = false;
            }
        }

        if (result.Length > 0 && result[^1] == ' ')
        {
            result.Length--;
        }

        return result.ToString();
    }

    private static bool IsSentenceTerminator(char ch) =>
        ch is '.' or '!' or '?' or '。' or '！' or '？';

    private static bool IsClosingWrapper(char ch) =>
        ch is '"' or '\'' or '”' or '’' or ')' or ']' or '」' or '』' or '】' or '）';

    private static bool IsUiaException(Exception ex) =>
        ex is ElementNotAvailableException
            or COMException
            or InvalidOperationException
            or NotSupportedException
            or ArgumentException;
}
