using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Emendly.Capture;

public sealed record WindowInfo(
    IntPtr Hwnd,
    uint Pid,
    string ProcessName,
    string ClassName,
    string Title);

public static class WindowEnumerator
{
    private const int SwRestore = 9;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    public static string FormatHwnd(IntPtr hwnd) => $"0x{hwnd.ToInt64():X8}";

    public static bool TryParseHwnd(string raw, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        long value;
        bool parsed;

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            parsed = long.TryParse(
                trimmed[2..],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out value);
        }
        else
        {
            parsed = long.TryParse(
                trimmed,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
        }

        if (!parsed || value <= 0)
        {
            return false;
        }

        hwnd = new IntPtr(value);
        return true;
    }

    public static IReadOnlyList<WindowInfo> ListTopLevelWindows()
    {
        var results = new List<WindowInfo>();

        EnumWindows((hwnd, lParam) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            var title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            var processName = GetProcessName(pid);
            var className = GetWindowClassName(hwnd);
            results.Add(new WindowInfo(hwnd, pid, processName, className, SanitizeSingleLine(title)));
            return true;
        }, IntPtr.Zero);

        return results;
    }

    public static WindowInfo? FindByTitle(string titleSubstring)
    {
        if (string.IsNullOrEmpty(titleSubstring))
        {
            return null;
        }

        var windows = ListTopLevelWindows();

        // Exact title wins, so "Untitled - Notepad" does not match "Untitled - Notepad3".
        foreach (var window in windows)
        {
            if (string.Equals(window.Title, titleSubstring, StringComparison.OrdinalIgnoreCase))
            {
                return window;
            }
        }

        foreach (var window in windows)
        {
            if (window.Title.Contains(titleSubstring, StringComparison.OrdinalIgnoreCase))
            {
                return window;
            }
        }

        return null;
    }

    public static WindowInfo? GetForegroundWindowInfo()
    {
        var hwnd = GetForegroundWindow();
        return GetWindowInfo(hwnd);
    }

    public static bool RestoreForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return false;
        }

        if (IsIconic(hwnd))
        {
            _ = ShowWindow(hwnd, SwRestore);
        }

        return SetForegroundWindow(hwnd);
    }

    public static bool IsWindowAlive(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && IsWindow(hwnd);

    public static WindowInfo? GetWindowInfo(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return null;
        }

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        var processName = GetProcessName(pid);
        var className = GetWindowClassName(hwnd);
        var title = SanitizeSingleLine(GetWindowTitle(hwnd));
        return new WindowInfo(hwnd, pid, processName, className, title);
    }

    internal static IntPtr GetFocusedChildHwnd(IntPtr targetHwnd)
    {
        var threadId = GetWindowThreadProcessId(targetHwnd, out _);
        if (threadId == 0)
        {
            return IntPtr.Zero;
        }

        var gui = new GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<GUITHREADINFO>(),
        };

        if (!GetGUIThreadInfo(threadId, ref gui) || gui.hwndFocus == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (gui.hwndFocus == targetHwnd || IsChild(targetHwnd, gui.hwndFocus))
        {
            return gui.hwndFocus;
        }

        return IntPtr.Zero;
    }

    internal static IReadOnlyList<IntPtr> EnumerateChildWindowsByClass(IntPtr parentHwnd, string className)
    {
        var matches = new List<IntPtr>();
        if (parentHwnd == IntPtr.Zero || !IsWindow(parentHwnd) || string.IsNullOrEmpty(className))
        {
            return matches;
        }

        EnumChildWindows(parentHwnd, (childHwnd, _) =>
        {
            if (string.Equals(GetWindowClassName(childHwnd), className, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(childHwnd);
            }

            return true;
        }, IntPtr.Zero);

        return matches;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        var copied = GetWindowText(hwnd, buffer, buffer.Capacity);
        return copied > 0 ? buffer.ToString() : string.Empty;
    }

    internal static string GetWindowClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        var copied = GetClassName(hwnd, buffer, buffer.Capacity);
        return copied > 0 ? buffer.ToString() : string.Empty;
    }

    private static string GetProcessName(uint pid)
    {
        if (pid == 0 || pid > int.MaxValue)
        {
            return string.Empty;
        }

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string SanitizeSingleLine(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }
}
