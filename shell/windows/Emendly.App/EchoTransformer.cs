using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Emendly.App;

public sealed class EchoTransformer : ITransformer
{
    private const int ChunkSize = 12;
    private const int ChunkDelayMs = 20;

    public string ModeName => "echo";

    public bool IsEchoFallback => true;

    public string StatusSummary => "Echo mode (engine binary not found)";

    public Task ConfigureAsync(string baseUrl, string model, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public async Task<TransformResult> TransformAsync(
        TransformRequest request,
        Action<string> onDelta,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        var source = !string.IsNullOrEmpty(request.Text)
            ? request.Text
            : request.Options is not null && request.Options.TryGetValue("brief", out var brief) && !string.IsNullOrEmpty(brief)
                ? brief
                : "(empty)";

        var result = $"[{request.Action.ToUpperInvariant()}] {source.ToUpperInvariant()}";

        for (var offset = 0; offset < result.Length; offset += ChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var len = Math.Min(ChunkSize, result.Length - offset);
            onDelta(result.Substring(offset, len));
            await Task.Delay(ChunkDelayMs, cancellationToken).ConfigureAwait(false);
        }

        sw.Stop();
        return new TransformResult(result, InputTokens: 0, OutputTokens: 0, LatencyMs: sw.ElapsedMilliseconds);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
