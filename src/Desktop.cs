using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using static LumaShell.Visuals;

namespace LumaShell;

internal sealed class Desktop
{
    internal const int BarHeight = 30;
    private static readonly Brush Glass = Paint("#E9222329");
    private readonly TextBlock activeApp = Text("Проводник", 13, bold: true);
    private readonly TextBlock feedback = Text("", 12, Blue);
    private readonly StackPanel dockRow = new() { Orientation = Orientation.Horizontal };
    private readonly Border dock;
    private readonly List<(Pin Pin, Border Dot)> indicators = [];
    private readonly Preferences settings;
    private readonly bool animate = SystemParameters.ClientAreaAnimation;
    private readonly MenuTray tray;
    private readonly TrayIcons trayIcons;
    private readonly List<DockSlot> slots = [];
    private readonly TranslateTransform dockSlide = new();
    private readonly ScaleTransform glassWidth = new();
    private readonly Action openLauncher;
    private double? hoverX;
    private bool rendering;
    private bool targetsDirty;
    private bool scanningWindows;
    private TimeSpan? lastFrame;
    private double targetGlassWidth = 1;
    private bool dockVisible;
    private readonly List<Pin> defaults;
    private readonly Dictionary<string, Pin> installed = new(StringComparer.OrdinalIgnoreCase);
    private List<Native.OpenWindow> windows = [];
    private string[] dynamicPaths = [];
    private nint foreground;

    internal Window MenuBar { get; } = Overlay("Luma • Строка меню", 800, BarHeight);
    internal Window Launcher { get; } = Overlay("Luma • Dock", 1040, 119);
    internal bool Interacting => dock.IsMouseOver || Launcher.IsActive;

