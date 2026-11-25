namespace Emendly.Capture;

public sealed class UiaCaptureStrategy : ICaptureStrategy
{
    public const string StrategyId = "uia";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    public string Name => StrategyId;

    public CaptureResult TryCapture(IntPtr topHwnd) =>
        TryCapture(topHwnd, CaptureOptions.Full);

    public CaptureResult TryCapture(IntPtr topHwnd, CaptureOptions options)
    {
        var window = WindowEnumerator.GetWindowInfo(topHwnd);
        if (window is null)
        {
            return CaptureResult.Unhandled(Name);
        }

        var timeout = options.Timeout is { } t && t > TimeSpan.Zero ? t : DefaultTimeout;
        var outcome = UiaProbe.ExecuteWithTimeout(window, timeout, options.IncludeContext);

        if (outcome.TimedOut)
        {
            return CaptureResult.Timeout(Name);
        }

        if (outcome.Snapshot is null
            || string.Equals(outcome.Snapshot.Report.Verdict, "unsupported", StringComparison.Ordinal))
        {
            return CaptureResult.Unhandled(Name);
        }

        var snapshot = outcome.Snapshot;
        var classification = SelectionClassifier.Classify(snapshot.SelectedText, snapshot.Report.ReadOnly);
        var report = snapshot.Report with { Strategy = StrategyId };

        return new CaptureResult(
            Handled: true,
            State: classification.State,
            Text: snapshot.SelectedText,
            SelectionLength: snapshot.Report.Selection.Length,
            ReadOnly: snapshot.Report.ReadOnly,
            StrategyName: StrategyId,
            CaretLocation: snapshot.CaretLocation,
            FieldContext: snapshot.FieldContext,
            SurroundingContext: snapshot.SurroundingContext,
            Report: report);
    }
}
