using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static LumaShell.Visuals;

namespace LumaShell;

internal sealed class TrayIcons
{
    private readonly Button toggle;
    private readonly StackPanel entries = new();
    private readonly ScrollViewer list;
    private readonly StackPanel actions = new();
    private readonly TextBlock status = Text("", 12, Muted);
    private readonly Popup popup;
    private readonly Border trayPanel;
    private readonly Border actionPanel;
    private readonly Action exitLuma;
    private readonly Action revealWindowsTray;
    private readonly DispatcherTimer menuTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private TraySnapshot.Item[] snapshot = [];
    private TraySnapshot.Item? selected;
    private bool refreshing;
    private bool busy;
    private bool openingWindow;
    private nint nativeMenu;

    internal FrameworkElement View => toggle;
    internal bool UsingNativeTray => busy;
    internal bool IsOpen => popup.IsOpen;

    internal TrayIcons(Action exitLuma, Action revealWindowsTray)
    {
        this.exitLuma = exitLuma;
        this.revealWindowsTray = revealWindowsTray;
        toggle = Button(Icon("M3,12 L10,5 L17,12", 14), "Трей приложений Luma", Show, Brushes.Transparent, 6);
        var body = new StackPanel { Width = 248 };
        var heading = new DockPanel();
        var windowsTray = Button(Icon("M2,3 H18 V16 H2 Z M2,12 H18 M12,14 H15", 14), "Открыть трей Windows", OpenWindowsTray,
            Brushes.Transparent, 6);
        DockPanel.SetDock(windowsTray, Dock.Right);
        heading.Children.Add(windowsTray);
        heading.Children.Add(Text("Трей приложений", 16, bold: true));
        body.Children.Add(heading);
        status.Margin = new Thickness(0, 4, 0, 7);
        status.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(status);
        list = new ScrollViewer { Content = entries, MaxHeight = 290, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(list);
        trayPanel = CardSurface(body);
        actionPanel = CardSurface(actions);
        actionPanel.Width = 207;
        actionPanel.VerticalAlignment = VerticalAlignment.Top;
        actionPanel.Visibility = Visibility.Collapsed;
        var host = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(215) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        host.Children.Add(actionPanel);
        Grid.SetColumn(trayPanel, 1);
        host.Children.Add(trayPanel);
        popup = new Popup { PlacementTarget = toggle, Placement = PlacementMode.Custom, AllowsTransparency = true,
            StaysOpen = false, Child = host };
        popup.CustomPopupPlacementCallback = (size, target, _) =>
            [new CustomPopupPlacement(new Point(target.Width - size.Width, target.Height + 6), PopupPrimaryAxis.Horizontal)];
        popup.Opened += (_, _) =>
        {
            Entrance(trayPanel);
            FocusFirstRow();
        };
        popup.Closed += (_, _) =>
        {
            menuTimer.Stop();
            nativeMenu = 0;
            popup.StaysOpen = false;
            HideActions();
        };
        toggle.IsVisibleChanged += (_, _) => { if (!toggle.IsVisible) popup.IsOpen = false; };
        menuTimer.Tick += (_, _) =>
        {
            if (NativeTrayMenu.IsMenuOpen(nativeMenu)) return;
            menuTimer.Stop();
            nativeMenu = 0;
            popup.StaysOpen = false;
        };
        host.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            if (selected is not null) { HideActions(); FocusFirstRow(); }
            else popup.IsOpen = false;
        };
    }

    private static Border CardSurface(UIElement child)
    {
        var surface = Surface(Gradient("#F334363E", "#FB23252A"), 19, child);
        surface.Padding = new Thickness(10);
        surface.BorderBrush = Paint("#58FFFFFF");
        surface.BorderThickness = new Thickness(1);
        return surface;
    }

    internal void Show()
    {
        if (!toggle.IsVisible) return;
        if (popup.IsOpen)
        {
            popup.IsOpen = false;
            return;
        }
        RenderPopup();
        popup.IsOpen = true;
        Refresh();
    }

    private void OpenWindowsTray()
    {
        popup.IsOpen = false;
        revealWindowsTray();
    }

    internal async void Refresh()
    {
        if (refreshing || busy || nativeMenu != 0) return;
        refreshing = true;
        try
        {
            var updated = (await Task.Run(TraySnapshot.Read)).OrderByDescending(item => item.Pinned)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
            if (busy || nativeMenu != 0 || selected is not null && popup.IsOpen) return;
            bool changed = !snapshot.Select(item => (item.Key, item.Title, item.Cached))
                .SequenceEqual(updated.Select(item => (item.Key, item.Title, item.Cached)));
            snapshot = updated;
            if (!popup.IsOpen || !changed) return;
            RenderPopup();
        }
        finally { refreshing = false; }
    }

