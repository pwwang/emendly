using System;
using System.Reflection;

namespace Emendly.App;

public static class VersionInfo
{
    private static volatile string? _engineVersion;

    public static string AppVersion { get; } = ReadAppVersion();

    public static string? EngineVersion
    {
        get => _engineVersion;
        set
        {
            _engineVersion = value;
            RefreshRequested?.Invoke();
        }
    }

    /// <summary>
    /// Raised when the engine version becomes known, so an open settings window
    /// can replace the placeholder instead of waiting to be reopened.
    /// </summary>
    public static event Action? RefreshRequested;

    public static int ProtocolVersion => EngineTransformer.SupportedProtocolVersion;

    public static string FormatFooter(string? engineVersion = null)
    {
        var resolvedEngine = engineVersion ?? EngineVersion;
        var engineText = string.IsNullOrWhiteSpace(resolvedEngine) ? "—" : resolvedEngine.Trim();
        return $"Emendly {AppVersion} · engine {engineText} · protocol {ProtocolVersion}";
    }

    private static string ReadAppVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plusIndex = info.IndexOf('+');
            var trimmed = plusIndex >= 0 ? info[..plusIndex] : info;
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                return trimmed.Trim();
            }
        }

        var version = assembly.GetName().Version;
        return version is not null ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.0.0";
    }
}
