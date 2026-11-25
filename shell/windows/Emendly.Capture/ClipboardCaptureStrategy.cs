namespace Emendly.Capture;

public sealed class ClipboardCaptureStrategy : ICaptureStrategy
{
    public const string StrategyId = "clipboard";
    private const int CopyPollIntervalMs = 15;
    private const int CopyWaitTimeoutMs = 150;
    private static readonly string[] ClipboardPatterns = { "Clipboard" };

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

        // Never send Ctrl+C unless the target window owns the foreground.
        if (WindowEnumerator.GetForegroundWindow() != topHwnd)
        {
            return CaptureResult.Unhandled(Name);
        }

        var snapshot = ClipboardHelper.CaptureSnapshot();
        var selectedText = string.Empty;

        try
        {
            var seqBefore = ClipboardHelper.GetSequenceNumber();
            if (!ClipboardHelper.SendCtrlChord(ClipboardHelper.VkC))
            {
                return CaptureResult.Unhandled(Name);
            }

            if (WaitForClipboardChange(seqBefore))
            {
                selectedText = ClipboardHelper.ReadText();
            }
        }
        finally
        {
            ClipboardHelper.RestoreSnapshot(snapshot);
        }

        var classification = SelectionClassifier.Classify(selectedText, readOnly: false);
        var verdict = selectedText.Length > 0 ? "selection" : "caret";
        var caretLocation = options.IncludeContext
            ? CaretLocator.Locate(null, topHwnd)
            : CaretLocator.GetMouseFallback();

        var report = new ProbeReport(
            Hwnd: WindowEnumerator.FormatHwnd(window.Hwnd),
            Process: window.ProcessName,
            Title: window.Title,
            WindowClass: window.ClassName,
            Element: new ElementInfo(
                Name: string.Empty,
                ControlType: "Window",
                ClassName: window.ClassName,
                AutomationId: string.Empty,
                IsKeyboardFocusable: true),
            Patterns: ClipboardPatterns,
            Enabled: true,
            ReadOnly: false,
            Selection: new SelectionInfo(selectedText.Length, UiaProbe.SanitizePreview(selectedText)),
            Caret: new CaretInfo(Supported: false, Offset: null),
            FieldTextLength: selectedText.Length,
            Verdict: verdict,
            Strategy: StrategyId);

        return new CaptureResult(
            Handled: true,
            State: classification.State,
            Text: selectedText,
            SelectionLength: selectedText.Length,
            ReadOnly: false,
            StrategyName: StrategyId,
            CaretLocation: caretLocation,
            FieldContext: options.IncludeContext ? selectedText : string.Empty,
            SurroundingContext: null,
            Report: report);
    }

    private static bool WaitForClipboardChange(uint seqBefore)
    {
        var deadline = Environment.TickCount64 + CopyWaitTimeoutMs;
        while (true)
        {
            if (ClipboardHelper.GetSequenceNumber() != seqBefore)
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            Thread.Sleep(CopyPollIntervalMs);
        }
    }
}
