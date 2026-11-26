using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Emendly.Core;

public sealed class EngineRequest
{
    public string Action { get; set; } = "proofread";
    public string Text { get; set; } = string.Empty;
    public string? Tone { get; set; }
    public string? TargetLanguage { get; set; }
    public string? Instruction { get; set; }
    public string? Brief { get; set; }
    public string? Context { get; set; }
    public bool IncludeContext { get; set; }
    public string? ContextBefore { get; set; }
    public string? ContextAfter { get; set; }
    public string? SystemPrefix { get; set; }
    public string? SystemSuffix { get; set; }
    public string? SystemOverride { get; set; }
    public string? UserTemplateOverride { get; set; }
    public IReadOnlyList<ProtocolGlossaryEntry>? Glossary { get; set; }
    public string? AppName { get; set; }
    public bool Stream { get; set; } = true;

    public EngineRequest CloneWithText(string text) => new()
    {
        Action = Action,
        Text = text,
        Tone = Tone,
        TargetLanguage = TargetLanguage,
        Instruction = Instruction,
        Brief = Brief,
        Context = Context,
        IncludeContext = IncludeContext,
        ContextBefore = ContextBefore,
        ContextAfter = ContextAfter,
        SystemPrefix = SystemPrefix,
        SystemSuffix = SystemSuffix,
        SystemOverride = SystemOverride,
        UserTemplateOverride = UserTemplateOverride,
        Glossary = Glossary,
        AppName = AppName,
        Stream = Stream
    };
}

public sealed record EngineResult(
    string Text,
    string Model,
    long LatencyMs,
    long InputTokens,
    long OutputTokens);

public sealed record EngineHealth(string Version, int Protocol);

public sealed record EngineActionInfo(string Name, string Description, bool RequiresText, int? MaxTokens);

public sealed class EngineException : Exception
{
    public string Code { get; }
    public bool Retryable { get; }

    public EngineException(string code, string message, bool retryable = false)
        : base(message)
    {
        Code = code;
        Retryable = retryable;
    }
}

public interface IEngineRunner
{
    Task<EngineResult> RunAsync(
        EngineRequest request,
        Action<string>? onDelta = null,
        CancellationToken cancellationToken = default);
}

public sealed class EngineRunner : IEngineRunner, IDisposable
{
    public const int SupportedProtocolVersion = 1;
    private const int HandshakeTimeoutMs = 5000;
    private const int CancelGraceMs = 600;

    private readonly string? _explicitEnginePath;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingCall> _pending = new(StringComparer.Ordinal);

    private string _baseUrl;
    private string _model;
    private string? _resolvedEnginePath;
    private string? _spawnedApiKey;
    private long _nextId;
    private Process? _process;
    private StreamWriter? _stdin;
    private bool _disposed;

    public EngineRunner(
        string? enginePath = null,
        string? baseUrl = null,
        string? model = null)
    {
        _explicitEnginePath = enginePath;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? HubSettings.DefaultBaseUrl : baseUrl.Trim();
        _model = string.IsNullOrWhiteSpace(model) ? HubSettings.DefaultModel : model.Trim();
    }

    public string? ResolvedEnginePath => _resolvedEnginePath ?? ResolveEnginePath(_explicitEnginePath);

    public string? EngineVersion { get; private set; }

    public static string? ReadApiKeyFromEnvironment() =>
        Environment.GetEnvironmentVariable("EMENDLY_API_KEY");

    public static string? ResolveEnginePath(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var envPath = Environment.GetEnvironmentVariable("EMENDLY_ENGINE");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return Path.GetFullPath(envPath);
        }

        var exeName = OperatingSystem.IsWindows() ? "emendly-engine.exe" : "emendly-engine";
        var baseDir = AppContext.BaseDirectory;

        var directCandidates = new[]
        {
            Path.Combine(baseDir, exeName),
            Path.Combine(baseDir, "engine", exeName),
            Path.Combine(baseDir, "..", "engine", exeName),
            Path.Combine(baseDir, "..", "..", "..", "emendly", "engine", "target", "release", exeName),
            Path.Combine(baseDir, "..", "..", "..", "emendly", "engine", "target", "debug", exeName)
        };

