using Emendly.Capture;

namespace Emendly.Capture.Scintilla;

public sealed class ScintillaCaptureStrategy : ICaptureStrategy
{
    public const string StrategyId = "scintilla";
    private const string ScintillaClassName = "Scintilla";
    private const int MaxContextScanBytes = 2400;
    private const int MaxContextHardCapBytes = 32768;
    private const int MaxContextScanLines = 20;
    private const int CopyRangeRetries = 3;
    private const int CopyRangeRetryDelayMs = 10;
    private static readonly string[] ScintillaPatterns = { "Scintilla" };

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

        var (editorHwnd, ambiguousView) = FindEditorWindow(topHwnd);
        if (editorHwnd == IntPtr.Zero)
        {
            return CaptureResult.Unhandled(Name);
        }

        if (!ScintillaMessages.TrySendLong(editorHwnd, ScintillaMessages.SCI_GETTEXTLENGTH, 0, 0, out var rawDocLength)
            || !ScintillaMessages.TrySendLong(editorHwnd, ScintillaMessages.SCI_GETLINECOUNT, 0, 0, out var rawLineCount)
            || !ScintillaMessages.TrySendLong(editorHwnd, ScintillaMessages.SCI_GETSELECTIONSTART, 0, 0, out var rawSelStart)
            || !ScintillaMessages.TrySendLong(editorHwnd, ScintillaMessages.SCI_GETSELECTIONEND, 0, 0, out var rawSelEnd))
        {
            return CaptureResult.Unhandled(Name);
        }

        var docLength = Math.Max(0L, rawDocLength);
        var lineCount = Math.Max(1L, rawLineCount);
        var selStart = Math.Clamp(rawSelStart, 0L, docLength);
        var selEnd = Math.Clamp(rawSelEnd, 0L, docLength);
        if (selEnd < selStart)
        {
            (selStart, selEnd) = (selEnd, selStart);
        }

        var enabled = WindowEnumerator.IsWindowEnabled(editorHwnd);
        var sciReadOnly = ScintillaMessages.TrySendLong(
            editorHwnd,
            ScintillaMessages.SCI_GETREADONLY,
            0,
            0,
            out var roFlag) && roFlag != 0;
        var readOnly = sciReadOnly || !enabled;

        var selectedText = string.Empty;
        SurroundingContext? surroundingContext = null;

        var hasSelectionRange = selEnd > selStart;
        var needContext = options.IncludeContext && docLength > 0;

        if (hasSelectionRange || needContext)
        {
            var clipboardSnapshot = ClipboardHelper.CaptureSnapshot();
            try
            {
                if (hasSelectionRange)
                {
                    selectedText = CopyRange(editorHwnd, selStart, selEnd);
                }

                if (needContext)
                {
                    surroundingContext = ReadSurroundingContext(editorHwnd, selStart, selEnd, docLength, lineCount);
                }
            }
            finally
            {
                ClipboardHelper.RestoreSnapshot(clipboardSnapshot);
            }
        }

        var classification = SelectionClassifier.Classify(selectedText, readOnly);
        var verdict = ComputeVerdict(enabled, readOnly, selectedText.Length);

        var caretLocation = options.IncludeContext
            ? LocateCaret(editorHwnd, topHwnd, selStart)
            : CaretLocator.GetMouseFallback();

        var fieldContext = options.IncludeContext
            ? BuildFieldContext(surroundingContext, selectedText)
            : string.Empty;

        var report = new ProbeReport(
            Hwnd: WindowEnumerator.FormatHwnd(window.Hwnd),
            Process: window.ProcessName,
            Title: window.Title,
            WindowClass: window.ClassName,
            Element: new ElementInfo(
                Name: string.Empty,
                ControlType: "Document",
                ClassName: ScintillaClassName,
                AutomationId: WindowEnumerator.FormatHwnd(editorHwnd),
                IsKeyboardFocusable: enabled),
            Patterns: ScintillaPatterns,
            Enabled: enabled,
            ReadOnly: readOnly,
            Selection: new SelectionInfo(selectedText.Length, UiaProbe.SanitizePreview(selectedText)),
            Caret: new CaretInfo(Supported: true, Offset: (int)Math.Min(selStart, int.MaxValue)),
            FieldTextLength: (int)Math.Min(docLength, int.MaxValue),
            Verdict: verdict,
            Strategy: StrategyId,
            AmbiguousView: ambiguousView ? (bool?)true : null);