    private void RenderPopup()
    {
        bool restoreFocus = entries.IsKeyboardFocusWithin || entries.Children.Count == 0;
        double offset = list.VerticalOffset;
        status.Text = snapshot.Length == 0 ? "Значки запущенных приложений не найдены" : "";
        status.Visibility = snapshot.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        entries.Children.Clear();
        foreach (var item in snapshot)
        {
            var row = new DockPanel();
            var icon = new Image { Source = item.Icon, Width = 17, Height = 17, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(Text(item.Title, 12));
            var button = Button(row, item.Title, () => ActivateIcon(item), Paint("#5720262F"));
            button.Tag = item;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Margin = new Thickness(0, 0, 0, 4);
            button.PreviewMouseRightButtonUp += (_, args) => { args.Handled = true; OpenContext(item, button); };
            button.PreviewKeyDown += (_, args) =>
            {
                if (args.Key != Key.Apps && !((args.Key == Key.F10 || args.SystemKey == Key.F10) &&
                    Keyboard.Modifiers == ModifierKeys.Shift)) return;
                args.Handled = true;
                OpenContext(item, button);
            };
            entries.Children.Add(button);
        }
        list.ScrollToVerticalOffset(offset);
        if (restoreFocus && popup.IsOpen) FocusFirstRow();
    }

    private void FocusFirstRow()
    {
        if (entries.Children.Count > 0 && entries.Children[0] is Button row)
            row.Focus();
    }

    private async Task OpenWindow(TraySnapshot.Item item)
    {
        if (openingWindow) return;
        openingWindow = true;
        try
        {
            bool opened = await TryOpenWindow(item);
            if (opened)
                popup.IsOpen = false;
            else
            {
                if (popup.IsOpen && entries.Children.OfType<Button>().FirstOrDefault(button => Equals(button.Tag, item)) is { } row &&
                    (selected is null || selected.Key != item.Key)) ShowActions(item, row);
                SetStatus(TrayAppActions.IsNvidiaControlPanel(item) ?
                    "Панель управления NVIDIA не открылась" : "Не удалось открыть окно");
            }
        }
        finally { openingWindow = false; }
    }

    private async Task<bool> TryOpenWindow(TraySnapshot.Item item)
    {
        if (TrayAppActions.IsNvidiaControlPanel(item))
            return await TrayAppActions.OpenNvidiaControlPanel() || await InvokeIcon(item);
        if (Prism.IsTrayClient(item.Path))
            return await Prism.OpenRunning(item.Path, invokeTray: _ => InvokeIcon(item)) == true;
        var window = await Task.Run(() => FindWindow(item));
        return window is not null && await Native.FocusWindow(window.Handle);
    }

    private static Native.OpenWindow? FindWindow(TraySnapshot.Item item)
    {
        var windows = Native.OpenWindows();
        uint owner = Native.WindowProcessId(item.Owner);
        var matches = windows.Where(candidate => candidate.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase) ||
            owner != 0 && candidate.Process == owner &&
            string.Equals(Native.ProcessImage(owner), item.OwnerExecutable, StringComparison.OrdinalIgnoreCase));
        return matches.OrderByDescending(candidate => candidate.Process == owner).FirstOrDefault();
    }

    private void ShowActions(TraySnapshot.Item item, FrameworkElement row)
    {
        selected = item;
        double offset = row.TranslatePoint(new Point(), trayPanel).Y;
        actionPanel.Margin = new Thickness(0, Math.Clamp(offset, 0, Math.Max(0, trayPanel.ActualHeight - 145)), 8, 0);
        actionPanel.Visibility = Visibility.Visible;
        RenderActions(item);
        Entrance(actionPanel, 0, 0.96);
    }

    private void HideActions()
    {
        selected = null;
        actionPanel.Visibility = Visibility.Collapsed;
    }