        foreach (var candidate in directCandidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        // Walk ancestors looking for a sibling `emendly` repository build output.
        var dir = new DirectoryInfo(baseDir);
        for (var depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
        {
            var siblingRelease = Path.Combine(dir.FullName, "emendly", "engine", "target", "release", exeName);
            if (File.Exists(siblingRelease))
            {
                return Path.GetFullPath(siblingRelease);
            }

            var siblingDebug = Path.Combine(dir.FullName, "emendly", "engine", "target", "debug", exeName);
            if (File.Exists(siblingDebug))
            {
                return Path.GetFullPath(siblingDebug);
            }
        }

        return null;
    }

    public async Task<EngineHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureStartedAsync(ReadApiKeyFromEnvironment(), cancellationToken).ConfigureAwait(false);
        return new EngineHealth(EngineVersion ?? "unknown", SupportedProtocolVersion);
    }

    public async Task ConfigureAsync(string baseUrl, string model, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _baseUrl = baseUrl.Trim();
        _model = model.Trim();
        await EnsureStartedAsync(ReadApiKeyFromEnvironment(), cancellationToken).ConfigureAwait(false);
        await SendConfigSetAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EngineActionInfo>> ListActionsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureStartedAsync(ReadApiKeyFromEnvironment(), cancellationToken).ConfigureAwait(false);

        var id = NextRequestId();
        var pending = PendingCall.ForActionsList();
        _pending[id] = pending;
        try
        {
            var line = $"{{\"id\":\"{id}\",\"type\":\"actions.list\"}}";
            await WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
            return await pending.ActionsTcs!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task<EngineResult> RunAsync(
        EngineRequest request,
        Action<string>? onDelta = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        var apiKey = ReadApiKeyFromEnvironment();
        await EnsureStartedAsync(apiKey, cancellationToken).ConfigureAwait(false);

        var id = NextRequestId();
        var pending = PendingCall.ForAction(onDelta);
        _pending[id] = pending;

        using var cancelReg = cancellationToken.Register(() =>
        {
            _ = SendCancelAsync(id, pending);
        });

        try
        {
            var line = BuildActionRunJson(id, request);
            await WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
            return await pending.ResultTcs!.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
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

            var path = ResolveEnginePath(_explicitEnginePath);
            if (string.IsNullOrEmpty(path))
            {
                throw new EngineException(
                    "engine_not_found",
                    "Could not locate emendly-engine. Set EMENDLY_ENGINE or build the engine in the sibling emendly repo.");
            }

            _resolvedEnginePath = path;
            StartProcessLocked(path, apiKey);
            await VerifyHealthLockedAsync(cancellationToken).ConfigureAwait(false);
            await SendConfigSetAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void StartProcessLocked(string enginePath, string? apiKey)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = enginePath,
            WorkingDirectory = Path.GetDirectoryName(enginePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrEmpty(apiKey))
        {
            startInfo.Environment["EMENDLY_API_KEY"] = apiKey;
        }
        else
        {
            startInfo.Environment.Remove("EMENDLY_API_KEY");
        }

        var proc = new Process { StartInfo = startInfo };
        if (!proc.Start())
        {
            throw new EngineException("internal", $"Failed to start engine at {enginePath}.");
        }

        _process = proc;
        _stdin = proc.StandardInput;
        _spawnedApiKey = apiKey;
        _ = Task.Run(() => ReadStdoutLoopAsync(proc.StandardOutput));
        _ = Task.Run(() => DrainStderrLoopAsync(proc.StandardError));
    }

    private async Task VerifyHealthLockedAsync(CancellationToken cancellationToken)
    {
        var id = NextRequestId();
        var pending = PendingCall.ForHealth();
        _pending[id] = pending;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(HandshakeTimeoutMs);

            var line = $"{{\"id\":\"{id}\",\"type\":\"health\"}}";
            await WriteLineAsync(line, timeoutCts.Token).ConfigureAwait(false);
            var health = await pending.HealthTcs!.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            if (health.Protocol != SupportedProtocolVersion)
            {
                throw new EngineException(
                    "protocol_mismatch",
                    $"Engine protocol {health.Protocol} is not supported (expected {SupportedProtocolVersion}).");
            }

            EngineVersion = health.Version;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendConfigSetAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl) && string.IsNullOrWhiteSpace(_model))
        {
            return;
        }

        var id = NextRequestId();
        var pending = PendingCall.ForConfig();
        _pending[id] = pending;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(HandshakeTimeoutMs);

            var line = BuildConfigSetJson(id, _baseUrl, _model);
            await WriteLineAsync(line, timeoutCts.Token).ConfigureAwait(false);
            await pending.ConfigTcs!.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendCancelAsync(string targetId, PendingCall pending)
    {
        try
        {
            var id = NextRequestId();
            var line = $"{{\"id\":\"{id}\",\"type\":\"cancel\",\"target\":\"{targetId}\"}}";
            await WriteLineAsync(line, CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(CancelGraceMs).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ignore write errors during cancellation.
        }

        pending.ResultTcs?.TrySetCanceled();
    }

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stdin is null)
            {
                throw new EngineException("internal", "Engine stdin is not open.");
            }

            await _stdin.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadStdoutLoopAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                HandleEventLine(line);
            }
        }
        catch (Exception)
        {
            // Process exited or stream closed.
        }
        finally
        {
            FailAllPending(new EngineException("internal", "Engine process exited unexpectedly."));
        }
    }

    private static async Task DrainStderrLoopAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
            {
                // Drain stderr so child process never blocks on pipe buffer.
            }
        }
        catch (Exception)
        {
            // Ignore stream close errors.
        }
    }

    private void HandleEventLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var evt = root.TryGetProperty("event", out var evtEl) ? evtEl.GetString() : null;

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(evt))
            {
                return;
            }

            if (!_pending.TryGetValue(id, out var pending))
            {
                return;
            }

            switch (evt)
            {
                case "health":
                {
                    var version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "unknown" : "unknown";
                    var protocol = root.TryGetProperty("protocol", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
                    pending.HealthTcs?.TrySetResult(new EngineHealth(version, protocol));
                    break;
                }
                case "config":
                {
                    pending.ConfigTcs?.TrySetResult(true);
                    break;
                }
                case "actions":
                {
                    var list = new List<EngineActionInfo>();
                    if (root.TryGetProperty("actions", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in arr.EnumerateArray())
                        {
                            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            var desc = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                            var reqText = item.TryGetProperty("requires_text", out var r) && r.GetBoolean();
                            int? maxTok = item.TryGetProperty("max_tokens", out var mt) && mt.ValueKind == JsonValueKind.Number
                                ? mt.GetInt32()
                                : null;
                            if (!string.IsNullOrEmpty(name))
                            {
                                list.Add(new EngineActionInfo(name, desc, reqText, maxTok));
                            }
                        }
                    }

                    pending.ActionsTcs?.TrySetResult(list);
                    break;
                }
                case "started":
                {
                    if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                    {
                        pending.Model = m.GetString() ?? _model;
                    }
                    break;
                }
                case "delta":
                {
                    var delta = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    if (delta.Length > 0)
                    {
                        pending.Accumulated.Append(delta);
                        pending.OnDelta?.Invoke(delta);
                    }
                    break;
                }
                case "done":
                {
                    var finalText = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString() ?? pending.Accumulated.ToString()
                        : pending.Accumulated.ToString();
                    var latency = root.TryGetProperty("latency_ms", out var lm) && lm.TryGetInt64(out var lmv) ? lmv : 0;
                    long inTok = 0;
                    long outTok = 0;
                    if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt64(out var itv))
                        {
                            inTok = itv;
                        }

                        if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt64(out var otv))
                        {
                            outTok = otv;
                        }
                    }

                    pending.ResultTcs?.TrySetResult(new EngineResult(
                        finalText,
                        pending.Model ?? _model,
                        latency,
                        inTok,
                        outTok));
                    break;
                }
                case "cancelled":
                {
                    pending.ResultTcs?.TrySetCanceled();
                    break;
                }
                case "error":
                {
                    var code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "internal" : "internal";
                    var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "Engine error" : "Engine error";
                    var retryable = root.TryGetProperty("retryable", out var r) && r.ValueKind == JsonValueKind.True;
                    pending.TrySetException(new EngineException(code, message, retryable));
                    break;
                }
            }
        }
        catch (JsonException)
        {
            // Ignore malformed engine output lines.
        }
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var pair in _pending)
        {
            pair.Value.TrySetException(ex);
        }
    }

    private async Task StopProcessLockedAsync()
    {
        var proc = _process;
        var stdin = _stdin;
        _process = null;
        _stdin = null;

        if (proc is null)
        {
            return;
        }

        try
        {
            if (stdin is not null && !proc.HasExited)
            {
                var id = NextRequestId();
                await stdin.WriteLineAsync($"{{\"id\":\"{id}\",\"type\":\"shutdown\"}}").ConfigureAwait(false);
                await stdin.FlushAsync().ConfigureAwait(false);
                stdin.Close();
            }

            if (!proc.HasExited && !proc.WaitForExit(1000))
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best-effort shutdown.
        }
        finally
        {
            proc.Dispose();
        }
    }

    private string NextRequestId() => $"req_{Interlocked.Increment(ref _nextId)}";

    public static string BuildConfigSetJson(string id, string baseUrl, string model)
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

    public static string BuildActionRunJson(string id, EngineRequest request)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("type", "actions.run");
            writer.WriteString("action", request.Action);
            writer.WriteString("text", request.Text ?? string.Empty);

            writer.WriteStartObject("options");
            if (request.IncludeContext)
            {
                writer.WriteBoolean("include_context", true);
            }

            WriteOptionalString(writer, "context_before", request.ContextBefore);
            WriteOptionalString(writer, "context_after", request.ContextAfter);
            WriteOptionalString(writer, "target_lang", request.TargetLanguage);
            WriteOptionalString(writer, "instruction", request.Instruction);
            WriteOptionalString(writer, "tone", request.Tone);
            WriteOptionalString(writer, "brief", request.Brief);
            WriteOptionalString(writer, "context", request.Context);
            WriteOptionalString(writer, "system_prefix", request.SystemPrefix);
            WriteOptionalString(writer, "system_suffix", request.SystemSuffix);
            WriteOptionalString(writer, "system_override", request.SystemOverride);
            WriteOptionalString(writer, "user_template_override", request.UserTemplateOverride);

            if (request.Glossary is { Count: > 0 })
            {
                writer.WriteStartArray("glossary");
                foreach (var entry in request.Glossary)
                {
                    if (string.IsNullOrWhiteSpace(entry.Source))
                    {
                        continue;
                    }

                    writer.WriteStartObject();
                    writer.WriteString("source", entry.Source);
                    if (!string.IsNullOrWhiteSpace(entry.Target))
                    {
                        writer.WriteString("target", entry.Target);
                    }

                    writer.WriteBoolean("do_not_translate", entry.DoNotTranslate);
                    if (!string.IsNullOrWhiteSpace(entry.Note))
                    {
                        writer.WriteString("note", entry.Note);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();

            if (!string.IsNullOrWhiteSpace(request.Context))
            {
                writer.WriteString("context", request.Context);
            }

            if (!string.IsNullOrWhiteSpace(request.AppName))
            {
                writer.WriteString("app", request.AppName);
            }

            writer.WriteBoolean("stream", request.Stream);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            writer.WriteString(name, value);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            StopProcessLockedAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Ignore shutdown errors on dispose.
        }

        _lifecycleLock.Dispose();
        _writeLock.Dispose();
    }

    private sealed class PendingCall
    {
        public Action<string>? OnDelta { get; }
        public StringBuilder Accumulated { get; } = new();
        public string? Model { get; set; }
        public TaskCompletionSource<EngineResult>? ResultTcs { get; }
        public TaskCompletionSource<EngineHealth>? HealthTcs { get; }
        public TaskCompletionSource<bool>? ConfigTcs { get; }
        public TaskCompletionSource<IReadOnlyList<EngineActionInfo>>? ActionsTcs { get; }

        private PendingCall(
            Action<string>? onDelta,
            TaskCompletionSource<EngineResult>? resultTcs,
            TaskCompletionSource<EngineHealth>? healthTcs,
            TaskCompletionSource<bool>? configTcs,
            TaskCompletionSource<IReadOnlyList<EngineActionInfo>>? actionsTcs)
        {
            OnDelta = onDelta;
            ResultTcs = resultTcs;
            HealthTcs = healthTcs;
            ConfigTcs = configTcs;
            ActionsTcs = actionsTcs;
        }

        public static PendingCall ForAction(Action<string>? onDelta) =>
            new(onDelta, new(TaskCreationOptions.RunContinuationsAsynchronously), null, null, null);

        public static PendingCall ForHealth() =>
            new(null, null, new(TaskCreationOptions.RunContinuationsAsynchronously), null, null);

        public static PendingCall ForConfig() =>
            new(null, null, null, new(TaskCreationOptions.RunContinuationsAsynchronously), null);

        public static PendingCall ForActionsList() =>
            new(null, null, null, null, new(TaskCreationOptions.RunContinuationsAsynchronously));

        public void TrySetException(Exception ex)
        {
            ResultTcs?.TrySetException(ex);
            HealthTcs?.TrySetException(ex);
            ConfigTcs?.TrySetException(ex);
            ActionsTcs?.TrySetException(ex);
        }
    }
}
