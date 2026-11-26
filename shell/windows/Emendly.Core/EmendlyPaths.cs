using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emendly.Core;

public static class EmendlyPaths
{
    public static readonly IReadOnlyList<string> MigratedFiles =
    [
        "prompts.json",
        "history.jsonl",
        "glossary.json",
        "tones.json",
        "chains.json",
        "settings.json"
    ];

    public static string DefaultDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Emendly");

    public static string LegacyDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EmendlyPro");

    public static string DefaultAppRulesPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Emendly",
            "app-rules.json");

    public static string ResolveDataDirectory(string? customDirectory)
    {
        if (!string.IsNullOrWhiteSpace(customDirectory))
        {
            return customDirectory;
        }

        Directory.CreateDirectory(DefaultDataDirectory);
        MigrateLegacyFiles();
        return DefaultDataDirectory;
    }

    // Copies each pre-rename file across once, only when the new location is
    // still empty, and leaves the old file untouched so a downgrade still works.
    private static void MigrateLegacyFiles()
    {
        if (!Directory.Exists(LegacyDataDirectory))
        {
            return;
        }

        foreach (var name in MigratedFiles)
        {
            var legacy = Path.Combine(LegacyDataDirectory, name);
            var current = Path.Combine(DefaultDataDirectory, name);
            if (!File.Exists(legacy) || File.Exists(current))
            {
                continue;
            }

            try
            {
                File.Copy(legacy, current);
            }
            catch (IOException)
            {
                // Leave the legacy file in place; the store recreates what it needs.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = Path.Combine(dir ?? ".", $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, content, Utf8NoBom);
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class HubSettings
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";
    public const string DefaultModel = "gpt-5-mini";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    [JsonPropertyName("history_paused")]
    public bool HistoryPaused { get; set; }

    [JsonPropertyName("history_max_entries")]
    public int HistoryMaxEntries { get; set; } = 500;

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    [JsonPropertyName("model")]
    public string Model { get; set; } = DefaultModel;

    [JsonPropertyName("engine_path")]
    public string? EnginePath { get; set; }

    public static string GetFilePath(string? dataDirectory = null) =>
        Path.Combine(EmendlyPaths.ResolveDataDirectory(dataDirectory), "settings.json");

    public static HubSettings Load(string? dataDirectory = null)
    {
        var path = GetFilePath(dataDirectory);
        if (!File.Exists(path))
        {
            return new HubSettings();
        }

        try
        {
            var raw = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<HubSettings>(raw, JsonOptions) ?? new HubSettings();
            if (parsed.HistoryMaxEntries <= 0)
            {
                parsed.HistoryMaxEntries = 500;
            }

            return parsed;
        }
        catch (Exception)
        {
            return new HubSettings();
        }
    }

    public void Save(string? dataDirectory = null)
    {
        var path = GetFilePath(dataDirectory);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        AtomicFile.WriteAllText(path, json);
    }
}
