using System.Text;

namespace Emendly.Capture;

public enum SelectionState
{
    Empty,
    Word,
    Passage,
}

public sealed record SelectionClassification(
    SelectionState State,
    bool ReadOnly,
    string TrimmedText,
    string UntrimmedText,
    Range UntrimmedRange,
    Range TrimmedRange,
    int CharacterCount)
{
    public string LeadingWhitespace =>
        TrimmedRange.Start.Value > 0
            ? UntrimmedText[..TrimmedRange.Start.Value]
            : string.Empty;

    public string TrailingWhitespace =>
        TrimmedRange.End.Value < UntrimmedText.Length
            ? UntrimmedText[TrimmedRange.End.Value..]
            : string.Empty;

    public string RestoreSurroundingWhitespace(string replacement)
    {
        if (string.IsNullOrEmpty(replacement) || State == SelectionState.Empty)
        {
            return replacement;
        }

        var leading = LeadingWhitespace;
        var trailing = TrailingWhitespace;
        var result = replacement;

        if (leading.Length > 0 && !result.StartsWith(leading, StringComparison.Ordinal))
        {
            result = leading + result;
        }

        if (trailing.Length > 0 && !result.EndsWith(trailing, StringComparison.Ordinal))
        {
            result += trailing;
        }

        return result;
    }
}

public static class SelectionClassifier
{
    public const int MaxAsciiWordLength = 40;
    public const int MaxNonAsciiWordLength = 20;

    public static SelectionClassification Classify(string? rawSelection, bool readOnly)
    {
        var untrimmed = rawSelection ?? string.Empty;
        var untrimmedRange = new Range(0, untrimmed.Length);

        var trimStart = 0;
        while (trimStart < untrimmed.Length && char.IsWhiteSpace(untrimmed[trimStart]))
        {
            trimStart++;
        }

        var trimEnd = untrimmed.Length;
        while (trimEnd > trimStart && char.IsWhiteSpace(untrimmed[trimEnd - 1]))
        {
            trimEnd--;
        }

        if (trimStart >= trimEnd)
        {
            return new SelectionClassification(
                State: SelectionState.Empty,
                ReadOnly: readOnly,
                TrimmedText: string.Empty,
                UntrimmedText: untrimmed,
                UntrimmedRange: untrimmedRange,
                TrimmedRange: new Range(0, 0),
                CharacterCount: 0);
        }

        var trimmed = untrimmed[trimStart..trimEnd];
        var trimmedRange = new Range(trimStart, trimEnd);
        var state = ClassifyTrimmed(trimmed);

        return new SelectionClassification(
            State: state,
            ReadOnly: readOnly,
            TrimmedText: trimmed,
            UntrimmedText: untrimmed,
            UntrimmedRange: untrimmedRange,
            TrimmedRange: trimmedRange,
            CharacterCount: trimmed.Length);
    }

    private static SelectionState ClassifyTrimmed(string trimmed)
    {
        var runeCount = 0;
        var asciiLetters = 0;
        var asciiLettersOrDigits = 0;
        var hasNonAsciiLetter = false;
        var hasNoSpaceScript = false;
        var hasSentenceFinalPunctuation = false;
        var hasNonAsciiSentenceFinalPunctuation = false;

        foreach (var rune in trimmed.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                return SelectionState.Passage;
            }

            runeCount++;

            if (rune.IsAscii)
            {
                var ch = (char)rune.Value;
                if (char.IsAsciiLetter(ch))
                {
                    asciiLetters++;
                    asciiLettersOrDigits++;
                }
                else if (char.IsAsciiDigit(ch))
                {
                    asciiLettersOrDigits++;
                }
                else if (ch is '.' or '!' or '?')
                {
                    hasSentenceFinalPunctuation = true;
                }
            }
            else
            {
                if (Rune.IsLetter(rune))
                {
                    hasNonAsciiLetter = true;
                }

                if (IsNoSpaceScript(rune))
                {
                    hasNoSpaceScript = true;
                }

                if (IsNonAsciiSentenceFinalPunctuation(rune))
                {
                    hasSentenceFinalPunctuation = true;
                    hasNonAsciiSentenceFinalPunctuation = true;
                }
            }
        }

        if (asciiLetters == 0)
        {
            if (hasSentenceFinalPunctuation && (hasNonAsciiLetter || hasNonAsciiSentenceFinalPunctuation))
            {
                return SelectionState.Passage;
            }

            return runeCount <= MaxNonAsciiWordLength ? SelectionState.Word : SelectionState.Passage;
        }

        if (hasNonAsciiSentenceFinalPunctuation || (hasNoSpaceScript && runeCount > MaxNonAsciiWordLength))
        {
            return SelectionState.Passage;
        }

        return asciiLettersOrDigits <= MaxAsciiWordLength ? SelectionState.Word : SelectionState.Passage;
    }

    private static bool IsNoSpaceScript(Rune rune)
    {
        var v = rune.Value;
        return (v >= 0x3040 && v <= 0x30FF)   // Hiragana & Katakana
            || (v >= 0x31F0 && v <= 0x31FF)   // Katakana Phonetic Extensions
            || (v >= 0x3400 && v <= 0x4DBF)   // CJK Extension A
            || (v >= 0x4E00 && v <= 0x9FFF)   // CJK Unified Ideographs
            || (v >= 0xF900 && v <= 0xFAFF)   // CJK Compatibility Ideographs
            || (v >= 0xFF65 && v <= 0xFF9F)   // Halfwidth Katakana
            || (v >= 0x20000 && v <= 0x2EBEF) // CJK Extensions B-F
            || (v >= 0x0E00 && v <= 0x0EFF)   // Thai & Lao
            || (v >= 0x0F00 && v <= 0x0FFF)   // Tibetan
            || (v >= 0x1000 && v <= 0x109F)   // Myanmar
            || (v >= 0x1780 && v <= 0x17FF);  // Khmer
    }

    private static bool IsNonAsciiSentenceFinalPunctuation(Rune rune) =>
        rune.Value is 0x3002 // 。
            or 0xFF01        // ！
            or 0xFF1F        // ？
            or 0xFF0E        // ．
            or 0xFF61        // ｡
            or 0x0964        // ।
            or 0x0965        // ॥
            or 0x061F;       // ؟
}
