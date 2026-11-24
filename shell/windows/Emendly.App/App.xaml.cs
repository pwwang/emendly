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
    private ITransformer? _transformer;
    private HotkeyWindow? _hotkeyWindow;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _undoMenuItem;
    private Drawing.Icon? _ownedTrayIcon;
    private PanelWindow? _activePanel;
    private NoticeWindow? _activeNotice;
    private SettingsWindow? _settingsWindow;
    private bool _probeInFlight;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            _ = HotkeyWindow.NotifyExistingInstance();
            Shutdown(0);
            return;
        }

        _settings = AppSettings.Load();
        _transformer = CreateTransformer(_settings);

        InitializeTrayIcon();
        InitializeHotkey();
    }

    private static ITransformer CreateTransformer(AppSettings settings)
    {
        var enginePath = AppSettings.ResolveEnginePath();
        if (!string.IsNullOrEmpty(enginePath))
        {
            ShellLogger.LogMeta("startup", "emendly", "engine");
            return new EngineTransformer(enginePath, settings.BaseUrl, settings.Model);
        }

        ShellLogger.LogMeta("startup", "emendly", "echo_fallback");
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
        if (_activePanel is { IsRequestRunning: true })
        {
            _activePanel.CancelActiveRequest();
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
            if (_activePanel is not null)
            {
                _activePanel.CloseAndRestoreFocus();
            }

            return;
        }

        _probeInFlight = true;
        try
        {
            var outcome = await Task.Run(() => UiaProbe.CaptureWithTimeout(foreground, ProbeTimeout));

            if (outcome.TimedOut)
            {
                ShellLogger.LogMeta("probe", foreground.ProcessName, "timeout");
                ShowNotice(foreground.ProcessName, "Timed out probing the focused text field.", CaretLocator.GetMouseFallback());
                return;
            }

            if (outcome.Snapshot is null)
            {
                ShellLogger.LogMeta("probe", foreground.ProcessName, "error");
                ShowNotice(foreground.ProcessName, outcome.ErrorMessage ?? "Could not inspect the focused window.", CaretLocator.GetMouseFallback());
                return;
            }

            var snapshot = outcome.Snapshot;
            ShellLogger.LogMeta("probe", foreground.ProcessName, snapshot.Report.Verdict);

            switch (snapshot.Report.Verdict)
            {
                case "selection":
                    OpenPanel(foreground, PanelMode.Selection, snapshot);
                    break;

                case "caret":
                    OpenPanel(foreground, PanelMode.Compose, snapshot);
                    break;

                case "read_only":
                    var reason = snapshot.Report.Enabled
                        ? "Focused text field is read-only."
                        : "Focused text field is disabled.";
                    ShowNotice(foreground.ProcessName, reason, snapshot.CaretLocation);
                    break;

                default:
                    ShowNotice(foreground.ProcessName, "No supported editable text field found.", snapshot.CaretLocation);
                    break;
            }
        }
        finally
        {
            _probeInFlight = false;
        }
    }

    private void OpenPanel(WindowInfo targetWindow, PanelMode mode, CaptureSnapshot snapshot)
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
            mode: mode,
            selectedText: snapshot.SelectedText,
            fieldContext: snapshot.FieldContext,
            caretLocation: snapshot.CaretLocation,
            transformer: _transformer,
            inserter: _inserter);

        panel.PasteCompleted += (_, _) => UpdateUndoMenuItem();
        panel.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activePanel, panel))
            {
                _activePanel = null;
            }
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

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var status = _transformer?.StatusSummary ?? "Unavailable";
        var dialog = new SettingsWindow(_settings, status);
        _settingsWindow = dialog;

        try
        {
            var saved = dialog.ShowDialog();
            if (saved != true)
            {
                return;
            }

            var hotkeyChanged = !string.Equals(_settings.Hotkey, dialog.HotkeyValue, StringComparison.OrdinalIgnoreCase);

            _settings.BaseUrl = dialog.BaseUrlValue;
            _settings.Model = dialog.ModelValue;
            _settings.Hotkey = dialog.HotkeyValue;
            _settings.Save();

            if (hotkeyChanged && _hotkeyWindow is not null)
            {
                if (!_hotkeyWindow.Register(_settings.Hotkey, out var error))
                {
                    ShowBalloon("Emendly", error ?? $"Could not register hotkey {_settings.Hotkey}.", Forms.ToolTipIcon.Warning);
                }
            }

            if (_transformer is not null)
            {
                _ = ConfigureTransformerSafeAsync(_settings.BaseUrl, _settings.Model);
            }
        }
        finally
        {
            _settingsWindow = null;
        }
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
