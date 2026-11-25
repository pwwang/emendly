using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Emendly.Capture;

namespace Emendly.App;

public partial class PanelWindow : Window
{
    private enum PanelStage
    {
        Unsupported,
        ChooseAction,
        PromptInput,
        Streaming,
        PreviewReady,
        ErrorOrCancelled,
    }

    private sealed record ActionRowElements(
        Border Row,
        Button? OptionButton,
        Path? ContextGlyphPath,
        Path? ContextSlashPath,
        TextBlock? LanguageCodeText,
        MenuAction Action);

    private const int MaxSelectionCharacters = 4000;

    private static readonly Brush RowNormalBg = Brushes.Transparent;
    private static readonly Brush RowSelectedBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x47));
    private static readonly Brush RowSelectedBorder = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
    private static readonly Brush IconAccentBrush = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
    private static readonly Brush IconMutedBrush = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly Brush BadgeBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x35));
    private static readonly Brush BadgeText = new SolidColorBrush(Color.FromRgb(0x93, 0xC5, 0xFD));
    private static readonly Brush LabelText = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6));
    private static readonly Brush DescText = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly Brush GroupHeaderText = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));

    private readonly WindowInfo _targetWindow;
    private readonly IntPtr _targetHwnd;
    private readonly string _strategyName;
    private readonly IntPtr _editorHwnd;
    private readonly SelectionClassification _classification;
    private readonly SurroundingContext? _surroundingContext;
    private readonly CaretLocation _caretLocation;
    private readonly AppSettings _settings;
    private readonly ITransformer _transformer;
    private readonly ClipboardInserter _inserter;
    private readonly List<MenuAction> _visibleActions = new();
    private readonly List<ActionRowElements> _actionRows = new();

    private PanelStage _stage = PanelStage.ChooseAction;
    private bool _expanded;
    private string _filter = string.Empty;
    private int _selectedIndex;
    private bool _optionsFocused;
    private bool _contextPreviewExpanded;
    private MenuAction? _highlighted;
    private string _targetLanguage = LanguageSettings.DefaultTargetLanguage;
    private bool _languagePickerOpen;
    private bool _updatingLanguageList;
    private string? _promptActionId;
    private string? _lastPromptActionId;
    private bool _promptForceOptionsOff;
    private string _activeAction = string.Empty;
    private ActionKind _activeKind;
    private bool _activeReadOnly;
    private string _resultText = string.Empty;
    private CancellationTokenSource? _requestCts;
    private bool _applying;
    private bool _closing;
    private bool _focusConfirmed;

    public event EventHandler? PasteCompleted;

    public event EventHandler? ActivationFailed;

    public PanelWindow(
        WindowInfo targetWindow,
        SelectionClassification classification,
        SurroundingContext? surroundingContext,
        CaretLocation caretLocation,
        AppSettings settings,
        ITransformer transformer,
        ClipboardInserter inserter,
        string? unsupportedReason,
        string strategyName = "uia",
        IntPtr editorHwnd = default)
    {
        InitializeComponent();

        _targetWindow = targetWindow;
        _targetHwnd = targetWindow.Hwnd;
        _strategyName = strategyName;
        _editorHwnd = editorHwnd;
        _classification = classification;
        _surroundingContext = surroundingContext;
        _caretLocation = caretLocation;
        _settings = settings;
        _transformer = transformer;
        _inserter = inserter;
        _targetLanguage = settings.GetEffectiveTargetLanguage(transformer.DefaultTargetLanguage);

        ModeBadgeText.Text = transformer.ModeName;

        if (!string.IsNullOrWhiteSpace(unsupportedReason))
        {
            ShowUnsupported(unsupportedReason);
        }
        else
        {
            SetStage(PanelStage.ChooseAction);
            BuildMenu(resetSelection: true);
        }

        PositionNearCaret(estimatedHeight: 420);
    }

    public void ShowAndActivate()
    {
        if (_closing)
        {
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        TakeKeyboardFocus();
        _ = ConfirmKeyboardFocusAsync();
    }

    private void ShowUnsupported(string reason)
    {
        UnsupportedReasonText.Text = reason;
        SetStage(PanelStage.Unsupported);
        ModeLineText.Text = string.IsNullOrWhiteSpace(_targetWindow.ProcessName)
            ? "Emendly"
            : $"Emendly · {_targetWindow.ProcessName}";
        ShellLogger.LogMenu("unsupported", 0);
    }

    private void TakeKeyboardFocus()
    {
        if (_closing || !IsVisible)
        {
            return;
        }

        var helper = new WindowInteropHelper(this);
        if (helper.Handle != IntPtr.Zero)
        {
            _ = WindowEnumerator.RestoreForeground(helper.Handle);
        }

        Activate();
        Focus();
        FocusStageTarget();
    }

    private void FocusStageTarget()
    {
        if (_closing || !IsVisible)
        {
            return;
        }

        if (_languagePickerOpen)
        {
            LanguageSearchBox.Focus();
            Keyboard.Focus(LanguageSearchBox);
            return;
        }

        switch (_stage)
        {
            case PanelStage.PromptInput:
                if (_optionsFocused
                    && _promptActionId == "compose"
                    && PromptContextChipButton.IsEnabled)
                {
                    PromptContextChipButton.Focus();
                    Keyboard.Focus(PromptContextChipButton);
                }
                else
                {
                    PromptInputBox.Focus();
                    Keyboard.Focus(PromptInputBox);
                }
                break;

            case PanelStage.ChooseAction:
                if (_optionsFocused && GetSelectedOptionButton() is { IsEnabled: true } optionButton)
                {
                    optionButton.Focus();
                    Keyboard.Focus(optionButton);
                }
                else
                {
                    _optionsFocused = false;
                    ActionListPanel.Focus();
                    Keyboard.Focus(ActionListPanel);
                }
                break;

            case PanelStage.Streaming:
                SecondaryButton.Focus();
                Keyboard.Focus(SecondaryButton);
                break;

            case PanelStage.PreviewReady:
            case PanelStage.ErrorOrCancelled:
                PrimaryButton.Focus();
                Keyboard.Focus(PrimaryButton);
                break;

            default:
                Keyboard.Focus(this);
                break;
        }
    }

    private async Task ConfirmKeyboardFocusAsync()
    {
        // Show() only requests activation. A hotkey-triggered window may be denied
        // foreground for a few hundred milliseconds, so check and retry twice.
        const int checkDelayMs = 150;
        const int maxChecks = 3;

        for (var check = 0; check < maxChecks; check++)
        {
            await Task.Delay(checkDelayMs);

            if (_closing || !IsVisible)
            {
                return;
            }

            if (IsKeyboardFocusWithin)
            {
                if (!_focusConfirmed)
                {
                    _focusConfirmed = true;
                    ShellLogger.LogFocus(_targetWindow.ProcessName);
                }

                return;
            }

            if (check + 1 < maxChecks)
            {
                TakeKeyboardFocus();
            }
        }

        ShellLogger.LogMeta("focus", _targetWindow.ProcessName, "failed");
        _focusConfirmed = false;
        _closing = true;
        Hide();
        Close();
        ActivationFailed?.Invoke(this, EventArgs.Empty);
    }

    public void CancelActiveRequest()
    {
        var cts = _requestCts;
        if (cts is not null && !cts.IsCancellationRequested)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Request already completed and disposed its token source.
            }
        }
    }

    private void SetStage(PanelStage stage)
    {
        _stage = stage;

        UnsupportedPanel.Visibility = stage == PanelStage.Unsupported ? Visibility.Visible : Visibility.Collapsed;
        MenuPanel.Visibility = stage == PanelStage.ChooseAction ? Visibility.Visible : Visibility.Collapsed;
        PromptPanel.Visibility = stage == PanelStage.PromptInput ? Visibility.Visible : Visibility.Collapsed;

        var previewVisible = stage is PanelStage.Streaming or PanelStage.PreviewReady or PanelStage.ErrorOrCancelled;
        PreviewPanel.Visibility = previewVisible ? Visibility.Visible : Visibility.Collapsed;

        LongSelectionBanner.Visibility = stage != PanelStage.Unsupported
            && _classification.CharacterCount > MaxSelectionCharacters
            ? Visibility.Visible
            : Visibility.Collapsed;

        switch (stage)
        {
            case PanelStage.ChooseAction:
                UpdateRowOptions();
                UpdateMenuHints();
                break;

            case PanelStage.PromptInput:
                UpdatePromptOptions();
                PromptErrorText.Visibility = Visibility.Collapsed;
                break;

            case PanelStage.Streaming:
                ConfigureStreamingButtons();
                break;

            case PanelStage.PreviewReady:
                ConfigurePreviewButtons();
                break;

            case PanelStage.ErrorOrCancelled:
                ConfigureErrorButtons();
                break;
        }

        UpdateModeLine();

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_closing && _stage == stage && IsVisible)
            {
                FocusStageTarget();
            }
        });
    }

    // ---- Menu construction -------------------------------------------------

    private void BuildMenu(bool resetSelection)
    {
        ActionListPanel.Children.Clear();
        _visibleActions.Clear();
        _actionRows.Clear();

        if (_expanded)
        {
            var groups = MenuModel.FilterExpanded(_filter);
            foreach (var group in groups)
            {
                AddGroupHeader(group.Label);
                foreach (var action in group.Items)
                {
                    AddActionRow(action);
                }
            }

            if (_visibleActions.Count == 0)
            {
                ActionListPanel.Children.Add(new TextBlock
                {
                    Text = "No matching actions",
                    Foreground = DescText,
                    Margin = new Thickness(8, 12, 0, 0),
                });
            }
        }
        else
        {
            foreach (var action in MenuModel.GetDefault(_classification.State))
            {
                AddActionRow(action);
            }

            AddMoreRow();
        }

        FilterBar.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        FilterText.Text = _filter.Length == 0 ? "Type to filter actions" : _filter;

        if (_visibleActions.Count == 0)
        {
            _selectedIndex = -1;
        }
        else
        {
            if (resetSelection)
            {
                _selectedIndex = 0;
            }

            _selectedIndex = Math.Clamp(_selectedIndex, 0, _visibleActions.Count - 1);
        }

        _optionsFocused = false;
        UpdateSelectionVisuals();

        var state = _expanded
            ? (_filter.Length == 0 ? "expanded" : "filtered")
            : StateLabelLower(_classification.State);
        ShellLogger.LogMenu(state, _visibleActions.Count);
    }

    private void AddGroupHeader(string label)
    {
        ActionListPanel.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            Foreground = GroupHeaderText,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 8, 0, 2),
        });
    }

    private void AddActionRow(MenuAction action)
    {
        var index = _visibleActions.Count;
        var number = index + 1;

        var numberBadge = new Border
        {
            Background = BadgeBg,
            CornerRadius = new CornerRadius(4),
            Width = 22,
            Height = 22,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = number.ToString(),
                Foreground = BadgeText,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var labelStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labelStack.Children.Add(new TextBlock
        {
            Text = action.Label,
            Foreground = LabelText,
            FontWeight = FontWeights.Medium,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        labelStack.Children.Add(new TextBlock
        {
            Text = action.Description,
            Foreground = DescText,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var itemGrid = new Grid();
        itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        itemGrid.Children.Add(numberBadge);
        Grid.SetColumn(labelStack, 1);
        itemGrid.Children.Add(labelStack);

        var rowGrid = new Grid();
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.Children.Add(itemGrid);

        Button? optionButton = null;
        Path? contextGlyphPath = null;
        Path? contextSlashPath = null;
        TextBlock? languageCodeText = null;

        if (action.Options == ActionOptionKind.Context)
        {
            contextGlyphPath = new Path
            {
                Data = (Geometry)FindResource("ContextIconGeometry"),
                Width = 16,
                Height = 16,
                Stretch = Stretch.None,
                Stroke = IconAccentBrush,
                StrokeThickness = 1.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };

            contextSlashPath = new Path
            {
                Data = (Geometry)FindResource("ContextSlashGeometry"),
                Width = 16,
                Height = 16,
                Stretch = Stretch.None,
                Stroke = IconMutedBrush,
                StrokeThickness = 1.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Visibility = Visibility.Collapsed,
            };

            var iconGrid = new Grid { Width = 16, Height = 16 };
            iconGrid.Children.Add(contextGlyphPath);
            iconGrid.Children.Add(contextSlashPath);

            optionButton = new Button
            {
                Style = (Style)FindResource("OptionIconButtonStyle"),
                Margin = new Thickness(8, 0, 2, 0),
                Content = iconGrid,
                Visibility = Visibility.Collapsed,
            };
            optionButton.Click += OnContextChipClick;

            Grid.SetColumn(optionButton, 1);
            rowGrid.Children.Add(optionButton);
        }
        else if (action.Options == ActionOptionKind.Language)
        {
            var globePath = new Path
            {
                Data = (Geometry)FindResource("GlobeIconGeometry"),
                Width = 9,
                Height = 9,
                Stretch = Stretch.None,
                Stroke = IconAccentBrush,
                StrokeThickness = 0.9,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
            };

            languageCodeText = new TextBlock
            {
                Foreground = IconAccentBrush,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                LineHeight = 9,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
            };

            var iconGrid = new Grid { Width = 18, Height = 18 };
            iconGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(9) });
            iconGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(9) });
            Grid.SetRow(globePath, 0);
            Grid.SetRow(languageCodeText, 1);
            iconGrid.Children.Add(globePath);
            iconGrid.Children.Add(languageCodeText);

            optionButton = new Button
            {
                Style = (Style)FindResource("OptionIconButtonStyle"),
                Margin = new Thickness(8, 0, 2, 0),
                Content = iconGrid,
                Visibility = Visibility.Collapsed,
            };
            optionButton.Click += OnLanguageChipClick;

            Grid.SetColumn(optionButton, 1);
            rowGrid.Children.Add(optionButton);
        }

        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(2, 0, 0, 0),
            BorderBrush = Brushes.Transparent,
            Background = RowNormalBg,
            Padding = new Thickness(8, 6, 10, 6),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = Cursors.Hand,
            Child = rowGrid,
        };

        row.MouseEnter += (_, _) =>
        {
            if (_selectedIndex != index)
            {
                SelectIndex(index);
            }
        };
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (IsDescendantOf(e.OriginalSource as DependencyObject, optionButton))
            {
                return;
            }

            SelectIndex(index);
            TriggerAction(action, forceOptionsOff: false);
        };

        _visibleActions.Add(action);
        _actionRows.Add(new ActionRowElements(row, optionButton, contextGlyphPath, contextSlashPath, languageCodeText, action));
        ActionListPanel.Children.Add(row);
    }

    private void AddMoreRow()
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = RowNormalBg,
            Padding = new Thickness(8, 6, 10, 6),
            Margin = new Thickness(0, 3, 0, 1),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "More actions…",
                Foreground = BadgeText,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
            },
        };

        row.MouseLeftButtonUp += (_, _) => SetExpanded(true);
        ActionListPanel.Children.Add(row);
    }

    private void SelectIndex(int index)
    {
        if (_visibleActions.Count == 0)
        {
            return;
        }

        var wasOptionsFocused = _optionsFocused;
        _selectedIndex = ((index % _visibleActions.Count) + _visibleActions.Count) % _visibleActions.Count;
        _optionsFocused = false;
        UpdateSelectionVisuals();

        if (wasOptionsFocused)
        {
            FocusStageTarget();
        }
    }

    private void MoveSelection(int delta) => SelectIndex(_selectedIndex + delta);

    private MenuAction? GetSelectedAction() =>
        _selectedIndex >= 0 && _selectedIndex < _visibleActions.Count
            ? _visibleActions[_selectedIndex]
            : null;

    private ActionRowElements? GetSelectedRow() =>
        _selectedIndex >= 0 && _selectedIndex < _actionRows.Count
            ? _actionRows[_selectedIndex]
            : null;

    private Button? GetSelectedOptionButton()
    {
        var row = GetSelectedRow();
        return row?.OptionButton is { Visibility: Visibility.Visible } optionButton ? optionButton : null;
    }

    private bool CanFocusCurrentOption =>
        GetSelectedOptionButton() is { IsEnabled: true };

    private void UpdateSelectionVisuals()
    {
        for (var i = 0; i < _actionRows.Count; i++)
        {
            var isSelected = i == _selectedIndex;
            var entry = _actionRows[i];
            entry.Row.Background = isSelected ? RowSelectedBg : RowNormalBg;
            entry.Row.BorderBrush = isSelected ? RowSelectedBorder : Brushes.Transparent;
        }

        if (_selectedIndex >= 0 && _selectedIndex < _actionRows.Count && IsLoaded)
        {
            _actionRows[_selectedIndex].Row.BringIntoView();
        }

        UpdateRowOptions();
        UpdateMenuHints();
        UpdateModeLine();
    }

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded)
        {
            return;
        }

        _expanded = expanded;
        _filter = string.Empty;
        _optionsFocused = false;
        BuildMenu(resetSelection: true);
        FocusStageTarget();
    }

    // ---- Row options -------------------------------------------------------

    private bool HasVisibleOptions =>
        _highlighted is { Options: not ActionOptionKind.None };

    private void UpdateRowOptions()
    {
        _highlighted = _stage == PanelStage.ChooseAction ? GetSelectedAction() : null;

        for (var i = 0; i < _actionRows.Count; i++)
        {
            var entry = _actionRows[i];
            if (entry.OptionButton is null)
            {
                continue;
            }

            var isHighlighted = _stage == PanelStage.ChooseAction && i == _selectedIndex;
            if (!isHighlighted)
            {
                entry.OptionButton.Visibility = Visibility.Collapsed;
                continue;
            }

            ConfigureRowOption(entry);
            entry.OptionButton.Visibility = Visibility.Visible;
        }

        UpdateContextPreview();
    }

    private void ConfigureRowOption(ActionRowElements entry)
    {
        if (entry.OptionButton is null)
        {
            return;
        }

        if (entry.Action.Options == ActionOptionKind.Context)
        {
            if (_surroundingContext is null)
            {
                entry.OptionButton.IsEnabled = false;
                entry.OptionButton.ToolTip = "Context unavailable in this app";
                if (entry.ContextGlyphPath is not null)
                {
                    entry.ContextGlyphPath.Stroke = IconMutedBrush;
                }

                if (entry.ContextSlashPath is not null)
                {
                    entry.ContextSlashPath.Visibility = Visibility.Visible;
                }

                return;
            }

            var on = _settings.GetContextEnabled(entry.Action.Id);
            entry.OptionButton.IsEnabled = true;
            entry.OptionButton.ToolTip = on
                ? "Include surrounding sentences (on)"
                : "Include surrounding sentences (off)";
            if (entry.ContextGlyphPath is not null)
            {
                entry.ContextGlyphPath.Stroke = on ? IconAccentBrush : IconMutedBrush;
            }

            if (entry.ContextSlashPath is not null)
            {
                entry.ContextSlashPath.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            }

            return;
        }

        if (entry.Action.Options == ActionOptionKind.Language)
        {
            entry.OptionButton.IsEnabled = true;
            entry.OptionButton.ToolTip = $"Target language: {_targetLanguage}";
            if (entry.LanguageCodeText is not null)
            {
                entry.LanguageCodeText.Text = MenuModel.GetLanguageCode(_targetLanguage);
            }
        }
    }

    private void UpdateContextPreview()
    {
        if (_stage != PanelStage.ChooseAction
            || _highlighted is not { Options: ActionOptionKind.Context }
            || _surroundingContext is null
            || !_settings.GetContextEnabled(_highlighted.Id))
        {
            ContextPreviewBorder.Visibility = Visibility.Collapsed;
            return;
        }

        ContextPreviewBorder.Visibility = Visibility.Visible;
        ContextPreviewHeaderText.Text = BuildContextHeader(_surroundingContext, "Context preview");
        ContextPreviewBox.Text = BuildContextText(_surroundingContext);
        ContextPreviewBox.Visibility = _contextPreviewExpanded
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static bool IsDescendantOf(DependencyObject? source, DependencyObject? ancestor)
    {
        if (source is null || ancestor is null)
        {
            return false;
        }

        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private void UpdatePromptOptions()
    {
        var isCompose = _promptActionId == "compose";
        PromptContextSection.Visibility = isCompose ? Visibility.Visible : Visibility.Collapsed;
        if (!isCompose)
        {
            PromptContextPreviewBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var context = _surroundingContext;
        if (context is null)
        {
            PromptContextChipButton.IsEnabled = false;
            PromptContextChipButton.Content = "Context unavailable in this app";
            PromptContextPreviewBorder.Visibility = Visibility.Collapsed;
            return;
        }

        PromptContextChipButton.IsEnabled = true;
        var on = _settings.GetContextEnabled("compose");
        PromptContextChipButton.Content = on ? "Context: on" : "Context: off";

        if (on)
        {
            PromptContextPreviewBorder.Visibility = Visibility.Visible;
            PromptContextPreviewHeaderText.Text = BuildContextHeader(context, "Context preview");
            PromptContextPreviewBox.Text = BuildContextText(context);
            PromptContextPreviewBox.Visibility = _contextPreviewExpanded
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        else
        {
            PromptContextPreviewBorder.Visibility = Visibility.Collapsed;
        }
    }

    private string BuildContextHeader(SurroundingContext context, string title)
    {
        var counts = context.HasAny
            ? $"{context.Before.Length}+{context.After.Length} chars"
            : "no surrounding text";
        return $"{title} · {counts} · click to {(_contextPreviewExpanded ? "hide" : "show")}";
    }

    private static string BuildContextText(SurroundingContext context)
    {
        var parts = new List<string>(2);
        if (context.Before.Length > 0)
        {
            parts.Add(context.Before);
        }

        if (context.After.Length > 0)
        {
            parts.Add(context.After);
        }

        return parts.Count == 0 ? "(no surrounding text)" : string.Join(Environment.NewLine, parts);
    }

    private void UpdateMenuHints()
    {
        if (_stage != PanelStage.ChooseAction)
        {
            return;
        }

        var parts = new List<string>(4);
        if (_expanded)
        {
            parts.Add(_filter.Length == 0 ? "Type to filter" : "Backspace deletes");
            parts.Add("Left back");
            if (CanFocusCurrentOption && !_optionsFocused)
            {
                parts.Add("Tab options");
            }
        }
        else
        {
            parts.Add("Tab more actions");
            if (CanFocusCurrentOption)
            {
                parts.Add("Space option");
            }
        }

        if (_optionsFocused)
        {
            parts.Add(_highlighted?.Options == ActionOptionKind.Language
                ? "Space cycles · Enter picks · Esc back"
                : "Space toggles · Esc back");
        }
        else
        {
            parts.Add("Esc closes");
        }

        MenuHintText.Text = string.Join(" · ", parts);
    }

    private void UpdateModeLine()
    {
        if (_stage == PanelStage.Unsupported)
        {
            return;
        }

        MenuAction? action = _stage switch
        {
            PanelStage.PromptInput => _promptActionId is null ? null : MenuModel.FindById(_promptActionId),
            PanelStage.ChooseAction => GetSelectedAction(),
            _ => MenuModel.FindById(_activeAction),
        };

        var label = action?.Label ?? "Emendly";
        ModeLineText.Text = $"{label} · {_classification.CharacterCount} characters · {StateLabel(_classification.State)}";
    }

    private void ToggleContext(string actionId)
    {
        if (_surroundingContext is null)
        {
            return;
        }

        var enabled = !_settings.GetContextEnabled(actionId);
        _settings.SetContextEnabled(actionId, enabled);

        if (_stage == PanelStage.PromptInput)
        {
            UpdatePromptOptions();
        }
        else
        {
            UpdateRowOptions();
        }
    }

    private void CycleLanguage()
    {
        _targetLanguage = MenuModel.GetNextLanguage(_targetLanguage);
        _settings.SetTargetLanguage(_targetLanguage);
        UpdateRowOptions();
    }

    private void ToggleOrCycleOption()
    {
        if (_highlighted is null)
        {
            return;
        }

        switch (_highlighted.Options)
        {
            case ActionOptionKind.Context:
                ToggleContext(_highlighted.Id);
                break;
            case ActionOptionKind.Language:
                CycleLanguage();
                break;
        }
    }

    private void ConfirmOption()
    {
        if (_highlighted is null)
        {
            return;
        }

        switch (_highlighted.Options)
        {
            case ActionOptionKind.Language:
                OpenLanguagePicker();
                break;
            case ActionOptionKind.Context:
                ToggleContext(_highlighted.Id);
                break;
        }
    }

    private void FocusOptions()
    {
        if (GetSelectedOptionButton() is { IsEnabled: true } optionButton)
        {
            _optionsFocused = true;
            optionButton.Focus();
            Keyboard.Focus(optionButton);
        }

        UpdateMenuHints();
    }

    // ---- Language picker ---------------------------------------------------

    private void OpenLanguagePicker()
    {
        _languagePickerOpen = true;
        LanguagePickerOverlay.Visibility = Visibility.Visible;

        _updatingLanguageList = true;
        LanguageSearchBox.Text = string.Empty;
        _updatingLanguageList = false;

        PopulateLanguages(string.Empty);

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_languagePickerOpen)
            {
                LanguageSearchBox.Focus();
                Keyboard.Focus(LanguageSearchBox);
            }
        });
    }

    private void CloseLanguagePicker(bool restoreFocus)
    {
        _languagePickerOpen = false;
        LanguagePickerOverlay.Visibility = Visibility.Collapsed;

        if (restoreFocus)
        {
            _optionsFocused = CanFocusCurrentOption;
            UpdateMenuHints();
            FocusStageTarget();
        }
    }

    private void PopulateLanguages(string term)
    {
        var current = _targetLanguage;
        var trimmed = term.Trim();
        var matches = new List<string>();

        foreach (var language in MenuModel.GetLanguages(_targetLanguage))
        {
            if (trimmed.Length == 0
                || language.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(language);
            }
        }

        var options = new List<string>();
        if (trimmed.Length > 0 && matches.Count == 0)
        {
            options.Add(trimmed);
        }

        options.AddRange(matches);

        _updatingLanguageList = true;
        LanguageListBox.Items.Clear();
        foreach (var option in options)
        {
            LanguageListBox.Items.Add(option);
        }

        if (options.Count > 0)
        {
            var selected = options.FindIndex(l => string.Equals(l, current, StringComparison.OrdinalIgnoreCase));
            LanguageListBox.SelectedIndex = selected >= 0 ? selected : 0;
        }

        _updatingLanguageList = false;
    }

    private void MoveLanguageSelection(int delta)
    {
        var count = LanguageListBox.Items.Count;
        if (count == 0)
        {
            return;
        }

        var index = LanguageListBox.SelectedIndex;
        index = index < 0 ? 0 : ((index + delta) % count + count) % count;
        LanguageListBox.SelectedIndex = index;
        LanguageListBox.ScrollIntoView(LanguageListBox.Items[index]);
    }

    private void ApplyLanguage(string language)
    {
        var trimmed = language.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        _settings.SetTargetLanguage(trimmed);
        _targetLanguage = trimmed;

        CloseLanguagePicker(restoreFocus: false);
        UpdateRowOptions();
        _optionsFocused = _highlighted?.Options == ActionOptionKind.Language && CanFocusCurrentOption;
        UpdateMenuHints();
        FocusStageTarget();
    }

    // ---- Actions -----------------------------------------------------------

    private void TriggerAction(MenuAction action, bool forceOptionsOff)
    {
        _optionsFocused = false;

        switch (action.Kind)
        {
            case ActionKind.Insert:
            case ActionKind.Custom:
                OpenPrompt(action.Id, forceOptionsOff);
                break;

            default:
                _ = RunActionAsync(action, forceOptionsOff);
                break;
        }
    }

    private void OpenPrompt(string actionId, bool forceOptionsOff)
    {
        var action = MenuModel.FindById(actionId);
        if (action is null)
        {
            return;
        }

        _promptActionId = actionId;
        _promptForceOptionsOff = forceOptionsOff;
        _optionsFocused = false;

        if (!string.Equals(_lastPromptActionId, actionId, StringComparison.Ordinal))
        {
            PromptInputBox.Text = string.Empty;
            _lastPromptActionId = actionId;
        }

        PromptTitleText.Text = actionId == "custom"
            ? "Your instruction"
            : "Brief";
        PromptErrorText.Visibility = Visibility.Collapsed;

        UpdatePromptOptions();
        SetStage(PanelStage.PromptInput);
    }

    private void RunPrompt()
    {
        var text = PromptInputBox.Text.Trim();
        var isCompose = _promptActionId == "compose";

        if (text.Length == 0)
        {
            PromptErrorText.Text = isCompose
                ? "Describe what to write before running."
                : "Enter an instruction before running.";
            PromptErrorText.Visibility = Visibility.Visible;
            PromptInputBox.Focus();
            return;
        }

        var action = _promptActionId is null ? null : MenuModel.FindById(_promptActionId);
        if (action is null)
        {
            return;
        }

        PromptErrorText.Visibility = Visibility.Collapsed;
        _ = RunActionAsync(action, _promptForceOptionsOff, text);
        _promptForceOptionsOff = false;
    }

    private string BuildSelectionText()
    {
        var text = _classification.TrimmedText;
        return text.Length <= MaxSelectionCharacters ? text : text[..MaxSelectionCharacters];
    }

    private Dictionary<string, object> BuildOptions(
        MenuAction action,
        bool forceOptionsOff,
        string? promptText,
        out string log)
    {
        var options = new Dictionary<string, object>(StringComparer.Ordinal);
        var logParts = new List<string>(2);

        switch (action.Id)
        {
            case "compose":
                if (!string.IsNullOrWhiteSpace(promptText))
                {
                    options["brief"] = promptText.Trim();
                }
                break;

            case "custom":
                if (!string.IsNullOrWhiteSpace(promptText))
                {
                    options["instruction"] = promptText.Trim();
                }
                break;
        }

        if (action.Options == ActionOptionKind.Context)
        {
            var context = _surroundingContext;
            var enabled = !forceOptionsOff
                && _settings.GetContextEnabled(action.Id)
                && context is not null
                && context.HasAny;

            options["include_context"] = enabled;
            if (enabled && context is not null)
            {
                if (context.Before.Length > 0)
                {
                    options["context_before"] = context.Before;
                }

                if (context.After.Length > 0)
                {
                    options["context_after"] = context.After;
                }
            }

            logParts.Add(enabled ? "context=on" : "context=off");
        }
        else if (action.Options == ActionOptionKind.Language)
        {
            if (!forceOptionsOff)
            {
                options["target_lang"] = _targetLanguage;
                logParts.Add($"lang={_targetLanguage}");
            }
            else
            {
                logParts.Add("lang=default");
            }
        }

        log = logParts.Count == 0 ? "none" : string.Join(",", logParts);
        return options;
    }

    private async Task RunActionAsync(MenuAction action, bool forceOptionsOff, string? promptText = null)
    {
        CancelActiveRequest();
        _requestCts?.Dispose();
        var cts = new CancellationTokenSource();
        _requestCts = cts;

        _activeAction = action.Id;
        _activeKind = action.Kind;
        _activeReadOnly = _classification.ReadOnly;
        _resultText = string.Empty;
        _optionsFocused = false;
        _languagePickerOpen = false;

        var selectionText = BuildSelectionText();
        var options = BuildOptions(action, forceOptionsOff, promptText, out var optionsLog);
        ShellLogger.LogActionOptions(action.Id, _targetWindow.ProcessName, optionsLog);

        var inputText = action.Id == "compose" ? string.Empty : selectionText;

        var inputBytes = Encoding.UTF8.GetByteCount(inputText)
            + Encoding.UTF8.GetByteCount(promptText ?? string.Empty);
        foreach (var value in options.Values)
        {
            if (value is string textValue)
            {
                inputBytes += Encoding.UTF8.GetByteCount(textValue);
            }
        }

        PreviewBox.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
        SetStage(PanelStage.Streaming);

        var request = new TransformRequest(
            Action: action.Id,
            Text: inputText,
            Context: null,
            AppProcess: _targetWindow.ProcessName,
            Options: options.Count > 0 ? options : null);

        var sw = Stopwatch.StartNew();
        var streamedText = new StringBuilder();

        try
        {
            var result = await Task.Run(
                () => _transformer.TransformAsync(
                    request,
                    delta =>
                    {
                        streamedText.Append(delta);
                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            if (_stage == PanelStage.Streaming && ReferenceEquals(_requestCts, cts))
                            {
                                PreviewBox.AppendText(delta);
                                PreviewBox.ScrollToEnd();
                            }
                        });
                    },
                    cts.Token),
                cts.Token);

            sw.Stop();
            if (!ReferenceEquals(_requestCts, cts))
            {
                return;
            }

            // The done payload is authoritative; accumulated deltas are the fallback when
            // the engine omits done.text. Never reuse the request text here.
            var doneText = !string.IsNullOrEmpty(result.Text) ? result.Text : streamedText.ToString();
            if (string.IsNullOrEmpty(doneText))
            {
                doneText = PreviewBox.Text;
            }

            _resultText = doneText;
            PreviewBox.Text = _resultText;
            PreviewBox.ScrollToHome();

            var latency = result.LatencyMs > 0 ? result.LatencyMs : sw.ElapsedMilliseconds;

            ShellLogger.LogAction(
                action.Id,
                _targetWindow.ProcessName,
                latency,
                inputBytes,
                Encoding.UTF8.GetByteCount(_resultText),
                "ok");

            SetStage(PanelStage.PreviewReady);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            ShellLogger.LogAction(action.Id, _targetWindow.ProcessName, sw.ElapsedMilliseconds, inputBytes, 0, "cancelled");
            if (ReferenceEquals(_requestCts, cts) && !_closing)
            {
                PreviewStatusText.Text = "cancelled";
                ErrorText.Text = "Request cancelled.";
                ErrorText.Visibility = Visibility.Visible;
                SetStage(PanelStage.ErrorOrCancelled);
            }
        }
        catch (TransformException ex)
        {
            sw.Stop();
            ShellLogger.LogAction(action.Id, _targetWindow.ProcessName, sw.ElapsedMilliseconds, inputBytes, 0, ex.Code);
            if (ReferenceEquals(_requestCts, cts) && !_closing)
            {
                PreviewStatusText.Text = ex.Code;
                ErrorText.Text = ex.Message;
                ErrorText.Visibility = Visibility.Visible;
                SetStage(PanelStage.ErrorOrCancelled);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            ShellLogger.LogAction(action.Id, _targetWindow.ProcessName, sw.ElapsedMilliseconds, inputBytes, 0, "internal");
            if (ReferenceEquals(_requestCts, cts) && !_closing)
            {
                PreviewStatusText.Text = "error";
                ErrorText.Text = ex.Message;
                ErrorText.Visibility = Visibility.Visible;
                SetStage(PanelStage.ErrorOrCancelled);
            }
        }
        finally
        {
            if (ReferenceEquals(_requestCts, cts))
            {
                _requestCts = null;
            }

            cts.Dispose();
        }
    }

    private Task SwitchReferenceListAsync()
    {
        var otherId = _activeAction == "synonyms" ? "antonyms" : "synonyms";
        var action = MenuModel.FindById(otherId);
        return action is null ? Task.CompletedTask : RunActionAsync(action, forceOptionsOff: false);
    }

    private void ConfigureStreamingButtons()
    {
        PreviewLockIcon.Visibility = Visibility.Visible;
        PreviewActionText.Text = "Generating…";
        PreviewStatusText.Text = "streaming";
        PreviewStreamingAccentBar.Visibility = Visibility.Visible;
        PreviewHintText.Text = "Esc cancels and returns";
        ErrorText.Visibility = Visibility.Collapsed;
        ReadOnlyNoticeText.Visibility = Visibility.Collapsed;

        PrimaryButton.Visibility = Visibility.Collapsed;
        SecondaryActionButton.Visibility = Visibility.Collapsed;
        SwitchListButton.Visibility = Visibility.Collapsed;
        SecondaryButton.Content = "Back";
        SecondaryButton.Visibility = Visibility.Visible;
    }

    private void ConfigurePreviewButtons()
    {
        var isReadOnly = _activeReadOnly;
        var isReferenceList = _activeKind == ActionKind.ReferenceList;
        var hasSelection = _classification.State != SelectionState.Empty;

        PreviewLockIcon.Visibility = Visibility.Collapsed;
        PreviewActionText.Text = "Result";
        PreviewStatusText.Text = "done";
        PreviewStreamingAccentBar.Visibility = Visibility.Collapsed;

        PrimaryButton.Content = isReadOnly || isReferenceList
            ? "Copy"
            : hasSelection
                ? "Replace"
                : "Insert";
        PrimaryButton.Visibility = Visibility.Visible;

        SecondaryButton.Content = "Back";
        SecondaryButton.Visibility = Visibility.Visible;

        SecondaryActionButton.Content = hasSelection ? "Replace" : "Insert";
        SecondaryActionButton.Visibility = !isReadOnly && isReferenceList
            ? Visibility.Visible
            : Visibility.Collapsed;

        SwitchListButton.Content = _activeAction == "synonyms" ? "Antonyms (A)" : "Synonyms (S)";
        SwitchListButton.Visibility = isReferenceList ? Visibility.Visible : Visibility.Collapsed;

        ReadOnlyNoticeText.Text = "Read-only field: nothing will be written.";
        ReadOnlyNoticeText.Visibility = isReadOnly ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;

        PreviewHintText.Text = isReadOnly
            ? "Enter copies · Esc back"
            : isReferenceList
                ? "Enter copies · A/S flips · Esc back"
                : hasSelection
                    ? "Enter replaces · Esc back"
                    : "Enter inserts · Esc back";
    }

    private void ConfigureErrorButtons()
    {
        PreviewLockIcon.Visibility = Visibility.Collapsed;
        PreviewActionText.Text = "Result";
        PreviewStreamingAccentBar.Visibility = Visibility.Collapsed;

        PrimaryButton.Content = "Back";
        PrimaryButton.Visibility = Visibility.Visible;
        SecondaryButton.Content = "Close";
        SecondaryButton.Visibility = Visibility.Visible;
        SecondaryActionButton.Visibility = Visibility.Collapsed;
        SwitchListButton.Visibility = Visibility.Collapsed;
        ReadOnlyNoticeText.Visibility = Visibility.Collapsed;
        PreviewHintText.Text = "Enter or Esc goes back";
    }

    private void InvokePrimaryResultAction()
    {
        if (_stage != PanelStage.PreviewReady)
        {
            return;
        }

        if (_activeReadOnly || _activeKind == ActionKind.ReferenceList)
        {
            _ = CopyResultAsync(_resultText);
        }
        else
        {
            _ = WriteResultAsync(_resultText);
        }
    }

    private void DiscardResultAndReturnToMenu()
    {
        var cts = _requestCts;
        _requestCts = null;
        if (cts is not null && !cts.IsCancellationRequested)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Request already completed and disposed its token source.
            }
        }

        _resultText = string.Empty;
        PreviewBox.Text = string.Empty;
        PreviewStatusText.Text = string.Empty;
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
        ReadOnlyNoticeText.Visibility = Visibility.Collapsed;

        _optionsFocused = false;
        SetStage(PanelStage.ChooseAction);
        UpdateSelectionVisuals();
        FocusStageTarget();
    }

    private async Task CopyResultAsync(string text)
    {
        if (_applying || string.IsNullOrEmpty(text))
        {
            return;
        }

        _applying = true;
        Hide();

        var sw = Stopwatch.StartNew();
        try
        {
            await _inserter.CopyTextAsync(text);
            sw.Stop();
            ShellLogger.LogAction(
                "copy",
                _targetWindow.ProcessName,
                sw.ElapsedMilliseconds,
                Encoding.UTF8.GetByteCount(_classification.UntrimmedText),
                Encoding.UTF8.GetByteCount(text),
                "ok");
        }
        catch (Exception)
        {
            sw.Stop();
            ShellLogger.LogAction("copy", _targetWindow.ProcessName, sw.ElapsedMilliseconds, 0, 0, "error");
        }
        finally
        {
            if (!_closing)
            {
                _closing = true;
                _ = WindowEnumerator.RestoreForeground(_targetHwnd);
                Close();
            }
        }
    }

    private async Task WriteResultAsync(string text)
    {
        if (_applying || string.IsNullOrEmpty(text))
        {
            return;
        }

        _applying = true;
        Hide();

        var hasSelection = _classification.State != SelectionState.Empty;
        var textToInsert = _classification.RestoreSurroundingWhitespace(text);

        var sw = Stopwatch.StartNew();
        try
        {
            var pasted = await _inserter.PasteAsync(
                targetHwnd: _targetHwnd,
                processName: _targetWindow.ProcessName,
                actionName: _activeAction,
                textToInsert: textToInsert,
                replacedText: hasSelection ? _classification.UntrimmedText : string.Empty,
                wasSelection: hasSelection,
                strategyName: _strategyName,
                editorHwnd: _editorHwnd);

            sw.Stop();
            ShellLogger.LogAction(
                "apply",
                _targetWindow.ProcessName,
                sw.ElapsedMilliseconds,
                Encoding.UTF8.GetByteCount(_classification.UntrimmedText),
                Encoding.UTF8.GetByteCount(textToInsert),
                pasted ? "ok" : "failed");

            if (pasted)
            {
                PasteCompleted?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                NoticeWindow.ShowNotice(
                    _targetWindow.ProcessName,
                    "Could not confirm focus returned to the target window, so nothing was pasted.",
                    _caretLocation);
            }
        }
        catch (Exception)
        {
            sw.Stop();
            ShellLogger.LogAction("apply", _targetWindow.ProcessName, sw.ElapsedMilliseconds, 0, 0, "error");
        }
        finally
        {
            if (!_closing)
            {
                _closing = true;
                Close();
            }
        }
    }

    public void CloseAndRestoreFocus()
    {
        if (_closing)
        {
            return;
        }

        CancelActiveRequest();
        _resultText = string.Empty;
        PreviewBox.Text = string.Empty;
        _closing = true;
        Hide();
        _ = WindowEnumerator.RestoreForeground(_targetHwnd);
        Close();
    }

    // ---- Keyboard ----------------------------------------------------------

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_languagePickerOpen)
        {
            HandleLanguagePickerKey(e);
            return;
        }

        switch (_stage)
        {
            case PanelStage.Unsupported:
                if (e.Key is Key.Escape or Key.Enter)
                {
                    e.Handled = true;
                    CloseAndRestoreFocus();
                }
                break;

            case PanelStage.ChooseAction:
                HandleChooseActionKey(e);
                break;

            case PanelStage.PromptInput:
                HandlePromptInputKey(e);
                break;

            case PanelStage.Streaming:
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    DiscardResultAndReturnToMenu();
                }
                else if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                }
                break;

            case PanelStage.PreviewReady:
                HandlePreviewKey(e);
                break;

            case PanelStage.ErrorOrCancelled:
                if (e.Key is Key.Escape or Key.Enter)
                {
                    e.Handled = true;
                    DiscardResultAndReturnToMenu();
                }
                break;
        }
    }

    private void HandleChooseActionKey(KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        var ctrl = (modifiers & ModifierKeys.Control) != 0;
        var shift = (modifiers & ModifierKeys.Shift) != 0;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_optionsFocused)
            {
                _optionsFocused = false;
                FocusStageTarget();
                UpdateMenuHints();
            }
            else
            {
                CloseAndRestoreFocus();
            }

            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_optionsFocused)
            {
                ConfirmOption();
                return;
            }

            var action = GetSelectedAction();
            if (action is not null)
            {
                TriggerAction(action, forceOptionsOff: ctrl);
            }

            return;
        }

        if (e.Key == Key.Up)
        {
            e.Handled = true;
            MoveSelection(-1);
            return;
        }

        if (e.Key == Key.Down)
        {
            e.Handled = true;
            MoveSelection(1);
            return;
        }

        if (e.Key == Key.Tab)
        {
            e.Handled = true;
            HandleTab(shift);
            return;
        }

        if (e.Key == Key.Right)
        {
            e.Handled = true;
            if (!_expanded)
            {
                SetExpanded(true);
            }

            return;
        }

        if (e.Key == Key.Left)
        {
            e.Handled = true;
            if (_optionsFocused)
            {
                _optionsFocused = false;
                FocusStageTarget();
                UpdateMenuHints();
            }
            else if (_expanded)
            {
                SetExpanded(false);
            }

            return;
        }

        if (e.Key == Key.Space)
        {
            if (HasVisibleOptions)
            {
                e.Handled = true;
                ToggleOrCycleOption();
            }

            return;
        }

        if (e.Key == Key.Back)
        {
            if (_expanded)
            {
                e.Handled = true;
                if (_filter.Length > 0)
                {
                    _filter = _filter[..^1];
                    BuildMenu(resetSelection: true);
                }
                else
                {
                    SetExpanded(false);
                }
            }

            return;
        }

        var digit = DigitFromKey(e.Key);
        if (digit > 0)
        {
            e.Handled = true;
            if (digit <= _visibleActions.Count)
            {
                _selectedIndex = digit - 1;
                _optionsFocused = false;
                UpdateSelectionVisuals();
                TriggerAction(_visibleActions[digit - 1], forceOptionsOff: ctrl);
            }

            return;
        }

        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
        {
            return;
        }

        var letter = LetterFromKey(e.Key);
        if (letter is null)
        {
            return;
        }

        e.Handled = true;
        if (!_expanded)
        {
            _expanded = true;
            _filter = string.Empty;
        }

        _filter += letter.Value;
        BuildMenu(resetSelection: true);
    }

    private void HandleTab(bool shift)
    {
        if (shift)
        {
            if (_optionsFocused)
            {
                _optionsFocused = false;
                FocusStageTarget();
                UpdateMenuHints();
                return;
            }

            if (_expanded)
            {
                SetExpanded(false);
            }

            return;
        }

        if (_optionsFocused)
        {
            _optionsFocused = false;
            FocusStageTarget();
            UpdateMenuHints();
            return;
        }

        if (_expanded)
        {
            if (CanFocusCurrentOption)
            {
                FocusOptions();
            }

            return;
        }

        SetExpanded(true);
    }

    private void HandlePromptInputKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_optionsFocused)
            {
                _optionsFocused = false;
                FocusStageTarget();
            }
            else
            {
                CloseAndRestoreFocus();
            }

            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                var caret = PromptInputBox.CaretIndex;
                PromptInputBox.Text = PromptInputBox.Text.Insert(caret, Environment.NewLine);
                PromptInputBox.CaretIndex = caret + Environment.NewLine.Length;
                return;
            }

            RunPrompt();
            return;
        }

        if (e.Key == Key.Space && _optionsFocused && _promptActionId == "compose")
        {
            e.Handled = true;
            ToggleContext("compose");
            return;
        }

        if (e.Key == Key.Tab && _promptActionId == "compose")
        {
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (shift)
            {
                if (_optionsFocused)
                {
                    e.Handled = true;
                    _optionsFocused = false;
                    FocusStageTarget();
                }

                return;
            }

            if (!_optionsFocused && PromptContextChipButton.IsEnabled)
            {
                e.Handled = true;
                _optionsFocused = true;
                FocusStageTarget();
                return;
            }

            if (_optionsFocused)
            {
                e.Handled = true;
                _optionsFocused = false;
                FocusStageTarget();
            }
        }
    }

    private void HandlePreviewKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DiscardResultAndReturnToMenu();
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            InvokePrimaryResultAction();
            return;
        }

        if (_activeKind != ActionKind.ReferenceList)
        {
            return;
        }

        if (e.Key == Key.A && _activeAction == "synonyms")
        {
            e.Handled = true;
            _ = SwitchReferenceListAsync();
        }
        else if (e.Key == Key.S && _activeAction == "antonyms")
        {
            e.Handled = true;
            _ = SwitchReferenceListAsync();
        }
    }

    private void HandleLanguagePickerKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseLanguagePicker(restoreFocus: true);
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var chosen = LanguageListBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(chosen))
            {
                chosen = LanguageSearchBox.Text.Trim();
            }

            if (!string.IsNullOrWhiteSpace(chosen))
            {
                ApplyLanguage(chosen);
            }

            return;
        }

        if (e.Key == Key.Up)
        {
            e.Handled = true;
            MoveLanguageSelection(-1);
            return;
        }

        if (e.Key == Key.Down)
        {
            e.Handled = true;
            MoveLanguageSelection(1);
            return;
        }

        if (e.Key == Key.Tab)
        {
            e.Handled = true;
        }
    }

    private static int DigitFromKey(Key key) =>
        key switch
        {
            Key.D1 or Key.NumPad1 => 1,
            Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4,
            Key.D5 or Key.NumPad5 => 5,
            Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7,
            Key.D8 or Key.NumPad8 => 8,
            Key.D9 or Key.NumPad9 => 9,
            _ => 0,
        };

    private static char? LetterFromKey(Key key)
    {
        var value = (int)key;
        var first = (int)Key.A;
        var last = (int)Key.Z;
        if (value < first || value > last)
        {
            return null;
        }

        return (char)('a' + (value - first));
    }

    // ---- UI event handlers -------------------------------------------------

    private void OnPrimaryButtonClick(object sender, RoutedEventArgs e)
    {
        if (_stage == PanelStage.PreviewReady)
        {
            InvokePrimaryResultAction();
        }
        else if (_stage == PanelStage.ErrorOrCancelled)
        {
            DiscardResultAndReturnToMenu();
        }
    }

    private void OnSecondaryButtonClick(object sender, RoutedEventArgs e)
    {
        if (_stage is PanelStage.Streaming or PanelStage.PreviewReady)
        {
            DiscardResultAndReturnToMenu();
            return;
        }

        CloseAndRestoreFocus();
    }

    private void OnSecondaryActionButtonClick(object sender, RoutedEventArgs e)
    {
        if (_stage == PanelStage.PreviewReady
            && _activeKind == ActionKind.ReferenceList
            && !_activeReadOnly)
        {
            _ = WriteResultAsync(_resultText);
        }
    }

    private void OnSwitchListButtonClick(object sender, RoutedEventArgs e)
    {
        if (_stage == PanelStage.PreviewReady && _activeKind == ActionKind.ReferenceList)
        {
            _ = SwitchReferenceListAsync();
        }
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e) => CloseAndRestoreFocus();

    private void OnContextChipClick(object sender, RoutedEventArgs e)
    {
        if (_stage == PanelStage.PromptInput && _promptActionId == "compose")
        {
            ToggleContext("compose");
            return;
        }

        if (_highlighted is { Options: ActionOptionKind.Context } action)
        {
            ToggleContext(action.Id);
            if (!_optionsFocused)
            {
                FocusStageTarget();
            }
        }
    }

    private void OnLanguageChipClick(object sender, RoutedEventArgs e)
    {
        if (_stage == PanelStage.ChooseAction)
        {
            OpenLanguagePicker();
        }
    }

    private void OnContextPreviewToggleClick(object sender, MouseButtonEventArgs e)
    {
        _contextPreviewExpanded = !_contextPreviewExpanded;

        if (_stage == PanelStage.PromptInput)
        {
            UpdatePromptOptions();
        }
        else
        {
            UpdateRowOptions();
        }
    }

    private void OnPromptBackClick(object sender, RoutedEventArgs e)
    {
        _optionsFocused = false;
        SetStage(PanelStage.ChooseAction);
        UpdateSelectionVisuals();
    }

    private void OnPromptRunClick(object sender, RoutedEventArgs e) => RunPrompt();

    private void OnLanguageSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingLanguageList)
        {
            return;
        }

        PopulateLanguages(LanguageSearchBox.Text);
    }

    private void OnLanguageListDoubleClick(object sender, MouseButtonEventArgs e) =>
        CommitLanguageListClick(e);

    private void OnLanguageListMouseUp(object sender, MouseButtonEventArgs e) =>
        CommitLanguageListClick(e);

    private void CommitLanguageListClick(MouseButtonEventArgs e)
    {
        if (!_languagePickerOpen)
        {
            return;
        }

        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var container = ItemsControl.ContainerFromElement(LanguageListBox, source) as ListBoxItem;

        if (container?.Content is string language)
        {
            ApplyLanguage(language);
        }
    }

    // ---- Window plumbing ---------------------------------------------------

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_closing || _applying)
        {
            return;
        }

        CancelActiveRequest();
        var hadFocus = _focusConfirmed;
        _closing = true;
        Close();

        if (!hadFocus)
        {
            ActivationFailed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ClampWithinMonitor();
    }

    private void PositionNearCaret(double estimatedHeight)
    {
        var monitor = CaretLocator.GetMonitorWorkArea(_caretLocation.Bounds.Left, _caretLocation.Bounds.Bottom);
        var scaleX = monitor.DpiScaleX > 0 ? monitor.DpiScaleX : 1.0;
        var scaleY = monitor.DpiScaleY > 0 ? monitor.DpiScaleY : 1.0;

        var workLeft = monitor.WorkArea.Left / scaleX;
        var workTop = monitor.WorkArea.Top / scaleY;
        var workRight = monitor.WorkArea.Right / scaleX;
        var workBottom = monitor.WorkArea.Bottom / scaleY;

        var panelWidth = Width;
        var desiredLeft = _caretLocation.Bounds.Left / scaleX;
        var desiredTop = _caretLocation.Bounds.Bottom / scaleY + (_caretLocation.FromCaret ? 6 : 12);

        if (desiredTop + estimatedHeight > workBottom)
        {
            desiredTop = _caretLocation.Bounds.Top / scaleY - estimatedHeight - 6;
        }

        Left = Math.Clamp(desiredLeft, workLeft + 4, Math.Max(workLeft + 4, workRight - panelWidth - 4));
        Top = Math.Clamp(desiredTop, workTop + 4, Math.Max(workTop + 4, workBottom - estimatedHeight - 4));
    }

    private void ClampWithinMonitor()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var monitor = CaretLocator.GetMonitorWorkArea(_caretLocation.Bounds.Left, _caretLocation.Bounds.Bottom);
        var scaleX = monitor.DpiScaleX > 0 ? monitor.DpiScaleX : 1.0;
        var scaleY = monitor.DpiScaleY > 0 ? monitor.DpiScaleY : 1.0;

        var workLeft = monitor.WorkArea.Left / scaleX;
        var workTop = monitor.WorkArea.Top / scaleY;
        var workRight = monitor.WorkArea.Right / scaleX;
        var workBottom = monitor.WorkArea.Bottom / scaleY;

        if (Left + ActualWidth > workRight - 4)
        {
            Left = Math.Max(workLeft + 4, workRight - ActualWidth - 4);
        }

        if (Top + ActualHeight > workBottom - 4)
        {
            Top = Math.Max(workTop + 4, workBottom - ActualHeight - 4);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelActiveRequest();
        base.OnClosed(e);
    }

    private static string StateLabel(SelectionState state) =>
        state switch
        {
            SelectionState.Empty => "Empty",
            SelectionState.Word => "Word",
            SelectionState.Passage => "Passage",
            _ => "Empty",
        };

    private static string StateLabelLower(SelectionState state) =>
        state switch
        {
            SelectionState.Empty => "empty",
            SelectionState.Word => "word",
            SelectionState.Passage => "passage",
            _ => "empty",
        };
}
