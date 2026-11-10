using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace LumaShell;

internal sealed class TopBar(Window window) : IDisposable
{
    private const int Callback = 0x8032;
    private nint handle;
    private uint taskbarCreated;
    private bool enabled;
    private Rectangle lastBounds;
    internal bool Registered { get; private set; }

    internal void Enable(bool value)
    {
        if (enabled == value) return;
        enabled = value;
        if (value)
        {
            if (handle == 0)
            {
                handle = new WindowInteropHelper(window).EnsureHandle();
                taskbarCreated = RegisterWindowMessage("TaskbarCreated");
                HwndSource.FromHwnd(handle).AddHook(Message);
            }
            Register();
        }
        else Remove();
    }

    private void Register()
    {
        if (!enabled) return;
        var data = Data();
        Registered = SHAppBarMessage(0, ref data) != 0;
        lastBounds = Rectangle.Empty;
        Place();
    }

    internal void Place()
    {
        if (!Registered || Forms.Screen.PrimaryScreen is not { } screen) return;
        var monitor = screen.Bounds;
        var dpi = VisualTreeHelper.GetDpi(window);
        int height = (int)Math.Ceiling(Desktop.BarHeight * dpi.DpiScaleY);
        var data = Data();
        data.Bounds = new RectPixels(monitor.Left, monitor.Top, monitor.Right, monitor.Top + height);
        SHAppBarMessage(2, ref data);
        data.Bounds.Bottom = data.Bounds.Top + height;
        if (data.Bounds.ToRectangle() != lastBounds)
        {
            SHAppBarMessage(3, ref data);
            lastBounds = data.Bounds.ToRectangle();
        }
        if (lastBounds.Width > 0)
        {
            window.Width = lastBounds.Width / dpi.DpiScaleX;
            window.Left = lastBounds.Left / dpi.DpiScaleX;
            window.Top = lastBounds.Top / dpi.DpiScaleY;
            Native.MoveWindowPixels(handle, lastBounds);
        }
    }

    private nint Message(nint windowHandle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == Callback && wParam == 1 || message == 0x02E0)
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Place);
        if (message == taskbarCreated && enabled)
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Register);
        return 0;
    }

    private void Remove()
    {
        if (!Registered) return;
        var data = Data();
        SHAppBarMessage(1, ref data);
        Registered = false;
    }

    private AppBarData Data() => new() { Size = (uint)Marshal.SizeOf<AppBarData>(), Window = handle, Message = Callback, Edge = 1 };

    public void Dispose() => Enable(false);

    [DllImport("shell32.dll")]
    private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData { public uint Size; public nint Window; public uint Message, Edge; public RectPixels Bounds; public nint Parameter; }

    [StructLayout(LayoutKind.Sequential)]
    private record struct RectPixels(int Left, int Top, int Right, int Bottom)
    {
        internal readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }
}
