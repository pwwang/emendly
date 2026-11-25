using Emendly.Capture.Scintilla;

namespace Emendly.Capture;

public static class CaptureChain
{
    private static readonly UiaCaptureStrategy UiaStrategy = new();
    private static readonly ScintillaCaptureStrategy ScintillaStrategy = new();
    private static readonly ClipboardCaptureStrategy ClipboardStrategy = new();

    private static readonly ICaptureStrategy[] DefaultChain =
        { UiaStrategy, ScintillaStrategy };

    private static readonly ICaptureStrategy[] ScintillaFirstChain =
        { ScintillaStrategy, UiaStrategy };

    private static readonly ICaptureStrategy[] ClipboardFallbackChain =
        { UiaStrategy, ScintillaStrategy, ClipboardStrategy };

    private static readonly ICaptureStrategy[] UiaOnlyChain =
        { UiaStrategy };

    public static IReadOnlyList<ICaptureStrategy> ResolveStrategies(string? processName, AppCaptureRules? rules)
    {
        var rule = rules?.GetStrategyForProcess(processName);
        return rule switch
        {
            ScintillaCaptureStrategy.StrategyId => ScintillaFirstChain,
            ClipboardCaptureStrategy.StrategyId => ClipboardFallbackChain,
            UiaCaptureStrategy.StrategyId => UiaOnlyChain,
            _ => DefaultChain,
        };
    }

    public static CaptureResult TryCapture(
        IntPtr topHwnd,
        AppCaptureRules? rules = null,
        CaptureOptions? options = null)
    {
        var window = WindowEnumerator.GetWindowInfo(topHwnd);
        if (window is null)
        {
            return CaptureResult.Unhandled();
        }

        var captureOptions = options ?? CaptureOptions.Full;
        var strategies = ResolveStrategies(window.ProcessName, rules);
        var deadline = captureOptions.Timeout is { } t && t > TimeSpan.Zero
            ? DateTime.UtcNow + t
            : (DateTime?)null;

        foreach (var strategy in strategies)
        {
            TimeSpan? remaining = null;
            if (deadline is { } dl)
            {
                var left = dl - DateTime.UtcNow;
                if (left <= TimeSpan.Zero)
                {
                    return CaptureResult.Timeout(strategy.Name);
                }

                remaining = left;
            }

            var stepOptions = new CaptureOptions(
                IncludeContext: captureOptions.IncludeContext,
                Timeout: remaining);

            var result = strategy.TryCapture(topHwnd, stepOptions);
            if (result.TimedOut)
            {
                return result;
            }

            if (result.Handled)
            {
                return result;
            }
        }

        return CaptureResult.Unhandled();
    }

    public static ProbeOutcome ProbeWithTimeout(WindowInfo window, AppCaptureRules? rules, TimeSpan timeout)
    {
        var outcome = ExecuteWithTimeout(window, rules, timeout, includeContext: false);
        if (outcome.TimedOut)
        {
            return ProbeOutcome.Timeout();
        }

        if (outcome.Snapshot is null)
        {
            return ProbeOutcome.Failure(outcome.ErrorMessage ?? "probe produced no report");
        }

        return ProbeOutcome.Success(outcome.Snapshot.Report);
    }

    public static CaptureOutcome CaptureWithTimeout(WindowInfo window, AppCaptureRules? rules, TimeSpan timeout) =>
        ExecuteWithTimeout(window, rules, timeout, includeContext: true);

    private static CaptureOutcome ExecuteWithTimeout(
        WindowInfo window,
        AppCaptureRules? rules,
        TimeSpan timeout,
        bool includeContext)
    {
        CaptureResult? result = null;
        Exception? failure = null;

        var worker = new Thread(() =>
        {
            try
            {
                var options = new CaptureOptions(IncludeContext: includeContext, Timeout: timeout);
                result = TryCapture(window.Hwnd, rules, options);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "Emendly.CaptureChain",
        };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();

        if (!worker.Join(timeout))
        {
            return CaptureOutcome.Timeout();
        }

        if (failure is not null)
        {
            return CaptureOutcome.Failure(failure.Message);
        }

        if (result is null)
        {
            return CaptureOutcome.Failure("capture produced no result");
        }

        if (result.TimedOut)
        {
            return CaptureOutcome.Timeout();
        }

        if (!result.Handled)
        {
            return CaptureOutcome.Success(UiaProbe.UnsupportedSnapshot(window, includeContext));
        }

        var report = result.Report ?? BuildFallbackReport(window, result);
        var snapshot = new CaptureSnapshot(
            Report: report,
            SelectedText: result.Text,
            FieldContext: result.FieldContext,
            CaretLocation: result.CaretLocation,
            SurroundingContext: result.SurroundingContext,
            StrategyName: result.StrategyName,
            EditorHwnd: result.EditorHwnd,
            AmbiguousView: result.AmbiguousView);

        return CaptureOutcome.Success(snapshot);
    }

    private static ProbeReport BuildFallbackReport(WindowInfo window, CaptureResult result)
    {
        var verdict = result.ReadOnly
            ? "read_only"
            : result.SelectionLength > 0 ? "selection" : "caret";

        return new ProbeReport(
            Hwnd: WindowEnumerator.FormatHwnd(window.Hwnd),
            Process: window.ProcessName,
            Title: window.Title,
            WindowClass: window.ClassName,
            Element: null,
            Patterns: Array.Empty<string>(),
            Enabled: !result.ReadOnly,
            ReadOnly: result.ReadOnly,
            Selection: new SelectionInfo(result.SelectionLength, UiaProbe.SanitizePreview(result.Text)),
            Caret: new CaretInfo(Supported: false, Offset: null),
            FieldTextLength: result.SelectionLength,
            Verdict: verdict,
            Strategy: string.IsNullOrEmpty(result.StrategyName) ? "none" : result.StrategyName,
            AmbiguousView: result.AmbiguousView ? (bool?)true : null);
    }
}
