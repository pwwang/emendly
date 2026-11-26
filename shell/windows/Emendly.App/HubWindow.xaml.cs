using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Emendly.Core;

namespace Emendly.App;

public partial class HubWindow : Window
{
    private static readonly SolidColorBrush NavActiveBg = new(Color.FromRgb(0x1E, 0x29, 0x42));
    private static readonly SolidColorBrush NavActiveBorder = new(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly SolidColorBrush NavActiveFg = new(Color.FromRgb(0xF3, 0xF4, 0xF6));
    private static readonly SolidColorBrush NavInactiveBg = Brushes.Transparent;
    private static readonly SolidColorBrush NavInactiveBorder = Brushes.Transparent;
    private static readonly SolidColorBrush NavInactiveFg = new(Color.FromRgb(0x9C, 0xA3, 0xAF));

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SavedPromptStore _promptStore = new();
    private readonly HistoryStore _historyStore = App.History;
    private readonly GlossaryStore _glossaryStore = new();
    private readonly AppProfilesStore _profilesStore = new();
    private readonly TonePresetStore _toneStore = new();
    private readonly ChainStore _chainStore = new();
    private readonly EngineRunner _engineRunner;
    private readonly ChainRunner _chainRunner;
    private readonly BatchRunner _batchRunner;

    private readonly List<SavedPrompt> _visiblePrompts = new();
    private readonly List<HistoryEntry> _visibleHistory = new();
    private readonly List<GlossaryTerm> _visibleGlossary = new();
    private readonly List<KeyValuePair<string, string>> _visibleProfiles = new();
    private readonly List<ActionChain> _visibleChains = new();
    private readonly List<ChainStep> _editingChainSteps = new();
    private readonly List<TonePreset> _visibleTones = new();

    private string? _editingPromptId;
    private string? _editingGlossaryId;
    private string? _editingChainId;
    private string? _editingToneId;
    private CancellationTokenSource? _batchCts;

    public HubWindow()
    {
        InitializeComponent();

        _engineRunner = new EngineRunner(AppSettings.ResolveEnginePath(), _settings.BaseUrl, _settings.Model);
        _chainRunner = new ChainRunner(_engineRunner, _promptStore);
        _batchRunner = new BatchRunner(_engineRunner, _chainRunner);

        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Closed += (_, _) =>
        {
            _batchCts?.Cancel();
            _batchCts?.Dispose();
            _engineRunner.Dispose();
        };

        InitializeDropdowns();
        RefreshAllViews();
        RefreshEnvironmentStatus();
        NavigateTo("prompts");
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        const int ImmersiveDarkMode = 20;
        const int ImmersiveDarkModeLegacy = 19;
        if (DwmSetWindowAttribute(handle, ImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, ImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }
    }

    private void InitializeDropdowns()
    {
        ProfileStrategyCombo.ItemsSource = AppProfilesStore.ValidStrategies;
        ProfileStrategyCombo.SelectedIndex = 0;
        ChainStepActionCombo.ItemsSource = new[] { "proofread", "rewrite", "translate", "humanize", "compose" };
        ChainStepActionCombo.SelectedIndex = 0;
        BatchActionCombo.ItemsSource = new[] { "proofread", "rewrite", "translate", "humanize", "Selected Chain" };
        BatchActionCombo.SelectedIndex = 0;
        RefreshRunningApps();
        ProfilesPathText.Text = $"Shared rules file: {_profilesStore.RulesPath}";
    }

    private void RefreshAllViews()
    {
        RefreshPromptsList();
        RefreshHistoryFiltersAndList();
        RefreshGlossaryList();
        RefreshProfilesList();
        RefreshChainsList();
        RefreshTonesList();
    }

    private void RefreshEnvironmentStatus()
    {
        var enginePath = EngineRunner.ResolveEnginePath(AppSettings.ResolveEnginePath());
        SidebarEngineStatus.Text = string.IsNullOrEmpty(enginePath)
            ? "Engine: not found (set EMENDLY_ENGINE)"
            : $"Engine: {System.IO.Path.GetFileName(enginePath)}";
        SidebarApiKeyStatus.Text = string.IsNullOrEmpty(AppSettings.ReadApiKey())
            ? "EMENDLY_API_KEY: not set"
            : "EMENDLY_API_KEY: set in environment";
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tab)
        {
            NavigateTo(tab);
        }
    }

    private void NavigateTo(string tab)
    {
        var key = (tab ?? "prompts").Trim().ToLowerInvariant();
        ViewPrompts.Visibility = key == "prompts" ? Visibility.Visible : Visibility.Collapsed;
        ViewHistory.Visibility = key == "history" ? Visibility.Visible : Visibility.Collapsed;
        ViewGlossary.Visibility = key == "glossary" ? Visibility.Visible : Visibility.Collapsed;
        ViewProfiles.Visibility = key == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        ViewChains.Visibility = key == "chains" ? Visibility.Visible : Visibility.Collapsed;
        ViewTones.Visibility = key == "tones" ? Visibility.Visible : Visibility.Collapsed;

        foreach (var (button, tag) in new[]
        {
            (NavPrompts, "prompts"), (NavHistory, "history"), (NavGlossary, "glossary"),
            (NavProfiles, "profiles"), (NavChains, "chains"), (NavTones, "tones")
        })
        {
            var active = tag == key;
            button.Background = active ? NavActiveBg : NavInactiveBg;
            button.BorderBrush = active ? NavActiveBorder : NavInactiveBorder;
            button.Foreground = active ? NavActiveFg : NavInactiveFg;
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        if (key == "history")
        {
            RefreshHistoryFiltersAndList();
        }
        else if (key == "glossary")
        {
            UpdateGlossaryPreview();
        }
    }

    private void RefreshPromptsList(string? selectId = null)
    {
        _visiblePrompts.Clear();
        _visiblePrompts.AddRange(_promptStore.Search(PromptSearchBox.Text));
        PromptsListBox.Items.Clear();
        foreach (var prompt in _visiblePrompts)
        {
            PromptsListBox.Items.Add(string.IsNullOrWhiteSpace(prompt.Hotkey) ? prompt.Name : $"{prompt.Name}  [{prompt.Hotkey}]");
        }

        if (_visiblePrompts.Count > 0)
        {
            var index = string.IsNullOrEmpty(selectId) ? 0 : _visiblePrompts.FindIndex(p => p.Id == selectId);
            PromptsListBox.SelectedIndex = index >= 0 ? index : 0;
        }
    }

    private void OnPromptSearchChanged(object sender, TextChangedEventArgs e) => RefreshPromptsList(_editingPromptId);

    private void OnPromptSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = PromptsListBox.SelectedIndex;
        if (index < 0 || index >= _visiblePrompts.Count)
        {
            return;
        }

        var prompt = _visiblePrompts[index];
        _editingPromptId = prompt.Id;
        PromptNameBox.Text = prompt.Name;
        PromptHotkeyBox.Text = prompt.Hotkey ?? string.Empty;
        PromptDescriptionBox.Text = prompt.Description;
        PromptSystemBox.Text = prompt.SystemPrompt;
        PromptTemplateBox.Text = prompt.UserTemplate;
        PromptIncludeContextCheck.IsChecked = prompt.IncludeContext;
        PromptRequiresSelectionCheck.IsChecked = prompt.RequiresSelection;
        PromptStatusText.Text = string.Empty;
    }

    private void OnPromptNewClick(object sender, RoutedEventArgs e)
    {
        PromptsListBox.SelectedIndex = -1;
        _editingPromptId = null;
        PromptNameBox.Text = string.Empty;
        PromptHotkeyBox.Text = string.Empty;
        PromptDescriptionBox.Text = string.Empty;
        PromptSystemBox.Text = string.Empty;
        PromptTemplateBox.Text = "{{text}}";
        PromptIncludeContextCheck.IsChecked = false;
        PromptRequiresSelectionCheck.IsChecked = true;
        PromptStatusText.Text = "Creating new prompt...";
        PromptNameBox.Focus();
    }

    private void OnPromptSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var saved = _promptStore.Upsert(new SavedPrompt
            {
                Id = _editingPromptId ?? string.Empty,
                Name = PromptNameBox.Text,
                Hotkey = PromptHotkeyBox.Text,
                Description = PromptDescriptionBox.Text,
                SystemPrompt = PromptSystemBox.Text,
                UserTemplate = PromptTemplateBox.Text,
                IncludeContext = PromptIncludeContextCheck.IsChecked == true,
                RequiresSelection = PromptRequiresSelectionCheck.IsChecked == true
            });
            _editingPromptId = saved.Id;
            RefreshPromptsList(saved.Id);
            PromptStatusText.Text = $"Saved '{saved.Name}'.";
        }
        catch (Exception ex)
        {
            PromptStatusText.Text = ex.Message;
        }
    }

    private void OnPromptDeleteClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_editingPromptId) && _promptStore.Delete(_editingPromptId))
        {
            _editingPromptId = null;
            RefreshPromptsList();
            PromptStatusText.Text = "Prompt deleted.";
        }
    }

    private async void OnPromptQuickRunClick(object sender, RoutedEventArgs e)
    {
        var prompt = _promptStore.GetById(_editingPromptId ?? string.Empty);
        var systemPrompt = PromptSystemBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            PromptRunStatusText.Text = "Enter a System Prompt first.";
            return;
        }

        PromptQuickRunButton.IsEnabled = false;
        PromptTestOutputBox.Text = string.Empty;
        PromptRunStatusText.Text = "Running...";
        var input = PromptTestInputBox.Text;
        try
        {
            var prefix = _glossaryStore.CompileSystemPrefix();
            var entries = _glossaryStore.ToProtocolEntries();
            var result = await _engineRunner.RunAsync(new EngineRequest
            {
                Action = "custom",
                Text = input,
                SystemOverride = systemPrompt,
                UserTemplateOverride = string.IsNullOrWhiteSpace(PromptTemplateBox.Text) ? "{{text}}" : PromptTemplateBox.Text,
                SystemPrefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix,
                Glossary = entries.Count > 0 ? entries : null,
                IncludeContext = PromptIncludeContextCheck.IsChecked == true,
                AppName = "Emendly.App",
                Stream = true
            }, delta => Dispatcher.InvokeAsync(() => PromptTestOutputBox.AppendText(delta)));

            PromptTestOutputBox.Text = result.Text;
            PromptRunStatusText.Text = $"Done in {result.LatencyMs} ms ({result.Model})";
            _historyStore.Record(new HistoryEntry
            {
                Action = string.IsNullOrWhiteSpace(prompt?.Name) ? "custom" : $"prompt:{prompt.Name}",
                AppName = "Emendly.App",
                InputText = input,
                OutputText = result.Text,
                Model = result.Model,
                LatencyMs = result.LatencyMs,
                InputTokens = result.InputTokens,
                OutputTokens = result.OutputTokens
            });
        }
        catch (Exception ex)
        {
            PromptRunStatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            PromptQuickRunButton.IsEnabled = true;
        }
    }

    private void OnPromptCopyOutputClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(PromptTestOutputBox.Text))
        {
            Clipboard.SetText(PromptTestOutputBox.Text);
            PromptRunStatusText.Text = "Copied output to clipboard.";
        }
    }

    private void RefreshHistoryFiltersAndList()
    {
        var currentFilter = HistoryActionFilterCombo.SelectedItem as string ?? "All actions";
        var actions = new List<string> { "All actions" };
        actions.AddRange(_historyStore.GetDistinctActions());
        HistoryActionFilterCombo.SelectionChanged -= OnHistoryActionFilterChanged;
        HistoryActionFilterCombo.ItemsSource = actions;
        HistoryActionFilterCombo.SelectedItem = actions.Contains(currentFilter) ? currentFilter : "All actions";
        HistoryActionFilterCombo.SelectionChanged += OnHistoryActionFilterChanged;
        HistoryPauseButton.Content = _historyStore.IsPaused ? "Resume Recording" : "Pause Recording";
        RefreshHistoryListOnly();
    }

    private void RefreshHistoryListOnly()
    {
        var filter = HistoryActionFilterCombo.SelectedItem as string;
        if (string.Equals(filter, "All actions", StringComparison.OrdinalIgnoreCase))
        {
            filter = null;
        }

        _visibleHistory.Clear();
        _visibleHistory.AddRange(_historyStore.Search(HistorySearchBox.Text, filter, limit: 250));
        HistoryListBox.Items.Clear();
        foreach (var entry in _visibleHistory)
        {
            var preview = entry.InputText.Replace('\r', ' ').Replace('\n', ' ');
            if (preview.Length > 52)
            {
                preview = preview[..52] + "...";
            }

            HistoryListBox.Items.Add($"[{entry.TimestampUtc.ToLocalTime():MM-dd HH:mm}] {entry.Action} ({entry.AppName})\n{preview}");
        }

        if (_visibleHistory.Count == 0)
        {
            HistoryDetailMeta.Text = _historyStore.IsPaused ? "Recording is paused. No matching entries." : "No history entries yet.";
            HistoryDetailInputBox.Text = string.Empty;
            HistoryDetailOutputBox.Text = string.Empty;
        }
        else
        {
            HistoryListBox.SelectedIndex = 0;
        }
    }

    private void OnHistorySearchChanged(object sender, TextChangedEventArgs e) => RefreshHistoryListOnly();
    private void OnHistoryActionFilterChanged(object sender, SelectionChangedEventArgs e) => RefreshHistoryListOnly();

    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = HistoryListBox.SelectedIndex;
        if (index < 0 || index >= _visibleHistory.Count)
        {
            return;
        }

        var entry = _visibleHistory[index];
        HistoryDetailMeta.Text = $"{entry.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  {entry.Action}  ·  {entry.AppName}  ·  {entry.Model}  ·  {entry.LatencyMs} ms ({entry.InputTokens}/{entry.OutputTokens} tok)";
        HistoryDetailInputBox.Text = entry.InputText;
        HistoryDetailOutputBox.Text = entry.OutputText;
        HistoryStatusText.Text = string.Empty;
    }

    private void OnHistoryTogglePauseClick(object sender, RoutedEventArgs e)
    {
        _historyStore.IsPaused = !_historyStore.IsPaused;
        HistoryPauseButton.Content = _historyStore.IsPaused ? "Resume Recording" : "Pause Recording";
        HistoryStatusText.Text = _historyStore.IsPaused ? "History recording paused." : "History recording resumed.";
    }

    private void OnHistoryClearAllClick(object sender, RoutedEventArgs e)
    {
        _historyStore.ClearAll();
        RefreshHistoryFiltersAndList();
        HistoryStatusText.Text = "History cleared.";
    }

    private void OnHistoryCopyOutputClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(HistoryDetailOutputBox.Text))
        {
            Clipboard.SetText(HistoryDetailOutputBox.Text);
            HistoryStatusText.Text = "Copied output.";
        }
    }

    private void OnHistoryDeleteEntryClick(object sender, RoutedEventArgs e)
    {
        var index = HistoryListBox.SelectedIndex;
        if (index >= 0 && index < _visibleHistory.Count && _historyStore.Delete(_visibleHistory[index].Id))
        {
            RefreshHistoryFiltersAndList();
            HistoryStatusText.Text = "Entry deleted.";
        }
    }

    private void RefreshGlossaryList(string? selectId = null)
    {
        _visibleGlossary.Clear();
        _visibleGlossary.AddRange(_glossaryStore.GetAll());
        GlossaryListBox.Items.Clear();
        foreach (var term in _visibleGlossary)
        {
            var mapping = term.DoNotTranslate ? "[Do not translate]" : $"-> {term.Target}";
            var language = string.IsNullOrWhiteSpace(term.TargetLanguage) ? "all" : term.TargetLanguage;
            GlossaryListBox.Items.Add($"{term.Source} {mapping} [{language}]{(term.Enabled ? string.Empty : " (disabled)")}");
        }

        if (_visibleGlossary.Count > 0)
        {
            var index = string.IsNullOrEmpty(selectId) ? 0 : _visibleGlossary.FindIndex(t => t.Id == selectId);
            GlossaryListBox.SelectedIndex = index >= 0 ? index : 0;
        }

        UpdateGlossaryPreview();
    }

    private void UpdateGlossaryPreview()
    {
        var compiled = _glossaryStore.CompileSystemPrefix(GlossaryPreviewLangBox.Text);
        GlossaryPreviewBox.Text = string.IsNullOrEmpty(compiled) ? "(No active terminology rules for this language filter.)" : compiled;
    }

    private void OnGlossarySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = GlossaryListBox.SelectedIndex;
        if (index < 0 || index >= _visibleGlossary.Count)
        {
            return;
        }

        var term = _visibleGlossary[index];
        _editingGlossaryId = term.Id;
        GlossarySourceBox.Text = term.Source;
        GlossaryTargetBox.Text = term.Target;
        GlossaryLanguageBox.Text = term.TargetLanguage;
        GlossaryNoteBox.Text = term.Note;
        GlossaryDntCheck.IsChecked = term.DoNotTranslate;
        GlossaryEnabledCheck.IsChecked = term.Enabled;
        GlossaryTargetBox.IsEnabled = !term.DoNotTranslate;
        GlossaryStatusText.Text = string.Empty;
    }

    private void OnGlossaryDntChanged(object sender, RoutedEventArgs e) => GlossaryTargetBox.IsEnabled = GlossaryDntCheck.IsChecked != true;
    private void OnGlossaryPreviewLangChanged(object sender, TextChangedEventArgs e) => UpdateGlossaryPreview();

    private void OnGlossaryNewClick(object sender, RoutedEventArgs e)
    {
        GlossaryListBox.SelectedIndex = -1;
        _editingGlossaryId = null;
        GlossarySourceBox.Text = string.Empty;
        GlossaryTargetBox.Text = string.Empty;
        GlossaryLanguageBox.Text = string.Empty;
        GlossaryNoteBox.Text = string.Empty;
        GlossaryDntCheck.IsChecked = false;
        GlossaryEnabledCheck.IsChecked = true;
        GlossaryTargetBox.IsEnabled = true;
        GlossaryStatusText.Text = "Adding new term...";
        GlossarySourceBox.Focus();
    }

    private void OnGlossarySaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var saved = _glossaryStore.Upsert(new GlossaryTerm
            {
                Id = _editingGlossaryId ?? string.Empty,
                Source = GlossarySourceBox.Text,
                Target = GlossaryTargetBox.Text,
                TargetLanguage = GlossaryLanguageBox.Text,
                Note = GlossaryNoteBox.Text,
                DoNotTranslate = GlossaryDntCheck.IsChecked == true,
                Enabled = GlossaryEnabledCheck.IsChecked == true
            });
            _editingGlossaryId = saved.Id;
            RefreshGlossaryList(saved.Id);
            GlossaryStatusText.Text = $"Saved term '{saved.Source}'.";
        }
        catch (Exception ex)
        {
            GlossaryStatusText.Text = ex.Message;
        }
    }

    private void OnGlossaryDeleteClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_editingGlossaryId) && _glossaryStore.Delete(_editingGlossaryId))
        {
            _editingGlossaryId = null;
            RefreshGlossaryList();
            GlossaryStatusText.Text = "Term deleted.";
        }
    }

    private void RefreshRunningApps() => ProfileAppCombo.ItemsSource = AppProfilesStore.GetRunningWindowedApps();

    private void RefreshProfilesList()
    {
        _visibleProfiles.Clear();
        _visibleProfiles.AddRange(_profilesStore.GetAll());
        ProfilesListBox.Items.Clear();
        foreach (var (exe, strategy) in _visibleProfiles)
        {
            ProfilesListBox.Items.Add($"{exe,-32} ->  {strategy}");
        }
    }

    private void OnProfileRefreshAppsClick(object sender, RoutedEventArgs e)
    {
        RefreshRunningApps();
        ProfilesStatusText.Text = "Refreshed running windowed apps.";
    }

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = ProfilesListBox.SelectedIndex;
        if (index >= 0 && index < _visibleProfiles.Count)
        {
            var rule = _visibleProfiles[index];
            ProfileAppCombo.Text = rule.Key;
            ProfileStrategyCombo.SelectedItem = rule.Value;
        }
    }

    private void OnProfileAddClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var strategy = ProfileStrategyCombo.SelectedItem as string ?? "uia";
            var normalized = _profilesStore.SetRule(ProfileAppCombo.Text, strategy);
            RefreshProfilesList();
            ProfilesStatusText.Text = $"Saved rule: {normalized} -> {strategy}";
        }
        catch (Exception ex)
        {
            ProfilesStatusText.Text = ex.Message;
        }
    }

    private void OnProfileRemoveClick(object sender, RoutedEventArgs e)
    {
        var index = ProfilesListBox.SelectedIndex;
        if (index >= 0 && index < _visibleProfiles.Count)
        {
            var exe = _visibleProfiles[index].Key;
            if (_profilesStore.RemoveRule(exe))
            {
                RefreshProfilesList();
                ProfilesStatusText.Text = $"Removed rule for {exe}.";
            }
        }
    }

    private void OnProfileSaveClick(object sender, RoutedEventArgs e)
    {
        _profilesStore.Save();
        ProfilesStatusText.Text = $"Saved {_visibleProfiles.Count} rule(s) to {_profilesStore.RulesPath}.";
    }

    private void RefreshChainsList(string? selectId = null)
    {
        _visibleChains.Clear();
        _visibleChains.AddRange(_chainStore.GetAll());
        ChainsListBox.Items.Clear();
        foreach (var chain in _visibleChains)
        {
            ChainsListBox.Items.Add($"{chain.Name}  ({string.Join(" -> ", chain.Steps.Select(step => step.Action))})");
        }

        if (_visibleChains.Count > 0)
        {
            var index = string.IsNullOrEmpty(selectId) ? 0 : _visibleChains.FindIndex(c => c.Id == selectId);
            ChainsListBox.SelectedIndex = index >= 0 ? index : 0;
        }
    }

    private void RefreshChainStepsList()
    {
        ChainStepsListBox.Items.Clear();
        for (var i = 0; i < _editingChainSteps.Count; i++)
        {
            var step = _editingChainSteps[i];
            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(step.Tone)) details.Add($"tone={step.Tone}");
            if (!string.IsNullOrWhiteSpace(step.TargetLanguage)) details.Add($"lang={step.TargetLanguage}");
            ChainStepsListBox.Items.Add($"{i + 1}. {step.Action}{(details.Count == 0 ? string.Empty : $" ({string.Join(", ", details)})")}");
        }
    }

    private void OnChainSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = ChainsListBox.SelectedIndex;
        if (index < 0 || index >= _visibleChains.Count)
        {
            return;
        }

        var chain = _visibleChains[index];
        _editingChainId = chain.Id;
        ChainNameBox.Text = chain.Name;
        _editingChainSteps.Clear();
        _editingChainSteps.AddRange(chain.Steps.Select(step => step.Clone()));
        RefreshChainStepsList();
        ChainStatusText.Text = string.Empty;
    }

    private void OnChainNewClick(object sender, RoutedEventArgs e)
    {
        ChainsListBox.SelectedIndex = -1;
        _editingChainId = null;
        ChainNameBox.Text = "New Chain";
        _editingChainSteps.Clear();
        _editingChainSteps.Add(new ChainStep { Action = "proofread" });
        RefreshChainStepsList();
        ChainStatusText.Text = "Editing new chain.";
    }

    private void OnChainAddStepClick(object sender, RoutedEventArgs e)
    {
        _editingChainSteps.Add(new ChainStep
        {
            Action = ChainStepActionCombo.SelectedItem as string ?? "proofread",
            Tone = string.IsNullOrWhiteSpace(ChainStepToneBox.Text) ? null : ChainStepToneBox.Text.Trim(),
            TargetLanguage = string.IsNullOrWhiteSpace(ChainStepLangBox.Text) ? null : ChainStepLangBox.Text.Trim()
        });
        RefreshChainStepsList();
    }

    private void OnChainRemoveStepClick(object sender, RoutedEventArgs e)
    {
        var index = ChainStepsListBox.SelectedIndex;
        if (index >= 0 && index < _editingChainSteps.Count)
        {
            _editingChainSteps.RemoveAt(index);
            RefreshChainStepsList();
        }
    }

    private void OnChainSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var saved = _chainStore.Upsert(new ActionChain
            {
                Id = _editingChainId ?? string.Empty,
                Name = ChainNameBox.Text,
                Steps = _editingChainSteps.Select(step => step.Clone()).ToList()
            });
            _editingChainId = saved.Id;
            RefreshChainsList(saved.Id);
            ChainStatusText.Text = $"Saved '{saved.Name}'.";
        }
        catch (Exception ex)
        {
            ChainStatusText.Text = ex.Message;
        }
    }

    private void OnChainDeleteClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_editingChainId) && _chainStore.Delete(_editingChainId))
        {
            _editingChainId = null;
            RefreshChainsList();
            ChainStatusText.Text = "Chain deleted.";
        }
    }

    private async void OnChainRunClick(object sender, RoutedEventArgs e)
    {
        if (_editingChainSteps.Count == 0)
        {
            ChainStatusText.Text = "Add at least one step first.";
            return;
        }

        var chain = new ActionChain
        {
            Id = _editingChainId ?? "adhoc",
            Name = ChainNameBox.Text,
            Steps = _editingChainSteps.Select(step => step.Clone()).ToList()
        };
        ChainRunButton.IsEnabled = false;
        ChainOutputBox.Text = string.Empty;
        ChainStatusText.Text = "Running chain...";
        var input = ChainInputBox.Text;
        try
        {
            var language = chain.Steps.FirstOrDefault(step => !string.IsNullOrWhiteSpace(step.TargetLanguage))?.TargetLanguage;
            var prefix = _glossaryStore.CompileSystemPrefix(language);
            var entries = _glossaryStore.ToProtocolEntries(language);
            var result = await _chainRunner.RunAsync(
                chain,
                input,
                glossaryPrefix: string.IsNullOrWhiteSpace(prefix) ? null : prefix,
                onStepProgress: progress => Dispatcher.InvokeAsync(() =>
                {
                    ChainStatusText.Text = progress.StepOutput is null
                        ? $"Step {progress.StepIndex}/{progress.TotalSteps}: {progress.Action}..."
                        : $"Completed step {progress.StepIndex}/{progress.TotalSteps} ({progress.Action})";
                }),
                onDelta: delta => Dispatcher.InvokeAsync(() => ChainOutputBox.AppendText(delta)),
                glossaryEntries: entries);
            ChainOutputBox.Text = result.FinalText;
            ChainStatusText.Text = $"Done ({result.StepOutputs.Count} steps, {result.TotalLatencyMs} ms)";
            _historyStore.Record(new HistoryEntry
            {
                Action = $"chain:{chain.Name}",
                AppName = "Emendly.App",
                InputText = input,
                OutputText = result.FinalText,
                Model = result.Model,
                LatencyMs = result.TotalLatencyMs,
                InputTokens = result.TotalInputTokens,
                OutputTokens = result.TotalOutputTokens
            });
        }
        catch (Exception ex)
        {
            ChainStatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            ChainRunButton.IsEnabled = true;
        }
    }

    private void OnChainCopyOutputClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ChainOutputBox.Text))
        {
            Clipboard.SetText(ChainOutputBox.Text);
            ChainStatusText.Text = "Copied chain output.";
        }
    }

    private async void OnBatchRunClick(object sender, RoutedEventArgs e)
    {
        _batchCts?.Cancel();
        _batchCts?.Dispose();
        _batchCts = new CancellationTokenSource();
        var cancellationToken = _batchCts.Token;
        BatchRunButton.IsEnabled = false;
        BatchCancelButton.IsEnabled = true;
        BatchProgressBar.Value = 0;
        BatchOutputBox.Text = string.Empty;
        BatchStatusText.Text = "Starting batch...";
        try
        {
            var mode = BatchActionCombo.SelectedItem as string ?? "proofread";
            var language = string.IsNullOrWhiteSpace(BatchLangBox.Text) ? null : BatchLangBox.Text.Trim();
            var prefix = _glossaryStore.CompileSystemPrefix(language);
            var entries = _glossaryStore.ToProtocolEntries(language);
            BatchRunResult result;
            if (mode == "Selected Chain" && _editingChainSteps.Count > 0)
            {
                var chain = new ActionChain { Name = ChainNameBox.Text, Steps = _editingChainSteps.Select(step => step.Clone()).ToList() };
                result = await _batchRunner.RunChainAsync(
                    BatchInputBox.Text,
                    chain,
                    glossaryPrefix: string.IsNullOrWhiteSpace(prefix) ? null : prefix,
                    onProgress: OnBatchProgress,
                    cancellationToken: cancellationToken,
                    glossaryEntries: entries);
            }
            else
            {
                result = await _batchRunner.RunActionAsync(
                    BatchInputBox.Text,
                    new EngineRequest
                    {
                        Action = mode == "Selected Chain" ? "proofread" : mode,
                        TargetLanguage = language,
                        SystemPrefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix,
                        Glossary = entries.Count > 0 ? entries : null,
                        AppName = "Emendly.App"
                    },
                    onProgress: OnBatchProgress,
                    cancellationToken: cancellationToken);
            }

            BatchOutputBox.Text = result.OutputText;
            BatchProgressBar.Value = 100;
            BatchStatusText.Text = $"Completed {result.ParagraphsProcessed} paragraph(s) in {result.TotalLatencyMs} ms.";
        }
        catch (OperationCanceledException)
        {
            BatchStatusText.Text = "Batch cancelled.";
        }
        catch (Exception ex)
        {
            BatchStatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            BatchRunButton.IsEnabled = true;
            BatchCancelButton.IsEnabled = false;
        }
    }

    private void OnBatchProgress(int current, int total, string latestOutput)
    {
        Dispatcher.InvokeAsync(() =>
        {
            BatchProgressBar.Value = total > 0 ? (double)current / total * 100.0 : 100.0;
            BatchStatusText.Text = $"Processed paragraph {current} / {total}";
        });
    }

    private void OnBatchCancelClick(object sender, RoutedEventArgs e)
    {
        _batchCts?.Cancel();
        BatchCancelButton.IsEnabled = false;
        BatchStatusText.Text = "Cancelling...";
    }

    private void OnBatchCopyOutputClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(BatchOutputBox.Text))
        {
            Clipboard.SetText(BatchOutputBox.Text);
            BatchStatusText.Text = "Copied batch output.";
        }
    }

    private void RefreshTonesList(string? selectId = null)
    {
        _visibleTones.Clear();
        _visibleTones.AddRange(_toneStore.GetAll());
        TonesListBox.Items.Clear();
        foreach (var tone in _visibleTones)
        {
            TonesListBox.Items.Add(tone.Name);
        }

        if (_visibleTones.Count > 0)
        {
            var index = string.IsNullOrEmpty(selectId) ? 0 : _visibleTones.FindIndex(t => t.Id == selectId);
            TonesListBox.SelectedIndex = index >= 0 ? index : 0;
        }
    }

    private void OnToneSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = TonesListBox.SelectedIndex;
        if (index < 0 || index >= _visibleTones.Count)
        {
            return;
        }

        var tone = _visibleTones[index];
        _editingToneId = tone.Id;
        ToneNameBox.Text = tone.Name;
        ToneDescriptionBox.Text = tone.Description;
        ToneInstructionsBox.Text = tone.Instructions;
        ToneStatusText.Text = string.Empty;
    }

    private void OnToneNewClick(object sender, RoutedEventArgs e)
    {
        TonesListBox.SelectedIndex = -1;
        _editingToneId = null;
        ToneNameBox.Text = string.Empty;
        ToneDescriptionBox.Text = string.Empty;
        ToneInstructionsBox.Text = string.Empty;
        ToneStatusText.Text = "Creating new tone preset...";
        ToneNameBox.Focus();
    }

    private void OnToneSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var saved = _toneStore.Upsert(new TonePreset
            {
                Id = _editingToneId ?? string.Empty,
                Name = ToneNameBox.Text,
                Description = ToneDescriptionBox.Text,
                Instructions = ToneInstructionsBox.Text
            });
            _editingToneId = saved.Id;
            RefreshTonesList(saved.Id);
            ToneStatusText.Text = $"Saved '{saved.Name}'.";
        }
        catch (Exception ex)
        {
            ToneStatusText.Text = ex.Message;
        }
    }

    private void OnToneDeleteClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_editingToneId) && _toneStore.Delete(_editingToneId))
        {
            _editingToneId = null;
            RefreshTonesList();
            ToneStatusText.Text = "Tone preset deleted.";
        }
    }
}
