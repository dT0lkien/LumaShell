using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace LumaShell;

internal sealed class ShellShortcuts : IDisposable
{
    internal const nuint ReplayMarker = 0x4C554D41;
    private delegate nint Callback(int code, nint message, nint data);
    private readonly Dispatcher ui;
    private readonly Dispatcher pump;
    private readonly Action capture;
    private readonly Action openLauncher;
    private readonly Action openFinder;
    private readonly WinTap tap = new();
    private readonly Callback callback;
    private Input heldWin;
    private nint hook;
    private bool suppressS;
    internal bool Snip { get; private set; }
    internal bool Launcher { get; private set; }
    internal bool Finder { get; private set; }
    internal volatile bool LauncherAvailable;
    internal volatile bool LauncherHotkeyRegistered;
    internal volatile bool FinderAvailable;
    internal volatile bool FinderHotkeyRegistered;

    internal ShellShortcuts(Dispatcher ui, Action capture, Action openLauncher, Action openFinder)
    {
        this.ui = ui;
        this.capture = capture;
        this.openLauncher = openLauncher;
        this.openFinder = openFinder;
        callback = OnKey;
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Luma keyboard hook" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        pump = ready.Task.GetAwaiter().GetResult();
    }

    internal bool Configure(bool snip, bool launcher, bool finder) => pump.Invoke(() =>
    {
        if ((snip || launcher || finder) && hook == 0)
        {
            hook = SetWindowsHookExW(13, callback, GetModuleHandleW(null), 0);
            if (hook == 0) return false;
        }
        Snip = snip;
        Launcher = launcher;
        Finder = finder;
        return true;
    });

    private nint OnKey(int code, nint message, nint data)
    {
        if (code != 0 || unchecked((nuint)Marshal.ReadIntPtr(data, 16)) == ReplayMarker)
            return Next(code, message, data);
        int key = Marshal.ReadInt32(data);
        bool down = message is 0x100 or 0x104;
        bool up = message is 0x101 or 0x105;
        if (!down && !up) return Next(code, message, data);
        bool win = key is 0x5B or 0x5C;
        bool modified = win && down && (Down(0x10) || Down(0x11) || Down(0x12) || Down(key == 0x5B ? 0x5C : 0x5B));
        bool intercept = Launcher && LauncherAvailable;
        bool snipChord = key == 0x53 && Snip && Down(0x10) && !Down(0x11) && !Down(0x12);
        switch (tap.Handle(key, down, intercept, modified, Finder && FinderAvailable, snipChord))
        {
            case WinTap.Action.Hold:
                heldWin = Keyboard(data, key, false);
                return 1;
            case WinTap.Action.Launch:
                // A real WM_HOTKEY gives the UI permission to activate from another app.
                if (!LauncherHotkeyRegistered || !SendActivationHotkey(Native.LauncherActivationKey))
                    ui.BeginInvoke(DispatcherPriority.Input, openLauncher);
                return 1;
            case WinTap.Action.OpenFolder:
                if (!FinderHotkeyRegistered || !SendActivationHotkey(Native.FinderActivationKey))
                    ui.BeginInvoke(DispatcherPriority.Input, openFinder);
                return 1;
            case WinTap.Action.Capture:
                ui.BeginInvoke(DispatcherPriority.Input, capture);
                return 1;
            case WinTap.Action.Suppress:
                return 1;
            case WinTap.Action.ReplayTap:
                SendInput(2, [heldWin, Keyboard(data, key, true)], Marshal.SizeOf<Input>());
                return 1;
            case WinTap.Action.ReplayChord:
                // The original Win-down is still held back. Queue both presses in order.
                if (SendInput(2, [heldWin, Keyboard(data, key, false)], Marshal.SizeOf<Input>()) == 2) return 1;
                break;
        }

        if (key == 0x53)
        {
            if (up && suppressS) { suppressS = false; return 1; }
            if (down && (suppressS || snipChord && (Down(0x5B) || Down(0x5C))))
            {
                if (!suppressS) ui.BeginInvoke(DispatcherPriority.Normal, capture);
                suppressS = true;
                return 1;
            }
        }
        return Next(code, message, data);
    }

    private nint Next(int code, nint message, nint data) => CallNextHookEx(hook, code, message, data);

    internal static bool SendTaskManagerShortcut()
    {
        const ushort control = 0x11, shift = 0x10, escape = 0x1B;
        if (Down(control) || Down(shift) || Down(escape) || Down(0x12) || Down(0x5B) || Down(0x5C)) return false;
        var inputs = new[]
        {
            ShortcutKey(control), ShortcutKey(shift), ShortcutKey(escape),
            ShortcutKey(escape, up: true), ShortcutKey(shift, up: true), ShortcutKey(control, up: true)
        };
        int size = Marshal.SizeOf<Input>();
        uint sent = SendInput((uint)inputs.Length, inputs, size);
        if (sent is > 0 and < 6) SendInput(3, inputs[3..], size);
        return sent == inputs.Length;
    }

    private static Input ShortcutKey(ushort key, bool up = false) => new()
    {
        Type = 1,
        Data = new() { Key = new() { Key = key, Flags = up ? 2u : 0u, Extra = ReplayMarker } }
    };

    private static bool SendActivationHotkey(ushort key)
    {
        var down = ShortcutKey(key);
        var up = ShortcutKey(key, up: true);
        int size = Marshal.SizeOf<Input>();
        uint sent = SendInput(2, [down, up], size);
        if (sent == 1) SendInput(1, [up], size);
        return sent != 0;
    }

    private static Input Keyboard(nint data, int key, bool up) => new()
    {
        Type = 1,
        Data = new()
        {
            Key = new()
            {
                Key = (ushort)key,
                Scan = (ushort)Marshal.ReadInt32(data, 4),
                Flags = ((uint)Marshal.ReadInt32(data, 8) & 1) | (up ? 2u : 0u),
                Extra = ReplayMarker
            }
        }
    };

    private static bool Down(int key) => GetAsyncKeyState(key) < 0;

    public void Dispose() => pump.Invoke(() =>
    {
        if (hook != 0) UnhookWindowsHookEx(hook);
        hook = 0;
        pump.BeginInvokeShutdown(DispatcherPriority.Normal);
    });

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public KeyInput Key;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")]
    private static extern nint SetWindowsHookExW(int type, Callback callback, nint module, uint thread);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetModuleHandleW(string? module);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
