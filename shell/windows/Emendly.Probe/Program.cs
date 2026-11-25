using Emendly.Capture;

namespace Emendly.Probe;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitNoWindow = 2;
    private const int ExitError = 3;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public static int Main(string[] args)
    {
        var parseResult = ParseArgs(args);
        if (parseResult.ShowHelp)
        {
            PrintUsage();
            return ExitOk;
        }

        if (parseResult.Error is not null)
        {
            WriteError(parseResult.Error, parseResult.Json);
            return ExitError;
        }

        try
        {
            if (parseResult.List)
            {
                return RunList(parseResult.Json);
            }

            return RunProbe(parseResult);
        }
        catch (Exception ex)
        {
            WriteError(ex.Message, parseResult.Json);
            return ExitError;
        }
    }

    private static int RunList(bool json)
    {
        var windows = WindowEnumerator.ListTopLevelWindows();
        if (json)
        {
            Console.WriteLine(ProbeJson.SerializeWindowList(windows));
            return ExitOk;
        }

        foreach (var w in windows)
        {
            Console.WriteLine(
                $"{WindowEnumerator.FormatHwnd(w.Hwnd)}  {w.Pid,6}  {w.ProcessName,-20}  {w.ClassName,-28}  {w.Title}");
        }

        return ExitOk;
    }

    private static int RunProbe(CliOptions options)
    {
        WindowInfo? window;

        if (options.HwndRaw is not null)
        {
            if (!WindowEnumerator.TryParseHwnd(options.HwndRaw, out var hwnd))
            {
                WriteError($"invalid hwnd: {options.HwndRaw}", options.Json);
                return ExitError;
            }

            window = WindowEnumerator.GetWindowInfo(hwnd);
        }
        else
        {
            window = WindowEnumerator.FindByTitle(options.Title!);
        }

        if (window is null)
        {
            if (options.Json)
            {
                Console.WriteLine(ProbeJson.SerializeError("window not found"));
            }
            else
            {
                Console.Error.WriteLine("No matching window found.");
            }

            return ExitNoWindow;
        }

        var rules = AppCaptureRules.LoadDefault();
        var outcome = CaptureChain.ProbeWithTimeout(window, rules, ProbeTimeout);
        if (outcome.TimedOut)
        {
            Console.WriteLine(ProbeJson.SerializeError("timeout"));
            Console.Out.Flush();
            Environment.Exit(ExitError);
            return ExitError;
        }

        if (outcome.Report is null)
        {
            WriteError(outcome.ErrorMessage ?? "internal error", options.Json);
            return ExitError;
        }

        if (options.Json)
        {
            Console.WriteLine(ProbeJson.Serialize(outcome.Report));
        }
        else
        {
            PrintHumanSummary(outcome.Report);
        }

        return ExitOk;
    }

    private static void PrintHumanSummary(ProbeReport report)
    {
        Console.WriteLine($"Window:      {report.Hwnd} ({report.Process}, class={report.WindowClass}) \"{report.Title}\"");
        Console.WriteLine($"Verdict:     {report.Verdict}");
        Console.WriteLine($"Strategy:    {report.Strategy}");
        if (report.AmbiguousView == true)
        {
            Console.WriteLine("View:        ambiguous (multiple views, fell back to first)");
        }

        if (report.Element is not null)
        {
            Console.WriteLine(
                $"Element:     {report.Element.ControlType} name=\"{report.Element.Name}\" class=\"{report.Element.ClassName}\" id=\"{report.Element.AutomationId}\" focusable={report.Element.IsKeyboardFocusable}");
            Console.WriteLine($"Patterns:    {string.Join(", ", report.Patterns)}");
            Console.WriteLine($"State:       enabled={report.Enabled} read_only={report.ReadOnly}");
            Console.WriteLine($"Selection:   length={report.Selection.Length} preview=\"{report.Selection.Preview}\"");
            Console.WriteLine($"Caret:       supported={report.Caret.Supported} offset={(report.Caret.Offset?.ToString() ?? "n/a")}");
            Console.WriteLine($"Field chars: {report.FieldTextLength}");
        }
        else
        {
            Console.WriteLine("Element:     none (no TextPattern found)");
        }
    }

    private static void WriteError(string message, bool json)
    {
        if (json)
        {
            Console.WriteLine(ProbeJson.SerializeError(message));
        }
        else
        {
            Console.Error.WriteLine($"Error: {message}");
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  Emendly.Probe.exe --list [--json]");
        Console.WriteLine("  Emendly.Probe.exe --probe --hwnd <0xHANDLE> [--json]");
        Console.WriteLine("  Emendly.Probe.exe --probe --title <SUBSTRING> [--json]");
    }

    private static CliOptions ParseArgs(string[] args)
    {
        var list = false;
        var probe = false;
        var json = false;
        var showHelp = false;
        string? hwnd = null;
        string? title = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--list":
                    list = true;
                    break;
                case "--probe":
                    probe = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                case "--hwnd":
                    if (i + 1 >= args.Length)
                    {
                        return CliOptions.Fail("missing value for --hwnd", json);
                    }
                    hwnd = args[++i];
                    break;
                case "--title":
                    if (i + 1 >= args.Length)
                    {
                        return CliOptions.Fail("missing value for --title", json);
                    }
                    title = args[++i];
                    break;
                default:
                    return CliOptions.Fail($"unknown argument: {args[i]}", json);
            }
        }

        if (showHelp)
        {
            return new CliOptions(false, false, json, true, null, null, null);
        }

        if (list == probe)
        {
            return CliOptions.Fail("specify exactly one of --list or --probe", json);
        }

        if (probe)
        {
            if ((hwnd is null) == (title is null))
            {
                return CliOptions.Fail("--probe requires exactly one of --hwnd or --title", json);
            }
        }

        return new CliOptions(list, probe, json, false, hwnd, title, null);
    }

    private sealed record CliOptions(
        bool List,
        bool Probe,
        bool Json,
        bool ShowHelp,
        string? HwndRaw,
        string? Title,
        string? Error)
    {
        public static CliOptions Fail(string error, bool json) =>
            new(false, false, json, false, null, null, error);
    }
}
