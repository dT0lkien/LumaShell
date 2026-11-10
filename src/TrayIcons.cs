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
using static LumaShell.Visuals;

namespace LumaShell;

internal sealed class TrayIcons
{
    private readonly Button toggle;
    private readonly StackPanel entries = new();
    private readonly StackPanel actions = new();
    private readonly TextBlock status = Text("", 12, Muted);
    private readonly Popup popup;
    private readonly Border trayPanel;
    private readonly Border actionPanel;
    private readonly Action exitLuma;
    private TraySnapshot.Item[] snapshot = [];
    private TraySnapshot.Item? selected;
    private bool refreshing;
    private bool busy;

    internal FrameworkElement View => toggle;

    internal TrayIcons(Action exitLuma)
    {
        this.exitLuma = exitLuma;
        toggle = Button(Icon("M3,12 L10,5 L17,12", 14), "Трей приложений Luma", Show, Brushes.Transparent, 6);
        var body = new StackPanel { Width = 258 };
        body.Children.Add(Text("Трей приложений", 16, bold: true));
        status.Margin = new Thickness(0, 4, 0, 7);
        status.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(status);
        body.Children.Add(new ScrollViewer { Content = entries, MaxHeight = 290, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
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
        popup.Opened += (_, _) => Entrance(trayPanel);
        popup.Closed += (_, _) => HideActions();
        toggle.Unloaded += (_, _) => popup.IsOpen = false;
        host.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            if (selected is not null) HideActions();
            else popup.IsOpen = false;
        };
    }

    private static Border CardSurface(UIElement child)
    {
        var surface = Surface(Gradient("#F32B2D39", "#FB16171F"), 18, child);
        surface.Padding = new Thickness(12);
        surface.BorderBrush = Line;
        surface.BorderThickness = new Thickness(1);
        return surface;
    }

    internal void Show()
    {
        if (!toggle.IsVisible) return;
        if (!popup.IsOpen)
        {
            RenderPopup();
            popup.IsOpen = true;
        }
        Refresh();
    }

    internal async void Refresh()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var previous = snapshot;
            snapshot = (await Task.Run(TraySnapshot.Read)).OrderByDescending(item => item.Pinned)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
            if (!popup.IsOpen || previous.Select(item => (item.Key, item.Title, item.Cached))
                .SequenceEqual(snapshot.Select(item => (item.Key, item.Title, item.Cached)))) return;
            if (selected is not null && !snapshot.Any(item => item.Key == selected.Key)) HideActions();
            RenderPopup();
        }
        finally { refreshing = false; }
    }

    private void RenderPopup()
    {
        status.Text = snapshot.Length == 0 ? "Значки запущенных приложений не найдены" : "Правый клик открывает действия слева";
        entries.Children.Clear();
        foreach (var item in snapshot)
        {
            var row = new DockPanel();
            var icon = new Image { Source = item.Icon, Width = 17, Height = 17, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(Text(item.Title, 12));
            var button = Button(row, item.Title, () => OpenWindow(item), Card);
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Margin = new Thickness(0, 0, 0, 4);
            button.PreviewMouseRightButtonUp += (_, args) => { args.Handled = true; ShowActions(item, button); };
            button.PreviewKeyDown += (_, args) =>
            {
                if (args.Key != Key.Apps && !(args.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)) return;
                args.Handled = true;
                ShowActions(item, button);
            };
            entries.Children.Add(button);
        }
    }

    private void OpenWindow(TraySnapshot.Item item)
    {
        var window = Native.OpenWindows().FirstOrDefault(candidate => candidate.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
        if (window is not null && Native.FocusWindow(window.Handle)) popup.IsOpen = false;
        else status.Text = "У приложения нет открытого окна";
    }

    private void ShowActions(TraySnapshot.Item item, FrameworkElement row)
    {
        if (busy) return;
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
        AddAction("Открыть окно", () => OpenWindow(item));
        AddAction("Родное меню значка", () => OpenNativeMenu(item));
        if (TrayAppActions.IsLuma(item.Path)) AddAction("Выйти из Luma", exitLuma);
        else if (TrayAppActions.CanEnd(item.Path)) AddAction("Завершить приложение…", () => ConfirmEnd(item));
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
        button.Margin = new Thickness(0, 0, 0, 4);
        actions.Children.Add(button);
    }

    private async void OpenNativeMenu(TraySnapshot.Item item)
    {
        if (busy) return;
        var anchor = trayPanel.PointToScreen(new Point(0, 0));
        busy = true;
        try
        {
            using var placement = NativeTrayMenu.PlaceClassicMenu(item, anchor);
            bool opened = await Task.Run(() => NativeTrayMenu.TryShow(item, anchor));
            if (!opened && popup.IsOpen) status.Text = "Windows не открыла меню значка. Системный трей доступен по Ctrl+Alt+B.";
            if (opened)
            {
                HideActions();
                await Task.Delay(450);
            }
        }
        finally { busy = false; }
    }

    private async void EndApplication(TraySnapshot.Item item)
    {
        if (busy) return;
        busy = true;
        try
        {
            bool ended = await Task.Run(() => TrayAppActions.TryEnd(item));
            HideActions();
            status.Text = ended ? "Приложение завершено" : "Не удалось однозначно найти процесс или Windows отказала в завершении";
            Refresh();
        }
        finally { busy = false; }
    }
}

internal static class TraySnapshot
{
    internal sealed record Item(string Key, string Path, string Title, bool Pinned, ImageSource Icon, bool Cached = false, nint Owner = 0, uint Uid = 0, Guid IconGuid = default);

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
                lock (items) items[(icon.Window, icon.Id)] = new($"live:{icon.Window}:{icon.Id}", path, title, icon.Preference == 2, image, Owner: icon.Window, Uid: icon.Id, IconGuid: icon.Guid);
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
