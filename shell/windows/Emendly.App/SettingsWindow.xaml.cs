using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Emendly.Capture;

namespace Emendly.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly HotkeyWindow? _hotkeyWindow;
    private readonly CaretLocation? _caretAnchor;
    private readonly string _originalHotkey;
    private readonly string _originalLanguage;

    private string _pendingHotkey;
    private bool _capturingHotkey;

    public SettingsWindow(
        AppSettings current,
        string transformerStatus,
        string? defaultTargetLanguage = null,
        HotkeyWindow? hotkeyWindow = null,
        CaretLocation? caretAnchor = null,
        bool topmost = false)
    {
        InitializeComponent();

        _settings = current;
        _hotkeyWindow = hotkeyWindow;
        _caretAnchor = caretAnchor;
        _originalHotkey = hotkeyWindow?.RegisteredHotkey is { Length: > 0 } registered
            ? registered
            : current.Hotkey;
        _pendingHotkey = _originalHotkey;
        _originalLanguage = current.GetEffectiveTargetLanguage(defaultTargetLanguage);

        Topmost = topmost;

        ShortcutButton.Content = _pendingHotkey;

        var languages = MenuModel.GetLanguages(_originalLanguage);
        DefaultLanguageCombo.ItemsSource = languages;
        DefaultLanguageCombo.SelectedIndex = MenuModel.FindLanguageIndex(languages, _originalLanguage);

        BaseUrlBox.Text = current.BaseUrl;
        ModelBox.Text = current.Model;

        EngineStatusText.Text = $"Backend: {transformerStatus}";
        var hasKey = !string.IsNullOrEmpty(AppSettings.ReadApiKey());
        ApiKeyStatusText.Text = hasKey
            ? "EMENDLY_API_KEY: detected in environment (never stored on disk)"
            : "EMENDLY_API_KEY: not set (set as an environment variable)";

        if (caretAnchor.HasValue)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            CenterOnCaretScreen(caretAnchor.Value, 400);
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        const int ImmersiveDarkMode = 20;
        const int UseImmersiveDarkModeBefore20H1 = 19;
        if (DwmSetWindowAttribute(handle, ImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        ApplyDarkTitleBar();
        if (_caretAnchor.HasValue)
        {
            CenterOnCaretScreen(_caretAnchor.Value, ActualHeight > 0 ? ActualHeight : 400);
        }

        var helper = new WindowInteropHelper(this);
        if (helper.Handle != IntPtr.Zero)
        {
            _ = WindowEnumerator.RestoreForeground(helper.Handle);
        }

        Activate();
    }

    private void CenterOnCaretScreen(CaretLocation anchor, double height)
    {
        var monitor = CaretLocator.GetMonitorWorkArea(anchor.Bounds.Left, anchor.Bounds.Bottom);
        var scaleX = monitor.DpiScaleX > 0 ? monitor.DpiScaleX : 1.0;
        var scaleY = monitor.DpiScaleY > 0 ? monitor.DpiScaleY : 1.0;

        var workLeft = monitor.WorkArea.Left / scaleX;
        var workTop = monitor.WorkArea.Top / scaleY;
        var workRight = monitor.WorkArea.Right / scaleX;
        var workBottom = monitor.WorkArea.Bottom / scaleY;

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var desiredLeft = workLeft + (workRight - workLeft - width) / 2.0;
        var desiredTop = workTop + (workBottom - workTop - height) / 2.0;

        Left = Math.Clamp(desiredLeft, workLeft, Math.Max(workLeft, workRight - width));
        Top = Math.Clamp(desiredTop, workTop, Math.Max(workTop, workBottom - height));
    }

    private void OnShortcutButtonClick(object sender, RoutedEventArgs e)
    {
        if (_capturingHotkey)
        {
            CancelHotkeyCapture();
            return;
        }

        BeginHotkeyCapture();
    }

    private void OnShortcutButtonLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_capturingHotkey)
        {
            CancelHotkeyCapture();
        }
    }

    private void BeginHotkeyCapture()
    {
        ShortcutButton.Focus();
        Keyboard.Focus(ShortcutButton);
        _capturingHotkey = true;
        ShortcutButton.Content = "Press the shortcut…";
        ShortcutErrorText.Visibility = Visibility.Collapsed;
    }

    private void CancelHotkeyCapture()
    {
        if (!_capturingHotkey)
        {
            return;
        }

        _capturingHotkey = false;
        ShortcutButton.Content = _pendingHotkey;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingHotkey)
        {
            HandleHotkeyCaptureKey(e);
        }
    }

    private void HandleHotkeyCaptureKey(KeyEventArgs e)
    {
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            CancelHotkeyCapture();
            ShortcutButton.Focus();
            Keyboard.Focus(ShortcutButton);
            return;
        }

        if (IsModifierKey(key))
        {
            return;
        }

        var modifiers = Keyboard.Modifiers
            & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows);

        if (modifiers == ModifierKeys.None && !HotkeyWindow.IsFunctionKey(key))
        {
            return;
        }

        var candidate = HotkeyWindow.FormatCombination(modifiers, key);
        if (!HotkeyWindow.TryParseHotkey(candidate, out _, out _, out var normalized))
        {
            return;
        }

        // Keep the captured combination pending until Save; Cancel must never alter the active registration.
        _pendingHotkey = normalized;
        _capturingHotkey = false;
        ShortcutButton.Content = _pendingHotkey;
        ShortcutErrorText.Visibility = Visibility.Collapsed;
        ShortcutButton.Focus();
        Keyboard.Focus(ShortcutButton);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        CancelHotkeyCapture();
        ValidationText.Visibility = Visibility.Collapsed;
        ShortcutErrorText.Visibility = Visibility.Collapsed;

        var baseUrl = BaseUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            ShowValidation("Base URL must not be empty.");
            return;
        }

        var model = ModelBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            ShowValidation("Model must not be empty.");
            return;
        }

        if (!HotkeyWindow.TryParseHotkey(_pendingHotkey, out _, out _, out var normalizedHotkey))
        {
            ShowValidation("Shortcut must be a function key such as F6, or a key with a modifier.");
            return;
        }

        var hotkeyChanged = !string.Equals(_originalHotkey, normalizedHotkey, StringComparison.OrdinalIgnoreCase);
        var effectiveHotkey = normalizedHotkey;

        if (hotkeyChanged && _hotkeyWindow is not null)
        {
            if (!_hotkeyWindow.TryReplace(normalizedHotkey, out var applied, out _))
            {
                _pendingHotkey = string.IsNullOrEmpty(applied) ? _originalHotkey : applied;
                ShortcutButton.Content = _pendingHotkey;
                ShortcutErrorText.Visibility = Visibility.Visible;
                ShortcutButton.Focus();
                Keyboard.Focus(ShortcutButton);
                return;
            }

            effectiveHotkey = applied;
            ShellLogger.LogHotkey(effectiveHotkey);
        }

        var selectedLanguage = (DefaultLanguageCombo.SelectedItem as string)?.Trim();
        if (string.IsNullOrEmpty(selectedLanguage))
        {
            selectedLanguage = LanguageSettings.DefaultTargetLanguage;
        }

        var languageChanged = !string.Equals(_originalLanguage, selectedLanguage, StringComparison.OrdinalIgnoreCase);

        _settings.Hotkey = effectiveHotkey;
        _settings.Language.DefaultTarget = selectedLanguage;
        if (languageChanged || !string.IsNullOrWhiteSpace(_settings.TargetLanguage))
        {
            _settings.TargetLanguage = selectedLanguage;
        }

        _settings.BaseUrl = baseUrl;
        _settings.Model = model;
        _settings.Save();

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        CancelHotkeyCapture();
        DialogResult = false;
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin;
}
