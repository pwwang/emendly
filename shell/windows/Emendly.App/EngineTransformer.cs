using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Emendly.App;

public sealed class EngineTransformer : ITransformer
{
    private const int SupportedProtocolVersion = 1;
    private const int HandshakeTimeoutMs = 5000;
    private const int CancelGraceMs = 600;

    private readonly string _enginePath;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingCall> _pending = new(StringComparer.Ordinal);

    private string _baseUrl;
    private string _model;
    private string? _spawnedApiKey;
    private long _nextRequestId;
    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _stdoutLoop;
    private Task? _stderrLoop;
    private bool _disposed;

    public EngineTransformer(string enginePath, string baseUrl, string model)
    {
        _enginePath = enginePath;
        _baseUrl = baseUrl;
        _model = model;
    }

    public string ModeName => "engine";

    public bool IsEchoFallback => false;

    public string StatusSummary => $"Engine ({Path.GetFileName(_enginePath)})";

    public async Task ConfigureAsync(string baseUrl, string model, CancellationToken cancellationToken = default)
    {
        _baseUrl = baseUrl;
        _model = model;

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is not null && !_process.HasExited)
            {
                await ApplyConfigLockedAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<TransformResult> TransformAsync(
        TransformRequest request,
        Action<string> onDelta,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var apiKey = AppSettings.ReadApiKey();
        await EnsureStartedAsync(apiKey, cancellationToken).ConfigureAwait(false);

        var requestId = NextRequestId();
        var pending = PendingCall.ForAction(onDelta);
        _pending[requestId] = pending;

        using var cancelRegistration = cancellationToken.Register(() =>
        {
            _ = SendCancelAndExpireAsync(requestId, pending);
        });

        try
        {
            var payload = BuildActionRunJson(requestId, request);
            await WriteLineAsync(payload, cancellationToken).ConfigureAwait(false);
            return await pending.ResultTcs!.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private async Task EnsureStartedAsync(string? apiKey, CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is not null && !_process.HasExited)
            {
                if (string.Equals(_spawnedApiKey, apiKey, StringComparison.Ordinal))
                {
                    return;
                }

                await StopProcessLockedAsync().ConfigureAwait(false);
            }

            StartProcessLocked(apiKey);
            await VerifyHealthLockedAsync(cancellationToken).ConfigureAwait(false);
            await ApplyConfigLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void StartProcessLocked(string? apiKey)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _enginePath,
            WorkingDirectory = Path.GetDirectoryName(_enginePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!string.IsNullOrEmpty(apiKey))
        {
            startInfo.Environment["EMENDLY_API_KEY"] = apiKey;
        }
        else
        {
            startInfo.Environment.Remove("EMENDLY_API_KEY");
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new TransformException("internal", "Failed to start engine process.", retryable: false);
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 193 or 216)
        {
            process.Dispose();
            throw new TransformException(
                "internal",
                $"The engine at {_enginePath} is not a Windows build ({ex.NativeErrorCode}). Build it for Windows, or point EMENDLY_ENGINE at a Windows binary.",
                retryable: false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            process.Dispose();
            throw new TransformException("internal", $"The engine could not start: {ex.Message}", retryable: false);
        }

        _process = process;
        _stdin = process.StandardInput;
        _spawnedApiKey = apiKey;
        _stdoutLoop = Task.Run(() => ReadStdoutLoopAsync(process));
        _stderrLoop = Task.Run(() => DrainStderrLoopAsync(process));
    }

    private async Task VerifyHealthLockedAsync(CancellationToken cancellationToken)
    {
        var id = NextRequestId();
        var pending = PendingCall.ForHealth();
        _pending[id] = pending;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(HandshakeTimeoutMs);

        try
        {
            var json = BuildSimpleRequestJson(id, "health");
            await WriteLineAsync(json, timeoutCts.Token).ConfigureAwait(false);

            var protocol = await pending.ProtocolTcs!.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            if (protocol != SupportedProtocolVersion)
            {
                await StopProcessLockedAsync().ConfigureAwait(false);
                throw new TransformException(
                    "internal",
                    $"Unsupported engine protocol version {protocol} (expected {SupportedProtocolVersion}).",
                    retryable: false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopProcessLockedAsync().ConfigureAwait(false);
            throw new TransformException("timeout", "Engine health check timed out.", retryable: true);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ApplyConfigLockedAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl) && string.IsNullOrWhiteSpace(_model))
        {
            return;
        }

        var id = NextRequestId();
        var pending = PendingCall.ForConfig();
        _pending[id] = pending;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(HandshakeTimeoutMs);

        try
        {
            var json = BuildConfigSetJson(id, _baseUrl, _model);
            await WriteLineAsync(json, timeoutCts.Token).ConfigureAwait(false);
            await pending.ConfigTcs!.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadStdoutLoopAsync(Process process)
    {
        try
        {
            var reader = process.StandardOutput;
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                DispatchEventLine(line);
            }
        }
        catch (Exception)
        {
            // Process exited or stream closed.
        }
        finally
        {
            FailAllPending("internal", "Engine process exited unexpectedly.", retryable: true);
        }
    }

    private static async Task DrainStderrLoopAsync(Process process)
    {
        try
        {
            var reader = process.StandardError;
            while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
            {
                // Drain stderr continuously so the child process pipe never blocks.
            }
        }
        catch (Exception)
        {
            // Ignore stream close on exit.
        }
    }

    private void DispatchEventLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idEl) || !root.TryGetProperty("event", out var eventEl))
            {
                return;
            }

            var id = idEl.GetString() ?? string.Empty;
            var eventName = eventEl.GetString() ?? string.Empty;

            if (!_pending.TryGetValue(id, out var pending))
            {
                return;
            }

            switch (eventName)
            {
                case "health":
                    var protocol = root.TryGetProperty("protocol", out var protoEl) && protoEl.TryGetInt32(out var p)
                        ? p
                        : -1;
                    pending.ProtocolTcs?.TrySetResult(protocol);
                    break;

                case "config":
                    pending.ConfigTcs?.TrySetResult(true);
                    break;

                case "started":
                    break;

                case "delta":
                    var deltaText = root.TryGetProperty("text", out var deltaEl) ? deltaEl.GetString() ?? string.Empty : string.Empty;
                    pending.Accumulated.Append(deltaText);
                    pending.OnDelta?.Invoke(deltaText);
                    break;

                case "done":
                    var finalText = root.TryGetProperty("text", out var doneTextEl)
                        ? doneTextEl.GetString() ?? pending.Accumulated.ToString()
                        : pending.Accumulated.ToString();
                    var latencyMs = root.TryGetProperty("latency_ms", out var latEl) && latEl.TryGetInt64(out var lat)
                        ? lat
                        : 0L;
                    long inputTokens = 0;
                    long outputTokens = 0;
                    if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                    {
                        if (usageEl.TryGetProperty("input_tokens", out var inEl) && inEl.TryGetInt64(out var iTok))
                        {
                            inputTokens = iTok;
                        }

                        if (usageEl.TryGetProperty("output_tokens", out var outEl) && outEl.TryGetInt64(out var oTok))
                        {
                            outputTokens = oTok;
                        }
                    }

                    pending.ResultTcs?.TrySetResult(new TransformResult(finalText, inputTokens, outputTokens, latencyMs));
                    break;

                case "cancelled":
                    pending.ResultTcs?.TrySetCanceled();
                    break;

                case "error":
                    var code = root.TryGetProperty("code", out var codeEl) ? codeEl.GetString() ?? "internal" : "internal";
                    var message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() ?? "Engine error" : "Engine error";
                    var retryable = root.TryGetProperty("retryable", out var retEl) && retEl.ValueKind == JsonValueKind.True;
                    pending.TrySetException(new TransformException(code, message, retryable));
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed engine lines rather than crashing the shell.
        }
    }

    private void FailAllPending(string code, string message, bool retryable)
    {
        var ex = new TransformException(code, message, retryable);
        foreach (var pair in _pending)
        {
            pair.Value.TrySetException(ex);
        }
    }

    private async Task SendCancelAndExpireAsync(string targetId, PendingCall pending)
    {
        try
        {
            var cancelId = NextRequestId();
            var payload = BuildCancelJson(cancelId, targetId);
            await WriteLineAsync(payload, CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(CancelGraceMs).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ignore write errors while cancelling.
        }
        finally
        {
            pending.ResultTcs?.TrySetCanceled();
        }
    }

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stdin = _stdin;
            if (stdin is null || _process is null || _process.HasExited)
            {
                throw new TransformException("internal", "Engine process is not running.", retryable: true);
            }

            await stdin.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task StopProcessLockedAsync()
    {
        var process = _process;
        var stdin = _stdin;
        _process = null;
        _stdin = null;

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited && stdin is not null)
            {
                await _writeLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    var shutdownJson = BuildSimpleRequestJson(NextRequestId(), "shutdown");
                    await stdin.WriteLineAsync(shutdownJson).ConfigureAwait(false);
                    await stdin.FlushAsync().ConfigureAwait(false);
                    stdin.Close();
                }
                finally
                {
                    _writeLock.Release();
                }

                using var cts = new CancellationTokenSource(1000);
                try
                {
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
            }
        }
        catch (Exception)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup.
            }
        }
        finally
        {
            process.Dispose();
        }

