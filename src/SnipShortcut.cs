using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace LumaShell;

internal sealed class SnipShortcut(Dispatcher dispatcher, Action capture) : IDisposable
{
    private delegate nint Callback(int code, nint message, nint data);
    private Callback? callback;
    private nint hook;
    private bool suppressS;
    internal bool Enabled => hook != 0;

    internal bool Enable()
    {
        if (Enabled) return true;
        callback = OnKey;
        hook = SetWindowsHookExW(13, callback, GetModuleHandleW(null), 0);
        if (!Enabled) callback = null;
        return Enabled;
    }

    private nint OnKey(int code, nint message, nint data)
    {
        if (code == 0 && Marshal.ReadInt32(data) == 0x53 && (Marshal.ReadInt32(data, 8) & 0x10) == 0)
        {
            if (message is 0x101 or 0x105)
            {
                if (suppressS) { suppressS = false; return 1; }
            }
            else if (message is 0x100 or 0x104 && (suppressS || ((Down(0x5B) || Down(0x5C)) &&
                Down(0x10) && !Down(0x11) && !Down(0x12))))
            {
                if (!suppressS) dispatcher.BeginInvoke(DispatcherPriority.Normal, capture);
                suppressS = true;
                return 1;
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    private static bool Down(int key) => GetAsyncKeyState(key) < 0;

    public void Dispose()
    {
        if (!Enabled) return;
        if (!UnhookWindowsHookEx(hook)) return;
        hook = 0;
        callback = null;
        suppressS = false;
    }

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
