using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Emendly.Capture;

public sealed record UndoEntry(
    IntPtr TargetHwnd,
    string ProcessName,
    string ActionName,
    string ReplacedText,
    string InsertedText,
    bool WasSelection);

public sealed class ClipboardInserter
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint MapVkToVsc = 0;
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;
    private const ushort VkZ = 0x5A;

    private const int ClipboardRetryCount = 12;
    private const int ClipboardRetryDelayMs = 15;
    private const int FocusSettleDelayMs = 60;
    private const int ForegroundPollIntervalMs = 20;
    private const int ForegroundWaitTimeoutMs = 300;
    private const int PasteConsumeDelayMs = 150;

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

    private sealed record ClipboardSnapshot(bool WasEmpty, Dictionary<string, object> Formats);

    public UndoEntry? LastUndo { get; private set; }

    public bool HasUndo => LastUndo is not null;

    public Task CopyTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Task.CompletedTask;
        }

        return SetClipboardTextAsync(text);
    }

    public async Task<bool> PasteAsync(
        IntPtr targetHwnd,
        string processName,
        string actionName,
        string textToInsert,
        string replacedText,
        bool wasSelection,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(textToInsert) || !WindowEnumerator.IsWindowAlive(targetHwnd))
        {
            return false;
        }

        var snapshot = await CaptureClipboardSnapshotAsync().ConfigureAwait(true);

        try
        {
            await SetClipboardTextAsync(textToInsert).ConfigureAwait(true);

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

            if (!SendCtrlChord(VkV))
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
                WasSelection: wasSelection);

            return true;
        }
        finally
        {
            await RestoreClipboardSnapshotAsync(snapshot).ConfigureAwait(true);
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

        if (!WindowEnumerator.RestoreForeground(entry.TargetHwnd))
        {
            return false;
        }

        await Task.Delay(FocusSettleDelayMs).ConfigureAwait(true);

        if (!SendCtrlChord(VkZ))
        {
            return false;
        }

        LastUndo = null;
        return true;
    }

    private static async Task<ClipboardSnapshot> CaptureClipboardSnapshotAsync()
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

    private static async Task SetClipboardTextAsync(string text)
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

    private static async Task RestoreClipboardSnapshotAsync(ClipboardSnapshot snapshot)
    {
        for (var attempt = 0; attempt < ClipboardRetryCount; attempt++)
        {
            try
            {
                RunOnSta(() =>
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
                });
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

    private static bool SendCtrlChord(ushort keyVk)
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
