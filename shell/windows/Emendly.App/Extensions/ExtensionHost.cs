using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Emendly.App.Extensions;

public static class ExtensionHost
{
    private static readonly object Gate = new();
    private static readonly List<IEmendlyExtension> Registered = new();

    public static IReadOnlyList<IEmendlyExtension> Extensions
    {
        get
        {
            lock (Gate)
            {
                return Registered.Count == 0
                    ? Array.Empty<IEmendlyExtension>()
                    : Registered.ToArray();
            }
        }
    }

    public static void Register(IEmendlyExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);

        lock (Gate)
        {
            if (!Registered.Contains(extension))
            {
                Registered.Add(extension);
            }
        }
    }

    public static void ApplyTrayMenu(ContextMenuStrip menu)
    {
        ArgumentNullException.ThrowIfNull(menu);

        var snapshot = Extensions;
        if (snapshot.Count == 0)
        {
            return;
        }

        foreach (var extension in snapshot)
        {
            try
            {
                extension.ConfigureTrayMenu(menu);
            }
            catch (Exception)
            {
                ShellLogger.LogMeta("extension.tray", extension.Name, "error");
            }
        }
    }

    public static TransformRequest ApplyRequestFilters(TransformRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = Extensions;
        if (snapshot.Count == 0)
        {
            return request;
        }

        var current = request;
        foreach (var extension in snapshot)
        {
            try
            {
                var filtered = extension.TransformRequestFilter(current);
                if (filtered is not null)
                {
                    current = filtered;
                }
            }
            catch (Exception)
            {
                ShellLogger.LogMeta("extension.filter", extension.Name, "error");
            }
        }

        return current;
    }

    public static void NotifyCompleted(
        TransformRequest request,
        TransformResult result,
        string? appProcess)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        var snapshot = Extensions;
        if (snapshot.Count == 0)
        {
            return;
        }

        foreach (var extension in snapshot)
        {
            try
            {
                extension.OnTransformCompleted(request, result, appProcess);
            }
            catch (Exception)
            {
                ShellLogger.LogMeta("extension.completed", extension.Name, "error");
            }
        }
    }
}
