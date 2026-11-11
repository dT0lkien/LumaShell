using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LumaShell;

internal static class Native
{
    internal const int HotkeyMessage = 0x0312;
    internal const int ClipboardMessage = 0x031D;
    internal const int IslandHotkey = 1;
    internal const int ScreenshotHotkey = 2;
    internal const int TrayHotkey = 3;
    internal const int LauncherHotkey = 4;
    internal const ushort LauncherActivationKey = 0x87; // F24

    [DllImport("user32.dll")]
    internal static extern bool AddClipboardFormatListener(nint window);

    [DllImport("user32.dll")]
    internal static extern bool RemoveClipboardFormatListener(nint window);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    internal static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int capacity);

    private delegate bool EnumerateWindow(nint window, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumerateWindow callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint window, EnumerateWindow callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint thread);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowStyle(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowStyle(nint window, int index, int value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out Rect bounds);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out Rect bounds, int length);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int GetDwmFlag(nint window, int attribute, out int value, int length);

    [DllImport("kernel32.dll")]
    private static extern nint OpenProcess(uint access, bool inherit, uint process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref int capacity);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);

    internal static bool RegisterIslandHotkey(nint window) => RegisterHotKey(window, IslandHotkey, 0x4003, 0x20);
    internal static bool RegisterScreenshotHotkey(nint window) => RegisterHotKey(window, ScreenshotHotkey, 0x4003, 0x53);
    internal static bool RegisterTrayHotkey(nint window) => RegisterHotKey(window, TrayHotkey, 0x4003, 0x42);
    internal static bool RegisterLauncherHotkey(nint window) => RegisterHotKey(window, LauncherHotkey, 0x4000, LauncherActivationKey);

    internal static System.Drawing.Rectangle? PrimaryWorkArea()
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(default, 1), ref info)) return null;
        return System.Drawing.Rectangle.FromLTRB(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
    }

    internal static void MoveWindowPixels(nint window, System.Drawing.Rectangle bounds) =>
        SetWindowPos(window, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x14);

    internal static void ExcludeFromTaskSwitching(nint window)
    {
        const int extendedStyle = -20, toolWindow = 0x80, appWindow = 0x40000;
        SetWindowStyle(window, extendedStyle, (GetWindowStyle(window, extendedStyle) | toolWindow) & ~appWindow);
        SetWindowPos(window, 0, 0, 0, 0, 0, 0x37); // FRAMECHANGED, keep size, position, Z order and activation.
    }

    internal static bool FocusWindow(nint window)
    {
        if (IsIconic(window)) ShowWindowAsync(window, 9);
        return SetForegroundWindow(window);
    }

    internal static string? Executable(nint window)
    {
        if (GetWindowThreadProcessId(window, out uint id) == 0) return null;
        return ProcessImage(id);
    }

    internal static uint WindowProcessId(nint window) => window != 0 && GetWindowThreadProcessId(window, out uint pid) != 0 ? pid : 0;

    internal static ushort InputLanguage(nint window)
    {
        uint thread = window == 0 ? 0 : GetWindowThreadProcessId(window, out _);
        // A zero thread ID would return Luma's own layout instead of the foreground layout.
        return thread == 0 ? (ushort)0 : unchecked((ushort)GetKeyboardLayout(thread));
    }

    internal static string? ProcessImage(uint id)
    {
        nint process = OpenProcess(0x1000, false, id);
        if (process == 0) return null;
        try
        {
            var path = new StringBuilder(1024);
            int size = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    internal static bool RestoreWindow(nint window) => ShowWindowAsync(window, 4);

    internal static bool ShowApplicationWindow(nint window) => ShowWindowAsync(window, IsIconic(window) ? 9 : 5);
    internal static bool IsApplicationWindowShown(nint window) => IsWindowVisible(window) && !IsIconic(window);

    internal static nint FindTitledWindow(string executable, string className, string title)
    {
        nint match = 0;
        var text = new StringBuilder(128);
        EnumWindows((window, _) =>
        {
            text.Clear();
            if (GetClassName(window, text, text.Capacity) == 0 ||
                !text.ToString().Equals(className, StringComparison.Ordinal) || GetWindow(window, 4) != 0) return true;
            text.Clear();
            if (GetWindowText(window, text, text.Capacity) == 0 ||
                !text.ToString().Equals(title, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Executable(window), executable, StringComparison.OrdinalIgnoreCase)) return true;
            if (match != 0)
            {
                match = 0;
                return false;
            }
            match = window;
            return true;
        }, 0);
        return match;
    }

    internal static void Shortcut(byte modifier, byte key)
    {
        keybd_event(modifier, 0, 1, 0);
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, 2, 0);
        keybd_event(modifier, 0, 3, 0);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, nuint extraInfo);

    internal static bool ShortcutModifiersDown() => GetAsyncKeyState(0x11) < 0 || GetAsyncKeyState(0x12) < 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    internal static List<OpenWindow> OpenWindows()
    {
        var windows = new List<OpenWindow>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsDesktop(window) || BelongsToCurrentProcess(window)) return true;
            int style = GetWindowStyle(window, -20);
            if ((style & 0x40000) == 0 && ((style & 0x80) != 0 || GetWindow(window, 4) != 0)) return true;
            if (GetDwmFlag(window, 14, out int cloaked, 4) == 0 && cloaked != 0) return true;
            var title = new StringBuilder(256);
            if (GetWindowText(window, title, title.Capacity) == 0) return true;
            var path = WindowApp(window);
            if (path is not null) windows.Add(new(window, path, title.ToString()));
            return windows.Count < 60;
        }, 0);
        return windows;
    }

    internal sealed record OpenWindow(nint Handle, string Path, string Title);

    internal static string? WindowApp(nint window)
    {
        string? path = Executable(window);
        if (path is null) return null;
        string name = Path.GetFileName(path).ToLowerInvariant();
        if (name is "searchhost.exe" or "searchapp.exe" or "startmenuexperiencehost.exe" or "shellexperiencehost.exe" or
            "textinputhost.exe" or "shellhost.exe" or "lockapp.exe") return null;
        if (name == "applicationframehost.exe")
        {
            string? app = null;
            EnumChildWindows(window, (child, _) =>
            {
                if (Executable(child) is not { } target || target.Equals(path, StringComparison.OrdinalIgnoreCase)) return true;
                app = target;
                return false;
            }, 0);
            return app;
        }
        if (name != "steamwebhelper.exe") return path;
        var folder = Path.GetDirectoryName(path);
        for (int level = 0; folder is not null && level < 7; level++, folder = Path.GetDirectoryName(folder))
        {
            string steam = Path.Combine(folder, "steam.exe");
            if (File.Exists(steam)) return steam;
        }
        return null;
    }

    internal static bool BelongsToCurrentProcess(nint window) => window != 0 &&
        GetWindowThreadProcessId(window, out uint process) != 0 && process == Environment.ProcessId;

    internal static bool IsDesktop(nint window)
    {
        if (window == 0) return true;
        var name = new StringBuilder(96);
        GetClassName(window, name, name.Capacity);
        return name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    internal static bool IsFileExplorerWindow(nint window)
    {
        var name = new StringBuilder(32);
        return GetClassName(window, name, name.Capacity) != 0 &&
            name.ToString() is "CabinetWClass" or "ExploreWClass";
    }

    internal static bool CoversPrimaryMonitor(nint window)
    {
        if (window == 0) return false;
        const int caption = 0x00C00000;
        if (IsZoomed(window) && (GetWindowStyle(window, -16) & caption) == caption) return false;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(default, 1), ref info)) return false;
        if (DwmGetWindowAttribute(window, 9, out var bounds, Marshal.SizeOf<Rect>()) != 0 &&
            !GetWindowRect(window, out bounds)) return false;

        return bounds.Left <= info.Monitor.Left && bounds.Top <= info.Monitor.Top &&
               bounds.Right >= info.Monitor.Right && bounds.Bottom >= info.Monitor.Bottom;
    }

    internal static string? BatteryText()
    {
        if (!GetSystemPowerStatus(out var power) || (power.BatteryFlag & 128) != 0 || power.Percent > 100)
            return null;
        return power.ACLineStatus == 1 ? $"ϟ {power.Percent}%" : $"{power.Percent}%";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte ACLineStatus, BatteryFlag, Percent, SystemStatus;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
}
