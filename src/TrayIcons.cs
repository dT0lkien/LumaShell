using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using static LumaShell.Visuals;

namespace LumaShell;

internal sealed class TrayIcons
{
    private readonly StackPanel view = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel applications = new() { Orientation = Orientation.Horizontal };
    private readonly Dictionary<(nint, uint), (Image Image, Button Button)> buttons = [];
    private readonly StackPanel entries = new();
    private readonly TextBlock status = Text("", 12, Muted);
    private readonly Popup popup;
    private TraySnapshot.Item[] snapshot = [];
    private bool refreshing;

    internal FrameworkElement View => view;

    internal TrayIcons()
    {
        view.Children.Add(applications);
        var toggle = Button(Icon("M3,12 L10,5 L17,12", 14), "Трей приложений Luma", Show, Brushes.Transparent, 6);
        view.Children.Add(toggle);
        var body = new StackPanel { Width = 270 };
        body.Children.Add(Text("Трей приложений", 16, bold: true));
        status.Margin = new Thickness(0, 4, 0, 7);
        status.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(status);
        body.Children.Add(new ScrollViewer { Content = entries, MaxHeight = 290, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var panel = Surface(Gradient("#F32B2D39", "#FB16171F"), 18, body);
        panel.Padding = new Thickness(12);
        panel.BorderBrush = Line;
        panel.BorderThickness = new Thickness(1);
        popup = new Popup { PlacementTarget = toggle, Placement = PlacementMode.Bottom, HorizontalOffset = -180,
            VerticalOffset = 6, AllowsTransparency = true, StaysOpen = false, Child = panel };
        toggle.Unloaded += (_, _) => popup.IsOpen = false;
    }

    internal void Show()
    {
        if (!view.IsVisible) return;
        popup.IsOpen = true;
        RenderPopup();
        Refresh();
    }

    internal async void Refresh()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            snapshot = await Task.Run(TraySnapshot.Read);
            var visible = snapshot.OrderByDescending(item => item.Pinned).Take(6).ToArray();
            if (!buttons.Keys.SequenceEqual(visible.Select(item => (item.Window, item.Id))))
            {
                applications.Children.Clear();
                buttons.Clear();
                foreach (var item in visible)
                {
                    var image = new Image { Source = item.Icon, Width = 17, Height = 17 };
                    var button = Button(image, item.Title, () => { if (!Open(item.Window)) Show(); }, Brushes.Transparent, 4);
                    button.PreviewMouseRightButtonUp += (_, args) => { args.Handled = true; Show(); };
                    buttons.Add((item.Window, item.Id), (image, button));
                    applications.Children.Add(button);
                }
            }
            else
            {
                foreach (var item in visible)
                {
                    var button = buttons[(item.Window, item.Id)];
                    button.Image.Source = item.Icon;
                    button.Button.ToolTip = item.Title;
                    System.Windows.Automation.AutomationProperties.SetName(button.Button, item.Title);
                }
            }
            if (popup.IsOpen) RenderPopup();
        }
        finally { refreshing = false; }
    }

    private void RenderPopup()
    {
        status.Text = snapshot.Length == 0 ? "Explorer пока не вернул значки приложений" : "Щелчок переключает на открытое окно";
        entries.Children.Clear();
        foreach (var item in snapshot.OrderByDescending(item => item.Pinned))
        {
            var row = new DockPanel();
            var icon = new Image { Source = item.Icon, Width = 17, Height = 17, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(Text(item.Title, 12));
            var button = Button(row, item.Title, () =>
            {
                if (Open(item.Window)) popup.IsOpen = false;
                else status.Text = "У приложения нет открытого окна";
            }, Card);
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Margin = new Thickness(0, 0, 0, 4);
            entries.Children.Add(button);
        }
    }

    private static bool Open(nint owner)
    {
        string? app = Native.WindowApp(owner);
        if (app is not null)
        {
            var window = Native.OpenWindows().FirstOrDefault(item => item.Path.Equals(app, StringComparison.OrdinalIgnoreCase));
            if (window is not null && Native.FocusWindow(window.Handle)) return true;
        }
        return false;
    }
}

internal static class TraySnapshot
{
    internal sealed record Item(nint Window, uint Id, string Title, bool Pinned, ImageSource Icon);

    internal static Item[] Read()
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
        if (icon.Window == 0 || icon.Preference == 1 || !IsWindow(icon.Window)) return 0;
        try
        {
            string path = Native.Executable(icon.Window) ?? "";
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
                lock (items) items[(icon.Window, icon.Id)] = new(icon.Window, icon.Id, title, icon.Preference == 2, image);
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
