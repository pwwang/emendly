using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emendly.Core;

public sealed class ChainStep
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "proofread";

    [JsonPropertyName("tone")]
    public string? Tone { get; set; }

    [JsonPropertyName("target_language")]
    public string? TargetLanguage { get; set; }

    [JsonPropertyName("instruction")]
    public string? Instruction { get; set; }

    [JsonPropertyName("saved_prompt_id")]
    public string? SavedPromptId { get; set; }

    public ChainStep Clone() => new()
    {
        Action = Action,
        Tone = Tone,
        TargetLanguage = TargetLanguage,
        Instruction = Instruction,
        SavedPromptId = SavedPromptId
    };
}

public sealed class ActionChain
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("steps")]
    public List<ChainStep> Steps { get; set; } = new();

    public ActionChain Clone() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        Steps = Steps.Select(s => s.Clone()).ToList()
    };
}

public sealed class ChainStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dataDirectory;
    private readonly List<ActionChain> _chains = new();

    public ChainStore(string? dataDirectory = null)
    {
        _dataDirectory = EmendlyPaths.ResolveDataDirectory(dataDirectory);
        Reload();
    }

    public string FilePath => Path.Combine(_dataDirectory, "chains.json");

    public IReadOnlyList<ActionChain> GetAll() => _chains.Select(c => c.Clone()).ToList();

    public ActionChain? GetById(string id) =>
        _chains.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))?.Clone();

    public ActionChain Add(ActionChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ValidateChain(chain);

        var copy = Normalize(chain);
        if (string.IsNullOrWhiteSpace(copy.Id) || _chains.Any(c => string.Equals(c.Id, copy.Id, StringComparison.OrdinalIgnoreCase)))
        {
            copy.Id = Guid.NewGuid().ToString("N")[..12];
        }

        _chains.Add(copy);
        Save();
        return copy.Clone();
    }

    public bool Update(ActionChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ValidateChain(chain);

        var index = _chains.FindIndex(c => string.Equals(c.Id, chain.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        _chains[index] = Normalize(chain);
        Save();
        return true;
    }

    public ActionChain Upsert(ActionChain chain)
    {
        if (!string.IsNullOrWhiteSpace(chain.Id) && Update(chain))
        {
            return GetById(chain.Id)!;
        }

        return Add(chain);
    }

    public bool Delete(string id)
    {
        var removed = _chains.RemoveAll(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            Save();
        }

        return removed;
    }

    public void Reload()
    {
        _chains.Clear();
        if (!File.Exists(FilePath))
        {
            _chains.AddRange(CreateStarterChains());
            Save();
            return;
        }

        try
        {
            var raw = File.ReadAllText(FilePath);
            var parsed = JsonSerializer.Deserialize<List<ActionChain>>(raw, JsonOptions);
            if (parsed is not null)
            {
                _chains.AddRange(parsed.Select(Normalize));
            }
        }
        catch (Exception)
        {
            _chains.AddRange(CreateStarterChains());
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(_chains, JsonOptions);
        AtomicFile.WriteAllText(FilePath, json);
    }

    private static void ValidateChain(ActionChain chain)
    {
        if (string.IsNullOrWhiteSpace(chain.Name))
        {
            throw new ArgumentException("Chain name must not be empty.", nameof(chain));
        }

        if (chain.Steps is null || chain.Steps.Count == 0)
        {
            throw new ArgumentException("Chain must contain at least one step.", nameof(chain));
        }
    }

    private static ActionChain Normalize(ActionChain chain) => new()
    {
        Id = string.IsNullOrWhiteSpace(chain.Id) ? Guid.NewGuid().ToString("N")[..12] : chain.Id.Trim(),
        Name = chain.Name.Trim(),
        Description = (chain.Description ?? string.Empty).Trim(),
        Steps = (chain.Steps ?? new List<ChainStep>()).Select(s => new ChainStep
        {
            Action = string.IsNullOrWhiteSpace(s.Action) ? "proofread" : s.Action.Trim(),
            Tone = string.IsNullOrWhiteSpace(s.Tone) ? null : s.Tone.Trim(),
            TargetLanguage = string.IsNullOrWhiteSpace(s.TargetLanguage) ? null : s.TargetLanguage.Trim(),
            Instruction = string.IsNullOrWhiteSpace(s.Instruction) ? null : s.Instruction.Trim(),
            SavedPromptId = string.IsNullOrWhiteSpace(s.SavedPromptId) ? null : s.SavedPromptId.Trim()
        }).ToList()
    };

    private static List<ActionChain> CreateStarterChains() =>
    [
        new ActionChain
        {
            Id = "translate-proofread",
            Name = "Translate & Proofread",
            Description = "Translate to the target language, then proofread for natural fluency.",
            Steps =
            [
                new ChainStep { Action = "translate", TargetLanguage = "German" },
                new ChainStep { Action = "proofread" }
            ]
        },
        new ActionChain
        {
            Id = "humanize-tighten",
            Name = "Humanize & Tighten",
            Description = "Make prose sound natural, then tighten for crispness.",
            Steps =
            [
                new ChainStep { Action = "humanize" },
                new ChainStep { Action = "rewrite", Tone = "concise" }
            ]
        }
    ];
}

public sealed record ChainStepProgress(
    int StepIndex,
    int TotalSteps,
    string Action,
    string? StepOutput);

public sealed record ChainRunResult(
    string FinalText,
    IReadOnlyList<string> StepOutputs,
    string Model,
    long TotalLatencyMs,
    long TotalInputTokens,
    long TotalOutputTokens);

public sealed class ChainRunner
{
    private readonly IEngineRunner _engine;
    private readonly SavedPromptStore? _promptStore;

    public ChainRunner(IEngineRunner engine, SavedPromptStore? promptStore = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _promptStore = promptStore;
    }

    public async Task<ChainRunResult> RunAsync(
        ActionChain chain,
        string initialText,
        string? context = null,
        string? glossaryPrefix = null,
        Action<ChainStepProgress>? onStepProgress = null,
        Action<string>? onDelta = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<ProtocolGlossaryEntry>? glossaryEntries = null)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.Steps is null || chain.Steps.Count == 0)
        {
            throw new ArgumentException("Action chain must have at least one step.", nameof(chain));
        }

        var currentText = initialText ?? string.Empty;
        var outputs = new List<string>(chain.Steps.Count);
        long totalLatency = 0;
        long totalIn = 0;
        long totalOut = 0;
        var lastModel = string.Empty;

        for (var i = 0; i < chain.Steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = chain.Steps[i];

            var req = new EngineRequest
            {
                Action = string.IsNullOrWhiteSpace(step.Action) ? "proofread" : step.Action,
                Text = currentText,
                Tone = step.Tone,
                TargetLanguage = step.TargetLanguage,
                Instruction = step.Instruction,
                Context = context,
                SystemPrefix = string.IsNullOrWhiteSpace(glossaryPrefix) ? null : glossaryPrefix,
                Glossary = glossaryEntries is { Count: > 0 } ? glossaryEntries : null,
                Stream = onDelta is not null
            };

            if (!string.IsNullOrWhiteSpace(step.SavedPromptId) && _promptStore is not null)
            {
                var saved = _promptStore.GetById(step.SavedPromptId);
                if (saved is not null)
                {
                    req.Action = "custom";
                    req.SystemOverride = saved.SystemPrompt;
                    req.UserTemplateOverride = saved.UserTemplate;
                    req.IncludeContext = saved.IncludeContext;
                }
            }

            onStepProgress?.Invoke(new ChainStepProgress(i + 1, chain.Steps.Count, req.Action, null));

            var isFinalStep = i == chain.Steps.Count - 1;
            var result = await _engine.RunAsync(
                req,
                isFinalStep ? onDelta : null,
                cancellationToken).ConfigureAwait(false);

            currentText = result.Text;
            outputs.Add(currentText);
            totalLatency += result.LatencyMs;
            totalIn += result.InputTokens;
            totalOut += result.OutputTokens;
            if (!string.IsNullOrEmpty(result.Model))
            {
                lastModel = result.Model;
            }

            onStepProgress?.Invoke(new ChainStepProgress(i + 1, chain.Steps.Count, req.Action, currentText));
        }

        return new ChainRunResult(currentText, outputs, lastModel, totalLatency, totalIn, totalOut);
    }
}
