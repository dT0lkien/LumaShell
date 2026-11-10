using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace LumaShell;

internal sealed class ShellShortcuts : IDisposable
{
    private delegate nint Callback(int code, nint message, nint data);
    private readonly Dispatcher ui;
    private readonly Dispatcher pump;
    private readonly Action capture;
    private readonly Action openLauncher;
    private readonly WinTap tap = new();
    private readonly Callback callback;
    private Input heldWin;
    private nint hook;
    private bool suppressS;
    internal bool Snip { get; private set; }
    internal bool Launcher { get; private set; }
    internal volatile bool LauncherAvailable;
    internal volatile bool LauncherHotkeyRegistered;

    internal ShellShortcuts(Dispatcher ui, Action capture, Action openLauncher)
    {
        this.ui = ui;
        this.capture = capture;
        this.openLauncher = openLauncher;
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

    internal bool Configure(bool snip, bool launcher) => pump.Invoke(() =>
    {
        if ((snip || launcher) && hook == 0)
        {
            hook = SetWindowsHookExW(13, callback, GetModuleHandleW(null), 0);
            if (hook == 0) return false;
        }
        Snip = snip;
        Launcher = launcher;
        return true;
    });

    private nint OnKey(int code, nint message, nint data)
    {
        if (code != 0 || (Marshal.ReadInt32(data, 8) & 0x10) != 0) return Next(code, message, data);
        int key = Marshal.ReadInt32(data);
        bool down = message is 0x100 or 0x104;
        bool up = message is 0x101 or 0x105;
        if (!down && !up) return Next(code, message, data);
        bool win = key is 0x5B or 0x5C;
        bool modified = win && down && (Down(0x10) || Down(0x11) || Down(0x12) || Down(key == 0x5B ? 0x5C : 0x5B));
        bool intercept = Launcher && LauncherAvailable;
        switch (tap.Handle(key, down, intercept, modified))
        {
            case WinTap.Action.Hold:
                heldWin = Keyboard(data, key, false);
                return 1;
            case WinTap.Action.Launch:
                // A real WM_HOTKEY gives the UI permission to activate from another app.
                if (!LauncherHotkeyRegistered || !SendLauncherHotkey())
                    ui.BeginInvoke(DispatcherPriority.Input, openLauncher);
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
            if (down && (suppressS || Snip && (Down(0x5B) || Down(0x5C)) && Down(0x10) && !Down(0x11) && !Down(0x12)))
            {
                if (!suppressS) ui.BeginInvoke(DispatcherPriority.Normal, capture);
                suppressS = true;
                return 1;
            }
        }
        return Next(code, message, data);
    }

    private nint Next(int code, nint message, nint data) => CallNextHookEx(hook, code, message, data);

    private static bool SendLauncherHotkey()
    {
        var down = new Input { Type = 1, Data = new() { Key = new() { Key = Native.LauncherActivationKey } } };
        var up = down;
        up.Data.Key.Flags = 2;
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
                Flags = ((uint)Marshal.ReadInt32(data, 8) & 1) | (up ? 2u : 0u)
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
