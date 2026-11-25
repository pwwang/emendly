using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Emendly.Capture;

internal sealed record ClipboardSnapshot(bool WasEmpty, Dictionary<string, object> Formats);

internal static class ClipboardHelper
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint MapVkToVsc = 0;

    internal const ushort VkControl = 0x11;
    internal const ushort VkC = 0x43;
    internal const ushort VkV = 0x56;
    internal const ushort VkZ = 0x5A;

    private const int ClipboardRetryCount = 12;
    private const int ClipboardRetryDelayMs = 15;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint Msg;
        public ushort ParamL;
        public ushort ParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    public static uint GetSequenceNumber() => GetClipboardSequenceNumber();

    public static ClipboardSnapshot CaptureSnapshot()
    {
        for (var attempt = 0; attempt < ClipboardRetryCount; attempt++)
        {
            try
            {
                return RunOnSta(ReadClipboardSnapshot);
            }
            catch (COMException) when (attempt + 1 < ClipboardRetryCount)
            {
                Thread.Sleep(ClipboardRetryDelayMs);
            }
        }

        return new ClipboardSnapshot(WasEmpty: true, Formats: new Dictionary<string, object>());
    }

    public static async Task<ClipboardSnapshot> CaptureSnapshotAsync()
    {
        for (var attempt = 0; attempt < ClipboardRetryCount; attempt++)
        {
            try
            {
                return RunOnSta(ReadClipboardSnapshot);
            }
            catch (COMException) when (attempt + 1 < ClipboardRetryCount)
            {
                await Task.Delay(ClipboardRetryDelayMs).ConfigureAwait(true);
            }
        }

        return new ClipboardSnapshot(WasEmpty: true, Formats: new Dictionary<string, object>());
    }

    public static string ReadText()
    {
        for (var attempt = 0; attempt < ClipboardRetryCount; attempt++)
        {
            try
            {
                return RunOnSta(() =>
                {
                    if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
                    {
                        return Clipboard.GetText(TextDataFormat.UnicodeText) ?? string.Empty;
                    }

                    if (Clipboard.ContainsText())
                    {
                        return Clipboard.GetText() ?? string.Empty;
                    }

                    return string.Empty;
                });
            }
            catch (COMException) when (attempt + 1 < ClipboardRetryCount)
            {
                Thread.Sleep(ClipboardRetryDelayMs);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        return string.Empty;
    }

    public static async Task SetTextAsync(string text)
    {
        for (var attempt = 0; attempt < ClipboardRetryCount; attempt++)
        {
            try
            {
                RunOnSta(() =>
                {
                    Clipboard.SetText(text, TextDataFormat.UnicodeText);
                    return true;
                });
                return;
            }
            catch (COMException) when (attempt + 1 < ClipboardRetryCount)
            {
                await Task.Delay(ClipboardRetryDelayMs).ConfigureAwait(true);
            }
        }

        throw new InvalidOperationException("Failed to write clipboard.");
    }

    public static void RestoreSnapshot(ClipboardSnapshot snapshot)
    {
        for (var attempt = 0; attempt < ClipboardRetryCount; attempt++)
        {
            try
            {
                RunOnSta(() => ApplySnapshot(snapshot));
                return;
            }
            catch (COMException) when (attempt + 1 < ClipboardRetryCount)
            {
                Thread.Sleep(ClipboardRetryDelayMs);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    public static async Task RestoreSnapshotAsync(ClipboardSnapshot snapshot)
    {
        for (var attempt = 0; attempt < ClipboardRetryCount; attempt++)
        {
            try
            {
                RunOnSta(() => ApplySnapshot(snapshot));
                return;
            }
            catch (COMException) when (attempt + 1 < ClipboardRetryCount)
            {
                await Task.Delay(ClipboardRetryDelayMs).ConfigureAwait(true);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    public static bool SendCtrlChord(ushort keyVk)
    {
        var ctrlScan = (ushort)MapVirtualKey(VkControl, MapVkToVsc);
        var keyScan = (ushort)MapVirtualKey(keyVk, MapVkToVsc);

        var inputs = new[]
        {
            KeyInput(VkControl, ctrlScan, 0),
            KeyInput(keyVk, keyScan, 0),
            KeyInput(keyVk, keyScan, KeyEventKeyUp),
            KeyInput(VkControl, ctrlScan, KeyEventKeyUp),
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return sent == inputs.Length;
    }

    private static ClipboardSnapshot ReadClipboardSnapshot()
    {
        var dataObject = Clipboard.GetDataObject();
        if (dataObject is null)
        {
            return new ClipboardSnapshot(WasEmpty: true, Formats: new Dictionary<string, object>());
        }

        var formatNames = dataObject.GetFormats(autoConvert: false);
        if (formatNames is null || formatNames.Length == 0)
        {
            return new ClipboardSnapshot(WasEmpty: true, Formats: new Dictionary<string, object>());
        }

        var saved = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var format in formatNames)
        {
            try
            {
                var data = dataObject.GetData(format, autoConvert: false);
                if (data is null)
                {
                    continue;
                }

                // Copy streams eagerly so the COM-backed clipboard buffer can be replaced safely.
                if (data is MemoryStream ms)
                {
                    saved[format] = ms.ToArray();
                }
                else
                {
                    saved[format] = data;
                }
            }
            catch (Exception)
            {
                // Skip delayed-render or non-serializable formats rather than aborting capture.
            }
        }

        if (saved.Count == 0 && Clipboard.ContainsText())
        {
            saved[DataFormats.UnicodeText] = Clipboard.GetText(TextDataFormat.UnicodeText);
        }

        return new ClipboardSnapshot(WasEmpty: saved.Count == 0, Formats: saved);
    }

    private static bool ApplySnapshot(ClipboardSnapshot snapshot)
    {
        if (snapshot.WasEmpty || snapshot.Formats.Count == 0)
        {
            Clipboard.Clear();
            return true;
        }

        var restored = new DataObject();
        foreach (var (format, value) in snapshot.Formats)
        {
            try
            {
                if (value is byte[] bytes)
                {
                    restored.SetData(format, new MemoryStream(bytes, writable: false), autoConvert: false);
                }
                else
                {
                    restored.SetData(format, value, autoConvert: false);
                }
            }
            catch (Exception)
            {
                // Skip individual formats that refuse re-insertion.
            }
        }

        Clipboard.SetDataObject(restored, copy: true);
        return true;
    }

    private static INPUT KeyInput(ushort vk, ushort scanCode, uint flags) =>
        new()
        {
            Type = InputKeyboard,
            Union = new INPUTUNION
            {
                Keyboard = new KEYBDINPUT
                {
                    VirtualKey = vk,
                    ScanCode = scanCode,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            },
        };

    private static T RunOnSta<T>(Func<T> func)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return func();
        }

        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "Emendly.ClipboardSta",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw failure;
        }

        return result;
    }
}
