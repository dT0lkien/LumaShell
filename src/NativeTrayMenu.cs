using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Interop.UIAutomationClient;

namespace LumaShell;

internal static class NativeTrayMenu
{
    internal static nint ShowContextMenu(TraySnapshot.Item item, Point anchor)
    {
        var placement = new ClassicMenuAnchor(item, anchor);
        TryActivate(item, placement);
        return placement.Menu;
    }

    internal static bool Invoke(TraySnapshot.Item item) => TryActivate(item, null);
    internal static bool IsMenuOpen(nint menu) => menu != 0 && IsWindowVisible(menu);
    internal static bool IsNativeSurface(nint window)
    {
        var name = new System.Text.StringBuilder(64);
        return window != 0 && GetClassName(window, name, name.Capacity) != 0 &&
            name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or
                "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow";
    }

    private static bool TryActivate(TraySnapshot.Item item, ClassicMenuAnchor? context)
    {
        object? automation = null;
        var candidates = new List<(IUIAutomationElement Element, bool Exact, nint Surface)>();
        nint overflow = 0;
        RectPixels original = default;
        bool openedOverflow = false;
        TaskbarAccess? taskbar = null;
        try
        {
            var client = new CUIAutomation();
            automation = client;
            if (automation is IUIAutomation2 timeouts)
            {
                timeouts.ConnectionTimeout = 850;
                timeouts.TransactionTimeout = 1100;
            }
            nint primary = FindWindow("Shell_TrayWnd", null);
            if (primary == 0) return false;
            taskbar = new TaskbarAccess(primary);
            overflow = OverflowWindow();
            bool positioned = TryIconBounds(item, out var iconBounds);
            FindMatches(client, primary, false, item.Title, positioned ? iconBounds : null, candidates);
            if (!candidates.Exists(candidate => candidate.Exact) && (overflow == 0 || !IsWindowVisible(overflow)))
            {
                openedOverflow = OpenOverflow(client, primary);
                overflow = OverflowWindow();
            }
            if (overflow != 0)
            {
                if (openedOverflow && GetWindowRect(overflow, out original))
                {
                    // Move a temporary overflow beside the Luma tray while Explorer handles the click.
                    var area = WorkingArea(context?.Anchor ?? new Point(original.Left, original.Top));
                    int width = original.Right - original.Left;
                    if (width > 0)
                    {
                        int left = context is null ? System.Windows.Forms.SystemInformation.VirtualScreen.Left - width - 64 :
                            Math.Clamp((int)context.Anchor.X - width - 10, area.Left, Math.Max(area.Left, area.Right - width));
                        SetWindowPos(overflow, 0, left, area.Top + 35, 0, 0, 0x15);
                    }
                    Thread.Sleep(90);
                }
                if (!candidates.Exists(candidate => candidate.Exact))
                {
                    positioned = TryIconBounds(item, out iconBounds);
                    FindMatches(client, overflow, true, item.Title, positioned ? iconBounds : null, candidates);
                    if (!candidates.Exists(candidate => candidate.Surface == overflow) && OverflowContent(overflow) is var content && content != 0)
                        FindMatches(client, content, true, item.Title, positioned ? iconBounds : null, candidates);
                }
            }
            var exact = candidates.FindAll(candidate => candidate.Exact);
            var choices = exact.Count > 0 ? exact : candidates;
            // Never guess by list index or executable name: an app can own several different icons.
            if (choices.Count != 1) return false;
            var (element, _, surface) = choices[0];
            if (context is not null)
            {
                // An actual Explorer click invokes the icon callback, including for elevated apps.
                if (!ClickElement(client, element, surface))
                {
                    if (element is not IUIAutomationElement3 menu) return false;
                    menu.ShowContextMenu();
                }
                // UI Automation can return success for a parent menu. Only accept the target app's menu.
                return context.WaitForMenu();
            }
            if (!InvokePrimary(element)) return false;
            if (openedOverflow) Thread.Sleep(180);
            return true;
        }
        catch (Exception error) when (error is COMException or InvalidCastException or UnauthorizedAccessException or InvalidOperationException)
        { return false; }
        finally
        {
            if (openedOverflow && overflow != 0)
            {
                // Return the overflow to its original position without changing icon pinning.
                uint flags = original.Right > original.Left ? 0x95u : 0x97u;
                SetWindowPos(overflow, 0, original.Left, original.Top, 0, 0, flags);
            }
            taskbar?.Dispose();
            foreach (var candidate in candidates) Marshal.ReleaseComObject(candidate.Element);
            if (automation is not null && Marshal.IsComObject(automation)) Marshal.ReleaseComObject(automation);
        }
    }