        return new CaptureResult(
            Handled: true,
            State: classification.State,
            Text: selectedText,
            SelectionLength: selectedText.Length,
            ReadOnly: readOnly,
            StrategyName: StrategyId,
            CaretLocation: caretLocation,
            FieldContext: fieldContext,
            SurroundingContext: surroundingContext,
            Report: report,
            EditorHwnd: editorHwnd,
            AmbiguousView: ambiguousView);
    }

    internal static bool SendPaste(IntPtr editorHwnd) =>
        WindowEnumerator.IsWindowAlive(editorHwnd)
        && ScintillaMessages.Send(editorHwnd, ScintillaMessages.SCI_PASTE);

    internal static bool SendUndo(IntPtr editorHwnd) =>
        WindowEnumerator.IsWindowAlive(editorHwnd)
        && ScintillaMessages.Send(editorHwnd, ScintillaMessages.SCI_UNDO);

    private static (IntPtr EditorHwnd, bool AmbiguousView) FindEditorWindow(IntPtr topHwnd)
    {
        var children = WindowEnumerator.EnumerateChildWindowsByClass(topHwnd, ScintillaClassName);
        if (children.Count == 0)
        {
            return (IntPtr.Zero, false);
        }

        var focusedHwnd = WindowEnumerator.GetFocusedChildHwnd(topHwnd);
        if (focusedHwnd != IntPtr.Zero)
        {
            for (var i = 0; i < children.Count; i++)
            {
                if (children[i] == focusedHwnd)
                {
                    return (focusedHwnd, false);
                }
            }
        }

        // Notepad++ keeps a hidden second Scintilla pane in single-view mode, so prefer visible views.
        var visible = new List<IntPtr>(children.Count);
        for (var i = 0; i < children.Count; i++)
        {
            if (WindowEnumerator.IsWindowVisible(children[i]))
            {
                visible.Add(children[i]);
            }
        }

        if (visible.Count > 0)
        {
            return (visible[0], visible.Count > 1);
        }

        return (children[0], children.Count > 1);
    }

    private static SurroundingContext? ReadSurroundingContext(
        IntPtr editorHwnd,
        long selStart,
        long selEnd,
        long docLength,
        long lineCount)
    {
        var rawBefore = ReadBeforeText(editorHwnd, selStart);
        var rawAfter = ReadAfterText(editorHwnd, selEnd, docLength, lineCount);
        return ContextReader.Read(rawBefore, rawAfter);
    }

    private static string ReadBeforeText(IntPtr editorHwnd, long selStart)
    {
        if (selStart <= 0)
        {
            return string.Empty;
        }

        if (!ScintillaMessages.TrySendLong(editorHwnd, ScintillaMessages.SCI_LINEFROMPOSITION, selStart, 0, out var startLine))
        {
            return string.Empty;
        }

        var line = Math.Max(0L, startLine);
        if (!ScintillaMessages.TrySendLong(editorHwnd, ScintillaMessages.SCI_POSITIONFROMLINE, line, 0, out var beforeStart)
            || beforeStart < 0)
        {
            return string.Empty;
        }

        while (line > 0
            && (selStart - beforeStart) < MaxContextScanBytes
            && (startLine - line) < MaxContextScanLines)
        {
            if (!ScintillaMessages.TrySendLong(editorHwnd, ScintillaMessages.SCI_POSITIONFROMLINE, line - 1, 0, out var prevLineStart)
                || prevLineStart < 0
                || prevLineStart >= beforeStart)
            {
                break;
            }

            if ((selStart - prevLineStart) > MaxContextHardCapBytes && beforeStart < selStart)
            {
                break;
            }

            beforeStart = prevLineStart;
            line--;
        }

        if (beforeStart >= selStart || (selStart - beforeStart) > MaxContextHardCapBytes)
        {
            return string.Empty;
        }

        return CopyRange(editorHwnd, beforeStart, selStart);
    }

    private static string ReadAfterText(IntPtr editorHwnd, long selEnd, long docLength, long lineCount)
    {
        if (selEnd >= docLength)
        {
            return string.Empty;
        }

        if (!ScintillaMessages.TrySendLong(editorHwnd, ScintillaMessages.SCI_LINEFROMPOSITION, selEnd, 0, out var endLine))
        {
            return string.Empty;
        }

        var lastLine = Math.Max(0L, lineCount - 1);
        var line = Math.Clamp(endLine, 0L, lastLine);

        if (!ScintillaMessages.TrySendLong(editorHwnd, ScintillaMessages.SCI_GETLINEENDPOSITION, line, 0, out var afterEnd)
            || afterEnd < 0)
        {
            return string.Empty;
        }

        while (line < lastLine
            && (afterEnd - selEnd) < MaxContextScanBytes
            && (line - endLine) < MaxContextScanLines)
        {
            if (!ScintillaMessages.TrySendLong(editorHwnd, ScintillaMessages.SCI_GETLINEENDPOSITION, line + 1, 0, out var nextLineEnd)
                || nextLineEnd <= afterEnd)
            {
                break;
            }

            if ((nextLineEnd - selEnd) > MaxContextHardCapBytes && afterEnd > selEnd)
            {
                break;
            }

            afterEnd = nextLineEnd;
            line++;
        }

        afterEnd = Math.Min(afterEnd, docLength);
        if (afterEnd <= selEnd || (afterEnd - selEnd) > MaxContextHardCapBytes)
        {
            return string.Empty;
        }

        return CopyRange(editorHwnd, selEnd, afterEnd);
    }

    private static string CopyRange(IntPtr editorHwnd, long start, long end)
    {
        if (start < 0 || end <= start)
        {
            return string.Empty;
        }

        for (var attempt = 0; attempt < CopyRangeRetries; attempt++)
        {
            var seqBefore = ClipboardHelper.GetSequenceNumber();
            if (!ScintillaMessages.Send(editorHwnd, ScintillaMessages.SCI_COPYRANGE, start, end))
            {
                return string.Empty;
            }

            if (ClipboardHelper.GetSequenceNumber() != seqBefore)
            {
                return ClipboardHelper.ReadText();
            }

            if (attempt + 1 < CopyRangeRetries)
            {
                Thread.Sleep(CopyRangeRetryDelayMs);
            }
        }

        return string.Empty;
    }

    private static CaretLocation LocateCaret(IntPtr editorHwnd, IntPtr topHwnd, long position)
    {
        if (ScintillaMessages.TrySendLong(editorHwnd, ScintillaMessages.SCI_POINTXFROMPOSITION, 0, position, out var rawX)
            && ScintillaMessages.TrySendLong(editorHwnd, ScintillaMessages.SCI_POINTYFROMPOSITION, 0, position, out var rawY))
        {
            var lineHeight = 18;
            if (ScintillaMessages.TrySendLong(editorHwnd, ScintillaMessages.SCI_LINEFROMPOSITION, position, 0, out var line)
                && ScintillaMessages.TrySendLong(editorHwnd, ScintillaMessages.SCI_TEXTHEIGHT, Math.Max(0L, line), 0, out var h)
                && h > 0)
            {
                lineHeight = (int)Math.Min(h, 256);
            }

            if (CaretLocator.TryClientCaretToScreen(editorHwnd, (int)rawX, (int)rawY, lineHeight, out var bounds))
            {
                return new CaretLocation(bounds, FromCaret: true);
            }
        }

        return CaretLocator.Locate(null, topHwnd);
    }

    private static string BuildFieldContext(SurroundingContext? surrounding, string selectedText)
    {
        if (surrounding is null)
        {
            return selectedText;
        }

        return string.Concat(surrounding.Before, selectedText, surrounding.After);
    }

    private static string ComputeVerdict(bool enabled, bool readOnly, int selectionLength)
    {
        if (readOnly || !enabled)
        {
            return "read_only";
        }

        return selectionLength > 0 ? "selection" : "caret";
    }
}