    internal Desktop(Preferences settings, Action<IslandTab> openIsland, Action capture, Action screenshotFolder, Action exit,
        Action nativeTray, Action toggleTaskbar, Action update, Updates releases, Action openLauncher, Action toggleWin)
    {
        this.settings = settings;
        this.openLauncher = openLauncher;
        var menu = new DockPanel { Margin = new Thickness(12, 0, 15, 0) };
        trayIcons = new TrayIcons(exit);
        tray = new MenuTray(() => openIsland(IslandTab.Notifications), trayIcons.Show, update, releases);
        DockPanel.SetDock(tray.View, Dock.Right);
        menu.Children.Add(tray.View);
        DockPanel.SetDock(trayIcons.View, Dock.Right);
        menu.Children.Add(trayIcons.View);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(Menu(Icon("M2,13 L8,2 L14,13 M5,10 L11,10", 16), "Luma", () =>
        [
            Entry("Приложения Luma", openLauncher), Entry("Открыть остров", () => openIsland(IslandTab.Home)),
            Entry("Временно показать панель Windows", nativeTray),
            Entry(settings.WinLauncher ? "Вернуть меню Windows на Win" : "Открывать приложения Luma по Win", toggleWin),
            Entry("Обновления Luma", update), Entry("Параметры Windows", () => Launch("ms-settings:")),
            new Separator(), Entry("Выйти из Luma", exit)
        ]));
        activeApp.MaxWidth = 124;
        left.Children.Add(Menu(activeApp, "Активное приложение", WindowEntries));
        left.Children.Add(Menu("Файл", () =>
        [
            Entry("Проводник", () => Launch("explorer.exe")), Entry("Сделать снимок", capture),
            Entry("Папка снимков Luma", screenshotFolder)
        ]));
        left.Children.Add(Menu("Правка", () =>
        [Entry("Буфер Luma", () => openIsland(IslandTab.Clipboard)), Entry("Настройки буфера Windows", () => Launch("ms-settings:clipboard"))]));
        left.Children.Add(Menu("Вид", () =>
        [Entry(settings.HideWindowsTaskbar ? "Вернуть панель задач Windows" : "Скрыть панель задач Windows", toggleTaskbar),
            Entry("Персонализация", () => Launch("ms-settings:personalization")),
            Entry("Параметры панели задач", () => Launch("ms-settings:taskbar"))]));
        left.Children.Add(Menu("Окно", WindowEntries));
        left.Children.Add(Menu("Dock", () =>
        [Entry("Закрепить приложение…", PickPin), Entry("Все приложения", () => Launch("explorer.exe", "shell:AppsFolder"))]));
        menu.Children.Add(left);
        MenuBar.Content = new Border
        {
            Background = Gradient("#D91C1F27", "#BD2F323B"), BorderBrush = Paint("#30FFFFFF"),
            BorderThickness = new Thickness(0, 0, 0, 1), Child = menu
        };

        defaults =
        [
            new("Проводник", "explorer.exe", "", ["explorer.exe"], "#FF80E3FF", "#FF1360DA", "M2,3 L18,3 L18,17 L2,17 Z M8,3 Q10,8 8,12 M5,8 L5,9 M14,8 L14,9 M5,13 Q10,16 15,12"),
            new("Приложения", "explorer.exe", "shell:AppsFolder", [], "#FFE4E9F4", "#FF687388", "M2,3 H7 V8 H2 Z M12,3 H17 V8 H12 Z M2,12 H7 V17 H2 Z M12,12 H17 V17 H12 Z"),
            new("Браузер", "https://www.bing.com/", "", ["msedge.exe", "chrome.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "arc.exe"], "#FF68DFFF", "#FF185CD2", "M18,10 A8,8 0 1 1 2,10 A8,8 0 1 1 18,10 M6,14 L8.5,8.5 L14,6 L11.5,11.5 Z"),
            new("Блокнот", "notepad.exe", "", ["notepad.exe"], "#FFFFC653", "#FFE69A37", "M4,3 L16,3 L16,17 L4,17 Z M7,7 L13,7 M7,10 L13,10 M7,13 L11,13"),
            new("Терминал", "cmd.exe", "", ["WindowsTerminal.exe", "cmd.exe", "OpenConsole.exe"], "#FF454653", "#FF101118", "M2,4 L18,4 L18,16 L2,16 Z M5,7 L8,10 L5,13 M10,13 L14,13"),
            new("Параметры", "ms-settings:", "", ["SystemSettings.exe"], "#FF9CA5B4", "#FF4D5361", "M10,3 L10,17 M3,10 L17,10 M5,5 L15,15 M15,5 L5,15 M14,10 A4,4 0 1 1 6,10 A4,4 0 1 1 14,10")
        ];
        var glass = Surface(Gradient("#85BAC7DE", "#CB29303D"), 23);
        glass.BorderBrush = Paint("#95FFFFFF");
        glass.BorderThickness = new Thickness(1);
        glass.RenderTransformOrigin = new Point(0.5, 0.5);
        glass.RenderTransform = glassWidth;
        glass.IsHitTestVisible = false;
        var frame = new Grid();
        frame.Children.Add(glass);
        frame.Children.Add(new Border { Padding = new Thickness(11, 8, 11, 5), Child = dockRow });
        dock = Surface(Brushes.Transparent, 23, frame);
        dock.HorizontalAlignment = HorizontalAlignment.Center;
        dock.VerticalAlignment = VerticalAlignment.Bottom;
        dock.RenderTransformOrigin = new Point(0.5, 1);
        dock.RenderTransform = dockSlide;
        dock.MouseMove += (_, args) => MovePointer(args.GetPosition(dockRow).X);
        dock.MouseLeave += (_, _) => MovePointer(null);
        dockRow.SizeChanged += (_, _) => { if (hoverX is not null) targetsDirty = true; };
        Launcher.IsVisibleChanged += (_, _) => { if (!Launcher.IsVisible) ResetIcons(); };
        dock.AllowDrop = true;
        dock.DragOver += (_, args) =>
        {
            args.Effects = args.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsExecutable)
                ? DragDropEffects.Link : DragDropEffects.None;
            args.Handled = true;
        };
        dock.Drop += (_, args) =>
        {
            if (args.Data.GetData(DataFormats.FileDrop) is string[] files)
                foreach (var file in files.Where(IsExecutable).Take(6)) AddPin(file);
        };
        var layout = new Grid();
        feedback.HorizontalAlignment = HorizontalAlignment.Center;
        feedback.VerticalAlignment = VerticalAlignment.Top;
        layout.Children.Add(feedback);
        layout.Children.Add(dock);
        Launcher.Content = layout;
        RebuildDock();
    }

    private static MenuItem Entry(string label, Action action)
    {
        var item = new MenuItem { Header = label, Foreground = White, Padding = new Thickness(10, 4, 12, 4) };
        item.Click += (_, _) => action();
        return item;
    }

    private static ContextMenu Context(FrameworkElement target, IEnumerable<Control> entries, PlacementMode placement)
    {
        var menu = new ContextMenu { Background = Glass, Foreground = White, BorderBrush = Line, Padding = new Thickness(4),
            PlacementTarget = target, Placement = placement };
        foreach (var entry in entries) menu.Items.Add(entry);
        return menu;
    }

    private static Button Menu(string text, Func<IEnumerable<Control>> entries) => Menu(Text(text, 12), text, entries);

    private static Button Menu(object label, string name, Func<IEnumerable<Control>> entries)
    {
        Button? button = null;
        button = Button(label, name, () => Context(button!, entries(), PlacementMode.Bottom).IsOpen = true, Brushes.Transparent, 8);
        return button;
    }

    private IEnumerable<Control> WindowEntries()
    {
        foreach (var window in Native.OpenWindows().Take(16))
        {
            var item = Entry(Label(window.Title, 60), () => Native.FocusWindow(window.Handle));
            if (window.Handle == foreground) item.FontWeight = FontWeights.Bold;
            yield return item;
        }
        yield return Entry("Диспетчер задач", () => Launch("taskmgr.exe"));
    }

    private void PickPin()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Закрепить в Dock", Filter = "Приложения|*.exe" };
        if (picker.ShowDialog() == true) AddPin(picker.FileName);
    }