    private static bool ClickElement(IUIAutomation client, IUIAutomationElement element, nint surface)
    {
        if (!IsWindowVisible(surface)) return false;
        var rect = element.CurrentBoundingRectangle;
        if (rect.right <= rect.left || rect.bottom <= rect.top) return false;
        var center = new PointPixels { X = rect.left + (rect.right - rect.left) / 2, Y = rect.top + (rect.bottom - rect.top) / 2 };
        nint hitWindow = WindowFromPoint(center);
        if (hitWindow != surface && !IsChild(surface, hitWindow)) return false;
        IUIAutomationElement? hit = null, parent = null;
        IUIAutomationTreeWalker? walker = null;
        bool matches = false;
        try
        {
            hit = client.ElementFromPoint(new tagPOINT { x = center.X, y = center.Y });
            if (hit is null) return false;
            walker = client.RawViewWalker;
            for (int depth = 0; depth < 5; depth++)
            {
                var node = parent ?? hit;
                if (client.CompareElements(node, element) != 0) { matches = true; break; }
                var next = walker.GetParentElement(node);
                if (parent is not null) Marshal.ReleaseComObject(parent);
                parent = next;
                if (parent is null) break;
            }
        }
        catch (COMException) { return false; }
        finally
        {
            if (parent is not null) Marshal.ReleaseComObject(parent);
            if (hit is not null) Marshal.ReleaseComObject(hit);
            if (walker is not null) Marshal.ReleaseComObject(walker);
        }
        if (!matches || !GetCursorPos(out var old) || !SetCursorPos(center.X, center.Y)) return false;
        try
        {
            hitWindow = WindowFromPoint(center);
            return (hitWindow == surface || IsChild(surface, hitWindow)) && RightClick();
        }
        finally
        {
            Thread.Sleep(75);
            if (GetCursorPos(out var current) && current.X == center.X && current.Y == center.Y)
                SetCursorPos(old.X, old.Y);
        }
    }

    private static bool RightClick()
    {
        var press = new MouseEvent { Data = new MouseData { Flags = 0x0008 } };
        var release = new MouseEvent { Data = new MouseData { Flags = 0x0010 } };
        int size = Marshal.SizeOf<MouseEvent>();
        uint sent = SendInput(2, [press, release], size);
        if (sent == 1) SendInput(1, [release], size);
        return sent == 2;
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
                    for (int attempt = 0; attempt < 20; attempt++)
                    {
                        nint overflow = OverflowWindow();
                        if (OverflowContent(overflow) != 0 && IsWindowVisible(overflow)) return true;
                        Thread.Sleep(15);
                    }
                    nint result = OverflowWindow();
                    return result != 0 && IsWindowVisible(result);
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
        List<(IUIAutomationElement Element, bool Exact, nint Surface)> candidates)
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
                    if (IsChevron(element) || !overflow && !InNotificationArea(client, element)) continue;
                    var rect = element.CurrentBoundingRectangle;
                    bool exact = bounds is { } icon && rect.right > rect.left && rect.bottom > rect.top &&
                        Math.Abs((long)rect.left + rect.right - icon.Left - icon.Right) <= 12 &&
                        Math.Abs((long)rect.top + rect.bottom - icon.Top - icon.Bottom) <= 12;
                    if (!exact && !SameLabel(element.CurrentName, title) && !SameLabel(element.CurrentHelpText, title)) continue;
                    if (candidates.Exists(candidate => client.CompareElements(candidate.Element, element) != 0)) continue;
                    candidates.Add((element, exact, window));
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
    [StructLayout(LayoutKind.Sequential)]
    private struct PointPixels { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseEvent { public uint Type; public MouseData Data; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData { public int X, Y; public uint Button, Flags, Time; public nuint Extra; }
    private delegate bool EnumerateWindow(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumerateWindow callback, nint parameter);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out RectPixels bounds);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out RectPixels bounds);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(PointPixels point);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointPixels point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, MouseEvent[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, System.Text.StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);

