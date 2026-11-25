using Emendly.Capture.Scintilla;

namespace Emendly.Capture;

public sealed record UndoEntry(
    IntPtr TargetHwnd,
    string ProcessName,
    string ActionName,
    string ReplacedText,
    string InsertedText,
    bool WasSelection,
    string StrategyName = "uia",
    IntPtr EditorHwnd = default);

public sealed class ClipboardInserter
{
    private const int FocusSettleDelayMs = 60;
    private const int ForegroundPollIntervalMs = 20;
    private const int ForegroundWaitTimeoutMs = 300;
    private const int PasteConsumeDelayMs = 150;

    public UndoEntry? LastUndo { get; private set; }

    public bool HasUndo => LastUndo is not null;

    public Task CopyTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Task.CompletedTask;
        }

        return ClipboardHelper.SetTextAsync(text);
    }

    public async Task<bool> PasteAsync(
        IntPtr targetHwnd,
        string processName,
        string actionName,
        string textToInsert,
        string replacedText,
        bool wasSelection,
        string strategyName = "uia",
        IntPtr editorHwnd = default,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(textToInsert) || !WindowEnumerator.IsWindowAlive(targetHwnd))
        {
            return false;
        }

        if (string.Equals(strategyName, ScintillaCaptureStrategy.StrategyId, StringComparison.Ordinal)
            && editorHwnd != IntPtr.Zero)
        {
            return await PasteScintillaAsync(
                targetHwnd,
                editorHwnd,
                processName,
                actionName,
                textToInsert,
                replacedText,
                wasSelection,
                cancellationToken).ConfigureAwait(true);
        }

        var snapshot = await ClipboardHelper.CaptureSnapshotAsync().ConfigureAwait(true);

        try
        {
            await ClipboardHelper.SetTextAsync(textToInsert).ConfigureAwait(true);

            // SetForegroundWindow can be refused while another process holds the foreground
            // lock, so ask and then wait for the change instead of trusting the return value.
            _ = WindowEnumerator.RestoreForeground(targetHwnd);
            if (!await WaitForForegroundAsync(targetHwnd, cancellationToken).ConfigureAwait(true))
            {
                return false;
            }

            await Task.Delay(FocusSettleDelayMs, cancellationToken).ConfigureAwait(true);

            // Never send Ctrl+V unless the target still owns the foreground.
            if (WindowEnumerator.GetForegroundWindow() != targetHwnd)
            {
                return false;
            }

            if (!ClipboardHelper.SendCtrlChord(ClipboardHelper.VkV))
            {
                return false;
            }

            await Task.Delay(PasteConsumeDelayMs, cancellationToken).ConfigureAwait(true);

            LastUndo = new UndoEntry(
                TargetHwnd: targetHwnd,
                ProcessName: processName,
                ActionName: actionName,
                ReplacedText: replacedText,
                InsertedText: textToInsert,
                WasSelection: wasSelection,
                StrategyName: strategyName,
                EditorHwnd: editorHwnd);

            return true;
        }
        finally
        {
            await ClipboardHelper.RestoreSnapshotAsync(snapshot).ConfigureAwait(true);
        }
    }

    private async Task<bool> PasteScintillaAsync(
        IntPtr targetHwnd,
        IntPtr editorHwnd,
        string processName,
        string actionName,
        string textToInsert,
        string replacedText,
        bool wasSelection,
        CancellationToken cancellationToken)
    {
        if (!WindowEnumerator.IsWindowAlive(editorHwnd))
        {
            return false;
        }

        var snapshot = await ClipboardHelper.CaptureSnapshotAsync().ConfigureAwait(true);

        try
        {
            await ClipboardHelper.SetTextAsync(textToInsert).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            if (!ScintillaCaptureStrategy.SendPaste(editorHwnd))
            {
                return false;
            }

            _ = WindowEnumerator.RestoreForeground(targetHwnd);

            LastUndo = new UndoEntry(
                TargetHwnd: targetHwnd,
                ProcessName: processName,
                ActionName: actionName,
                ReplacedText: replacedText,
                InsertedText: textToInsert,
                WasSelection: wasSelection,
                StrategyName: ScintillaCaptureStrategy.StrategyId,
                EditorHwnd: editorHwnd);

            return true;
        }
        finally
        {
            await ClipboardHelper.RestoreSnapshotAsync(snapshot).ConfigureAwait(true);
        }
    }

    private static async Task<bool> WaitForForegroundAsync(IntPtr targetHwnd, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + ForegroundWaitTimeoutMs;
        while (true)
        {
            if (WindowEnumerator.GetForegroundWindow() == targetHwnd)
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            await Task.Delay(ForegroundPollIntervalMs, cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task<bool> UndoLastAsync()
    {
        var entry = LastUndo;
        if (entry is null || !WindowEnumerator.IsWindowAlive(entry.TargetHwnd))
        {
            return false;
        }

        if (string.Equals(entry.StrategyName, ScintillaCaptureStrategy.StrategyId, StringComparison.Ordinal)
            && entry.EditorHwnd != IntPtr.Zero
            && WindowEnumerator.IsWindowAlive(entry.EditorHwnd))
        {
            if (!ScintillaCaptureStrategy.SendUndo(entry.EditorHwnd))
            {
                return false;
            }

            _ = WindowEnumerator.RestoreForeground(entry.TargetHwnd);
            LastUndo = null;
            return true;
        }

        if (!WindowEnumerator.RestoreForeground(entry.TargetHwnd))
        {
            return false;
        }

        await Task.Delay(FocusSettleDelayMs).ConfigureAwait(true);

        if (!ClipboardHelper.SendCtrlChord(ClipboardHelper.VkZ))
        {
            return false;
        }

        LastUndo = null;
        return true;
    }
}
