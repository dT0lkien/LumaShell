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
        return new ClassicMenuAnchor(owner, anchor, item.Path);
    }

    internal static bool TryActivate(TraySnapshot.Item item, bool contextMenu)
    {
        object? automation = null;
        var candidates = new List<(IUIAutomationElement Element, bool Exact)>();
        nint overflow = 0;
        RectPixels original = default;
        bool restoreOverflow = false;
        try
        {
            var client = new CUIAutomation();
            automation = client;
            nint primary = FindWindow("Shell_TrayWnd", null);
            overflow = OverflowWindow();
            bool openedOverflow = false;
            if (OverflowContent(overflow) == 0 && primary != 0 && (overflow == 0 || !IsWindowVisible(overflow)))
            {
                openedOverflow = OpenOverflow(client, primary);
                overflow = OverflowWindow();
            }
            // A hidden XAML overflow can have an empty automation tree. Materialize it before
            // looking up the item; do not reveal the taskbar or change any IsPromoted preferences.
            if (overflow != 0 && (openedOverflow || !IsWindowVisible(overflow)) && GetWindowRect(overflow, out original))
            {
                int width = Math.Max(1, original.Right - original.Left);
                var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
                restoreOverflow = SetWindowPos(overflow, 0, screen.Left - width - 64, screen.Top, 0, 0, 0x55);
                if (restoreOverflow) Thread.Sleep(80);
            }
            bool positioned = TryIconBounds(item, out var iconBounds);
            if (primary != 0) FindMatches(client, primary, false, item.Title, positioned ? iconBounds : null, candidates);
            if (overflow != 0)
            {
                nint content = OverflowContent(overflow);
                FindMatches(client, content != 0 ? content : overflow, true, item.Title, positioned ? iconBounds : null, candidates);
            }
            var exact = candidates.FindAll(candidate => candidate.Exact);
            var choices = exact.Count > 0 ? exact : candidates;
            // Never guess by list index or executable name: an app can own several different icons.
            if (choices.Count != 1) return false;
            var element = choices[0].Element;
            if (contextMenu)
            {
                if (element is not IUIAutomationElement3 menu) return false;
                menu.ShowContextMenu();
            }
            else
            {
                if (!InvokePrimary(element)) return false;
            }
            // Providers can dispatch asynchronously; keep the overflow alive until the callback runs.
            if (restoreOverflow) Thread.Sleep(170);
            return true;
        }
        catch (Exception error) when (error is COMException or InvalidCastException or UnauthorizedAccessException or InvalidOperationException)
        { return false; }
        finally
        {
            if (restoreOverflow) SetWindowPos(overflow, 0, original.Left, original.Top, 0, 0, 0x95);
            foreach (var candidate in candidates) Marshal.ReleaseComObject(candidate.Element);
            if (automation is not null && Marshal.IsComObject(automation)) Marshal.ReleaseComObject(automation);
        }
    }

    private static bool InvokePrimary(IUIAutomationElement element)
    {
        object? pattern = null;
        try
        {
            try { pattern = element.GetCurrentPattern(10000); }
            catch (COMException) { }
            if (pattern is IUIAutomationInvokePattern invoke)
            {
                invoke.Invoke();
                return true;
            }
            if (pattern is not null && Marshal.IsComObject(pattern)) Marshal.ReleaseComObject(pattern);
            pattern = null;
            pattern = element.GetCurrentPattern(10018); // LegacyIAccessible for older native tray providers.
            if (pattern is not IUIAutomationLegacyIAccessiblePattern legacy) return false;
            legacy.DoDefaultAction();
            return true;
        }
        finally { if (pattern is not null && Marshal.IsComObject(pattern)) Marshal.ReleaseComObject(pattern); }
    }

    private static nint OverflowWindow()
    {
        nint window = FindWindow("TopLevelWindowForOverflowXamlIsland", null);
        return window != 0 ? window : FindWindow("NotifyIconOverflowWindow", null);
    }

    private static nint OverflowContent(nint overflow)
    {
        if (overflow == 0) return 0;
        nint content = FindWindowEx(overflow, 0, "Windows.UI.Composition.DesktopWindowContentBridge", null);
        return content != 0 ? content : FindWindowEx(overflow, 0, "ToolbarWindow32", null);
    }

    private static bool IsChevron(IUIAutomationElement element) => element.CurrentClassName switch
    {
        "SystemTray.ChevronIconView" or "SystemTray.OverflowButton" => true,
        "SystemTray.NormalButton" => element.CurrentAutomationId == "SystemTrayIcon",
        _ => false
    };

    private static bool OpenOverflow(IUIAutomation client, nint primary)
    {
        IUIAutomationElement? root = null;
        IUIAutomationCondition? condition = null;
        IUIAutomationElementArray? elements = null;
        try
        {
            root = client.ElementFromHandle(primary);
            condition = client.CreatePropertyCondition(30003, 50000);
            elements = root.FindAll(TreeScope.TreeScope_Descendants, condition);
            for (int index = 0; index < Math.Min(elements.Length, 200); index++)
            {
                var element = elements.GetElement(index);
                object? pattern = null;
                try
                {
                    if (!IsChevron(element)) continue;
                    pattern = element.GetCurrentPattern(10000);
                    if (pattern is not IUIAutomationInvokePattern invoke) continue;
                    invoke.Invoke();
                    for (int attempt = 0; attempt < 15 && OverflowContent(OverflowWindow()) == 0; attempt++) Thread.Sleep(10);
                    return OverflowWindow() != 0;
                }
                finally
                {
                    if (pattern is not null && Marshal.IsComObject(pattern)) Marshal.ReleaseComObject(pattern);
                    Marshal.ReleaseComObject(element);
                }
            }
            return false;
        }
        catch (COMException) { return false; }
        finally
        {
            if (elements is not null) Marshal.ReleaseComObject(elements);
            if (condition is not null) Marshal.ReleaseComObject(condition);
            if (root is not null) Marshal.ReleaseComObject(root);
        }
    }

    private static void FindMatches(IUIAutomation client, nint window, bool overflow, string title, RectPixels? bounds,
        List<(IUIAutomationElement Element, bool Exact)> candidates)
    {
        IUIAutomationElement? root = null;
        IUIAutomationCondition? condition = null;
        IUIAutomationElementArray? elements = null;
        try
        {
            root = client.ElementFromHandle(window);
            condition = client.CreatePropertyCondition(30003, 50000);
            elements = root.FindAll(TreeScope.TreeScope_Descendants, condition);
            for (int index = 0; index < Math.Min(elements.Length, 400); index++)
            {
                var element = elements.GetElement(index);
                bool kept = false;
                try
                {
                    if (!overflow && (IsChevron(element) || !InNotificationArea(client, element))) continue;
                    var rect = element.CurrentBoundingRectangle;
                    bool exact = bounds is { } icon && rect.right > rect.left && rect.bottom > rect.top &&
                        Math.Abs((long)rect.left + rect.right - icon.Left - icon.Right) <= 12 &&
                        Math.Abs((long)rect.top + rect.bottom - icon.Top - icon.Bottom) <= 12;
                    if (!exact && !SameLabel(element.CurrentName, title) && !SameLabel(element.CurrentHelpText, title)) continue;
                    if (candidates.Exists(candidate => client.CompareElements(candidate.Element, element) != 0)) continue;
                    candidates.Add((element, exact));
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

    private static bool InNotificationArea(IUIAutomation client, IUIAutomationElement element)
    {
        var walker = client.ControlViewWalker;
        IUIAutomationElement? parent = null;
        try
        {
            for (int depth = 0; depth < 7; depth++)
            {
                var current = parent ?? element;
                string name = current.CurrentClassName ?? "";
                string id = current.CurrentAutomationId ?? "";
                if (name.StartsWith("SystemTray.", StringComparison.Ordinal) || name == "TrayNotifyWnd" ||
                    id is "NotificationAreaIcons" or "NotifyIconStack" or "SystemTrayFrame") return true;
                var next = walker.GetParentElement(current);
                if (parent is not null) Marshal.ReleaseComObject(parent);
                parent = next;
                if (parent is null) break;
            }
            return false;
        }
        finally
        {
            if (parent is not null) Marshal.ReleaseComObject(parent);
            Marshal.ReleaseComObject(walker);
        }
    }

    private static bool SameLabel(string? candidate, string title) => !string.IsNullOrWhiteSpace(candidate) &&
        Visuals.Label(candidate, 85).Equals(title, StringComparison.CurrentCultureIgnoreCase);

    private static bool TryIconBounds(TraySnapshot.Item item, out RectPixels bounds)
    {
        bounds = default;
        if (item.Owner != 0 || item.IconGuid != Guid.Empty)
        {
            var identifier = new NotifyIconIdentifier { Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
                Owner = item.Owner, Uid = item.Uid, Guid = item.IconGuid };
            if (Shell_NotifyIconGetRect(ref identifier, out bounds) >= 0 && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
                return true;
        }
        if (!item.Cached || !item.HasUid) return false;
        RectPixels found = default;
        bool matched = false;
        EnumWindows((window, _) =>
        {
            if (!string.Equals(Native.Executable(window), item.Path, StringComparison.OrdinalIgnoreCase)) return true;
            var identifier = new NotifyIconIdentifier { Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Owner = window, Uid = item.Uid };
            if (Shell_NotifyIconGetRect(ref identifier, out var rect) < 0 || rect.Right <= rect.Left || rect.Bottom <= rect.Top) return true;
            found = rect;
            matched = true;
            return false;
        }, 0);
        bounds = found;
        return matched;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier { public uint Size; public nint Owner; public uint Uid; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RectPixels { public int Left, Top, Right, Bottom; }
    private delegate bool EnumerateWindow(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumerateWindow callback, nint parameter);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out RectPixels bounds);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out RectPixels bounds);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    private sealed class ClassicMenuAnchor : IDisposable
    {
        private readonly WinEvent callback;
        private readonly nint hook;

        internal ClassicMenuAnchor(uint process, Point anchor, string path)
        {
            callback = (_, _, menu, _, _, _, _) =>
            {
                if (process == 0 && !string.Equals(Native.Executable(menu), path, StringComparison.OrdinalIgnoreCase)) return;
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
            if (process != 0 || !string.IsNullOrEmpty(path)) hook = SetWinEventHook(6, 6, 0, callback, process, 0, 0);
        }

        public void Dispose() { if (hook != 0) UnhookWinEvent(hook); }

        private delegate void WinEvent(nint hook, uint type, nint window, int objectId, int child, uint thread, uint time);
        [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEvent callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, System.Text.StringBuilder name, int capacity);
    }
}
