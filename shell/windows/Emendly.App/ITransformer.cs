using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Emendly.App;

public sealed record TransformRequest(
    string Action,
    string Text,
    string? Context = null,
    string? AppProcess = null,
    IReadOnlyDictionary<string, object>? Options = null);

public sealed record TransformResult(
    string Text,
    long InputTokens,
    long OutputTokens,
    long LatencyMs);

public sealed class TransformException : Exception
{
    public string Code { get; }
    public bool Retryable { get; }

    public TransformException(string code, string message, bool retryable)
        : base(message)
    {
        Code = code;
        Retryable = retryable;
    }
}

public interface ITransformer : IAsyncDisposable
{
    string ModeName { get; }
    bool IsEchoFallback { get; }
    string StatusSummary { get; }
    string? EngineVersion { get; }
    string? DefaultTargetLanguage { get; }

    Task ConfigureAsync(string baseUrl, string model, CancellationToken cancellationToken = default);

    Task<TransformResult> TransformAsync(
        TransformRequest request,
        Action<string> onDelta,
        CancellationToken cancellationToken = default);
}
