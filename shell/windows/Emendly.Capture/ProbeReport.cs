using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emendly.Capture;

public sealed record ElementInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("control_type")] string ControlType,
    [property: JsonPropertyName("class_name")] string ClassName,
    [property: JsonPropertyName("automation_id")] string AutomationId,
    [property: JsonPropertyName("is_keyboard_focusable")] bool IsKeyboardFocusable);

public sealed record SelectionInfo(
    [property: JsonPropertyName("length")] int Length,
    [property: JsonPropertyName("preview")] string Preview);

public sealed record CaretInfo(
    [property: JsonPropertyName("supported")] bool Supported,
    [property: JsonPropertyName("offset")] int? Offset);

public sealed record ProbeReport(
    [property: JsonPropertyName("hwnd")] string Hwnd,
    [property: JsonPropertyName("process")] string Process,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("window_class")] string WindowClass,
    [property: JsonPropertyName("element")] ElementInfo? Element,
    [property: JsonPropertyName("patterns")] IReadOnlyList<string> Patterns,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("read_only")] bool ReadOnly,
    [property: JsonPropertyName("selection")] SelectionInfo Selection,
    [property: JsonPropertyName("caret")] CaretInfo Caret,
    [property: JsonPropertyName("field_text_length")] int FieldTextLength,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

public sealed record ProbeErrorReport(
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("error")] string Error);

public sealed record WindowListItem(
    [property: JsonPropertyName("hwnd")] string Hwnd,
    [property: JsonPropertyName("pid")] uint Pid,
    [property: JsonPropertyName("process")] string Process,
    [property: JsonPropertyName("window_class")] string WindowClass,
    [property: JsonPropertyName("title")] string Title);

public static class ProbeJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(ProbeReport report) =>
        JsonSerializer.Serialize(report, Options);

    public static string SerializeError(string error) =>
        JsonSerializer.Serialize(new ProbeErrorReport("unsupported", error), Options);

    public static string SerializeWindowList(IReadOnlyList<WindowInfo> windows)
    {
        var items = windows.Select(w => new WindowListItem(
            WindowEnumerator.FormatHwnd(w.Hwnd),
            w.Pid,
            w.ProcessName,
            w.ClassName,
            w.Title)).ToList();

        return JsonSerializer.Serialize(items, Options);
    }
}
