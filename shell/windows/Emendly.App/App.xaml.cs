using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Emendly.Capture;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Emendly.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\Emendly.App.SingleInstance";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly ClipboardInserter _inserter = new();

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private AppSettings _settings = new();
    private AppCaptureRules _captureRules = AppCaptureRules.Empty;
    private ITransformer? _transformer;
    private HotkeyWindow? _hotkeyWindow;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _undoMenuItem;
    private Drawing.Icon? _ownedTrayIcon;
    private PanelWindow? _activePanel;
    private NoticeWindow? _activeNotice;
    private SettingsWindow? _settingsWindow;
    private bool _shutdownAfterSettings;
    private bool _probeInFlight;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var openSettingsAndExit = HasFlag(e.Args, "--settings");

        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            _ = HotkeyWindow.NotifyExistingInstance(openSettings: openSettingsAndExit);
            Shutdown(0);
            return;
        }

        _settings = AppSettings.Load();
        _captureRules = AppCaptureRules.LoadDefault();
        _transformer = CreateTransformer(_settings);

        if (openSettingsAndExit)
        {
            _shutdownAfterSettings = true;
            _hotkeyWindow = new HotkeyWindow();
            _hotkeyWindow.ActivateRequested += OnActivateRequested;
            _hotkeyWindow.OpenSettingsRequested += (_, _) => OpenSettings();
            OpenSettings();
            return;
        }

        InitializeTrayIcon();
        InitializeHotkey();
    }

    private static bool HasFlag(string[] args, string flag)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static ITransformer CreateTransformer(AppSettings settings)
    {
        var enginePath = AppSettings.ResolveEnginePath();
        if (!string.IsNullOrEmpty(enginePath))
        {
            ShellLogger.LogMeta("startup", "emendly", "engine");
            ShellLogger.LogNote("engine_path", enginePath);
            return new EngineTransformer(enginePath, settings.BaseUrl, settings.Model);
        }

        ShellLogger.LogMeta("startup", "emendly", "echo_fallback");
        ShellLogger.LogNote("engine_path", "none resolved");
        return new EchoTransformer();
    }

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, _) => UpdateUndoMenuItem();

        var settingsItem = new Forms.ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) => OpenSettings();

        _undoMenuItem = new Forms.ToolStripMenuItem("Undo last insert")
        {
            Enabled = false,
        };
        _undoMenuItem.Click += (_, _) => _ = UndoLastInsertAsync();

        var quitItem = new Forms.ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => Shutdown(0);

        menu.Items.Add(settingsItem);
        menu.Items.Add(_undoMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(quitItem);

        _ownedTrayIcon = CreateTrayIconBitmap();

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _ownedTrayIcon ?? Drawing.SystemIcons.Application,
            Text = BuildTrayTooltip(),
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    private string BuildTrayTooltip()
    {
        if (_transformer is { IsEchoFallback: true })
        {
            return "Emendly (echo mode - engine not found)";
        }

        return "Emendly - Ready";
    }

    private void InitializeHotkey()
    {
        _hotkeyWindow = new HotkeyWindow();
        _hotkeyWindow.HotkeyPressed += OnHotkeyPressed;
        _hotkeyWindow.ActivateRequested += OnActivateRequested;
        _hotkeyWindow.OpenSettingsRequested += (_, _) => OpenSettings();

        if (!_hotkeyWindow.Register(_settings.Hotkey, out var error))
        {
            ShowBalloon("Emendly", error ?? $"Failed to register hotkey {_settings.Hotkey}.", Forms.ToolTipIcon.Warning);
        }
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        _ = HandleHotkeyAsync();
    }

    private async Task HandleHotkeyAsync()
    {
        // The open panel owns the keyboard: a second hotkey cancels and dismisses it
        // rather than stacking another panel.
        if (_activePanel is not null)
        {
            _activePanel.CancelActiveRequest();
            _activePanel.CloseAndRestoreFocus();
            return;
        }

        if (_probeInFlight)
        {
            return;
        }

        var foreground = WindowEnumerator.GetForegroundWindowInfo();
        if (foreground is null)
        {
            ShowNotice("Emendly", "No active window found.", CaretLocator.GetMouseFallback());
            return;
        }

        if (foreground.Pid == (uint)Environment.ProcessId)
        {
            return;
        }

        _probeInFlight = true;
        try
        {
            var outcome = await Task.Run(() => CaptureChain.CaptureWithTimeout(foreground, _captureRules, ProbeTimeout));

            if (outcome.TimedOut)
            {
                ShellLogger.LogProbe(foreground.ProcessName, "timeout", "none");
                ShowNotice(foreground.ProcessName, "Timed out probing the focused text field.", CaretLocator.GetMouseFallback());
                return;
            }

            if (outcome.Snapshot is null)
            {
                ShellLogger.LogProbe(foreground.ProcessName, "error", "none");
                ShowNotice(foreground.ProcessName, outcome.ErrorMessage ?? "Could not inspect the focused window.", CaretLocator.GetMouseFallback());
                return;
            }

            var snapshot = outcome.Snapshot;
            ShellLogger.LogProbe(
                foreground.ProcessName,
                snapshot.Report.Verdict,
                snapshot.StrategyName,
                snapshot.AmbiguousView);

            switch (snapshot.Report.Verdict)
            {
                case "selection":
                case "caret":
                    var writableClassification = SelectionClassifier.Classify(snapshot.SelectedText, readOnly: false);
                    OpenPanel(foreground, writableClassification, snapshot, unsupportedReason: null);
                    break;

                case "read_only":
                    var readOnlyClassification = SelectionClassifier.Classify(snapshot.SelectedText, readOnly: true);
                    if (readOnlyClassification.State == SelectionState.Empty)
                    {
                        var reason = snapshot.Report.Enabled
                            ? "Select text first. This window is read-only, so results are copied, not inserted."
                            : "This window is disabled.";
                        ShowNotice(foreground.ProcessName, reason, CaretLocator.GetMouseFallback());
                        return;
                    }
                    else
                    {
                        OpenPanel(foreground, readOnlyClassification, snapshot, unsupportedReason: null);
                    }
                    break;

                default:
                    var unsupportedClassification = SelectionClassifier.Classify(string.Empty, readOnly: true);
                    OpenPanel(
                        foreground,
                        unsupportedClassification,
                        snapshot,
                        unsupportedReason: "No supported editable text field found.");
                    break;
            }
        }
        finally
        {
            _probeInFlight = false;
        }
    }

    private void OpenPanel(
        WindowInfo targetWindow,
        SelectionClassification classification,
        CaptureSnapshot snapshot,
        string? unsupportedReason)
    {
        if (_transformer is null)
        {
            return;
        }

        CloseNotice();

        if (_activePanel is not null)
        {
            var previous = _activePanel;
            _activePanel = null;
            previous.Close();
        }

        var panel = new PanelWindow(
            targetWindow: targetWindow,
            classification: classification,
            surroundingContext: snapshot.SurroundingContext,
            caretLocation: snapshot.CaretLocation,
            settings: _settings,
            transformer: _transformer,
            inserter: _inserter,
            unsupportedReason: unsupportedReason,
            strategyName: snapshot.StrategyName,
            editorHwnd: snapshot.EditorHwnd);

        panel.PasteCompleted += (_, _) => UpdateUndoMenuItem();
        panel.SettingsRequested += (_, args) =>
        {
            _ = Dispatcher.InvokeAsync(() => OpenSettings(args.CaretLocation, args.WasTopmost));
        };
        panel.ActivationFailed += (_, _) => ShowNotice(
            targetWindow.ProcessName,
            "Emendly is open but could not take keyboard focus. Click the panel to type, or press Esc to close it.",
            snapshot.CaretLocation);
        panel.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activePanel, panel))
            {
                _activePanel = null;
            }

            // Settings is only ever open beside the panel, so it must never outlive it.
            CloseSettingsWindow();
        };

        _activePanel = panel;
        panel.ShowAndActivate();
    }

    private void ShowNotice(string appName, string reason, CaretLocation anchor)
    {
        CloseNotice();

        var notice = NoticeWindow.ShowNotice(appName, reason, anchor);
        notice.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeNotice, notice))
            {
                _activeNotice = null;
            }
        };
        _activeNotice = notice;
    }

    private void CloseNotice()
    {
        if (_activeNotice is not null)
        {
            var previous = _activeNotice;
            _activeNotice = null;
            previous.Close();
        }
    }

    private void OnActivateRequested(object? sender, EventArgs e)
    {
        if (_activePanel is not null)
        {
            _activePanel.ShowAndActivate();
            return;
        }

        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var hotkey = _hotkeyWindow?.RegisteredHotkey ?? _settings.Hotkey;
        ShowBalloon("Emendly", $"Emendly is already running. Press {hotkey} in any text field.", Forms.ToolTipIcon.Info);
    }

    private void OpenSettings(CaretLocation? caretAnchor = null, bool topmost = false)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        // The panel stays open underneath; settings is placed beside it instead of taking its
        // place. Only a visible, laid-out panel can anchor the window.
        Rect? panelBounds = null;
        if (_activePanel is { IsVisible: true } panel
            && panel.ActualWidth > 0
            && panel.ActualHeight > 0
            && !double.IsNaN(panel.Left)
            && !double.IsNaN(panel.Top))
        {
            panelBounds = new Rect(panel.Left, panel.Top, panel.ActualWidth, panel.ActualHeight);
            topmost = panel.Topmost;
        }

        var status = _transformer?.StatusSummary ?? "Unavailable";
        var dialog = new SettingsWindow(
            _settings,
            status,
            _transformer?.DefaultTargetLanguage,
            _hotkeyWindow,
            caretAnchor,
            topmost,
            panelBounds);
        _settingsWindow = dialog;

        dialog.Saved += (_, _) => OnSettingsSaved();
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, dialog))
            {
                _settingsWindow = null;
            }

            if (_shutdownAfterSettings)
            {
                _shutdownAfterSettings = false;
                Shutdown(0);
            }
        };

        dialog.Show();
    }

    private void OnSettingsSaved()
    {
        if (_transformer is not null)
        {
            _ = ConfigureTransformerSafeAsync(_settings.BaseUrl, _settings.Model);
        }

        _activePanel?.ApplySavedSettings(_transformer?.DefaultTargetLanguage);
    }

    private void CloseSettingsWindow()
    {
        var window = _settingsWindow;
        if (window is null)
        {
            return;
        }

        // Clear the field first so the Closed handler does not run twice.
        _settingsWindow = null;
        window.Close();
    }

    private async Task ConfigureTransformerSafeAsync(string baseUrl, string model)
    {
        try
        {
            if (_transformer is not null)
            {
                await _transformer.ConfigureAsync(baseUrl, model);
            }
        }
        catch (Exception)
        {
            ShellLogger.LogMeta("config.set", "emendly", "error");
        }
    }

    private async Task UndoLastInsertAsync()
    {
        var entry = _inserter.LastUndo;
        if (entry is null)
        {
            UpdateUndoMenuItem();
            return;
        }

        var undone = await _inserter.UndoLastAsync();
        UpdateUndoMenuItem();

        if (!undone)
        {
            ShowBalloon("Emendly", "Could not undo: the target window is no longer available.", Forms.ToolTipIcon.Info);
        }
        else
        {
            ShellLogger.LogMeta("undo", entry.ProcessName, "ok");
        }
    }

    private void UpdateUndoMenuItem()
    {
        if (_undoMenuItem is null)
        {
            return;
        }

        var entry = _inserter.LastUndo;
        _undoMenuItem.Enabled = entry is not null;
        _undoMenuItem.Text = entry is null
            ? "Undo last insert"
            : $"Undo last insert ({entry.ProcessName})";
    }

    private void ShowBalloon(string title, string text, Forms.ToolTipIcon icon)
    {
        _trayIcon?.ShowBalloonTip(4500, title, text, icon);
    }

    private static Drawing.Icon? CreateTrayIconBitmap()
    {
        try
        {
            using var bitmap = new Drawing.Bitmap(16, 16);
            using (var g = Drawing.Graphics.FromImage(bitmap))
            {
                g.Clear(Drawing.Color.FromArgb(0x25, 0x63, 0xEB));
                using var font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Point);
                using var brush = new Drawing.SolidBrush(Drawing.Color.White);
                g.DrawString("E", font, brush, 2.5f, 0.5f);
            }

            var hIcon = bitmap.GetHicon();
            try
            {
                using var temp = Drawing.Icon.FromHandle(hIcon);
                return (Drawing.Icon)temp.Clone();
            }
            finally
            {
                _ = DestroyIcon(hIcon);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CloseNotice();
        _shutdownAfterSettings = false;
        CloseSettingsWindow();

        if (_activePanel is not null)
        {
            _activePanel.Close();
            _activePanel = null;
        }

        _hotkeyWindow?.Dispose();
        _hotkeyWindow = null;

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _ownedTrayIcon?.Dispose();
        _ownedTrayIcon = null;

        if (_transformer is not null)
        {
            try
            {
                _transformer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Ignore teardown errors during process exit.
            }

            _transformer = null;
        }

        if (_instanceMutex is not null)
        {
            if (_ownsMutex)
            {
                try
                {
                    _instanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Already released.
                }
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        base.OnExit(e);
    }
}
