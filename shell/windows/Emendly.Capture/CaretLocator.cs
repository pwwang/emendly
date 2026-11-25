using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Emendly.Capture;

public readonly record struct CaretLocation(Rect Bounds, bool FromCaret);

public readonly record struct MonitorWorkArea(Rect WorkArea, double DpiScaleX, double DpiScaleY);

public static class CaretLocator
{
    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public WindowEnumerator.RECT rcMonitor;
        public WindowEnumerator.RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    public static CaretLocation Locate(TextPatternRange? range, IntPtr targetHwnd)
    {
        if (range is not null && TryGetRangeBounds(range, out var rangeRect))
        {
            return new CaretLocation(rangeRect, FromCaret: true);
        }

        if (targetHwnd != IntPtr.Zero && TryGetWin32CaretBounds(targetHwnd, out var win32Rect))
        {
            return new CaretLocation(win32Rect, FromCaret: true);
        }

        return GetMouseFallback();
    }

    public static CaretLocation GetMouseFallback()
    {
        if (GetCursorPos(out var pt))
        {
            return new CaretLocation(new Rect(pt.X, pt.Y, 1, 1), FromCaret: false);
        }

        return new CaretLocation(new Rect(200, 200, 1, 1), FromCaret: false);
    }

    internal static bool TryClientCaretToScreen(
        IntPtr hwnd,
        int clientX,
        int clientY,
        int height,
        out Rect bounds)
    {
        bounds = Rect.Empty;

        if (hwnd == IntPtr.Zero || !WindowEnumerator.GetClientRect(hwnd, out var clientRect))
        {
            return false;
        }

        if (clientX < clientRect.Left || clientX > clientRect.Right
            || clientY < clientRect.Top || clientY > clientRect.Bottom)
        {
            return false;
        }

        var pt = new POINT { X = clientX, Y = clientY };
        if (!ClientToScreen(hwnd, ref pt))
        {
            return false;
        }

        bounds = new Rect(pt.X, pt.Y, 1, Math.Max(1, height));
        return true;
    }

    public static MonitorWorkArea GetMonitorWorkArea(double physicalX, double physicalY)
    {
        var pt = new POINT
        {
            X = (int)Math.Round(physicalX),
            Y = (int)Math.Round(physicalY),
        };

        var monitor = MonitorFromPoint(pt, MonitorDefaultToNearest);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return new MonitorWorkArea(
                new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight),
                1.0,
                1.0);
        }

        var scaleX = 1.0;
        var scaleY = 1.0;
        try
        {
            if (GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out var dpiY) == 0
                && dpiX > 0
                && dpiY > 0)
            {
                scaleX = dpiX / 96.0;
                scaleY = dpiY / 96.0;
            }
        }
        catch (Exception)
        {
            scaleX = 1.0;
            scaleY = 1.0;
        }

        var work = info.rcWork;
        var workRect = new Rect(
            work.Left,
            work.Top,
            Math.Max(1, work.Right - work.Left),
            Math.Max(1, work.Bottom - work.Top));

        return new MonitorWorkArea(workRect, scaleX, scaleY);
    }

    private static bool TryGetRangeBounds(TextPatternRange range, out Rect bounds)
    {
        bounds = Rect.Empty;

        if (TryFirstValidRect(SafeGetBoundingRectangles(range), out var direct))
        {
            bounds = direct;
            return true;
        }

        // A zero-width caret range often returns no rectangles until expanded by one character.
        try
        {
            var forward = range.Clone();
            if (forward.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1) > 0
                && TryFirstValidRect(SafeGetBoundingRectangles(forward), out var forwardRect))
            {
                bounds = new Rect(forwardRect.Left, forwardRect.Top, 1, Math.Max(1, forwardRect.Height));
                return true;
            }

            var backward = range.Clone();
            if (backward.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1) < 0
                && TryFirstValidRect(SafeGetBoundingRectangles(backward), out var backwardRect))
            {
                bounds = new Rect(backwardRect.Right, backwardRect.Top, 1, Math.Max(1, backwardRect.Height));
                return true;
            }
        }
        catch (Exception)
        {
            return false;
        }

        return false;
    }

    private static Rect[] SafeGetBoundingRectangles(TextPatternRange range)
    {
        try
        {
            return range.GetBoundingRectangles() ?? Array.Empty<Rect>();
        }
        catch (Exception)
        {
            return Array.Empty<Rect>();
        }
    }

    private static bool TryFirstValidRect(Rect[] rects, out Rect valid)
    {
        foreach (var rect in rects)
        {
            if (rect.IsEmpty)
            {
                continue;
            }

            if (double.IsNaN(rect.Left) || double.IsInfinity(rect.Left)
                || double.IsNaN(rect.Top) || double.IsInfinity(rect.Top)
                || double.IsNaN(rect.Width) || double.IsInfinity(rect.Width)
                || double.IsNaN(rect.Height) || double.IsInfinity(rect.Height))
            {
                continue;
            }

            if (rect.Left == 0 && rect.Top == 0 && rect.Width == 0 && rect.Height == 0)
            {
                continue;
            }

            valid = rect;
            return true;
        }

        valid = Rect.Empty;
        return false;
    }

    private static bool TryGetWin32CaretBounds(IntPtr targetHwnd, out Rect bounds)
    {
        bounds = Rect.Empty;

        var threadId = WindowEnumerator.GetWindowThreadProcessId(targetHwnd, out _);
        if (threadId == 0)
        {
            return false;
        }

        var gui = new WindowEnumerator.GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<WindowEnumerator.GUITHREADINFO>(),
        };

        if (!WindowEnumerator.GetGUIThreadInfo(threadId, ref gui) || gui.hwndCaret == IntPtr.Zero)
        {
            return false;
        }

        var topLeft = new POINT { X = gui.rcCaret.Left, Y = gui.rcCaret.Top };
        var bottomRight = new POINT { X = gui.rcCaret.Right, Y = gui.rcCaret.Bottom };
        if (!ClientToScreen(gui.hwndCaret, ref topLeft) || !ClientToScreen(gui.hwndCaret, ref bottomRight))
        {
            return false;
        }

        var width = Math.Max(1, bottomRight.X - topLeft.X);
        var height = Math.Max(1, bottomRight.Y - topLeft.Y);
        if (topLeft.X == 0 && topLeft.Y == 0 && bottomRight.X == 0 && bottomRight.Y == 0)
        {
            return false;
        }

        bounds = new Rect(topLeft.X, topLeft.Y, width, height);
        return true;
    }
}
