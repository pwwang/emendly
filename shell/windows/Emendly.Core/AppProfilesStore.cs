using System.Diagnostics;
using System.Text.Json;

namespace Emendly.Core;

public sealed class AppProfilesStore
{
    public static readonly IReadOnlyList<string> ValidStrategies =
    [
        "uia",
        "scintilla",
        "clipboard",
        "clipboard-ro",
        "clipboard-nokeys"
    ];

    private static readonly HashSet<string> ValidStrategySet =
        new(ValidStrategies, StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _rulesPath;
    private readonly SortedDictionary<string, string> _rules = new(StringComparer.OrdinalIgnoreCase);

    public AppProfilesStore(string? rulesFilePath = null)
    {
        _rulesPath = string.IsNullOrWhiteSpace(rulesFilePath)
            ? EmendlyPaths.DefaultAppRulesPath
            : rulesFilePath;
        Reload();
    }

    public string RulesPath => _rulesPath;

    public static bool IsValidStrategy(string? strategy) =>
        !string.IsNullOrWhiteSpace(strategy) && ValidStrategySet.Contains(strategy.Trim());

    public static string NormalizeExeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = Path.GetFileName(name.Trim()).ToLowerInvariant();
        if (string.IsNullOrEmpty(trimmed))
        {
            return string.Empty;
        }

        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}.exe";
    }

    public IReadOnlyDictionary<string, string> GetAll() =>
        new SortedDictionary<string, string>(_rules, StringComparer.OrdinalIgnoreCase);

    public string? GetRule(string exeName)
    {
        var key = NormalizeExeName(exeName);
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        return _rules.TryGetValue(key, out var strategy) ? strategy : null;
    }

    public string SetRule(string exeName, string strategy)
    {
        var key = NormalizeExeName(exeName);
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("Application executable name must not be empty.", nameof(exeName));
        }

        var normalizedStrategy = strategy?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!IsValidStrategy(normalizedStrategy))
        {
            throw new ArgumentException(
                $"Invalid strategy '{strategy}'. Allowed values: {string.Join(", ", ValidStrategies)}.",
                nameof(strategy));
        }

        _rules[key] = normalizedStrategy;
        Save();
        return key;
    }

    public bool RemoveRule(string exeName)
    {
        var key = NormalizeExeName(exeName);
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var removed = _rules.Remove(key);
        if (removed)
        {
            Save();
        }

        return removed;
    }

    public void Reload()
    {
        _rules.Clear();
        if (!File.Exists(_rulesPath))
        {
            return;
        }

        try
        {
            var raw = File.ReadAllText(_rulesPath);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
            if (parsed is null)
            {
                return;
            }

            foreach (var (exe, strategy) in parsed)
            {
                var key = NormalizeExeName(exe);
                var val = strategy?.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(key) && IsValidStrategy(val))
                {
                    _rules[key] = val!;
                }
            }
        }
        catch (Exception)
        {
            // Ignore malformed file and start clean.
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(_rules, JsonOptions);
        AtomicFile.WriteAllText(_rulesPath, json);
    }

    public static IReadOnlyList<string> GetRunningWindowedApps()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }

        foreach (var proc in processes)
        {
            try
            {
                if (proc.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(proc.ProcessName))
                {
                    names.Add(NormalizeExeName(proc.ProcessName));
                }
            }
            catch (Exception)
            {
                // Access denied or process exited while enumerating.
            }
            finally
            {
                proc.Dispose();
            }
        }

        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