    private sealed class TaskbarAccess : IDisposable
    {
        private readonly nint window;
        private readonly bool restore;

        internal TaskbarAccess(nint window)
        {
            this.window = window;
            if (IsWindowVisible(window)) return;
            restore = ShowWindowAsync(window, 4);
            if (restore)
            {
                for (int attempt = 0; attempt < 12 && !IsWindowVisible(window); attempt++) Thread.Sleep(15);
            }
        }

        public void Dispose()
        {
            if (restore) ShowWindowAsync(window, 0);
        }
    }

    private static System.Drawing.Rectangle WorkingArea(Point anchor) =>
        System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)anchor.X, (int)anchor.Y)).WorkingArea;

    internal static Point MenuPosition(Point anchor, System.Drawing.Rectangle area, int width, int height)
    {
        const int gap = 8;
        int top = Math.Clamp((int)anchor.Y, area.Top, Math.Max(area.Top, area.Bottom - height));
        return new Point(Math.Max(area.Left, (int)anchor.X - width - gap), top);
    }

    private sealed class ClassicMenuAnchor
    {
        private readonly uint owner;
        private readonly string executable;
        private readonly HashSet<nint> existing = [];
        private readonly System.Text.StringBuilder className = new(24);
        internal Point Anchor { get; }
        internal nint Menu { get; private set; }

        internal ClassicMenuAnchor(TraySnapshot.Item item, Point anchor)
        {
            Anchor = anchor;
            executable = item.OwnerExecutable ?? item.Path;
            uint candidate = Native.WindowProcessId(item.Owner);
            if (candidate != 0 && !string.IsNullOrEmpty(executable) &&
                string.Equals(Native.ProcessImage(candidate), executable, StringComparison.OrdinalIgnoreCase)) owner = candidate;
            EnumWindows((window, _) =>
            {
                if (IsWindowVisible(window) && IsMenu(window)) existing.Add(window);
                return true;
            }, 0);
        }

        internal bool WaitForMenu()
        {
            for (int attempt = 0; attempt < 22; attempt++)
            {
                EnumWindows((window, _) => !TryObserve(window), 0);
                if (Menu != 0) return true;
                Thread.Sleep(25);
            }
            return false;
        }

        private bool TryObserve(nint window)
        {
            if (window == 0 || existing.Contains(window) || !IsWindowVisible(window) || !IsMenu(window) ||
                !GetWindowRect(window, out var bounds)) return false;
            uint process = Native.WindowProcessId(window);
            if ((owner == 0 || process != owner) &&
                (string.IsNullOrEmpty(executable) || !string.Equals(Native.ProcessImage(process), executable, StringComparison.OrdinalIgnoreCase)))
                return false;
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            if (width <= 0 || height <= 0) return false;
            Point position = MenuPosition(Anchor, WorkingArea(Anchor), width, height);
            SetWindowPos(window, 0, (int)position.X, (int)position.Y, 0, 0, 0x15);
            Menu = window;
            return true;
        }

        private bool IsMenu(nint window)
        {
            className.Clear();
            return GetClassName(window, className, className.Capacity) != 0 && className.ToString() == "#32768";
        }
    }
}
