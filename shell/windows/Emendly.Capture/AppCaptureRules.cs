using System.IO;
using System.Text.Json;

namespace Emendly.Capture;

public sealed class AppCaptureRules
{
    public static readonly AppCaptureRules Empty =
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private readonly Dictionary<string, string> _rules;

    private AppCaptureRules(Dictionary<string, string> rules)
    {
        _rules = rules;
    }

    public static string UserRulesPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Emendly",
            "app-rules.json");

    public static string BundledRulesPath =>
        Path.Combine(AppContext.BaseDirectory, "app-rules.json");

    public static AppCaptureRules LoadDefault()
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        MergeFile(merged, BundledRulesPath);
        MergeFile(merged, UserRulesPath);
        return new AppCaptureRules(merged);
    }

    public static AppCaptureRules LoadFromFile(string path)
    {
        var rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        MergeFile(rules, path);
        return new AppCaptureRules(rules);
    }

    public string? GetStrategyForProcess(string? processName)
    {
        var key = NormalizeProcessName(processName);
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        return _rules.TryGetValue(key, out var strategy) ? strategy : null;
    }

    private static void MergeFile(Dictionary<string, string> target, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            var raw = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
            if (parsed is null)
            {
                return;
            }

            foreach (var (exe, strategy) in parsed)
            {
                var key = NormalizeProcessName(exe);
                var value = strategy?.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                {
                    target[key] = value;
                }
            }
        }
        catch (Exception)
        {
            // Ignore malformed rule files and keep defaults.
        }
    }

    private static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = name.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        return trimmed.ToLowerInvariant();
    }
}