    private void RenderActions(TraySnapshot.Item item)
    {
        actions.Children.Clear();
        var title = Text(item.Title, 13, bold: true);
        title.Margin = new Thickness(3, 0, 3, 6);
        actions.Children.Add(title);
        if (TrayAppActions.IsLuma(item.Path))
        {
            AddAction("Показать трей Windows", OpenWindowsTray);
            AddAction("Выйти из Luma", exitLuma);
            return;
        }
        bool nvidia = TrayAppActions.IsNvidiaControlPanel(item);
        string open = nvidia ? "Панель управления NVIDIA" : Prism.IsTrayClient(item.Path) ? "Открыть Prism" : "Открыть окно";
        AddAction(open, async () => await OpenWindow(item));
        if (!nvidia && !Prism.IsTrayClient(item.Path)) AddAction("Двойной щелчок по значку", async () =>
        {
            if (await InvokeIcon(item, doubleClick: true)) popup.IsOpen = false;
            else SetStatus("Двойной щелчок недоступен. Можно открыть трей Windows.");
        });
        AddAction("Родное меню значка", () => OpenContext(item, null));
        AddAction("Показать трей Windows", OpenWindowsTray);
        if (!nvidia && TrayAppActions.CanEnd(item.Path)) AddAction("Завершить приложение…", () => ConfirmEnd(item));
    }

    private void ConfirmEnd(TraySnapshot.Item item)
    {
        actions.Children.Clear();
        var note = Text("Принудительное завершение. Несохранённые данные могут потеряться.", 12, Muted);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(3, 0, 3, 6);
        actions.Children.Add(note);
        AddAction("Завершить", () => EndApplication(item));
        AddAction("Отмена", () => RenderActions(item));
    }

    private void AddAction(string label, Action action)
    {
        var button = Button(label, action, Card);
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.Margin = new Thickness(0, 0, 0, 4);
        actions.Children.Add(button);
    }

    private async void OpenContext(TraySnapshot.Item item, FrameworkElement? button)
    {
        if (busy || openingWindow || !popup.IsOpen || nativeMenu != 0) return;
        SetStatus("");
        if (button is not null) ShowActions(item, button);
        if (TrayAppActions.IsLuma(item.Path)) return;
        double y = button?.TranslatePoint(new Point(), trayPanel).Y ?? actionPanel.Margin.Top;
        var anchor = trayPanel.PointToScreen(new Point(0, Math.Max(0, y)));
        busy = true;
        popup.StaysOpen = true;
        try
        {
            nint menu = await Task.Run(() => NativeTrayMenu.ShowContextMenu(item, anchor));
            if (!popup.IsOpen) return;
            if (NativeTrayMenu.IsMenuOpen(menu))
            {
                nativeMenu = menu;
                HideActions();
                SetStatus("");
                menuTimer.Start();
            }
            else SetStatus("Родное меню недоступно. Действия Luma — слева.");
        }
        finally
        {
            if (nativeMenu == 0) popup.StaysOpen = false;
            busy = false;
        }
    }