    private void AddPin(string path)
    {
        if (settings.Pins.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        if (settings.Pins.Count >= 6) { feedback.Text = "Можно закрепить до шести своих приложений"; return; }
        settings.Pins.Add(path);
        settings.Save();
        RebuildDock();
    }

    private static bool IsExecutable(string path) => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    internal async void RefreshWindows(nint active)
    {
        if (scanningWindows) return;
        scanningWindows = true;
        try
        {
            var snapshot = await Task.Run(() =>
            {
                var items = Native.OpenWindows();
                var path = items.FirstOrDefault(item => item.Handle == active)?.Path ??
                    (!Native.IsDesktop(active) ? Native.WindowApp(active) : null);
                return (Items: items, ActivePath: path);
            });
            ApplyWindows(active, snapshot.Items, snapshot.ActivePath);
        }
        finally { scanningWindows = false; }
    }

    private void ApplyWindows(nint active, List<Native.OpenWindow> snapshot, string? path)
    {
        foreground = active;
        windows = snapshot;
        activeApp.Text = path is null ? "Проводник" : Installed(path).Label;
        var visible = windows.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !defaults.Any(pin => pin.Matches(path)) && !settings.Pins.Contains(path, StringComparer.OrdinalIgnoreCase))
            .Take(5).ToArray();
        // Keep the order of open apps stable when their Z order changes.
        var ordered = dynamicPaths.Where(path => visible.Contains(path, StringComparer.OrdinalIgnoreCase))
            .Concat(visible.Where(path => !dynamicPaths.Contains(path, StringComparer.OrdinalIgnoreCase))).ToArray();
        if (!dynamicPaths.SequenceEqual(ordered, StringComparer.OrdinalIgnoreCase))
        {
            dynamicPaths = ordered;
            if (!Interacting) RebuildDock();
            else dockDirty = true;
        }
        if (dockDirty && !Interacting) RebuildDock();
        UpdateIndicators();
    }

    private bool dockDirty;

    private void RebuildDock()
    {
        dockDirty = false;
        ResetIcons();
        slots.Clear();
        dockRow.Children.Clear();
        indicators.Clear();
        foreach (var pin in defaults) dockRow.Children.Add(Tile(pin));
        foreach (var path in settings.Pins.Where(IsExecutable)) dockRow.Children.Add(Tile(Installed(path), custom: true));
        if (dynamicPaths.Any(path => !settings.Pins.Contains(path, StringComparer.OrdinalIgnoreCase)))
        {
            AddSeparator();
            foreach (var path in dynamicPaths.Where(path => !settings.Pins.Contains(path, StringComparer.OrdinalIgnoreCase)))
                dockRow.Children.Add(Tile(Installed(path), custom: true));
        }
        AddSeparator();
        dockRow.Children.Add(Tile(new("Корзина", "explorer.exe", "shell:RecycleBinFolder", [], "#FFE6ECF2", "#FF778397",
            "M3,6 L17,6 M7,6 L7,3 L13,3 L13,6 M5,6 L6,17 L14,17 L15,6 M9,9 L9,14 M11,9 L11,14")));
        UpdateIndicators();
        ResizeDock();
    }

    private void AddSeparator() => dockRow.Children.Add(Slot(new Border { Width = 1, Height = 42,
        Background = Paint("#60FFFFFF"), Margin = new Thickness(7, 0, 7, 0) }));

