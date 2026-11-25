using System.Windows;

namespace Emendly.App;

public partial class SettingsWindow : Window
{
    public string BaseUrlValue => BaseUrlBox.Text.Trim();
    public string ModelValue => ModelBox.Text.Trim();
    public string HotkeyValue { get; private set; } = AppSettings.DefaultHotkey;

    public SettingsWindow(AppSettings current, string transformerStatus)
    {
        InitializeComponent();

        BaseUrlBox.Text = current.BaseUrl;
        ModelBox.Text = current.Model;
        HotkeyBox.Text = current.Hotkey;
        HotkeyValue = current.Hotkey;

        EngineStatusText.Text = $"Backend: {transformerStatus}";
        var hasKey = !string.IsNullOrEmpty(AppSettings.ReadApiKey());
        ApiKeyStatusText.Text = hasKey
            ? "EMENDLY_API_KEY: detected in environment (never stored on disk)"
            : "EMENDLY_API_KEY: not set (set as an environment variable)";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BaseUrlBox.Text))
        {
            ShowValidation("Base URL must not be empty.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ModelBox.Text))
        {
            ShowValidation("Model must not be empty.");
            return;
        }

        if (!HotkeyWindow.TryParseHotkey(HotkeyBox.Text, out _, out _, out var normalized))
        {
            ShowValidation("Hotkey must be a function key such as F6, or a key with a modifier (for example: Ctrl+Alt+E).");
            return;
        }

        HotkeyValue = normalized;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }
}
