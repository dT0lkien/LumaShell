using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace LumaShell;

internal sealed class ShellShortcuts(Dispatcher dispatcher, Action capture, Action openLauncher) : IDisposable
{
    private delegate nint Callback(int code, nint message, nint data);
    private Callback? callback;
    private nint hook;
    private bool suppressS;
    private readonly WinTap tap = new();
    internal bool Snip { get; private set; }
    internal bool Launcher { get; private set; }
    internal bool LauncherAvailable { get; set; }

    internal bool Configure(bool snip, bool launcher)
    {
        if ((snip || launcher) && hook == 0)
        {
            callback = OnKey;
            hook = SetWindowsHookExW(13, callback, GetModuleHandleW(null), 0);
            if (hook == 0) { callback = null; return false; }
        }
        if (!launcher) tap.Reset();
        Snip = snip;
        Launcher = launcher;
        return true;
    }

    private nint OnKey(int code, nint message, nint data)
    {
        if (code != 0 || (Marshal.ReadInt32(data, 8) & 0x10) != 0) return CallNextHookEx(hook, code, message, data);
        int key = Marshal.ReadInt32(data);
        bool down = message is 0x100 or 0x104;
        bool up = message is 0x101 or 0x105;
        if (!down && !up) return CallNextHookEx(hook, code, message, data);
        bool isWin = key is 0x5B or 0x5C;
        bool modified = isWin && down && (Down(0x10) || Down(0x11) || Down(0x12) || Down(key == 0x5B ? 0x5C : 0x5B));
        if (Launcher && tap.Handle(key, down, modified, isWin && down && Down(key)) && LauncherAvailable && ReleaseWithoutStart(key))
        {
            dispatcher.BeginInvoke(DispatcherPriority.Normal, openLauncher);
            return 1;
        }
        if (key == 0x53)
        {
            if (up && suppressS) { suppressS = false; return 1; }
            if (down && (suppressS || Snip && (Down(0x5B) || Down(0x5C)) && Down(0x10) && !Down(0x11) && !Down(0x12)))
            {
                if (!suppressS) dispatcher.BeginInvoke(DispatcherPriority.Normal, capture);
                suppressS = true;
                return 1;
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    // Release Win after an inert key pair so Explorer sees a chord instead of opening Start.
    private static bool ReleaseWithoutStart(int key)
    {
        Input[] inputs = [Keyboard(0xFF, 0), Keyboard(0xFF, 2), Keyboard((ushort)key, 3)];
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    private static Input Keyboard(ushort key, uint flags) => new() { Type = 1, Data = new() { Key = new() { Key = key, Flags = flags } } };
    private static bool Down(int key) => GetAsyncKeyState(key) < 0;

    public void Dispose()
    {
        if (hook == 0 || !UnhookWindowsHookEx(hook)) return;
        hook = 0;
        callback = null;
        suppressS = false;
        tap.Reset();
    }

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
