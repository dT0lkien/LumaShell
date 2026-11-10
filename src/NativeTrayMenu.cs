using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Interop.UIAutomationClient;

namespace LumaShell;

internal static class NativeTrayMenu
{
    internal static IDisposable PlaceClassicMenu(TraySnapshot.Item item, Point anchor)
    {
        uint owner = Native.WindowProcessId(item.Owner);
        if (owner == 0 || !string.Equals(Native.ProcessImage(owner), item.Path, StringComparison.OrdinalIgnoreCase))
        {
            uint found = 0;
            foreach (var window in Native.OpenWindows())
            {
                if (!window.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase)) continue;
                uint process = Native.WindowProcessId(window.Handle);
                if (found != 0 && process != found) { found = 0; break; }
                found = process;
            }
            owner = found;
        }
        return new ClassicMenuAnchor(owner, anchor);
    }

    internal static bool TryShow(TraySnapshot.Item item, Point anchor)
    {
        object? automation = null;
        var candidates = new List<(IUIAutomationElement3 Element, nint Overflow, bool Exact)>();
        try
        {
            var client = new CUIAutomation();
            automation = client;
            bool positioned = TryIconBounds(item, out var iconBounds);
            nint primary = FindWindow("Shell_TrayWnd", null);
            nint overflow = FindWindow("TopLevelWindowForOverflowXamlIsland", null);
            if (overflow == 0) overflow = FindWindow("NotifyIconOverflowWindow", null);
            if (primary != 0) FindMatches(client, primary, false, item.Title, positioned ? iconBounds : null, candidates);
            if (overflow != 0) FindMatches(client, overflow, true, item.Title, positioned ? iconBounds : null, candidates);
            var exact = candidates.FindAll(candidate => candidate.Exact);
            var choices = exact.Count > 0 ? exact : candidates;
            if (choices.Count != 1) return false;
            var selected = choices[0];
            if (selected.Overflow == 0 || IsWindowVisible(selected.Overflow))
            {
                selected.Element.ShowContextMenu();
                return true;
            }

            // Windows 11 may require its overflow host to be visible for the provider to handle the action.
            // Only the overflow window moves; the main taskbar and its preferences are untouched.
            if (!GetWindowRect(selected.Overflow, out var old) || old.Right <= old.Left) return false;
            int left = (int)anchor.X - (old.Right - old.Left) - 8;
            if (left < System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)anchor.X, (int)anchor.Y)).WorkingArea.Left)
                return false;
            if (!SetWindowPos(selected.Overflow, 0, left, (int)anchor.Y, 0, 0, 0x55)) return false;
            try
            {
                selected.Element.ShowContextMenu();
                Thread.Sleep(170);
                return true;
            }
            finally { SetWindowPos(selected.Overflow, 0, old.Left, old.Top, 0, 0, 0x95); }
        }
        catch (Exception error) when (error is COMException or InvalidCastException or UnauthorizedAccessException or InvalidOperationException)
        { return false; }
        finally
        {
            foreach (var candidate in candidates) Marshal.ReleaseComObject(candidate.Element);
            if (automation is not null && Marshal.IsComObject(automation)) Marshal.ReleaseComObject(automation);
        }
    }

    private static void FindMatches(IUIAutomation client, nint window, bool overflow, string title, RectPixels? bounds,
        List<(IUIAutomationElement3 Element, nint Overflow, bool Exact)> candidates)
    {
        IUIAutomationElement? root = null;
        IUIAutomationCondition? condition = null;
        IUIAutomationElementArray? elements = null;
        try
        {
            root = client.ElementFromHandle(window);
            condition = client.CreatePropertyCondition(30003, 50000); // ControlType Button.
            elements = root.FindAll(TreeScope.TreeScope_Descendants, condition);
            for (int index = 0; index < Math.Min(elements.Length, 160); index++)
            {
                var element = elements.GetElement(index);
                bool kept = false;
                try
                {
                    if (!overflow && element.CurrentClassName?.StartsWith("SystemTray.", StringComparison.Ordinal) != true) continue;
                    if (element is not IUIAutomationElement3 menu) continue;
                    var rect = element.CurrentBoundingRectangle;
                    bool exact = bounds is { } icon && rect.right > rect.left && rect.bottom > rect.top &&
                        Math.Abs((long)rect.left + rect.right - icon.Left - icon.Right) <= 12 &&
                        Math.Abs((long)rect.top + rect.bottom - icon.Top - icon.Bottom) <= 12;
                    if (!exact && !SameLabel(element.CurrentName, title) && !SameLabel(element.CurrentHelpText, title)) continue;
                    candidates.Add((menu, overflow ? window : 0, exact));
                    kept = true;
                }
                catch (COMException) { }
                finally { if (!kept) Marshal.ReleaseComObject(element); }
            }
        }
        catch (COMException) { }
        finally
        {
            if (elements is not null) Marshal.ReleaseComObject(elements);
            if (condition is not null) Marshal.ReleaseComObject(condition);
            if (root is not null) Marshal.ReleaseComObject(root);
        }
    }

    private static bool SameLabel(string? candidate, string title) => !string.IsNullOrWhiteSpace(candidate) &&
        Visuals.Label(candidate, 85).Equals(title, StringComparison.CurrentCultureIgnoreCase);

    private static bool TryIconBounds(TraySnapshot.Item item, out RectPixels bounds)
    {
        bounds = default;
        if (item.Owner == 0 && item.IconGuid == Guid.Empty) return false;
        var identifier = new NotifyIconIdentifier { Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            Owner = item.Owner, Uid = item.Uid, Guid = item.IconGuid };
        return Shell_NotifyIconGetRect(ref identifier, out bounds) >= 0 && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier { public uint Size; public nint Owner; public uint Uid; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RectPixels { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out RectPixels bounds);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out RectPixels bounds);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    private sealed class ClassicMenuAnchor : IDisposable
    {
        private readonly WinEvent callback;
        private readonly nint hook;

        internal ClassicMenuAnchor(uint process, Point anchor)
        {
            callback = (_, _, menu, _, _, _, _) =>
            {
                var name = new System.Text.StringBuilder(32);
                if (menu == 0 || GetClassName(menu, name, name.Capacity) == 0 || name.ToString() != "#32768" ||
                    !GetWindowRect(menu, out var bounds)) return;
                var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)anchor.X, (int)anchor.Y)).WorkingArea;
                int width = bounds.Right - bounds.Left;
                int left = (int)anchor.X - width - 8;
                if (left < area.Left) return;
                int top = Math.Clamp((int)anchor.Y, area.Top, Math.Max(area.Top, area.Bottom - (bounds.Bottom - bounds.Top)));
                SetWindowPos(menu, 0, left, top, 0, 0, 0x15);
            };
            if (process != 0) hook = SetWinEventHook(6, 6, 0, callback, process, 0, 0);
        }

        public void Dispose() { if (hook != 0) UnhookWinEvent(hook); }

        private delegate void WinEvent(nint hook, uint type, nint window, int objectId, int child, uint thread, uint time);
        [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEvent callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, System.Text.StringBuilder name, int capacity);
    }
}
