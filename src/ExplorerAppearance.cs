using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace LumaShell;

// DWM owns the window frame. Explorer owns its tabs, sidebar and folder views.
internal sealed class ExplorerAppearance
{
    private const uint ColorDefault = 0xFFFFFFFF;
    private const int CornerPreference = 33;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int CaptionTextColor = 36;
    private static readonly int[] Attributes = [CornerPreference, BorderColor, CaptionColor, CaptionTextColor];
    private static readonly string ExplorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
    private readonly Dictionary<nint, Frame> frames = [];

    internal void Refresh(IReadOnlyList<Native.OpenWindow> windows, nint foreground)
    {
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
            if (frame.Active == active) continue;
            frame.Active = active;
            foreach (int attribute in Attributes) Apply(window.Handle, frame, attribute, ValueFor(attribute, active));
        }
    }

    internal void Restore()
    {
        foreach (var (handle, frame) in frames)
        {
            if (Native.WindowProcessId(handle) != frame.Owner || !Native.IsFileExplorerWindow(handle)) continue;
            foreach (var (attribute, change) in frame.Changes)
            {
                // Respect a value changed by Explorer or Windows since Luma applied its palette.
                if (DwmGetWindowAttribute(handle, attribute, out uint current, sizeof(uint)) >= 0 &&
                    current != change.Applied) continue;
                uint previous = change.Previous;
                DwmSetWindowAttribute(handle, attribute, ref previous, sizeof(uint));
            }
        }
        frames.Clear();
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

    // COLORREF stores red in the low byte.
    private static uint ValueFor(int attribute, bool active) => attribute switch
    {
        CornerPreference => 2, // DWMWCP_ROUND
        BorderColor => active ? 0x005D715Eu : 0x00384238u,
        CaptionColor => active ? 0x00202720u : 0x001E231Eu,
        CaptionTextColor => active ? 0x00EDF2EEu : 0x00C1CCC3u,
        _ => ColorDefault
    };

    private sealed class Frame(uint owner)
    {
        internal uint Owner { get; } = owner;
        internal bool? Active { get; set; }
        internal Dictionary<int, (uint Previous, uint Applied)> Changes { get; } = [];
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out uint value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref uint value, int size);
}
