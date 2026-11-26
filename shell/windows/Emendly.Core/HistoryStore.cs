using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emendly.Core;

public sealed class HistoryEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    [JsonPropertyName("timestamp_utc")]
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("app_name")]
    public string AppName { get; set; } = string.Empty;

    [JsonPropertyName("input_text")]
    public string InputText { get; set; } = string.Empty;

    [JsonPropertyName("output_text")]
    public string OutputText { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("latency_ms")]
    public long LatencyMs { get; set; }

    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; set; }

    public HistoryEntry Clone() => new()
    {
        Id = Id,
        TimestampUtc = TimestampUtc,
        Action = Action,
        AppName = AppName,
        InputText = InputText,
        OutputText = OutputText,
        Model = Model,
        LatencyMs = LatencyMs,
        InputTokens = InputTokens,
        OutputTokens = OutputTokens
    };
}

public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dataDirectory;
    private readonly List<HistoryEntry> _entries = new();
    private bool _isPaused;
    private int _maxEntries;

    public HistoryStore(string? dataDirectory = null, int? maxEntries = null)
    {
        _dataDirectory = EmendlyPaths.ResolveDataDirectory(dataDirectory);
        var settings = HubSettings.Load(_dataDirectory);
        _isPaused = settings.HistoryPaused;
        _maxEntries = maxEntries is > 0 ? maxEntries.Value : settings.HistoryMaxEntries;
        if (_maxEntries <= 0)
        {
            _maxEntries = 500;
        }

        Reload();
    }

    public string FilePath => Path.Combine(_dataDirectory, "history.jsonl");

    public int Count => _entries.Count;

    public int MaxEntries
    {
        get => _maxEntries;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "MaxEntries must be positive.");
            }

            _maxEntries = value;
            var settings = HubSettings.Load(_dataDirectory);
            settings.HistoryMaxEntries = value;
            settings.Save(_dataDirectory);
            TrimToRetention();
            RewriteFile();
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            _isPaused = value;
            var settings = HubSettings.Load(_dataDirectory);
            settings.HistoryPaused = value;
            settings.Save(_dataDirectory);
        }
    }

    public bool Record(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_isPaused)
        {
            return false;
        }

        var copy = entry.Clone();
        if (string.IsNullOrWhiteSpace(copy.Id))
        {
            copy.Id = Guid.NewGuid().ToString("N")[..12];
        }

        if (copy.TimestampUtc == default)
        {
            copy.TimestampUtc = DateTimeOffset.UtcNow;
        }

        _entries.Add(copy);
        TrimToRetention();
        RewriteFile();
        return true;
    }

    public IReadOnlyList<HistoryEntry> Search(
        string? query = null,
        string? actionFilter = null,
        int limit = 200)
    {
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        var q = query?.Trim() ?? string.Empty;
        var hasAction = !string.IsNullOrWhiteSpace(actionFilter)
            && !string.Equals(actionFilter.Trim(), "all", StringComparison.OrdinalIgnoreCase);
        var act = actionFilter?.Trim() ?? string.Empty;

        var results = new List<HistoryEntry>();
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            var e = _entries[i];
            if (hasAction && !string.Equals(e.Action, act, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (hasQuery
                && !e.InputText.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !e.OutputText.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !e.Action.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !e.AppName.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !e.Model.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results.Add(e.Clone());
            if (limit > 0 && results.Count >= limit)
            {
                break;
            }
        }

        return results;
    }

    public IReadOnlyList<string> GetDistinctActions() =>
        _entries
            .Select(e => e.Action)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool Delete(string id)
    {
        var removed = _entries.RemoveAll(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            RewriteFile();
        }

        return removed;
    }

    public void ClearAll()
    {
        _entries.Clear();
        RewriteFile();
    }

    public void Reload()
    {
        _entries.Clear();
        var settings = HubSettings.Load(_dataDirectory);
        _isPaused = settings.HistoryPaused;

        if (!File.Exists(FilePath))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var parsed = JsonSerializer.Deserialize<HistoryEntry>(line, CompactJson);
                    if (parsed is not null)
                    {
                        _entries.Add(parsed);
                    }
                }
                catch (JsonException)
                {
                    // Skip corrupted JSONL lines.
                }
            }

            TrimToRetention();
        }
        catch (IOException)
        {
            // Keep whatever was read cleanly.
        }
    }

    private void TrimToRetention()
    {
        if (_entries.Count > _maxEntries)
        {
            _entries.RemoveRange(0, _entries.Count - _maxEntries);
        }
    }

    private void RewriteFile()
    {
        var sb = new StringBuilder();
        foreach (var entry in _entries)
        {
            sb.Append(JsonSerializer.Serialize(entry, CompactJson));
            sb.Append('\n');
        }

        AtomicFile.WriteAllText(FilePath, sb.ToString());
    }
}
