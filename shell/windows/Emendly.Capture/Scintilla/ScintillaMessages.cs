using System.Runtime.InteropServices;

namespace Emendly.Capture.Scintilla;

internal static class ScintillaMessages
{
    public const uint SCI_GETTEXTLENGTH = 2006;
    public const uint SCI_POSITIONFROMLINE = 2135;
    public const uint SCI_GETLINEENDPOSITION = 2136;
    public const uint SCI_GETREADONLY = 2140;
    public const uint SCI_GETSELECTIONSTART = 2143;
    public const uint SCI_GETSELECTIONEND = 2144;
    public const uint SCI_GETLINECOUNT = 2154;
    public const uint SCI_POINTXFROMPOSITION = 2164;
    public const uint SCI_POINTYFROMPOSITION = 2165;
    public const uint SCI_LINEFROMPOSITION = 2166;
    public const uint SCI_UNDO = 2176;
    public const uint SCI_PASTE = 2179;
    public const uint SCI_TEXTHEIGHT = 2279;
    public const uint SCI_COPYRANGE = 2423;

    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint DefaultTimeoutMs = 1000;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    public static bool TrySendLong(
        IntPtr hwnd,
        uint msg,
        long wParam,
        long lParam,
        out long value,
        uint timeoutMs = DefaultTimeoutMs)
    {
        value = 0;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var ok = SendMessageTimeout(
            hwnd,
            msg,
            new IntPtr(wParam),
            new IntPtr(lParam),
            SmtoBlock | SmtoAbortIfHung,
            timeoutMs,
            out var result);

        if (ok == IntPtr.Zero)
        {
            return false;
        }

        value = result.ToInt64();
        return true;
    }

    public static bool Send(
        IntPtr hwnd,
        uint msg,
        long wParam = 0,
        long lParam = 0,
        uint timeoutMs = DefaultTimeoutMs) =>
        TrySendLong(hwnd, msg, wParam, lParam, out _, timeoutMs);
}
