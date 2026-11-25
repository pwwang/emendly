using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Emendly.App;

public sealed class HotkeyWindow : IDisposable
{
    public const string WindowTitle = "Emendly.HotkeyWindow";

    private const int HotkeyId = 0x454D;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly uint ActivateMessage = RegisterWindowMessage("Emendly.App.Activate");

    private readonly HwndSource _source;
    private bool _registered;
    private bool _disposed;

    public event EventHandler? HotkeyPressed;
    public event EventHandler? ActivateRequested;
    public event EventHandler? OpenSettingsRequested;

    public string? RegisteredHotkey { get; private set; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(
        IntPtr hwndParent,
        IntPtr hwndChildAfter,
        string? lpszClass,
        string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public HotkeyWindow()
    {
        var parameters = new HwndSourceParameters(WindowTitle)
        {
            ParentWindow = HwndMessage,
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public static bool NotifyExistingInstance(bool openSettings = false)
    {
        if (ActivateMessage == 0)
        {
            return false;
        }

        var hwnd = FindWindowEx(HwndMessage, IntPtr.Zero, null, WindowTitle);
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        return PostMessage(hwnd, ActivateMessage, openSettings ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
    }

    public bool Register(string hotkeySpec, out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!TryParseHotkey(hotkeySpec, out var modifiers, out var virtualKey, out var normalized))
        {
            error = $"Invalid hotkey '{hotkeySpec}'. Use a key with a modifier, or a function key such as F6.";
            return false;
        }

        Unregister();

        if (!RegisterHotKey(_source.Handle, HotkeyId, modifiers | ModNoRepeat, virtualKey))
        {
            var win32Error = Marshal.GetLastWin32Error();
            error = $"Could not register hotkey {normalized} (Win32 error {win32Error}). It may already be in use.";
            return false;
        }

        _registered = true;
        RegisteredHotkey = normalized;
        error = null;
        return true;
    }

    public void Unregister()
    {
        if (_registered && !_disposed)
        {
            _ = UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
            RegisteredHotkey = null;
        }
    }

    public static bool TryParseHotkey(
        string spec,
        out uint modifiers,
        out uint virtualKey,
        out string normalized)
    {
        modifiers = 0;
        virtualKey = 0;
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        var tokens = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        var parts = new List<string>(tokens.Length);
        Key? key = null;

        foreach (var rawToken in tokens)
        {
            var token = rawToken.Trim();
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModControl;
                if (!parts.Contains("Ctrl"))
                {
                    parts.Add("Ctrl");
                }
            }
            else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModAlt;
                if (!parts.Contains("Alt"))
                {
                    parts.Add("Alt");
                }
            }
            else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModShift;
                if (!parts.Contains("Shift"))
                {
                    parts.Add("Shift");
                }
            }
            else if (token.Equals("Win", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModWin;
                if (!parts.Contains("Win"))
                {
                    parts.Add("Win");
                }
            }
            else
            {
                if (key is not null)
                {
                    return false;
                }

                if (!TryParseKey(token, out var parsedKey))
                {
                    return false;
                }

                key = parsedKey;
            }
        }

        if (key is null || (modifiers == 0 && !IsFunctionKey(key.Value)))
        {
            return false;
        }

        var vk = KeyInterop.VirtualKeyFromKey(key.Value);
        if (vk <= 0)
        {
            return false;
        }

        virtualKey = (uint)vk;
        parts.Add(FormatKeyName(key.Value));
        normalized = string.Join("+", parts);
        return true;
    }

    private static bool TryParseKey(string token, out Key key)
    {
        if (token.Length == 1)
        {
            var ch = char.ToUpperInvariant(token[0]);
            if (ch is >= 'A' and <= 'Z')
            {
                key = Key.A + (ch - 'A');
                return true;
            }

            if (ch is >= '0' and <= '9')
            {
                key = Key.D0 + (ch - '0');
                return true;
            }
        }

        return Enum.TryParse(token, ignoreCase: true, out key) && key != Key.None;
    }

    public static bool IsFunctionKey(Key key) => key is >= Key.F1 and <= Key.F24;

    public static string FormatKeyName(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((char)('0' + (key - Key.D0))).ToString();
        }

        return key.ToString();
    }

    public static string FormatCombination(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(5);
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModifierKeys.Windows) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(FormatKeyName(key));
        return string.Join("+", parts);
    }

    public bool TryReplace(string hotkeySpec, out string applied, out string? error)
    {
        var previous = RegisteredHotkey;
        if (Register(hotkeySpec, out error))
        {
            applied = RegisteredHotkey ?? hotkeySpec;
            return true;
        }

        applied = previous ?? string.Empty;
        if (!string.IsNullOrEmpty(previous))
        {
            _ = Register(previous, out _);
        }

        return false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }
        else if (ActivateMessage != 0 && (uint)msg == ActivateMessage)
        {
            handled = true;
            if (wParam != IntPtr.Zero)
            {
                OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                ActivateRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Unregister();
        _disposed = true;
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
