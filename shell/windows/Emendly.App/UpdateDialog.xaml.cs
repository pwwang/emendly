using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Emendly.App;

public partial class UpdateDialog : Window
{
    private readonly UpdateCheckResult _result;
    private CancellationTokenSource? _downloadCts;
    private bool _downloading;

    public UpdateDialog(UpdateCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        InitializeComponent();

        _result = result;
        var current = string.IsNullOrWhiteSpace(result.CurrentVersion)
            ? VersionInfo.AppVersion
            : result.CurrentVersion;
        var latest = string.IsNullOrWhiteSpace(result.LatestVersion)
            ? "latest"
            : result.LatestVersion;

        VersionTransitionText.Text = $"v{current}  ->  v{latest}";
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(result.ReleaseNotes)
            ? "No release notes provided for this version."
            : result.ReleaseNotes;
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
        const int UseImmersiveDarkModeBefore20H1 = 19;
        if (DwmSetWindowAttribute(handle, ImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        ApplyDarkTitleBar();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_downloading)
        {
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;
        ErrorText.Text = string.Empty;

        var latest = _result.LatestVersion ?? "latest";
        if (!UpdateChecker.IsDirectInstallerUrl(_result.DownloadUrl))
        {
            try
            {
                await UpdateChecker.DownloadAndLaunchInstallerAsync(
                    _result.DownloadUrl,
                    latest,
                    CancellationToken.None,
                    releaseUrl: _result.ReleaseUrl);
                Close();
            }
            catch (Exception ex)
            {
                ShowError($"Could not open release page: {ex.Message}");
            }

            return;
        }

        _downloading = true;
        DownloadButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressStatusText.Text = "Downloading installer...";
        DownloadProgressBar.IsIndeterminate = true;
        DownloadProgressBar.Value = 0;

        _downloadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _downloadCts = cts;

        var progress = new Progress<double>(fraction =>
        {
            DownloadProgressBar.IsIndeterminate = false;
            var percent = (int)Math.Round(fraction * 100.0);
            DownloadProgressBar.Value = percent;
            ProgressStatusText.Text = $"Downloading installer... {percent}%";
        });

        try
        {
            await UpdateChecker.DownloadAndLaunchInstallerAsync(
                _result.DownloadUrl,
                latest,
                cts.Token,
                progress,
                _result.ReleaseUrl);

            ShellLogger.LogMeta("update.download", "emendly", "ok");
            Close();
        }
        catch (OperationCanceledException)
        {
            // Window closed or download cancelled by user.
        }
        catch (Exception ex)
        {
            ShellLogger.LogMeta("update.download", "emendly", "error");
            ProgressPanel.Visibility = Visibility.Collapsed;
            ShowError($"Download failed: {ex.Message}. You can open the release page to download manually.");
        }
        finally
        {
            _downloading = false;
            DownloadButton.IsEnabled = true;
        }
    }

    private void OnOpenReleasePageClick(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateChecker.OpenReleasePage(_result.ReleaseUrl);
        }
        catch (Exception ex)
        {
            ShowError($"Could not open browser: {ex.Message}");
        }
    }

    private void OnLaterClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_downloadCts is not null)
        {
            try
            {
                _downloadCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _downloadCts.Dispose();
            _downloadCts = null;
        }

        base.OnClosed(e);
    }
}