        if (_stdoutLoop is not null)
        {
            try { await _stdoutLoop.ConfigureAwait(false); } catch (Exception) { }
            _stdoutLoop = null;
        }

        if (_stderrLoop is not null)
        {
            try { await _stderrLoop.ConfigureAwait(false); } catch (Exception) { }
            _stderrLoop = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopProcessLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }

        _lifecycleLock.Dispose();
        _writeLock.Dispose();
    }

    private string NextRequestId() => $"r{Interlocked.Increment(ref _nextRequestId)}";

    private static string BuildSimpleRequestJson(string id, string type)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("type", type);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string BuildCancelJson(string id, string targetId)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("type", "cancel");
            writer.WriteString("target", targetId);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string BuildConfigSetJson(string id, string baseUrl, string model)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("type", "config.set");
            writer.WriteStartObject("patch");
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                writer.WriteString("base_url", baseUrl);
            }

            if (!string.IsNullOrWhiteSpace(model))
            {
                writer.WriteString("model", model);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string BuildActionRunJson(string id, TransformRequest request)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("type", "actions.run");
            writer.WriteString("action", request.Action);
            writer.WriteString("text", request.Text);
            if (request.Options is { Count: > 0 })
            {
                writer.WriteStartObject("options");
                foreach (var pair in request.Options)
                {
                    writer.WriteString(pair.Key, pair.Value);
                }

                writer.WriteEndObject();
            }

            if (!string.IsNullOrEmpty(request.Context))
            {
                writer.WriteString("context", request.Context);
            }

            if (!string.IsNullOrEmpty(request.AppProcess))
            {
                writer.WriteString("app", request.AppProcess);
            }

            writer.WriteBoolean("stream", true);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private sealed class PendingCall
    {
        public Action<string>? OnDelta { get; }
        public StringBuilder Accumulated { get; } = new();
        public TaskCompletionSource<TransformResult>? ResultTcs { get; }
        public TaskCompletionSource<int>? ProtocolTcs { get; }
        public TaskCompletionSource<bool>? ConfigTcs { get; }

        private PendingCall(
            Action<string>? onDelta,
            TaskCompletionSource<TransformResult>? resultTcs,
            TaskCompletionSource<int>? protocolTcs,
            TaskCompletionSource<bool>? configTcs)
        {
            OnDelta = onDelta;
            ResultTcs = resultTcs;
            ProtocolTcs = protocolTcs;
            ConfigTcs = configTcs;
        }

        public static PendingCall ForAction(Action<string> onDelta) =>
            new(onDelta, new(TaskCreationOptions.RunContinuationsAsynchronously), null, null);

        public static PendingCall ForHealth() =>
            new(null, null, new(TaskCreationOptions.RunContinuationsAsynchronously), null);

        public static PendingCall ForConfig() =>
            new(null, null, null, new(TaskCreationOptions.RunContinuationsAsynchronously));

        public void TrySetException(Exception ex)
        {
            ResultTcs?.TrySetException(ex);
            ProtocolTcs?.TrySetException(ex);
            ConfigTcs?.TrySetException(ex);
        }
    }
}
