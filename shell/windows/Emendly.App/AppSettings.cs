using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emendly.App;

public sealed class AppSettings
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";
    public const string DefaultModel = "gpt-5-mini";
    public const string DefaultHotkey = "Ctrl+Alt+E";

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    [JsonPropertyName("model")]
    public string Model { get; set; } = DefaultModel;

    [JsonPropertyName("hotkey")]
    public string Hotkey { get; set; } = DefaultHotkey;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string UserSettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Emendly",
            "appsettings.json");

    public static string BundledSettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public static AppSettings Load()
    {
        var path = File.Exists(UserSettingsPath) ? UserSettingsPath : BundledSettingsPath;
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            var raw = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<AppSettings>(raw, JsonOptions);
            if (parsed is null)
            {
                return new AppSettings();
            }

            parsed.BaseUrl = string.IsNullOrWhiteSpace(parsed.BaseUrl) ? DefaultBaseUrl : parsed.BaseUrl.Trim();
            parsed.Model = string.IsNullOrWhiteSpace(parsed.Model) ? DefaultModel : parsed.Model.Trim();
            parsed.Hotkey = string.IsNullOrWhiteSpace(parsed.Hotkey) ? DefaultHotkey : parsed.Hotkey.Trim();
            return parsed;
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(UserSettingsPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Only base_url, model, and hotkey are serialized; API keys never touch disk.
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(UserSettingsPath, json);
    }

    public static string? ReadApiKey()
    {
        var value = Environment.GetEnvironmentVariable("EMENDLY_API_KEY", EnvironmentVariableTarget.Process);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        value = Environment.GetEnvironmentVariable("EMENDLY_API_KEY", EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        value = Environment.GetEnvironmentVariable("EMENDLY_API_KEY", EnvironmentVariableTarget.Machine);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static string? ResolveEnginePath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("EMENDLY_ENGINE");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var resolved = ResolveBinaryCandidate(fromEnv.Trim());
            if (resolved is not null)
            {
                return resolved;
            }
        }

        var baseDir = AppContext.BaseDirectory;
        var relativeDefault = Path.GetFullPath(
            Path.Combine(baseDir, "..", "..", "engine", "target", "debug", "emendly-engine"));
        var direct = ResolveBinaryCandidate(relativeDefault);
        if (direct is not null)
        {
            return direct;
        }

        // Also walk ancestors so `dotnet run` from bin/Debug/net9.0-windows finds repo engine/.
        var fromAncestors = SearchAncestorsForEngine(baseDir, maxDepth: 6)
            ?? SearchAncestorsForEngine(Environment.CurrentDirectory, maxDepth: 4);
        return fromAncestors;
    }

    private static string? SearchAncestorsForEngine(string startDir, int maxDepth)
    {
        var dir = new DirectoryInfo(startDir);
        for (var i = 0; i <= maxDepth && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "engine", "target", "debug", "emendly-engine");
            var resolved = ResolveBinaryCandidate(candidate);
            if (resolved is not null)
            {
                return resolved;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string? ResolveBinaryCandidate(string path)
    {
        if (File.Exists(path))
        {
            return Path.GetFullPath(path);
        }

        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var exePath = path + ".exe";
            if (File.Exists(exePath))
            {
                return Path.GetFullPath(exePath);
            }
        }

        return null;
    }
}

public static class ShellLogger
{
    public static void LogAction(
        string action,
        string processName,
        long latencyMs,
        int inputBytes,
        int outputBytes,
        string status)
    {
        var record = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow.ToString("O"),
            action,
            app = processName,
            latency_ms = latencyMs,
            input_bytes = inputBytes,
            output_bytes = outputBytes,
            status,
        });

        Debug.WriteLine($"[Emendly] {record}");
    }

    public static void LogMeta(string eventName, string processName, string status)
    {
        var record = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow.ToString("O"),
            @event = eventName,
            app = processName,
            status,
        });

        Debug.WriteLine($"[Emendly] {record}");
    }
}
