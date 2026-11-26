using System.Text;

namespace Emendly.Core;

public sealed record BatchSegment(string Text, bool IsParagraph);

public sealed record BatchRunResult(
    string OutputText,
    int ParagraphsProcessed,
    long TotalLatencyMs,
    long TotalInputTokens,
    long TotalOutputTokens);

public sealed class BatchRunner
{
    private readonly IEngineRunner _engine;
    private readonly ChainRunner _chainRunner;

    public BatchRunner(IEngineRunner engine, ChainRunner? chainRunner = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _chainRunner = chainRunner ?? new ChainRunner(engine);
    }

    /// <summary>
    /// Splits text into alternating content paragraphs and blank-line separators so
    /// original blank-line spacing and line endings are preserved losslessly.
    /// </summary>
    public static IReadOnlyList<BatchSegment> SplitSegments(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return Array.Empty<BatchSegment>();
        }

        var lines = SplitPreservingLineEndings(input);
        var segments = new List<BatchSegment>();
        var current = new StringBuilder();
        bool? currentIsParagraph = null;

        foreach (var line in lines)
        {
            var isBlank = string.IsNullOrWhiteSpace(line);
            var lineIsParagraph = !isBlank;

            if (currentIsParagraph is null)
            {
                currentIsParagraph = lineIsParagraph;
                current.Append(line);
            }
            else if (currentIsParagraph == lineIsParagraph)
            {
                current.Append(line);
            }
            else
            {
                FlushSegment(segments, current, currentIsParagraph.Value);
                current.Clear();
                currentIsParagraph = lineIsParagraph;
                current.Append(line);
            }
        }

        if (current.Length > 0 && currentIsParagraph is not null)
        {
            FlushSegment(segments, current, currentIsParagraph.Value);
        }

        return segments;
    }

    public async Task<BatchRunResult> RunActionAsync(
        string input,
        EngineRequest templateRequest,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(templateRequest);
        var segments = SplitSegments(input);
        var totalParagraphs = segments.Count(s => s.IsParagraph);
        if (totalParagraphs == 0)
        {
            return new BatchRunResult(input ?? string.Empty, 0, 0, 0, 0);
        }

        var output = new StringBuilder();
        var processed = 0;
        long totalLatency = 0;
        long totalIn = 0;
        long totalOut = 0;

        foreach (var segment in segments)
        {
            if (!segment.IsParagraph)
            {
                output.Append(segment.Text);
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var (body, trailingNewline) = SplitTrailingNewline(segment.Text);

            var req = templateRequest.CloneWithText(body);
            req.Stream = false;
            var res = await _engine.RunAsync(req, null, cancellationToken).ConfigureAwait(false);

            output.Append(res.Text);
            output.Append(trailingNewline);

            processed++;
            totalLatency += res.LatencyMs;
            totalIn += res.InputTokens;
            totalOut += res.OutputTokens;
            onProgress?.Invoke(processed, totalParagraphs, res.Text);
        }

        return new BatchRunResult(output.ToString(), processed, totalLatency, totalIn, totalOut);
    }

    public async Task<BatchRunResult> RunChainAsync(
        string input,
        ActionChain chain,
        string? context = null,
        string? glossaryPrefix = null,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<ProtocolGlossaryEntry>? glossaryEntries = null)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var segments = SplitSegments(input);
        var totalParagraphs = segments.Count(s => s.IsParagraph);
        if (totalParagraphs == 0)
        {
            return new BatchRunResult(input ?? string.Empty, 0, 0, 0, 0);
        }

        var output = new StringBuilder();
        var processed = 0;
        long totalLatency = 0;
        long totalIn = 0;
        long totalOut = 0;

        foreach (var segment in segments)
        {
            if (!segment.IsParagraph)
            {
                output.Append(segment.Text);
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var (body, trailingNewline) = SplitTrailingNewline(segment.Text);

            var res = await _chainRunner.RunAsync(
                chain,
                body,
                context,
                glossaryPrefix,
                onStepProgress: null,
                onDelta: null,
                cancellationToken: cancellationToken,
                glossaryEntries: glossaryEntries).ConfigureAwait(false);

            output.Append(res.FinalText);
            output.Append(trailingNewline);

            processed++;
            totalLatency += res.TotalLatencyMs;
            totalIn += res.TotalInputTokens;
            totalOut += res.TotalOutputTokens;
            onProgress?.Invoke(processed, totalParagraphs, res.FinalText);
        }

        return new BatchRunResult(output.ToString(), processed, totalLatency, totalIn, totalOut);
    }

    private static void FlushSegment(List<BatchSegment> segments, StringBuilder sb, bool isParagraph)
    {
        if (sb.Length > 0)
        {
            segments.Add(new BatchSegment(sb.ToString(), isParagraph));
        }
    }

    private static List<string> SplitPreservingLineEndings(string input)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == '\n')
            {
                lines.Add(input.Substring(start, i - start + 1));
                start = i + 1;
            }
            else if (input[i] == '\r')
            {
                if (i + 1 < input.Length && input[i + 1] == '\n')
                {
                    lines.Add(input.Substring(start, i - start + 2));
                    i++;
                    start = i + 1;
                }
                else
                {
                    lines.Add(input.Substring(start, i - start + 1));
                    start = i + 1;
                }
            }
        }

        if (start < input.Length)
        {
            lines.Add(input[start..]);
        }

        return lines;
    }

    private static (string Body, string TrailingNewline) SplitTrailingNewline(string text)
    {
        if (text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return (text[..^2], "\r\n");
        }

        if (text.EndsWith('\n') || text.EndsWith('\r'))
        {
            return (text[..^1], text[^1..]);
        }

        return (text, string.Empty);
    }
}