    private void SetStatus(string message)
    {
        status.Text = message;
        status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ActivateIcon(TraySnapshot.Item item)
    {
        if (busy || openingWindow) return;
        if (Prism.IsTrayClient(item.Path) || TrayAppActions.IsNvidiaControlPanel(item))
        {
            await OpenWindow(item);
            return;
        }
        if (await InvokeIcon(item, bringWindowForward: true))
        {
            popup.IsOpen = false;
        }
        else if (popup.IsOpen) await OpenWindow(item);
    }

    internal async Task<bool> InvokeByPath(string executable)
    {
        if (busy || nativeMenu != 0) return false;
        var matches = snapshot.Where(item => item.Path.Equals(executable, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1 && await InvokeIcon(matches[0])) return true;
        if (matches.Length > 1) return false;
        var refreshed = await Task.Run(() => TraySnapshot.Read().Where(item =>
            item.Path.Equals(executable, StringComparison.OrdinalIgnoreCase)).ToArray());
        if (refreshed.Length == 1 && (matches.Length == 0 || refreshed[0].Key != matches[0].Key ||
            refreshed[0].Title != matches[0].Title))
            return await InvokeIcon(refreshed[0]);
        return false;
    }

    private async Task<bool> InvokeIcon(TraySnapshot.Item item, bool doubleClick = false, bool bringWindowForward = false)
    {
        if (busy || nativeMenu != 0) return false;
        busy = true;
        popup.StaysOpen = true;
        try
        {
            Point anchor = popup.IsOpen ? trayPanel.PointToScreen(new Point()) :
                toggle.IsVisible ? toggle.PointToScreen(new Point(0, toggle.ActualHeight + 7)) : NativeTrayMenu.CursorPosition();
            var existing = bringWindowForward ? await Task.Run(() => FindWindow(item)) : null;
            bool sent = await Task.Run(() => NativeTrayMenu.Invoke(item, anchor, doubleClick));
            if (!sent) SessionLog.Write($"Tray action unavailable for {Path.GetFileName(item.Path)}.");
            if (sent && existing is not null && !NativeTrayMenu.HasForegroundMenu &&
                Native.WindowMatches(existing) && !Native.IsApplicationWindowShown(existing.Handle))
                return await Native.FocusWindow(existing.Handle);
            return sent;
        }
        finally { popup.StaysOpen = false; busy = false; }
    }

    private async void EndApplication(TraySnapshot.Item item)
    {
        if (busy) return;
        busy = true;
        try
        {
            bool ended = await Task.Run(() => TrayAppActions.TryEnd(item));
            HideActions();
            SetStatus(ended ? "Приложение завершено" : "Не удалось завершить приложение");
            Refresh();
        }
        finally { busy = false; }
    }
}

internal static class TraySnapshot
{
    internal sealed record Item(string Key, string Path, string Title, bool Pinned, ImageSource Icon, bool Cached = false,
        nint Owner = 0, uint Uid = 0, Guid IconGuid = default, bool HasUid = false, string? OwnerExecutable = null);

    internal static Item[] Read()
    {
        var live = ReadExplorer();
        return live.Concat(TrayRegistry.Read(live)).ToArray();
    }

    private static Item[] ReadExplorer()
    {
        object? shell = null;
        try
        {
            // Explorer's private read callback, used by Chromium for its notification-area integration.
            shell = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("25DEAD04-1EAC-4911-9E3A-AD0A4AB560FD"), true)!);
            if (shell is not ITrayNotify tray) return [];
            var callback = new TraySnapshotCallback();
            if (tray.RegisterCallback(callback, out uint registration) < 0) return [];
            try { return callback.Snapshot(); }
            finally { tray.UnregisterCallback(ref registration); }
        }
        catch (Exception error) when (error is COMException or InvalidCastException or UnauthorizedAccessException or InvalidOperationException)
        { return []; }
        finally { if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); }
    }

    [ComImport, Guid("D133CE13-3537-48BA-93A7-AFCD5D2053B4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITrayNotify
    {
        [PreserveSig] int RegisterCallback([MarshalAs(UnmanagedType.Interface)] ITraySnapshotCallback callback, out uint token);
        [PreserveSig] int UnregisterCallback(ref uint token);
    }
}

[ComVisible(true), Guid("D782CCBA-AFB0-43F1-94DB-FDA3779EACCB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITraySnapshotCallback
{
    [PreserveSig] int Notify(uint notification, nint item);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class TraySnapshotCallback : ITraySnapshotCallback
{
    private readonly Dictionary<(nint, uint), TraySnapshot.Item> items = [];
    internal TraySnapshotCallback() { }

    internal TraySnapshot.Item[] Snapshot()
    {
        lock (items) return items.Values.ToArray();
    }

    public int Notify(uint notification, nint item)
    {
        if (item == 0) return 0;
        var icon = Marshal.PtrToStructure<NotifyItem>(item);
        if (icon.Window == 0 || !IsWindow(icon.Window)) return 0;
        try
        {
            string executable = Native.Executable(icon.Window) ?? "";
            string path = Native.WindowApp(icon.Window) ?? executable;
            string title = Label(Marshal.PtrToStringUni(icon.Tooltip) ?? "", 85);
            if (title.Length == 0) title = Path.GetFileNameWithoutExtension(path);
            if (title.Length == 0) return 0;
            ImageSource? image = null;
            nint owned = icon.Icon == 0 ? 0 : CopyIcon(icon.Icon);
            if (owned != 0)
            {
                try { image = Launcher.IconImage(owned); }
                finally { DestroyIcon(owned); }
            }
            if (image is null && File.Exists(path))
            {
                using var programIcon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (programIcon is not null) image = Launcher.IconImage(programIcon.Handle);
            }
            if (image is not null)
                lock (items) items[(icon.Window, icon.Id)] = new($"live:{icon.Window}:{icon.Id}", path, title, icon.Preference == 2,
                    image, Owner: icon.Window, Uid: icon.Id, IconGuid: icon.Guid, OwnerExecutable: executable);
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyItem
    {
        public nint Executable, Tooltip, Icon, Window;
        public uint Preference, Id;
        public Guid Guid;
    }
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint CopyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