    private Pin Installed(string path)
    {
        if (installed.TryGetValue(path, out var pin)) return pin;
        string name = Path.GetFileNameWithoutExtension(path);
        ImageSource? image = null;
        try
        {
            string? description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) name = description;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is not null)
            {
                image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException) { }
        pin = new(Label(name, 55), path, "", [], "#FF7482A4", "#FF33415F", "M3,4 H17 V16 H3 Z M3,8 H17", image);
        installed[path] = pin;
        return pin;
    }

    private FrameworkElement Tile(Pin pin, bool custom = false)
    {
        UIElement icon = pin.Image is null ? Icon(pin.Glyph, 28) : new Image { Source = pin.Image, Width = 37, Height = 37 };
        var tile = Surface(Gradient(pin.Light, pin.Dark), 12, icon);
        tile.Width = tile.Height = 49;
        tile.BorderBrush = Paint("#4BFFFFFF");
        tile.BorderThickness = new Thickness(1);
        var dot = Surface(White, 2);
        dot.Width = dot.Height = 4;
        dot.Margin = new Thickness(0, 5, 0, 0);
        dot.Visibility = Visibility.Hidden;
        indicators.Add((pin, dot));
        var column = new StackPanel();
        column.Children.Add(tile);
        column.Children.Add(dot);
        var button = Button(column, pin.Label, () => Activate(pin), Brushes.Transparent, 4);
        button.ToolTip = new ToolTip { Content = pin.Label, Placement = PlacementMode.Top, VerticalOffset = -18 };
        var scale = new ScaleTransform(1, 1);
        tile.RenderTransform = scale;
        tile.RenderTransformOrigin = new Point(0.5, 1);
        button.PreviewMouseRightButtonUp += (_, args) =>
        {
            var entries = windows.Where(item => pin.Matches(item.Path)).Take(8)
                .Select(item => (Control)Entry(Label(item.Title, 58), () => Native.FocusWindow(item.Handle))).ToList();
            entries.Add(Entry("Открыть", () => OpenPin(pin)));
            if (custom)
            {
                bool pinned = settings.Pins.Contains(pin.Target, StringComparer.OrdinalIgnoreCase);
                entries.Add(Entry(pinned ? "Открепить" : "Закрепить в Dock", () =>
                {
                    if (!pinned) AddPin(pin.Target);
                    else
                    {
                        settings.Pins.RemoveAll(path => path.Equals(pin.Target, StringComparison.OrdinalIgnoreCase));
                        settings.Save();
                        RebuildDock();
                    }
                }));
            }
            Context(button, entries, PlacementMode.Top).IsOpen = true;
            args.Handled = true;
        };
        return Slot(button, scale);
    }

    private Grid Slot(FrameworkElement element, ScaleTransform? scale = null)
    {
        var slot = new Grid();
        var offset = new TranslateTransform();
        element.RenderTransform = offset;
        slot.Children.Add(element);
        slots.Add(new(slot, scale, offset));
        return slot;
    }

    private bool OpenPin(Pin pin)
    {
        if (pin.Arguments != "shell:AppsFolder") return Launch(pin.Target, pin.Arguments);
        openLauncher();
        return true;
    }

    private void Activate(Pin pin)
    {
        feedback.Text = "";
        var matches = windows.Where(item => pin.Matches(item.Path)).ToArray();
        var match = matches.FirstOrDefault(item => item.Handle != foreground) ?? matches.FirstOrDefault();
        if (match is not null && Native.FocusWindow(match.Handle)) return;
        if (!OpenPin(pin)) feedback.Text = "Windows не удалось открыть приложение";
    }

    private void UpdateIndicators()
    {
        foreach (var (pin, dot) in indicators)
        {
            bool running = windows.Any(item => pin.Matches(item.Path));
            dot.Visibility = running ? Visibility.Visible : Visibility.Hidden;
            dot.Opacity = running && windows.Any(item => item.Handle == foreground && pin.Matches(item.Path)) ? 1 : 0.62;
        }
    }

    private void MovePointer(double? x)
    {
        if (!animate || x == hoverX) return;
        hoverX = x;
        targetsDirty = true;
        if (rendering) return;
        rendering = true;
        CompositionTarget.Rendering += AnimateIcons;
    }

    private void CalculateTargets()
    {
        targetsDirty = false;
        double total = 0;
        foreach (var tile in slots)
        {
            if (tile.Scale is null) continue;
            double center = tile.Anchor.TranslatePoint(new Point(tile.Anchor.ActualWidth / 2, 0), dockRow).X;
            double distance = hoverX is { } pointer ? (pointer - center) / 57 : double.PositiveInfinity;
            tile.Growth = 0.38 * Math.Exp(-0.5 * distance * distance);
            total += tile.Growth * 49;
        }
        double prefix = -total / 2;
        foreach (var tile in slots)
        {
            double extra = tile.Growth * 49;
            tile.TargetX = prefix + extra / 2;
            prefix += extra;
        }
        targetGlassWidth = 1 + total / Math.Max(1, dock.ActualWidth);
    }

    private void AnimateIcons(object? sender, EventArgs args)
    {
        if (args is not RenderingEventArgs frame || lastFrame == frame.RenderingTime) return;
        double elapsed = lastFrame is { } previous ? (frame.RenderingTime - previous).TotalMilliseconds : 8;
        lastFrame = frame.RenderingTime;
        double blend = 1 - Math.Exp(-Math.Clamp(elapsed, 0, 64) / 52);
        if (targetsDirty) CalculateTargets();
        bool changing = false;
        foreach (var tile in slots)
        {
            double scale = 1 + tile.Growth, x = tile.TargetX, y = -tile.Growth * 16;
            if (tile.Scale is { } transform)
            {
                changing |= Math.Abs(transform.ScaleX - scale) > 0.002;
                transform.ScaleX = transform.ScaleY = Approach(transform.ScaleX, scale, 0.002, blend);
            }
            changing |= Math.Abs(tile.Offset.X - x) > 0.1 || Math.Abs(tile.Offset.Y - y) > 0.1;
            tile.Offset.X = Approach(tile.Offset.X, x, 0.1, blend);
            tile.Offset.Y = Approach(tile.Offset.Y, y, 0.1, blend);
        }
        changing |= Math.Abs(glassWidth.ScaleX - targetGlassWidth) > 0.002;
        glassWidth.ScaleX = Approach(glassWidth.ScaleX, targetGlassWidth, 0.002, blend);
        if (!changing) StopRendering();
    }

    private static double Approach(double current, double target, double precision, double blend) => Math.Abs(target - current) <= precision
        ? target : current + (target - current) * blend;

    private void StopRendering()
    {
        if (rendering) CompositionTarget.Rendering -= AnimateIcons;
        rendering = false;
        lastFrame = null;
    }

    private void ResetIcons()
    {
        StopRendering();
        hoverX = null;
        glassWidth.ScaleX = 1;
        foreach (var tile in slots)
        {
            if (tile.Scale is { } scale) scale.ScaleX = scale.ScaleY = 1;
            tile.Offset.X = tile.Offset.Y = 0;
        }
    }

    internal void SetDockVisible(bool visible, bool immediate)
    {
        if (immediate && !visible && Launcher.IsVisible) Launcher.Hide();
        if (dockVisible == visible) return;
        dockVisible = visible;
        if (visible && !Launcher.IsVisible) Launcher.Show();
        if (!animate || immediate)
        {
            dockSlide.BeginAnimation(TranslateTransform.YProperty, null);
            dock.BeginAnimation(UIElement.OpacityProperty, null);
            if (!visible && Launcher.IsVisible) Launcher.Hide();
            return;
        }
        var duration = TimeSpan.FromMilliseconds(visible ? 240 : 165);
        dockSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(visible ? 10 : dockSlide.Y, visible ? 0 : 13, duration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        var fade = new DoubleAnimation(visible ? 0.25 : dock.Opacity, visible ? 1 : 0, duration);
        if (!visible) fade.Completed += (_, _) => { if (!dockVisible && Launcher.IsVisible) Launcher.Hide(); };
        dock.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    internal static bool Launch(string target, string arguments = "")
    {
        try { Process.Start(new ProcessStartInfo(target, arguments) { UseShellExecute = true })?.Dispose(); return true; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException) { return false; }
    }

    internal void RefreshClock() => tray.Refresh();
    internal void RefreshTray() => trayIcons.Refresh();

    internal void PlaceMenu(Rect area)
    {
        MenuBar.Width = area.Width;
        MenuBar.Left = area.Left;
        MenuBar.Top = area.Top;
    }

    internal void PlaceDock(Rect area)
    {
        Launcher.Width = Math.Min(1040, area.Width);
        Launcher.Left = area.Left + (area.Width - Launcher.Width) / 2;
        Launcher.Top = area.Bottom - Launcher.Height - 5;
        ResizeDock();
    }

    private void ResizeDock()
    {
        double desired = indicators.Count * 57 + 62;
        double scale = Math.Clamp((Launcher.Width - 12) / (desired + 55), 0.1, 1);
        dock.LayoutTransform = scale < 1 ? new ScaleTransform(scale, scale) : Transform.Identity;
    }

    private sealed record Pin(string Label, string Target, string Arguments, string[] Processes,
        string Light, string Dark, string Glyph, ImageSource? Image = null)
    {
        internal bool Matches(string path) => string.Equals(Target, path, StringComparison.OrdinalIgnoreCase) ||
            Processes.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
    }

    private sealed record DockSlot(Grid Anchor, ScaleTransform? Scale, TranslateTransform Offset)
    {
        internal double Growth { get; set; }
        internal double TargetX { get; set; }
    }
}
