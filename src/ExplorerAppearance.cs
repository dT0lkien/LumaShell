using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LumaShell;

// DWM owns the window frame. Explorer owns its tabs, sidebar and folder views.
internal sealed class ExplorerAppearance
{
    private const uint ColorDefault = 0xFFFFFFFF;
    private const int CornerPreference = 33;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int CaptionTextColor = 36;
    private const string AppThemeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static readonly string ExplorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
    private readonly Dictionary<nint, Frame> frames = [];

    internal void Refresh(IReadOnlyList<Native.OpenWindow> windows, nint foreground)
    {
        bool dark = UsesDarkAppMode();
        foreach (var (handle, frame) in frames.ToArray())
            if (Native.WindowProcessId(handle) != frame.Owner) frames.Remove(handle);

        foreach (var window in windows)
        {
            if (!window.Path.Equals(ExplorerPath, StringComparison.OrdinalIgnoreCase) ||
                !Native.IsFileExplorerWindow(window.Handle)) continue;
            uint owner = Native.WindowProcessId(window.Handle);
            if (owner == 0) continue;
            bool active = window.Handle == foreground;
            if (!frames.TryGetValue(window.Handle, out var frame))
            {
                frame = new Frame(owner);
                frames.Add(window.Handle, frame);
            }
            var appearance = (Active: active, Dark: dark);
            if (frame.Appearance == appearance) continue;
            frame.Appearance = appearance;
            Apply(window.Handle, frame, CornerPreference, 2); // DWMWCP_ROUND
            // COLORREF stores red in the low byte.
            uint border = dark
                ? (active ? 0x005D715Eu : 0x00384238u)
                : (active ? 0x00A2B2A2u : 0x00CED6CEu);
            Apply(window.Handle, frame, BorderColor, border);
            if (dark)
            {
                Apply(window.Handle, frame, CaptionColor, active ? 0x00202720u : 0x001E231Eu);
                Apply(window.Handle, frame, CaptionTextColor, active ? 0x00EDF2EEu : 0x00C1CCC3u);
            }
            else
            {
                // Let Explorer and DWM draw the light caption and its tab background together.
                RestoreAttribute(window.Handle, frame, CaptionColor);
                RestoreAttribute(window.Handle, frame, CaptionTextColor);
            }
        }
    }

    internal void Restore()
    {
        foreach (var (handle, frame) in frames)
        {
            if (Native.WindowProcessId(handle) != frame.Owner || !Native.IsFileExplorerWindow(handle)) continue;
            foreach (int attribute in frame.Changes.Keys.ToArray()) RestoreAttribute(handle, frame, attribute);
        }
        frames.Clear();
    }

    private static void RestoreAttribute(nint handle, Frame frame, int attribute)
    {
        if (!frame.Changes.Remove(attribute, out var change)) return;
        // Respect a value changed by Explorer or Windows since Luma applied its palette.
        if (DwmGetWindowAttribute(handle, attribute, out uint current, sizeof(uint)) >= 0 &&
            current != change.Applied) return;
        uint previous = change.Previous;
        DwmSetWindowAttribute(handle, attribute, ref previous, sizeof(uint));
    }

    private static void Apply(nint handle, Frame frame, int attribute, uint value)
    {
        bool readCurrent = DwmGetWindowAttribute(handle, attribute, out uint current, sizeof(uint)) >= 0;
        uint previous = readCurrent ? current : attribute == CornerPreference ? 0 : ColorDefault;
        if (frame.Changes.TryGetValue(attribute, out var change) && (!readCurrent || current == change.Applied))
            previous = change.Previous;
        if (DwmSetWindowAttribute(handle, attribute, ref value, sizeof(uint)) >= 0)
            frame.Changes[attribute] = (previous, value);
    }

    private static bool UsesDarkAppMode()
    {
        try { return Registry.GetValue(AppThemeKey, "AppsUseLightTheme", 1) is 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return false; }
    }

    private sealed class Frame(uint owner)
    {
        internal uint Owner { get; } = owner;
        internal (bool Active, bool Dark)? Appearance { get; set; }
        internal Dictionary<int, (uint Previous, uint Applied)> Changes { get; } = [];
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out uint value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref uint value, int size);
}
