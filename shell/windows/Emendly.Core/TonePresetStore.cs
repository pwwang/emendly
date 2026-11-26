using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emendly.Core;

public sealed class TonePreset
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("instructions")]
    public string Instructions { get; set; } = string.Empty;

    public TonePreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        Instructions = Instructions
    };
}

public sealed class TonePresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dataDirectory;
    private readonly List<TonePreset> _tones = new();

    public TonePresetStore(string? dataDirectory = null)
    {
        _dataDirectory = EmendlyPaths.ResolveDataDirectory(dataDirectory);
        Reload();
    }

    public string FilePath => Path.Combine(_dataDirectory, "tones.json");

    public IReadOnlyList<TonePreset> GetAll() => _tones.Select(t => t.Clone()).ToList();

    public TonePreset? GetById(string id) =>
        _tones.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))?.Clone();

    public TonePreset Add(TonePreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ValidatePreset(preset);

        var copy = Normalize(preset);
        if (string.IsNullOrWhiteSpace(copy.Id) || _tones.Any(t => string.Equals(t.Id, copy.Id, StringComparison.OrdinalIgnoreCase)))
        {
            copy.Id = Guid.NewGuid().ToString("N")[..12];
        }

        _tones.Add(copy);
        Save();
        return copy.Clone();
    }

    public bool Update(TonePreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ValidatePreset(preset);

        var index = _tones.FindIndex(t => string.Equals(t.Id, preset.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        _tones[index] = Normalize(preset);
        Save();
        return true;
    }

    public TonePreset Upsert(TonePreset preset)
    {
        if (!string.IsNullOrWhiteSpace(preset.Id) && Update(preset))
        {
            return GetById(preset.Id)!;
        }

        return Add(preset);
    }

    public bool Delete(string id)
    {
        var removed = _tones.RemoveAll(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            Save();
        }

        return removed;
    }

    public void Reload()
    {
        _tones.Clear();
        if (!File.Exists(FilePath))
        {
            _tones.AddRange(CreateStarterTones());
            Save();
            return;
        }

        try
        {
            var raw = File.ReadAllText(FilePath);
            var parsed = JsonSerializer.Deserialize<List<TonePreset>>(raw, JsonOptions);
            if (parsed is not null)
            {
                _tones.AddRange(parsed.Select(Normalize));
            }
        }
        catch (Exception)
        {
            _tones.AddRange(CreateStarterTones());
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(_tones, JsonOptions);
        AtomicFile.WriteAllText(FilePath, json);
    }

    private static void ValidatePreset(TonePreset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.Name))
        {
            throw new ArgumentException("Tone preset name must not be empty.", nameof(preset));
        }

        if (string.IsNullOrWhiteSpace(preset.Instructions))
        {
            throw new ArgumentException("Tone preset instructions must not be empty.", nameof(preset));
        }
    }

    private static TonePreset Normalize(TonePreset preset) => new()
    {
        Id = string.IsNullOrWhiteSpace(preset.Id) ? Guid.NewGuid().ToString("N")[..12] : preset.Id.Trim(),
        Name = preset.Name.Trim(),
        Description = (preset.Description ?? string.Empty).Trim(),
        Instructions = preset.Instructions.Trim()
    };

    private static List<TonePreset> CreateStarterTones() =>
    [
        new TonePreset
        {
            Id = "concise-executive",
            Name = "Concise Executive",
            Description = "Bottom-line up front, short active sentences, zero fluff.",
            Instructions = "Write in a concise executive tone: lead with the conclusion, use active voice, prefer short sentences, and eliminate hedging or filler phrases."
        },
        new TonePreset
        {
            Id = "warm-colleague",
            Name = "Warm Colleague",
            Description = "Approachable, collaborative, and respectful workplace prose.",
            Instructions = "Write in a warm, collegial tone: friendly and collaborative without being overly casual, clear about next steps, and respectful of the reader's time."
        },
        new TonePreset
        {
            Id = "technical-spec",
            Name = "Technical Spec",
            Description = "Unambiguous, precise engineering language suitable for RFCs and docs.",
            Instructions = "Write in a crisp technical specification tone: unambiguous, objective, precise with terminology and constraints, avoiding marketing adjectives."
        },
        new TonePreset
        {
            Id = "plain-spoken",
            Name = "Plain Spoken",
            Description = "Clear everyday language accessible to any reader.",
            Instructions = "Write in plain, everyday English: simple familiar words, short paragraphs, direct phrasing, and zero corporate jargon."
        }
    ];
}
