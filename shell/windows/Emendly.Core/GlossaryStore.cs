using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emendly.Core;

public sealed class GlossaryTerm
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("target_language")]
    public string TargetLanguage { get; set; } = string.Empty;

    [JsonPropertyName("do_not_translate")]
    public bool DoNotTranslate { get; set; }

    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    public GlossaryTerm Clone() => new()
    {
        Id = Id,
        Source = Source,
        Target = Target,
        TargetLanguage = TargetLanguage,
        DoNotTranslate = DoNotTranslate,
        Note = Note,
        Enabled = Enabled
    };
}

public sealed class ProtocolGlossaryEntry
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Target { get; set; }

    [JsonPropertyName("do_not_translate")]
    public bool DoNotTranslate { get; set; }

    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
}

public sealed class GlossaryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dataDirectory;
    private readonly List<GlossaryTerm> _terms = new();

    public GlossaryStore(string? dataDirectory = null)
    {
        _dataDirectory = EmendlyPaths.ResolveDataDirectory(dataDirectory);
        Reload();
    }

    public string FilePath => Path.Combine(_dataDirectory, "glossary.json");

    public IReadOnlyList<GlossaryTerm> GetAll() => _terms.Select(t => t.Clone()).ToList();

    public GlossaryTerm? GetById(string id) =>
        _terms.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))?.Clone();

    public GlossaryTerm Add(GlossaryTerm term)
    {
        ArgumentNullException.ThrowIfNull(term);
        ValidateTerm(term);

        var copy = Normalize(term);
        if (string.IsNullOrWhiteSpace(copy.Id) || _terms.Any(t => string.Equals(t.Id, copy.Id, StringComparison.OrdinalIgnoreCase)))
        {
            copy.Id = Guid.NewGuid().ToString("N")[..12];
        }

        _terms.Add(copy);
        Save();
        return copy.Clone();
    }

    public bool Update(GlossaryTerm term)
    {
        ArgumentNullException.ThrowIfNull(term);
        ValidateTerm(term);

        var index = _terms.FindIndex(t => string.Equals(t.Id, term.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        _terms[index] = Normalize(term);
        Save();
        return true;
    }

    public GlossaryTerm Upsert(GlossaryTerm term)
    {
        if (!string.IsNullOrWhiteSpace(term.Id) && Update(term))
        {
            return GetById(term.Id)!;
        }

        return Add(term);
    }

    public bool Delete(string id)
    {
        var removed = _terms.RemoveAll(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            Save();
        }

        return removed;
    }

    public IReadOnlyList<GlossaryTerm> GetMatchingTerms(string? targetLanguage = null)
    {
        var lang = targetLanguage?.Trim() ?? string.Empty;
        return _terms
            .Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Source))
            .Where(t =>
                string.IsNullOrWhiteSpace(t.TargetLanguage)
                || (!string.IsNullOrEmpty(lang)
                    && string.Equals(t.TargetLanguage.Trim(), lang, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(t => t.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.TargetLanguage, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Clone())
            .ToList();
    }

    public string CompileSystemPrefix(string? targetLanguage = null)
    {
        var matching = GetMatchingTerms(targetLanguage);
        if (matching.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("Terminology rules (follow strictly):");

        foreach (var term in matching)
        {
            var source = term.Source.Trim();
            var note = string.IsNullOrWhiteSpace(term.Note) ? string.Empty : $" (note: {term.Note.Trim()})";
            sb.Append('\n');
            if (term.DoNotTranslate)
            {
                sb.Append($"- \"{source}\" -> keep exactly as \"{source}\" (do not translate){note}");
            }
            else if (!string.IsNullOrWhiteSpace(term.Target))
            {
                sb.Append($"- \"{source}\" -> \"{term.Target.Trim()}\"{note}");
            }
            else
            {
                sb.Append($"- \"{source}\" -> keep exactly as \"{source}\"{note}");
            }
        }

        return sb.ToString();
    }

    public IReadOnlyList<ProtocolGlossaryEntry> ToProtocolEntries(string? targetLanguage = null)
    {
        return GetMatchingTerms(targetLanguage)
            .Select(t => new ProtocolGlossaryEntry
            {
                Source = t.Source.Trim(),
                Target = t.DoNotTranslate || string.IsNullOrWhiteSpace(t.Target) ? null : t.Target.Trim(),
                DoNotTranslate = t.DoNotTranslate,
                Note = string.IsNullOrWhiteSpace(t.Note) ? null : t.Note.Trim()
            })
            .ToList();
    }

    public void Reload()
    {
        _terms.Clear();
        if (!File.Exists(FilePath))
        {
            return;
        }

        try
        {
            var raw = File.ReadAllText(FilePath);
            var parsed = JsonSerializer.Deserialize<List<GlossaryTerm>>(raw, JsonOptions);
            if (parsed is not null)
            {
                _terms.AddRange(parsed.Select(Normalize));
            }
        }
        catch (Exception)
        {
            // Ignore corrupted glossary file and start clean.
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(_terms, JsonOptions);
        AtomicFile.WriteAllText(FilePath, json);
    }

    private static void ValidateTerm(GlossaryTerm term)
    {
        if (string.IsNullOrWhiteSpace(term.Source))
        {
            throw new ArgumentException("Glossary source term must not be empty.", nameof(term));
        }

        if (!term.DoNotTranslate && string.IsNullOrWhiteSpace(term.Target))
        {
            throw new ArgumentException("Glossary term requires either a target replacement or DoNotTranslate.", nameof(term));
        }
    }

    private static GlossaryTerm Normalize(GlossaryTerm term) => new()
    {
        Id = string.IsNullOrWhiteSpace(term.Id) ? Guid.NewGuid().ToString("N")[..12] : term.Id.Trim(),
        Source = (term.Source ?? string.Empty).Trim(),
        Target = term.DoNotTranslate ? string.Empty : (term.Target ?? string.Empty).Trim(),
        TargetLanguage = (term.TargetLanguage ?? string.Empty).Trim(),
        DoNotTranslate = term.DoNotTranslate,
        Note = (term.Note ?? string.Empty).Trim(),
        Enabled = term.Enabled
    };
}
