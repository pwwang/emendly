namespace Emendly.Capture;

public readonly record struct CaptureOptions(bool IncludeContext = true, TimeSpan? Timeout = null)
{
    public static readonly CaptureOptions Full = new(IncludeContext: true);
    public static readonly CaptureOptions ProbeOnly = new(IncludeContext: false);
}

public sealed record CaptureResult(
    bool Handled,
    SelectionState State,
    string Text,
    int SelectionLength,
    bool ReadOnly,
    string StrategyName,
    CaretLocation CaretLocation,
    string FieldContext = "",
    SurroundingContext? SurroundingContext = null,
    ProbeReport? Report = null,
    IntPtr EditorHwnd = default,
    bool AmbiguousView = false,
    bool TimedOut = false)
{
    public static CaptureResult Unhandled(string strategyName = "") =>
        new(
            Handled: false,
            State: SelectionState.Empty,
            Text: string.Empty,
            SelectionLength: 0,
            ReadOnly: false,
            StrategyName: strategyName,
            CaretLocation: CaretLocator.GetMouseFallback());

    public static CaptureResult Timeout(string strategyName = "") =>
        new(
            Handled: false,
            State: SelectionState.Empty,
            Text: string.Empty,
            SelectionLength: 0,
            ReadOnly: false,
            StrategyName: strategyName,
            CaretLocation: CaretLocator.GetMouseFallback(),
            TimedOut: true);
}

public interface ICaptureStrategy
{
    string Name { get; }

    CaptureResult TryCapture(IntPtr topHwnd);

    CaptureResult TryCapture(IntPtr topHwnd, CaptureOptions options) => TryCapture(topHwnd);
}
