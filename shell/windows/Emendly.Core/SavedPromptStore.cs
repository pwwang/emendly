using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emendly.Core;

public sealed class SavedPrompt
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("system_prompt")]
    public string SystemPrompt { get; set; } = string.Empty;

    [JsonPropertyName("user_template")]
    public string UserTemplate { get; set; } = "{{text}}";

    [JsonPropertyName("hotkey")]
    public string? Hotkey { get; set; }

    [JsonPropertyName("include_context")]
    public bool IncludeContext { get; set; }

    [JsonPropertyName("requires_selection")]
    public bool RequiresSelection { get; set; } = true;

    public SavedPrompt Clone() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        SystemPrompt = SystemPrompt,
        UserTemplate = UserTemplate,
        Hotkey = Hotkey,
        IncludeContext = IncludeContext,
        RequiresSelection = RequiresSelection
    };
}

public sealed class SavedPromptStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dataDirectory;
    private readonly List<SavedPrompt> _prompts = new();

    public SavedPromptStore(string? dataDirectory = null)
    {
        _dataDirectory = EmendlyPaths.ResolveDataDirectory(dataDirectory);
        Reload();
    }

    public string FilePath => Path.Combine(_dataDirectory, "prompts.json");

    public IReadOnlyList<SavedPrompt> GetAll() => _prompts.Select(p => p.Clone()).ToList();

    public SavedPrompt? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return _prompts.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))?.Clone();
    }

    public SavedPrompt? GetByHotkey(string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            return null;
        }

        return _prompts.FirstOrDefault(p => string.Equals(p.Hotkey, hotkey, StringComparison.OrdinalIgnoreCase))?.Clone();
    }

    public IReadOnlyList<SavedPrompt> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return GetAll();
        }

        var term = query.Trim();
        return _prompts
            .Where(p =>
                p.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || p.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
                || p.SystemPrompt.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (p.Hotkey?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(p => p.Clone())
            .ToList();
    }

    public SavedPrompt Add(SavedPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ValidatePrompt(prompt);

        var copy = Normalize(prompt);
        if (string.IsNullOrWhiteSpace(copy.Id) || _prompts.Any(p => string.Equals(p.Id, copy.Id, StringComparison.OrdinalIgnoreCase)))
        {
            copy.Id = Guid.NewGuid().ToString("N")[..12];
        }

        _prompts.Add(copy);
        Save();
        return copy.Clone();
    }

    public bool Update(SavedPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ValidatePrompt(prompt);

        var index = _prompts.FindIndex(p => string.Equals(p.Id, prompt.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        _prompts[index] = Normalize(prompt);
        Save();
        return true;
    }

    public SavedPrompt Upsert(SavedPrompt prompt)
    {
        if (!string.IsNullOrWhiteSpace(prompt.Id) && Update(prompt))
        {
            return GetById(prompt.Id)!;
        }

        return Add(prompt);
    }

    public bool Delete(string id)
    {
        var removed = _prompts.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            Save();
        }

        return removed;
    }

    public void Reload()
    {
        _prompts.Clear();
        if (!File.Exists(FilePath))
        {
            _prompts.AddRange(CreateStarterPrompts());
            Save();
            return;
        }

        try
        {
            var raw = File.ReadAllText(FilePath);
            var parsed = JsonSerializer.Deserialize<List<SavedPrompt>>(raw, JsonOptions);
            if (parsed is not null)
            {
                _prompts.AddRange(parsed.Select(Normalize));
            }
        }
        catch (Exception)
        {
            _prompts.AddRange(CreateStarterPrompts());
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(_prompts, JsonOptions);
        AtomicFile.WriteAllText(FilePath, json);
    }

    private static void ValidatePrompt(SavedPrompt prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt.Name))
        {
            throw new ArgumentException("Prompt name must not be empty.", nameof(prompt));
        }

        if (string.IsNullOrWhiteSpace(prompt.SystemPrompt))
        {
            throw new ArgumentException("System prompt must not be empty.", nameof(prompt));
        }
    }

    private static SavedPrompt Normalize(SavedPrompt prompt) => new()
    {
        Id = string.IsNullOrWhiteSpace(prompt.Id) ? Guid.NewGuid().ToString("N")[..12] : prompt.Id.Trim(),
        Name = prompt.Name.Trim(),
        Description = (prompt.Description ?? string.Empty).Trim(),
        SystemPrompt = prompt.SystemPrompt.Trim(),
        UserTemplate = string.IsNullOrWhiteSpace(prompt.UserTemplate) ? "{{text}}" : prompt.UserTemplate,
        Hotkey = string.IsNullOrWhiteSpace(prompt.Hotkey) ? null : prompt.Hotkey.Trim(),
        IncludeContext = prompt.IncludeContext,
        RequiresSelection = prompt.RequiresSelection
    };

    private static List<SavedPrompt> CreateStarterPrompts() =>
    [
        new SavedPrompt
        {
            Id = "exec-summary",
            Name = "Executive Summary",
            Description = "Distill long notes or memos into crisp executive bullet points.",
            SystemPrompt = "You are an executive communications editor. Distill the user's text into a concise executive summary with 3-5 clear bullet points highlighting decisions, risks, and next steps. Return only the summary.",
            UserTemplate = "{{text}}",
            Hotkey = "Ctrl+Alt+1",
            IncludeContext = false,
            RequiresSelection = true
        },
        new SavedPrompt
        {
            Id = "tighten-email",
            Name = "Tighten Email",
            Description = "Cut filler words and make an email crisp, direct, and polite.",
            SystemPrompt = "You are an expert business editor. Rewrite the email to be 30-50% shorter, clear, polite, and action-oriented while preserving all factual details and commitments. Return only the revised email.",
            UserTemplate = "{{text}}",
            Hotkey = "Ctrl+Alt+2",
            IncludeContext = false,
            RequiresSelection = true
        },
        new SavedPrompt
        {
            Id = "code-review-comment",
            Name = "Code Review Comment",
            Description = "Turn rough review notes into constructive, specific engineering feedback.",
            SystemPrompt = "You are a senior staff engineer. Rewrite the user's rough note into a clear, respectful, constructive code review comment that explains the concrete risk and suggests a clean fix. Return only the comment.",
            UserTemplate = "{{text}}",
            Hotkey = "Ctrl+Alt+3",
            IncludeContext = true,
            RequiresSelection = true
        },
        new SavedPrompt
        {
            Id = "academic-polish",
            Name = "Academic Polish",
            Description = "Refine prose for clarity, precision, and formal academic style.",
            SystemPrompt = "You are an academic copy editor. Polish the text for formal scholarly tone, logical flow, and lexical precision without changing technical claims, citations, or meaning. Return only the polished text.",
            UserTemplate = "{{text}}",
            Hotkey = "Ctrl+Alt+4",
            IncludeContext = false,
            RequiresSelection = true
        }
    ];
}
