using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Emendly.Capture;

public sealed record ProbeOutcome(ProbeReport? Report, bool TimedOut, string? ErrorMessage)
{
    public static ProbeOutcome Success(ProbeReport report) => new(report, false, null);
    public static ProbeOutcome Timeout() => new(null, true, "timeout");
    public static ProbeOutcome Failure(string message) => new(null, false, message);
}

public sealed record CaptureSnapshot(
    ProbeReport Report,
    string SelectedText,
    string FieldContext,
    CaretLocation CaretLocation,
    SurroundingContext? SurroundingContext = null);

public sealed record CaptureOutcome(CaptureSnapshot? Snapshot, bool TimedOut, string? ErrorMessage)
{
    public static CaptureOutcome Success(CaptureSnapshot snapshot) => new(snapshot, false, null);
    public static CaptureOutcome Timeout() => new(null, true, "timeout");
    public static CaptureOutcome Failure(string message) => new(null, false, message);
}

public static class UiaProbe
{
    private const int MaxPreviewLength = 80;
    private const int MaxFieldContextLength = 4000;
    private const int MaxVisitedNodes = 2000;
    private const int MaxParentDepth = 64;

    public static ProbeOutcome ProbeWithTimeout(WindowInfo window, TimeSpan timeout)
    {
        var outcome = ExecuteWithTimeout(window, timeout, includeTextDetails: false);
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

    public static CaptureOutcome CaptureWithTimeout(WindowInfo window, TimeSpan timeout) =>
        ExecuteWithTimeout(window, timeout, includeTextDetails: true);

    private static CaptureOutcome ExecuteWithTimeout(WindowInfo window, TimeSpan timeout, bool includeTextDetails)
    {
        CaptureSnapshot? snapshot = null;
        Exception? failure = null;
        var deadline = DateTime.UtcNow + timeout;

        var worker = new Thread(() =>
        {
            try
            {
                snapshot = ProbeInternal(window, deadline, includeTextDetails);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "Emendly.UiaProbe",
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

        if (snapshot is null)
        {
            return CaptureOutcome.Failure("probe produced no report");
        }

        return CaptureOutcome.Success(snapshot);
    }

    private static CaptureSnapshot ProbeInternal(WindowInfo window, DateTime deadline, bool includeTextDetails)
    {
        var windowElement = SafeCall<AutomationElement?>(() => AutomationElement.FromHandle(window.Hwnd), null);
        if (windowElement is null)
        {
            return UnsupportedSnapshot(window, includeTextDetails);
        }

        var targetElement = ResolveTextElement(windowElement, window.Hwnd, deadline);
        if (targetElement is null)
        {
            return UnsupportedSnapshot(window, includeTextDetails);
        }

        var textPattern = TryGetTextPattern(targetElement);
        if (textPattern is null)
        {
            return UnsupportedSnapshot(window, includeTextDetails);
        }

        var elementInfo = ReadElementInfo(targetElement);
        var patterns = ReadSupportedPatterns(targetElement);
        if (!patterns.Contains("TextPattern", StringComparer.Ordinal))
        {
            patterns.Insert(0, "TextPattern");
        }

        var enabled = SafeCall(() => targetElement.Current.IsEnabled, false);
        var valuePattern = TryGetValuePattern(targetElement);
        var readOnly = DetermineReadOnly(valuePattern, textPattern, enabled);

        var docRange = SafeCall<TextPatternRange?>(() => textPattern.DocumentRange, null);
        var fieldText = includeTextDetails ? ReadFieldText(docRange, valuePattern) : null;
        var fieldTextLength = fieldText is not null
            ? fieldText.Length
            : ReadFieldTextLength(docRange, valuePattern);

        var (selection, caret, selectedText, primarySelection) = ReadSelectionAndCaret(textPattern, docRange);

        if (selection.Length > fieldTextLength)
        {
            fieldTextLength = selection.Length;
        }

        var verdict = ComputeVerdict(enabled, readOnly, selection.Length);

        var report = new ProbeReport(
            Hwnd: WindowEnumerator.FormatHwnd(window.Hwnd),
            Process: window.ProcessName,
            Title: window.Title,
            WindowClass: window.ClassName,
            Element: elementInfo,
            Patterns: patterns,
            Enabled: enabled,
            ReadOnly: readOnly,
            Selection: selection,
            Caret: caret,
            FieldTextLength: fieldTextLength,
            Verdict: verdict);

        var caretLocation = includeTextDetails
            ? CaretLocator.Locate(primarySelection, window.Hwnd)
            : CaretLocator.GetMouseFallback();

        var fieldContext = includeTextDetails
            ? ExtractSurroundingContext(fieldText ?? string.Empty, caret.Offset)
            : string.Empty;

        var surroundingContext = includeTextDetails
            ? ContextReader.Read(primarySelection, docRange)
            : null;

        return new CaptureSnapshot(
            Report: report,
            SelectedText: includeTextDetails ? selectedText : string.Empty,
            FieldContext: fieldContext,
            CaretLocation: caretLocation,
            SurroundingContext: surroundingContext);
    }

    private static CaptureSnapshot UnsupportedSnapshot(WindowInfo window, bool includeTextDetails) =>
        new(
            Report: UnsupportedReport(window),
            SelectedText: string.Empty,
            FieldContext: string.Empty,
            CaretLocation: includeTextDetails
                ? CaretLocator.Locate(null, window.Hwnd)
                : CaretLocator.GetMouseFallback());

    private static AutomationElement? ResolveTextElement(
        AutomationElement windowElement,
        IntPtr targetHwnd,
        DateTime deadline)
    {
        var globalFocused = SafeCall<AutomationElement?>(() => AutomationElement.FocusedElement, null);
        if (globalFocused is not null && BelongsToWindow(globalFocused, windowElement, targetHwnd))
        {
            if (TryGetTextPattern(globalFocused) is not null)
            {
                return globalFocused;
            }
        }

        // When probing a non-foreground window, fall back to its thread's focused child HWND.
        var focusedChildHwnd = WindowEnumerator.GetFocusedChildHwnd(targetHwnd);
        if (focusedChildHwnd != IntPtr.Zero)
        {
            var threadFocused = SafeCall<AutomationElement?>(() => AutomationElement.FromHandle(focusedChildHwnd), null);
            if (threadFocused is not null && TryGetTextPattern(threadFocused) is not null)
            {
                return threadFocused;
            }
        }

        return FindFirstTextDescendantBreadthFirst(windowElement, deadline);
    }

    private static AutomationElement? FindFirstTextDescendantBreadthFirst(
        AutomationElement root,
        DateTime deadline)
    {
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);
        var visited = 0;

        while (queue.Count > 0 && visited < MaxVisitedNodes && DateTime.UtcNow < deadline)
        {
            var current = queue.Dequeue();
            visited++;

            var controlType = SafeCall<ControlType?>(() => current.Current.ControlType, null);
            if ((Equals(controlType, ControlType.Document) || Equals(controlType, ControlType.Edit))
                && TryGetTextPattern(current) is not null)
            {
                return current;
            }

            var child = SafeCall<AutomationElement?>(() => walker.GetFirstChild(current), null);
            while (child is not null && visited + queue.Count < MaxVisitedNodes && DateTime.UtcNow < deadline)
            {
                queue.Enqueue(child);
                child = SafeCall<AutomationElement?>(() => walker.GetNextSibling(child), null);
            }
        }

        return null;
    }

    private static bool BelongsToWindow(
        AutomationElement candidate,
        AutomationElement windowElement,
        IntPtr targetHwnd)
    {
        var walker = TreeWalker.RawViewWalker;
        AutomationElement? current = candidate;
        var targetHwndInt = unchecked((int)targetHwnd.ToInt64());

        for (var depth = 0; depth < MaxParentDepth && current is not null; depth++)
        {
            if (SafeCall(() => Automation.Compare(current, windowElement), false))
            {
                return true;
            }

            if (targetHwndInt != 0 && SafeCall(() => current.Current.NativeWindowHandle, 0) == targetHwndInt)
            {
                return true;
            }

            if (SafeCall(() => Automation.Compare(current, AutomationElement.RootElement), false))
            {
                return false;
            }

            current = SafeCall<AutomationElement?>(() => walker.GetParent(current), null);
        }

        return false;
    }

    private static ElementInfo ReadElementInfo(AutomationElement element)
    {
        var name = SafeCall(() => element.Current.Name ?? string.Empty, string.Empty);
        var controlType = SafeCall<ControlType?>(() => element.Current.ControlType, null);
        var className = SafeCall(() => element.Current.ClassName ?? string.Empty, string.Empty);
        var automationId = SafeCall(() => element.Current.AutomationId ?? string.Empty, string.Empty);
        var focusable = SafeCall(() => element.Current.IsKeyboardFocusable, false);

        return new ElementInfo(
            Name: name,
            ControlType: FormatControlType(controlType),
            ClassName: className,
            AutomationId: automationId,
            IsKeyboardFocusable: focusable);
    }

    private static List<string> ReadSupportedPatterns(AutomationElement element)
    {
        var supported = SafeCall(
            () => element.GetSupportedPatterns() ?? Array.Empty<AutomationPattern>(),
            Array.Empty<AutomationPattern>());

        return supported
            .Select(FormatPatternName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool DetermineReadOnly(ValuePattern? valuePattern, TextPattern textPattern, bool enabled)
    {
        if (valuePattern is not null)
        {
            return SafeCall(() => valuePattern.Current.IsReadOnly, !enabled);
        }

        if (!enabled)
        {
            return true;
        }

        var attr = SafeCall<object?>(
            () => textPattern.DocumentRange?.GetAttributeValue(TextPattern.IsReadOnlyAttribute),
            null);

        if (attr is bool isReadOnly)
        {
            return isReadOnly;
        }

        return false;
    }

    private static int ReadFieldTextLength(TextPatternRange? docRange, ValuePattern? valuePattern)
    {
        var length = SafeCall(() => docRange?.GetText(-1)?.Length ?? -1, -1);
        if (length >= 0)
        {
            return length;
        }

        if (valuePattern is not null)
        {
            return SafeCall(() => valuePattern.Current.Value?.Length ?? 0, 0);
        }

        return 0;
    }

    private static string ReadFieldText(TextPatternRange? docRange, ValuePattern? valuePattern)
    {
        var text = SafeCall<string?>(() => docRange?.GetText(-1), null);
        if (text is not null)
        {
            return text;
        }

        if (valuePattern is not null)
        {
            return SafeCall(() => valuePattern.Current.Value ?? string.Empty, string.Empty);
        }

        return string.Empty;
    }

    private static string ExtractSurroundingContext(string fieldText, int? caretOffset)
    {
        if (string.IsNullOrWhiteSpace(fieldText))
        {
            return string.Empty;
        }

        if (fieldText.Length <= MaxFieldContextLength)
        {
            return fieldText;
        }

        var center = Math.Clamp(caretOffset ?? fieldText.Length, 0, fieldText.Length);
        var half = MaxFieldContextLength / 2;
        var start = Math.Clamp(center - half, 0, fieldText.Length - MaxFieldContextLength);
        return fieldText.Substring(start, MaxFieldContextLength);
    }

    private static (SelectionInfo Selection, CaretInfo Caret, string SelectedText, TextPatternRange? PrimarySelection) ReadSelectionAndCaret(
        TextPattern textPattern,
        TextPatternRange? docRange)
    {
        var selections = SafeCall(
            () => textPattern.GetSelection() ?? Array.Empty<TextPatternRange>(),
            Array.Empty<TextPatternRange>());

        if (selections.Length == 0 || selections[0] is not { } primarySelection)
        {
            return (new SelectionInfo(0, string.Empty), new CaretInfo(false, null), string.Empty, null);
        }

        var selectedText = SafeCall(() => primarySelection.GetText(-1) ?? string.Empty, string.Empty);
        var selection = new SelectionInfo(selectedText.Length, SanitizePreview(selectedText));

        int? caretOffset = null;
        if (docRange is not null)
        {
            caretOffset = SafeCall<int?>(() =>
            {
                var prefix = docRange.Clone();
                prefix.MoveEndpointByRange(
                    TextPatternRangeEndpoint.End,
                    primarySelection,
                    TextPatternRangeEndpoint.Start);
                return prefix.GetText(-1)?.Length ?? 0;
            }, null);
        }

        return (selection, new CaretInfo(true, caretOffset), selectedText, primarySelection);
    }

    private static string ComputeVerdict(bool enabled, bool readOnly, int selectionLength)
    {
        if (readOnly || !enabled)
        {
            return "read_only";
        }

        return selectionLength > 0 ? "selection" : "caret";
    }

    private static ProbeReport UnsupportedReport(WindowInfo window) =>
        new(
            Hwnd: WindowEnumerator.FormatHwnd(window.Hwnd),
            Process: window.ProcessName,
            Title: window.Title,
            WindowClass: window.ClassName,
            Element: null,
            Patterns: Array.Empty<string>(),
            Enabled: false,
            ReadOnly: false,
            Selection: new SelectionInfo(0, string.Empty),
            Caret: new CaretInfo(false, null),
            FieldTextLength: 0,
            Verdict: "unsupported");

    private static TextPattern? TryGetTextPattern(AutomationElement element) =>
        SafeCall<TextPattern?>(() =>
            element.TryGetCurrentPattern(TextPattern.Pattern, out var obj) && obj is TextPattern pattern
                ? pattern
                : null,
            null);

    private static ValuePattern? TryGetValuePattern(AutomationElement element) =>
        SafeCall<ValuePattern?>(() =>
            element.TryGetCurrentPattern(ValuePattern.Pattern, out var obj) && obj is ValuePattern pattern
                ? pattern
                : null,
            null);

    private static string FormatControlType(ControlType? controlType)
    {
        var name = controlType?.ProgrammaticName;
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        const string prefix = "ControlType.";
        return name.StartsWith(prefix, StringComparison.Ordinal)
            ? name[prefix.Length..]
            : name;
    }

    private static string FormatPatternName(AutomationPattern pattern)
    {
        var name = pattern.ProgrammaticName;
        if (!string.IsNullOrEmpty(name))
        {
            const string suffix = "Identifiers.Pattern";
            return name.EndsWith(suffix, StringComparison.Ordinal)
                ? name[..^suffix.Length]
                : name;
        }

        var shortName = Automation.PatternName(pattern);
        if (string.IsNullOrEmpty(shortName))
        {
            return $"Pattern({pattern.Id})";
        }

        return shortName.EndsWith("Pattern", StringComparison.Ordinal)
            ? shortName
            : shortName + "Pattern";
    }

    internal static string SanitizePreview(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(Math.Min(text.Length, MaxPreviewLength));
        var previousWasSpace = false;

        foreach (var ch in text)
        {
            if (sb.Length >= MaxPreviewLength)
            {
                break;
            }

            if (ch == '\r' || ch == '\n')
            {
                if (!previousWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    previousWasSpace = true;
                }
                continue;
            }

            if (char.IsControl(ch))
            {
                if (!previousWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    previousWasSpace = true;
                }
                continue;
            }

            sb.Append(ch);
            previousWasSpace = ch == ' ';
        }

        return sb.ToString();
    }

    private static T SafeCall<T>(Func<T> action, T fallback)
    {
        try
        {
            return action();
        }
        catch (ElementNotAvailableException)
        {
            return fallback;
        }
        catch (COMException)
        {
            return fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
        catch (NotSupportedException)
        {
            return fallback;
        }
        catch (ArgumentException)
        {
            return fallback;
        }
    }
}
