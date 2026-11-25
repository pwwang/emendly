using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emendly.App;

public sealed class LanguageSettings
{
    public const string DefaultTargetLanguage = "German";

    [JsonPropertyName("default_target")]
    public string DefaultTarget { get; set; } = DefaultTargetLanguage;
}

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

    [JsonPropertyName("language")]
    public LanguageSettings Language { get; set; } = new();

    [JsonPropertyName("target_language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetLanguage { get; set; }

    [JsonPropertyName("context_enabled")]
    public Dictionary<string, bool> ContextEnabled { get; set; } = new(StringComparer.Ordinal);

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
            parsed.Language ??= new LanguageSettings();
            parsed.Language.DefaultTarget = string.IsNullOrWhiteSpace(parsed.Language.DefaultTarget)
                ? LanguageSettings.DefaultTargetLanguage
                : parsed.Language.DefaultTarget.Trim();
            parsed.TargetLanguage = string.IsNullOrWhiteSpace(parsed.TargetLanguage)
                ? null
                : parsed.TargetLanguage.Trim();
            parsed.ContextEnabled = parsed.ContextEnabled is not null
                ? new Dictionary<string, bool>(parsed.ContextEnabled, StringComparer.Ordinal)
                : new Dictionary<string, bool>(StringComparer.Ordinal);
            return parsed;
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    public bool GetContextEnabled(string actionId) =>
        !ContextEnabled.TryGetValue(actionId, out var enabled) || enabled;

    public void SetContextEnabled(string actionId, bool enabled)
    {
        ContextEnabled[actionId] = enabled;
        Save();
    }

    public string GetEffectiveTargetLanguage(string? engineDefaultTarget)
    {
        if (!string.IsNullOrWhiteSpace(TargetLanguage))
        {
            return TargetLanguage;
        }

        if (!string.IsNullOrWhiteSpace(engineDefaultTarget))
        {
            return engineDefaultTarget.Trim();
        }

        return string.IsNullOrWhiteSpace(Language.DefaultTarget)
            ? LanguageSettings.DefaultTargetLanguage
            : Language.DefaultTarget;
    }

    public void SetTargetLanguage(string language)
    {
        var trimmed = language.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        TargetLanguage = trimmed;
        Save();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(UserSettingsPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Only non-secret settings are serialized; API keys never touch disk.
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
        // On Windows the .exe must win. A stale extensionless file in the same folder
        // is usually the Linux build, and starting it fails with Win32 error 193.
        if (OperatingSystem.IsWindows())
        {
            var windowsPath = path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : path + ".exe";
            if (File.Exists(windowsPath))
            {
                return Path.GetFullPath(windowsPath);
            }

            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }

            return null;
        }

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

        Append(record);
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

        Append(record);
    }

    public static void LogProbe(
        string processName,
        string status,
        string strategy,
        bool ambiguousView = false)
    {
        var record = ambiguousView
            ? JsonSerializer.Serialize(new
            {
                ts = DateTimeOffset.UtcNow.ToString("O"),
                @event = "probe",
                app = processName,
                status,
                strategy,
                ambiguous_view = true,
            })
            : JsonSerializer.Serialize(new
            {
                ts = DateTimeOffset.UtcNow.ToString("O"),
                @event = "probe",
                app = processName,
                status,
                strategy,
            });

        Append(record);
    }

    public static void LogFocus(string targetProcess)
    {
        var record = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow.ToString("O"),
            @event = "focus",
            target = targetProcess,
        });

        Append(record);
    }

    public static void LogMenu(string state, int items)
    {
        var record = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow.ToString("O"),
            @event = "menu",
            state,
            items,
        });

        Append(record);
    }

    public static void LogActionOptions(string action, string processName, string options)
    {
        var record = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow.ToString("O"),
            @event = "action",
            action,
            app = processName,
            options,
        });

        Append(record);
    }

    public static void LogNote(string eventName, string message)
    {
        var record = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow.ToString("O"),
            @event = eventName,
            message,
        });

        Append(record);
    }

    private static void Append(string record)
    {
        Debug.WriteLine($"[Emendly] {record}");

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Emendly");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "emendly.log"), record + Environment.NewLine);
        }
        catch
        {
            // Logging must never take the shell down.
        }
    }
}
