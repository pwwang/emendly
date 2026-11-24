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
using Emendly.Capture;

namespace Emendly.App;

public enum PanelMode
{
    Selection,
    Compose,
}

public partial class PanelWindow : Window
{
    private enum PanelStage
    {
        ChooseAction,
        ComposePrompt,
        Streaming,
        PreviewReady,
        ErrorOrCancelled,
    }

    private sealed record ActionItem(int Number, string Id, string Label, string Description);

    private static readonly ActionItem[] Actions =
    {
        new(1, "proofread", "Proofread", "Fix spelling, grammar, and punctuation"),
        new(2, "rewrite", "Rewrite", "Improve clarity and flow"),
        new(3, "compose", "Compose", "Draft new text from a brief"),
        new(4, "translate", "Translate", "Translate to target language"),
        new(5, "synonyms", "Synonyms", "Suggest concise alternatives"),
    };

    private static readonly Brush RowNormalBg = Brushes.Transparent;
    private static readonly Brush RowSelectedBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x47));
    private static readonly Brush RowSelectedBorder = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
    private static readonly Brush BadgeBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x35));
    private static readonly Brush BadgeText = new SolidColorBrush(Color.FromRgb(0x93, 0xC5, 0xFD));
    private static readonly Brush LabelText = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6));
    private static readonly Brush DescText = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));

    private readonly WindowInfo _targetWindow;
    private readonly PanelMode _mode;
    private readonly string _selectedText;
    private readonly string _fieldContext;
    private readonly CaretLocation _caretLocation;
    private readonly ITransformer _transformer;
    private readonly ClipboardInserter _inserter;
    private readonly Border[] _actionRows = new Border[Actions.Length];

    private PanelStage _stage;
    private int _selectedIndex;
    private string _activeAction = string.Empty;
    private string _resultText = string.Empty;
    private CancellationTokenSource? _requestCts;
    private bool _applying;
    private bool _closing;

    public event EventHandler? PasteCompleted;

    public bool IsRequestRunning => _stage == PanelStage.Streaming && _requestCts is not null;

    public PanelWindow(
        WindowInfo targetWindow,
        PanelMode mode,
        string selectedText,
        string fieldContext,
        CaretLocation caretLocation,
        ITransformer transformer,
        ClipboardInserter inserter)
    {
        InitializeComponent();

        _targetWindow = targetWindow;
        _mode = mode;
        _selectedText = selectedText;
        _fieldContext = fieldContext;
        _caretLocation = caretLocation;
        _transformer = transformer;
        _inserter = inserter;

        var appLabel = string.IsNullOrWhiteSpace(targetWindow.ProcessName) ? "window" : targetWindow.ProcessName;
        HeaderMetaText.Text = mode == PanelMode.Selection
            ? $"· {appLabel} · {selectedText.Length} chars"
            : $"· {appLabel} · compose";

        ModeBadgeText.Text = transformer.ModeName;

        if (!string.IsNullOrWhiteSpace(fieldContext))
        {
            UseContextCheckBox.Content = $"Include surrounding text from {appLabel} ({fieldContext.Length} chars)";
            UseContextCheckBox.Visibility = Visibility.Visible;
        }

        BuildActionRows();

        if (mode == PanelMode.Selection)
        {
            SetStage(PanelStage.ChooseAction);
        }
        else
        {
            SetStage(PanelStage.ComposePrompt);
        }

        PositionNearCaret(estimatedHeight: mode == PanelMode.Selection ? 260 : 180);
    }

    public void ShowAndActivate()
    {
        Show();
        var helper = new WindowInteropHelper(this);
        if (helper.Handle != IntPtr.Zero)
        {
            _ = WindowEnumerator.RestoreForeground(helper.Handle);
        }

        Activate();
        if (_stage == PanelStage.ComposePrompt)
        {
            BriefBox.Focus();
            Keyboard.Focus(BriefBox);
        }
        else
        {
            Focus();
        }
    }

    public void CancelActiveRequest()
    {
        var cts = _requestCts;
        if (cts is not null && !cts.IsCancellationRequested)
        {
            cts.Cancel();
        }
    }

    private void BuildActionRows()
    {
        ActionListPanel.Children.Clear();

        for (var i = 0; i < Actions.Length; i++)
        {
            var index = i;
            var item = Actions[i];

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
                    Text = item.Number.ToString(),
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
                Text = item.Label,
                Foreground = LabelText,
                FontWeight = FontWeights.Medium,
                FontSize = 13,
            });
            labelStack.Children.Add(new TextBlock
            {
                Text = item.Description,
                Foreground = DescText,
                FontSize = 11,
            });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(numberBadge);
            Grid.SetColumn(labelStack, 1);
            grid.Children.Add(labelStack);

            var row = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(2, 0, 0, 0),
                BorderBrush = Brushes.Transparent,
                Background = RowNormalBg,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                Child = grid,
            };

            row.MouseEnter += (_, _) => SelectActionIndex(index);
            row.MouseLeftButtonUp += (_, _) => TriggerAction(index);

            _actionRows[i] = row;
            ActionListPanel.Children.Add(row);
        }

        SelectActionIndex(0);
    }

    private void SelectActionIndex(int index)
    {
        _selectedIndex = (index + Actions.Length) % Actions.Length;
        for (var i = 0; i < _actionRows.Length; i++)
        {
            var isSelected = i == _selectedIndex;
            _actionRows[i].Background = isSelected ? RowSelectedBg : RowNormalBg;
            _actionRows[i].BorderBrush = isSelected ? RowSelectedBorder : Brushes.Transparent;
        }
    }

    private void SetStage(PanelStage stage)
    {
        _stage = stage;

        ActionPanel.Visibility = stage == PanelStage.ChooseAction ? Visibility.Visible : Visibility.Collapsed;
        ComposePanel.Visibility = stage == PanelStage.ComposePrompt ? Visibility.Visible : Visibility.Collapsed;
        PreviewPanel.Visibility = stage is PanelStage.Streaming or PanelStage.PreviewReady or PanelStage.ErrorOrCancelled
            ? Visibility.Visible
            : Visibility.Collapsed;

        switch (stage)
        {
            case PanelStage.ComposePrompt:
                _ = Dispatcher.InvokeAsync(() =>
                {
                    BriefBox.Focus();
                    Keyboard.Focus(BriefBox);
                });
                break;

            case PanelStage.Streaming:
                ErrorText.Visibility = Visibility.Collapsed;
                PreviewStatusText.Text = "Generating...";
                PreviewHintText.Text = "Esc or hotkey cancels";
                PrimaryButton.Visibility = Visibility.Collapsed;
                SecondaryButton.Content = "Cancel";
                SecondaryButton.Visibility = Visibility.Visible;
                break;

            case PanelStage.PreviewReady:
                ErrorText.Visibility = Visibility.Collapsed;
                PreviewHintText.Text = "Enter applies · Esc discards";
                PrimaryButton.Content = "Apply";
                PrimaryButton.Visibility = Visibility.Visible;
                SecondaryButton.Content = "Discard";
                SecondaryButton.Visibility = Visibility.Visible;
                break;

            case PanelStage.ErrorOrCancelled:
                PreviewHintText.Text = "Enter retries · Esc closes";
                PrimaryButton.Content = "Back";
                PrimaryButton.Visibility = Visibility.Visible;
                SecondaryButton.Content = "Close";
                SecondaryButton.Visibility = Visibility.Visible;
                break;
        }
    }

    private void TriggerAction(int index)
    {
        SelectActionIndex(index);
        var item = Actions[_selectedIndex];

        if (item.Id == "compose")
        {
            SetStage(PanelStage.ComposePrompt);
            return;
        }

        _ = RunActionAsync(item.Id, item.Label, brief: null);
    }

    private async Task RunActionAsync(string actionId, string actionLabel, string? brief)
    {
        CancelActiveRequest();
        _requestCts?.Dispose();
        var cts = new CancellationTokenSource();
        _requestCts = cts;

        _activeAction = actionId;
        _resultText = string.Empty;
        PreviewActionText.Text = actionLabel;
        PreviewBox.Text = string.Empty;
        SetStage(PanelStage.Streaming);

        Dictionary<string, string>? options = null;
        if (!string.IsNullOrWhiteSpace(brief))
        {
            options = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["brief"] = brief.Trim(),
            };
        }

        string? context = null;
        if (actionId == "compose")
        {
            if (_mode == PanelMode.Selection && !string.IsNullOrWhiteSpace(_selectedText))
            {
                context = _selectedText;
            }
            else if (UseContextCheckBox.Visibility == Visibility.Visible
                && UseContextCheckBox.IsChecked == true
                && !string.IsNullOrWhiteSpace(_fieldContext))
            {
                context = _fieldContext;
            }
        }

        var inputText = actionId == "compose" ? string.Empty : _selectedText;
        var inputBytes = Encoding.UTF8.GetByteCount(inputText)
            + Encoding.UTF8.GetByteCount(brief ?? string.Empty)
            + Encoding.UTF8.GetByteCount(context ?? string.Empty);

        var request = new TransformRequest(
            Action: actionId,
            Text: inputText,
            Context: context,
            AppProcess: _targetWindow.ProcessName,
            Options: options);

        var sw = Stopwatch.StartNew();

        try
        {
            var result = await Task.Run(
                () => _transformer.TransformAsync(
                    request,
                    delta =>
                    {
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

            _resultText = !string.IsNullOrEmpty(result.Text) ? result.Text : PreviewBox.Text;
            PreviewBox.Text = _resultText;
            PreviewBox.ScrollToHome();

            var latency = result.LatencyMs > 0 ? result.LatencyMs : sw.ElapsedMilliseconds;
            PreviewStatusText.Text = $"Ready · {_resultText.Length} chars · {latency} ms";

            ShellLogger.LogAction(
                actionId,
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
            ShellLogger.LogAction(actionId, _targetWindow.ProcessName, sw.ElapsedMilliseconds, inputBytes, 0, "cancelled");
            if (ReferenceEquals(_requestCts, cts) && !_closing)
            {
                PreviewStatusText.Text = "Cancelled";
                ErrorText.Text = "Request cancelled.";
                ErrorText.Visibility = Visibility.Visible;
                SetStage(PanelStage.ErrorOrCancelled);
            }
        }
        catch (TransformException ex)
        {
            sw.Stop();
            ShellLogger.LogAction(actionId, _targetWindow.ProcessName, sw.ElapsedMilliseconds, inputBytes, 0, ex.Code);
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
            ShellLogger.LogAction(actionId, _targetWindow.ProcessName, sw.ElapsedMilliseconds, inputBytes, 0, "internal");
            if (ReferenceEquals(_requestCts, cts) && !_closing)
            {
                PreviewStatusText.Text = "Error";
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

    private async Task ApplyResultAsync()
    {
        if (_applying || string.IsNullOrEmpty(_resultText))
        {
            return;
        }

        _applying = true;
        Hide();

        var sw = Stopwatch.StartNew();
        try
        {
            var pasted = await _inserter.PasteAsync(
                targetHwnd: _targetWindow.Hwnd,
                processName: _targetWindow.ProcessName,
                actionName: _activeAction,
                textToInsert: _resultText,
                replacedText: _mode == PanelMode.Selection ? _selectedText : string.Empty,
                wasSelection: _mode == PanelMode.Selection);

            sw.Stop();
            ShellLogger.LogAction(
                "apply",
                _targetWindow.ProcessName,
                sw.ElapsedMilliseconds,
                Encoding.UTF8.GetByteCount(_selectedText),
                Encoding.UTF8.GetByteCount(_resultText),
                pasted ? "ok" : "failed");

            if (pasted)
            {
                PasteCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception)
        {
            sw.Stop();
            ShellLogger.LogAction("apply", _targetWindow.ProcessName, sw.ElapsedMilliseconds, 0, 0, "error");
        }
        finally
        {
            _closing = true;
            Close();
        }
    }

    public void CloseAndRestoreFocus()
    {
        if (_closing)
        {
            return;
        }

        CancelActiveRequest();
        _closing = true;
        Hide();
        _ = WindowEnumerator.RestoreForeground(_targetWindow.Hwnd);
        Close();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (_stage)
        {
            case PanelStage.ChooseAction:
                HandleChooseActionKey(e);
                break;

            case PanelStage.ComposePrompt:
                HandleComposePromptKey(e);
                break;

            case PanelStage.Streaming:
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    CancelActiveRequest();
                }
                break;

            case PanelStage.PreviewReady:
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    _ = ApplyResultAsync();
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    CloseAndRestoreFocus();
                }
                break;

            case PanelStage.ErrorOrCancelled:
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    CloseAndRestoreFocus();
                }
                else if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    SetStage(_mode == PanelMode.Selection ? PanelStage.ChooseAction : PanelStage.ComposePrompt);
                }
                break;
        }
    }

    private void HandleChooseActionKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseAndRestoreFocus();
            return;
        }

        if (e.Key == Key.Up)
        {
            e.Handled = true;
            SelectActionIndex(_selectedIndex - 1);
            return;
        }

        if (e.Key == Key.Down)
        {
            e.Handled = true;
            SelectActionIndex(_selectedIndex + 1);
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            TriggerAction(_selectedIndex);
            return;
        }

        var digitIndex = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            Key.D5 or Key.NumPad5 => 4,
            _ => -1,
        };

        if (digitIndex >= 0)
        {
            e.Handled = true;
            TriggerAction(digitIndex);
        }
    }

    private void HandleComposePromptKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseAndRestoreFocus();
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                var caret = BriefBox.CaretIndex;
                BriefBox.Text = BriefBox.Text.Insert(caret, Environment.NewLine);
                BriefBox.CaretIndex = caret + Environment.NewLine.Length;
                return;
            }

            if (!string.IsNullOrWhiteSpace(BriefBox.Text))
            {
                _ = RunActionAsync("compose", "Compose", BriefBox.Text);
            }
        }
    }

    private void OnPrimaryButtonClick(object sender, RoutedEventArgs e)
    {
        if (_stage == PanelStage.PreviewReady)
        {
            _ = ApplyResultAsync();
        }
        else if (_stage == PanelStage.ErrorOrCancelled)
        {
            SetStage(_mode == PanelMode.Selection ? PanelStage.ChooseAction : PanelStage.ComposePrompt);
        }
    }

    private void OnSecondaryButtonClick(object sender, RoutedEventArgs e)
    {
        if (_stage == PanelStage.Streaming)
        {
            CancelActiveRequest();
        }
        else
        {
            CloseAndRestoreFocus();
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_closing || _applying)
        {
            return;
        }

        CancelActiveRequest();
        _closing = true;
        Close();
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
}
