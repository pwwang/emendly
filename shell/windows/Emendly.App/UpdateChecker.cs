using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Emendly.App;

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Error,
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string CurrentVersion,
    string? LatestVersion = null,
    string? ReleaseUrl = null,
    string? DownloadUrl = null,
    string? ReleaseNotes = null,
    string? ErrorMessage = null);

public readonly record struct SemVer(int Major, int Minor, int Patch) : IComparable<SemVer>
{
    public int CompareTo(SemVer other)
    {
        var majorCompare = Major.CompareTo(other.Major);
        if (majorCompare != 0)
        {
            return majorCompare;
        }

        var minorCompare = Minor.CompareTo(other.Minor);
        return minorCompare != 0 ? minorCompare : Patch.CompareTo(other.Patch);
    }

    public static bool operator >(SemVer left, SemVer right) => left.CompareTo(right) > 0;
    public static bool operator <(SemVer left, SemVer right) => left.CompareTo(right) < 0;
    public static bool operator >=(SemVer left, SemVer right) => left.CompareTo(right) >= 0;
    public static bool operator <=(SemVer left, SemVer right) => left.CompareTo(right) <= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public static class UpdateChecker
{
    public const string DefaultReleaseApiUrl = "https://api.github.com/repos/pwwang/emendly/releases/latest";
    public const string DefaultReleasesPageUrl = "https://github.com/pwwang/emendly/releases/latest";
    public const string UpdateUrlEnvVar = "EMENDLY_UPDATE_URL";

    public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(15);

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public static string ResolveEndpointUrl()
    {
        var overrideUrl = Environment.GetEnvironmentVariable(UpdateUrlEnvVar);
        return string.IsNullOrWhiteSpace(overrideUrl)
            ? DefaultReleaseApiUrl
            : overrideUrl.Trim();
    }

    public static bool ShouldRunAutoCheck(AppSettings settings, DateTimeOffset? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.CheckUpdatesOnStartup)
        {
            return false;
        }

        if (settings.LastUpdateCheckUtc is null)
        {
            return true;
        }

        var now = utcNow ?? DateTimeOffset.UtcNow;
        return (now - settings.LastUpdateCheckUtc.Value) >= AutoCheckInterval;
    }

    public static bool TryParseSemVer(string? raw, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var span = raw.Trim().AsSpan();
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
        {
            span = span[1..];
        }

        var suffixIndex = span.IndexOfAny('-', '+');
        if (suffixIndex >= 0)
        {
            span = span[..suffixIndex];
        }

        if (span.IsEmpty)
        {
            return false;
        }

        var parts = span.ToString().Split('.');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        var patch = 0;
        if (parts.Length == 3
            && !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch))
        {
            return false;
        }

        version = new SemVer(major, minor, patch);
        return true;
    }

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(
        CancellationToken cancellationToken = default,
        string? currentVersion = null,
        string? endpointUrl = null)
    {
        var current = string.IsNullOrWhiteSpace(currentVersion)
            ? VersionInfo.AppVersion
            : currentVersion.Trim();
        var url = string.IsNullOrWhiteSpace(endpointUrl)
            ? ResolveEndpointUrl()
            : endpointUrl.Trim();

        if (!TryCreateHttpUri(url, out var uri))
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                current,
                ErrorMessage: "Invalid update endpoint URL.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(MetadataTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", $"Emendly/{current}");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await SharedHttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Error,
                    current,
                    ErrorMessage: $"Update server returned HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(timeoutCts.Token)
                .ConfigureAwait(false);

            using var doc = await JsonDocument
                .ParseAsync(stream, cancellationToken: timeoutCts.Token)
                .ConfigureAwait(false);

            return ParseReleasePayload(doc.RootElement, current);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                current,
                ErrorMessage: "Update check timed out.");
        }
        catch (HttpRequestException ex)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                current,
                ErrorMessage: $"Could not reach update server: {ex.Message}");
        }
        catch (JsonException)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                current,
                ErrorMessage: "Received invalid release metadata.");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                current,
                ErrorMessage: $"Update check failed: {ex.Message}");
        }
    }

    public static UpdateCheckResult ParseReleasePayload(JsonElement root, string currentVersion)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                currentVersion,
                ErrorMessage: "Release metadata is not a JSON object.");
        }

        var rawTag = ReadString(root, "tag_name");
        if (string.IsNullOrWhiteSpace(rawTag))
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                currentVersion,
                ErrorMessage: "Release metadata is missing tag_name.");
        }

        var latestVersion = rawTag.Trim().TrimStart('v', 'V');
        if (!TryParseSemVer(latestVersion, out var latestSemVer))
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                currentVersion,
                ErrorMessage: $"Unrecognized release version: {rawTag}");
        }

        if (!TryParseSemVer(currentVersion, out var currentSemVer))
        {
            currentSemVer = new SemVer(0, 0, 0);
        }

        var releaseUrl = ReadString(root, "html_url");
        if (!TryCreateHttpUri(releaseUrl, out _))
        {
            releaseUrl = DefaultReleasesPageUrl;
        }

        var body = ReadString(root, "body")?.Trim();
        var name = ReadString(root, "name")?.Trim();
        var releaseNotes = !string.IsNullOrEmpty(body) ? body : name;

        var downloadUrl = FindWindowsAssetUrl(root);
        var status = latestSemVer > currentSemVer
            ? UpdateCheckStatus.UpdateAvailable
            : UpdateCheckStatus.UpToDate;

        return new UpdateCheckResult(
            status,
            currentVersion,
            latestVersion,
            releaseUrl,
            downloadUrl,
            releaseNotes);
    }

    public static bool IsDirectInstallerUrl(string? url)
    {
        if (!TryCreateHttpUri(url, out var uri))
        {
            return false;
        }

        return uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string?> DownloadAndLaunchInstallerAsync(
        string? downloadUrl,
        string latestVersion,
        CancellationToken ct = default,
        IProgress<double>? progress = null,
        string? releaseUrl = null)
    {
        if (!IsDirectInstallerUrl(downloadUrl))
        {
            var fallbackUrl = TryCreateHttpUri(releaseUrl, out _)
                ? releaseUrl!
                : (TryCreateHttpUri(downloadUrl, out _) ? downloadUrl! : DefaultReleasesPageUrl);
            OpenReleasePage(fallbackUrl);
            return null;
        }

        var uri = new Uri(downloadUrl!, UriKind.Absolute);
        var safeVersion = SanitizeVersionForFilename(latestVersion);
        var updatesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Emendly",
            "updates");
        Directory.CreateDirectory(updatesDir);

        var fileName = $"Emendly-v{safeVersion}-win-x64-setup.exe";
        var targetPath = Path.Combine(updatesDir, fileName);
        var tempPath = targetPath + ".part";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", $"Emendly/{VersionInfo.AppVersion}");
            request.Headers.TryAddWithoutValidation("Accept", "application/octet-stream");

            using var response = await SharedHttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var destination = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                var buffer = new byte[81920];
                long downloaded = 0;

                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    downloaded += read;

                    if (totalBytes is > 0)
                    {
                        progress?.Report(Math.Clamp((double)downloaded / totalBytes.Value, 0.0, 1.0));
                    }
                }
            }

            var info = new FileInfo(tempPath);
            if (!info.Exists || info.Length <= 0)
            {
                throw new InvalidOperationException("Downloaded installer file is empty.");
            }

            File.Move(tempPath, targetPath, overwrite: true);
            progress?.Report(1.0);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = targetPath,
            UseShellExecute = true,
        });

        return targetPath;
    }

    public static void OpenReleasePage(string? url)
    {
        var resolved = TryCreateHttpUri(url, out var uri)
            ? uri.AbsoluteUri
            : DefaultReleasesPageUrl;

        Process.Start(new ProcessStartInfo
        {
            FileName = resolved,
            UseShellExecute = true,
        });
    }

    private static string? FindWindowsAssetUrl(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? zipFallback = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = ReadString(asset, "name");
            var url = ReadString(asset, "browser_download_url");
            if (string.IsNullOrWhiteSpace(name) || !TryCreateHttpUri(url, out _))
            {
                continue;
            }

            if (name.EndsWith("-win-x64-setup.exe", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            if (zipFallback is null && name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
            {
                zipFallback = url;
            }
        }

        return zipFallback;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        return null;
    }

    private static bool TryCreateHttpUri(string? raw, out Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(raw)
            && Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var parsed)
            && (parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    private static string SanitizeVersionForFilename(string? version)
    {
        var trimmed = (version ?? string.Empty).Trim().TrimStart('v', 'V');
        var sb = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_')
            {
                sb.Append(ch);
            }
        }

        return sb.Length > 0 ? sb.ToString() : "latest";
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup errors for partial downloads.
        }
    }
}
