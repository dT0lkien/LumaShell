using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static LumaShell.Visuals;

namespace LumaShell;

internal sealed class Desktop
{
    internal const int BarHeight = 30;
    private const double IconSize = 56;
    private const double SlotSize = IconSize + 8;
    private const string BrowserLink = "https://www.bing.com/";
    private readonly TextBlock activeApp = Text("Проводник", 13, bold: true);
    private readonly TextBlock feedback = Text("", 12, Blue);
    private readonly StackPanel dockRow = new() { Orientation = Orientation.Horizontal };
    private readonly Border dock;
    private readonly List<(Pin Pin, Border Dot)> indicators = [];
    private readonly HashSet<string> activatingPins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Preferences settings;
    private readonly bool animate = SystemParameters.ClientAreaAnimation;
    private readonly MenuTray tray;
    private readonly TrayIcons trayIcons;
    private readonly ExplorerAppearance explorerAppearance = new();
    private readonly List<DockSlot> slots = [];
    private readonly TranslateTransform dockSlide = new();
    private readonly ScaleTransform glassWidth = new();
    private readonly Popup downloads = new() { Placement = PlacementMode.Top, StaysOpen = false, AllowsTransparency = true };
    private CancellationTokenSource? loadingDownloads;
    private QuickLook? downloadPreview;
    private readonly Action openLauncher;
    private readonly Action<string?> openFinder;
    private double? hoverX;
    private bool rendering;
    private bool targetsDirty;
    private bool scanningWindows;
    private TimeSpan? lastFrame;
    private double targetGlassWidth = 1;
    private bool dockVisible;
    private readonly List<Pin> defaults;
    private Pin browser = new("Браузер по умолчанию", BrowserLink, "", [], "#FF596E87", "#FF303A48",
        "M18,10 A8,8 0 1 1 2,10 A8,8 0 1 1 18,10 M2,10 H18 M10,2 C5,5 5,15 10,18 M10,2 C15,5 15,15 10,18");
    private readonly Dictionary<string, Pin> installed = new(StringComparer.OrdinalIgnoreCase);
    private List<Native.OpenWindow> windows = [];
    private readonly Dictionary<string, nint> recentWindows = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<nint, (uint Process, string Id)> appIdentities = [];
    private string[] dynamicPaths = [];
    private nint foreground;
    private nint finderHandle;
    private bool openingTaskManager;

    internal Window MenuBar { get; } = Overlay("Luma • Строка меню", 800, BarHeight);
    internal Window DockWindow { get; } = Overlay("Luma • Dock", 1120, 145);
    internal bool Interacting => dock.IsMouseOver || DockWindow.IsActive || downloads.IsOpen || downloadPreview is not null;
    internal bool UsingNativeTray => trayIcons.UsingNativeTray;
    internal bool TrayIsOpen => trayIcons.IsOpen;
    internal Task<bool> InvokeTrayIcon(string executable) => trayIcons.InvokeByPath(executable);

    internal Desktop(Preferences settings, Action<IslandTab> openIsland, Action capture, Action screenshotFolder, Action exit,
        Action nativeTray, Action toggleTaskbar, Action update, Updates releases, Action openLauncher, Action toggleWin,
        Action toggleStartup, Action<string?> openFinder, Action applyWallpaper, Action toggleFolderRouting,
        Action toggleAutoInstall, Action openSessionLog)
    {
        this.settings = settings;
        this.openLauncher = openLauncher;
        this.openFinder = openFinder;
        var menu = new DockPanel { Margin = new Thickness(12, 0, 15, 0) };
        trayIcons = new TrayIcons(exit, nativeTray);
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
            Entry(settings.AutoStart ? "Выключить автозапуск Luma" : "Запускать Luma при входе", toggleStartup),
            Entry("Обновления Luma", update),
            Entry(settings.AutoInstallUpdates ? "Выключить установку в простое" : "Устанавливать обновления в простое", toggleAutoInstall),
            Entry("Журнал Luma", openSessionLog), Entry("Параметры Windows", () => Launch("ms-settings:")),
            new Separator(), Entry("Выйти из Luma", exit)
        ]));
        activeApp.MaxWidth = 124;
        left.Children.Add(Menu(activeApp, "Активное приложение", WindowEntries));
        left.Children.Add(Menu("Файл", () =>
        [
            Entry("Проводник", () => openFinder(null)), Entry("Проводник Windows", () => Launch("explorer.exe")),
            Entry("Сделать снимок", capture),
            Entry("Папка снимков Luma", screenshotFolder)
        ]));
        left.Children.Add(Menu("Правка", () =>
        [Entry("Буфер Luma", () => openIsland(IslandTab.Clipboard)), Entry("Настройки буфера Windows", () => Launch("ms-settings:clipboard"))]));
        left.Children.Add(Menu("Вид", () =>
        [Entry(settings.HideWindowsTaskbar ? "Вернуть панель задач Windows" : "Скрыть панель задач Windows", toggleTaskbar),
            Entry(settings.StyleExplorerFrame ? "Вернуть рамку Проводника Windows" : "Оформить рамку Проводника", ToggleExplorerFrame),
            Entry("Установить обои Luma", applyWallpaper),
            Entry(settings.RouteFolders ? "Открывать папки в Проводнике Windows" : "Открывать папки в Luma Finder", toggleFolderRouting),
            Entry("Персонализация", () => Launch("ms-settings:personalization")),
            Entry("Параметры панели задач", () => Launch("ms-settings:taskbar"))]));
        left.Children.Add(Menu("Окно", WindowEntries));
        left.Children.Add(Menu("Dock", () =>
        [Entry("Закрепить приложение…", PickPin), Entry("Все приложения", () => Launch("explorer.exe", "shell:AppsFolder"))]));
        menu.Children.Add(left);
        MenuBar.Content = new Border
        {
            Background = Gradient("#CD2C2E33", "#AC202126"), BorderBrush = Paint("#38FFFFFF"),
            BorderThickness = new Thickness(0, 0, 0, 1), Child = menu
        };

        defaults =
        [
            new("Проводник", "explorer.exe", "", ["explorer.exe"], "#FF80E3FF", "#FF1360DA", "M2,3 L18,3 L18,17 L2,17 Z M8,3 Q10,8 8,12 M5,8 L5,9 M14,8 L14,9 M5,13 Q10,16 15,12"),
            new("Приложения", "explorer.exe", "shell:AppsFolder", [], "#FFE4E9F4", "#FF687388", "M2,3 H7 V8 H2 Z M12,3 H17 V8 H12 Z M2,12 H7 V17 H2 Z M12,12 H17 V17 H12 Z"),
            browser,
            new("Блокнот", "notepad.exe", "", ["notepad.exe"], "#FFFFC653", "#FFE69A37", "M4,3 L16,3 L16,17 L4,17 Z M7,7 L13,7 M7,10 L13,10 M7,13 L11,13"),
            new("Терминал", "cmd.exe", "", ["WindowsTerminal.exe", "cmd.exe", "OpenConsole.exe"], "#FF454653", "#FF101118", "M2,4 L18,4 L18,16 L2,16 Z M5,7 L8,10 L5,13 M10,13 L14,13"),
            new("Параметры", "ms-settings:", "", ["SystemSettings.exe"], "#FF9CA5B4", "#FF4D5361", "M10,3 L10,17 M3,10 L17,10 M5,5 L15,15 M15,5 L5,15 M14,10 A4,4 0 1 1 6,10 A4,4 0 1 1 14,10")
        ];
        var glass = Surface(Gradient("#9D858991", "#AA393B43"), 23);
        glass.BorderBrush = Paint("#6AFFFFFF");
        glass.BorderThickness = new Thickness(1);
        glass.RenderTransformOrigin = new Point(0.5, 0.5);
        glass.RenderTransform = glassWidth;
        glass.IsHitTestVisible = false;
        var frame = new Grid();
        frame.Children.Add(glass);
        frame.Children.Add(new Border { Padding = new Thickness(9, 7, 9, 4), Child = dockRow });
        dock = Surface(Brushes.Transparent, 23, frame);
        dock.HorizontalAlignment = HorizontalAlignment.Center;
        dock.VerticalAlignment = VerticalAlignment.Bottom;
        dock.RenderTransformOrigin = new Point(0.5, 1);
        dock.RenderTransform = dockSlide;
        dock.MouseMove += (_, args) => MovePointer(args.GetPosition(dockRow).X);
        dock.MouseLeave += (_, _) => MovePointer(null);
        dockRow.SizeChanged += (_, _) => { if (hoverX is not null) targetsDirty = true; };
        DockWindow.IsVisibleChanged += (_, _) =>
        {
            if (DockWindow.IsVisible) return;
            downloads.IsOpen = false;
            ResetIcons();
        };
        downloads.Closed += (_, _) =>
        {
            var previous = loadingDownloads;
            loadingDownloads = null;
            previous?.Cancel();
        };
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
        DockWindow.Content = layout;
        RebuildDock();
    }

    private static MenuItem Entry(string label, Action action) => MenuEntry(label, action);

    private static ContextMenu Context(FrameworkElement target, IEnumerable<Control> entries, PlacementMode placement)
    {
        var menu = PopupMenu(target, placement);
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
            var item = Entry(Label(window.Title, 60), async () =>
            {
                if (Native.IsTaskManager(window.Path)) await OpenTaskManager(window.Handle);
                else await Native.FocusWindow(window.Handle);
            });
            if (window.Handle == foreground) item.FontWeight = FontWeights.Bold;
            yield return item;
        }
        yield return Entry("Диспетчер задач", async () => await OpenTaskManager());
    }

    private async Task OpenTaskManager(nint window = 0)
    {
        if (openingTaskManager) return;
        openingTaskManager = true;
        try
        {
            await DockWindow.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Input);
            if (!await Native.OpenTaskManager(window))
                feedback.Text = "Не удалось открыть Диспетчер задач. Нажми Ctrl+Shift+Esc.";
        }
        finally { openingTaskManager = false; }
    }

    private void PickPin()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Закрепить в Dock", Filter = "Приложения|*.exe" };
        if (picker.ShowDialog() == true) AddPin(picker.FileName);
    }

    private void AddPin(string path)
    {
        if (PinnedTarget(path, path) is not null) return;
        feedback.Text = AddPin(path, null) ?? "";
    }

    internal bool IsPinned(Launcher.AppEntry app) => PinnedTarget(app.Target, app.Location) is not null;

    internal string? TogglePin(Launcher.AppEntry app)
    {
        if (PinnedTarget(app.Target, app.Location) is { } target) return RemovePin(target);
        if (Launcher.OpensFinder(app.Target) || defaults.Any(pin => pin.Matches(app.Target) || app.Location is { } path && pin.Matches(path)))
            return "Приложение уже есть в Dock";
        return AddPin(app.Target, new DockPinDetails(app.Name, app.Location), app.Icon);
    }

    private string? PinnedTarget(string target, string? location) => settings.Pins.FirstOrDefault(pin =>
        pin.Equals(target, StringComparison.OrdinalIgnoreCase) || location is not null &&
        (pin.Equals(location, StringComparison.OrdinalIgnoreCase) || settings.PinDetails.TryGetValue(pin, out var details) &&
            string.Equals(details?.Location, location, StringComparison.OrdinalIgnoreCase)));

    private string? AddPin(string target, DockPinDetails? details, ImageSource? icon = null)
    {
        if (!CanPin(target)) return "Приложение больше недоступно";
        if (settings.Pins.Count >= 6) return "В Dock можно закрепить до шести своих приложений";
        settings.Pins.Add(target);
        if (details is not null) settings.PinDetails[target] = details;
        if (!settings.Save())
        {
            settings.Pins.Remove(target);
            settings.PinDetails.Remove(target);
            return "Не удалось сохранить Dock";
        }
        installed.Remove(target);
        if (details is not null && icon is not null) installed[target] = CatalogPin(target, details, icon);
        RebuildDock();
        return null;
    }

    private string? RemovePin(string target)
    {
        int index = settings.Pins.FindIndex(path => path.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        string key = settings.Pins[index];
        settings.Pins.RemoveAt(index);
        settings.PinDetails.Remove(key, out var details);
        if (!settings.Save())
        {
            settings.Pins.Insert(index, key);
            if (details is not null) settings.PinDetails[key] = details;
            return "Не удалось сохранить Dock";
        }
        installed.Remove(key);
        RebuildDock();
        return null;
    }

    private static bool IsExecutable(string path) => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
    private static bool CanPin(string path) => Launcher.IsShellApp(path) || IsExecutable(path) ||
        path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    private bool CustomPinMatches(string path) => settings.Pins.Any(pin =>
        pin.Equals(path, StringComparison.OrdinalIgnoreCase) || settings.PinDetails.TryGetValue(pin, out var details) &&
        string.Equals(details?.Location, path, StringComparison.OrdinalIgnoreCase));

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
                var identities = items.Select(item => (item.Handle, item.Process, Id: Native.WindowApplicationId(item.Handle)))
                    .Where(item => item.Id is not null).ToDictionary(item => item.Handle, item => (item.Process, item.Id!));
                return (Items: items, ActivePath: path, Identities: identities, Browser: Native.DefaultBrowser());
            });
            appIdentities = snapshot.Identities;
            UpdateBrowser(snapshot.Browser);
            ApplyWindows(active, snapshot.Items, snapshot.ActivePath);
        }
        finally { scanningWindows = false; }
    }

    private void UpdateBrowser(string? executable)
    {
        if (string.Equals(browser.Location, executable, StringComparison.OrdinalIgnoreCase)) return;
        int index = defaults.IndexOf(browser);
        browser = browser with { Location = executable };
        defaults[index] = browser;
        dockDirty = true;
    }

    private void ApplyWindows(nint active, List<Native.OpenWindow> snapshot, string? path)
    {
        foreground = active;
        windows = snapshot;
        foreach (string key in recentWindows.Keys.Where(key =>
            !windows.Any(window => window.Handle == recentWindows[key] && window.Path.Equals(key, StringComparison.OrdinalIgnoreCase))).ToArray())
            recentWindows.Remove(key);
        if (active != 0 && path is not null && windows.Any(window => window.Handle == active))
            recentWindows[path] = active;
        RefreshExplorerFrame();
        activeApp.Text = active != 0 && active == finderHandle ? "Luma Finder" : path is null ? "Проводник" : Installed(path).Label;
        var visible = windows.Where(item => !defaults.Any(pin => pin.Matches(item.Path)) && !CustomPinMatches(item.Path) &&
                !(appIdentities.TryGetValue(item.Handle, out var app) && app.Process == item.Process &&
                    settings.Pins.Contains(@"shell:AppsFolder\" + app.Id, StringComparer.OrdinalIgnoreCase)))
            .Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Keep existing Dock slots when another app gains focus.
        var ordered = dynamicPaths.Where(path => visible.Contains(path, StringComparer.OrdinalIgnoreCase))
            .Concat(visible.Where(path => !dynamicPaths.Contains(path, StringComparer.OrdinalIgnoreCase))).Take(5).ToArray();
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

    private void ToggleExplorerFrame()
    {
        settings.StyleExplorerFrame = !settings.StyleExplorerFrame;
        settings.Save();
        RefreshExplorerFrame();
    }

    private void RefreshExplorerFrame()
    {
        if (settings.Chrome && settings.StyleExplorerFrame && !SystemParameters.HighContrast)
            explorerAppearance.Refresh(windows, foreground);
        else RestoreExplorerFrame();
    }

    internal void RestoreExplorerFrame() => explorerAppearance.Restore();

    private void RebuildDock()
    {
        dockDirty = false;
        ResetIcons();
        slots.Clear();
        dockRow.Children.Clear();
        indicators.Clear();
        foreach (var pin in defaults)
        {
            if (pin == browser && browser.Location is { } path)
            {
                if (CustomPinMatches(path)) continue;
                var app = Installed(path);
                dockRow.Children.Add(Tile(browser with { Image = app.Image, Label = app.Label }));
            }
            else dockRow.Children.Add(Tile(pin));
        }
        foreach (var path in settings.Pins.Where(CanPin)) dockRow.Children.Add(Tile(Installed(path), custom: true));
        if (dynamicPaths.Any(path => !CustomPinMatches(path)))
        {
            AddSeparator();
            foreach (var path in dynamicPaths.Where(path => !CustomPinMatches(path)))
                dockRow.Children.Add(Tile(Installed(path), custom: true));
        }
        AddSeparator();
        dockRow.Children.Add(Tile(new("Загрузки", "explorer.exe", "shell:Downloads", [], "#FF75D8FF", "#FF288FE0", "M2,5 H9 L11,7 H18 V17 H2 Z")));
        dockRow.Children.Add(Tile(new("Корзина", "explorer.exe", "shell:RecycleBinFolder", [], "#FFE6ECF2", "#FF778397",
            "M3,6 L17,6 M7,6 L7,3 L13,3 L13,6 M5,6 L6,17 L14,17 L15,6 M9,9 L9,14 M11,9 L11,14")));
        UpdateIndicators();
        ResizeDock();
    }

    private void AddSeparator() => dockRow.Children.Add(Slot(new Border { Width = 1, Height = 34,
        Background = Paint("#45FFFFFF"), Margin = new Thickness(8, 0, 8, 0) }));

    private Pin Installed(string path)
    {
        if (installed.TryGetValue(path, out var pin)) return pin;
        if (settings.PinDetails.TryGetValue(path, out var details) && details is not null)
        {
            pin = CatalogPin(path, details);
            installed[path] = pin;
            LoadIcon(path);
            return pin;
        }
        string name = Path.GetFileNameWithoutExtension(path);
        try
        {
            string? description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) name = description;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException) { }
        pin = new(Label(name, 55), path, "", [], "#FF7482A4", "#FF33415F", "M3,4 H17 V16 H3 Z M3,8 H17");
        installed[path] = pin;
        LoadIcon(path);
        return pin;
    }

    private async void LoadIcon(string path)
    {
        var image = await Task.Run(() => IsExecutable(path) ? DockArt.Extract(path) : Launcher.ShellIcon(path));
        if (image is null || !installed.TryGetValue(path, out var pin)) return;
        installed[path] = pin with { Image = image };
        dockDirty = true;
    }

    private static Pin CatalogPin(string target, DockPinDetails details, ImageSource? image = null) =>
        new(Label(details.Name ?? target, 55), target, "", [], "#FF7482A4", "#FF33415F",
            "M3,4 H17 V16 H3 Z M3,8 H17", image, details.Location);

    private FrameworkElement Tile(Pin pin, bool custom = false)
    {
        var artwork = pin.Image ?? DockArt.Builtin(pin.Target, pin.Arguments);
        UIElement icon = artwork is null ? Icon(pin.Glyph, 36) : new Image
            { Source = artwork, Width = IconSize, Height = IconSize, Stretch = Stretch.Uniform };
        // Native artwork supplies its own silhouette; do not wrap it in a generic colored tile.
        var tile = Surface(artwork is null ? Gradient(pin.Light, pin.Dark) : Brushes.Transparent, 13, icon);
        tile.Width = tile.Height = IconSize;
        RenderOptions.SetBitmapScalingMode(tile, BitmapScalingMode.HighQuality);
        var dot = Surface(White, 2.5);
        dot.Width = dot.Height = 5;
        dot.Margin = new Thickness(0, 4, 0, 0);
        dot.Visibility = Visibility.Hidden;
        indicators.Add((pin, dot));
        var column = new StackPanel();
        column.Children.Add(tile);
        column.Children.Add(dot);
        var bounce = new TranslateTransform();
        Button? button = null;
        button = Button(column, pin.Label, async () =>
        {
            if (pin.Arguments == "shell:Downloads")
            {
                ShowDownloads(button!);
                return;
            }
            if (!await Activate(pin) || !animate) return;
            var jump = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(540), FillBehavior = FillBehavior.Stop };
            jump.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            jump.KeyFrames.Add(new EasingDoubleKeyFrame(-18, KeyTime.FromPercent(0.34), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            jump.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.66), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            jump.KeyFrames.Add(new EasingDoubleKeyFrame(-5, KeyTime.FromPercent(0.83), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            jump.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            bounce.BeginAnimation(TranslateTransform.YProperty, jump);
        }, Brushes.Transparent, 4);
        button.Width = SlotSize;
        var plainButton = new FrameworkElementFactory(typeof(Border));
        plainButton.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        plainButton.AppendChild(presenter);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = plainButton };
        button.ToolTip = new ToolTip { Content = pin.Label, Placement = PlacementMode.Top, VerticalOffset = -25,
            Background = Paint("#EC30343C"), Foreground = White, BorderBrush = Paint("#45FFFFFF"),
            Padding = new Thickness(10, 5, 10, 5), FontSize = 12 };
        ToolTipService.SetInitialShowDelay(button, 120);
        var scale = new ScaleTransform(1, 1);
        var lift = new TranslateTransform();
        var motion = new TransformGroup();
        motion.Children.Add(scale);
        motion.Children.Add(lift);
        motion.Children.Add(bounce);
        tile.RenderTransform = motion;
        tile.RenderTransformOrigin = new Point(0.5, 1);
        button.PreviewMouseRightButtonUp += (_, args) =>
        {
            var entries = windows.Where(item => MatchesWindow(pin, item)).Take(8)
                .Select(item => (Control)Entry(Label(item.Title, 58), async () => await Activate(pin, item))).ToList();
            entries.Add(pin.Arguments == "shell:Downloads" ? Entry("Открыть в Finder", () => OpenPin(pin))
                : Entry("Открыть", async () => await Activate(pin)));
            if (custom || pin.Target == BrowserLink && pin.Location is not null)
            {
                string target = pin.Target == BrowserLink ? pin.Location ?? pin.Target : pin.Target;
                bool pinned = settings.Pins.Contains(target, StringComparer.OrdinalIgnoreCase);
                entries.Add(Entry(pinned ? "Открепить" : "Закрепить в Dock", () =>
                {
                    if (!pinned) AddPin(target);
                    else feedback.Text = RemovePin(target) ?? "";
                }));
            }
            Context(button, entries, PlacementMode.Top).IsOpen = true;
            args.Handled = true;
        };
        return Slot(button, scale, lift);
    }

    private async void ShowDownloads(Button anchor)
    {
        if (downloads.IsOpen)
        {
            downloads.IsOpen = false;
            return;
        }
        const double width = 328;
        var rows = new StackPanel();
        var pending = Text("Читаем папку…", 12, Muted);
        pending.Margin = new Thickness(6, 5, 0, 6);
        rows.Children.Add(pending);
        var panel = new StackPanel { Width = width };
        var title = Text("Загрузки", 16, bold: true);
        title.Margin = new Thickness(5, 0, 0, 8);
        panel.Children.Add(title);
        panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        string folder = FinderFiles.Downloads;
        var open = Button("Открыть в Finder", () => { downloads.IsOpen = false; openFinder(folder); });
        open.HorizontalContentAlignment = HorizontalAlignment.Left;
        open.Margin = new Thickness(0, 7, 0, 0);
        panel.Children.Add(open);
        var frame = Surface(Gradient("#F435373E", "#FC212328"), 18, panel);
        frame.Padding = new Thickness(11);
        frame.BorderBrush = Paint("#60FFFFFF");
        frame.BorderThickness = new Thickness(1);
        downloads.Child = frame;
        downloads.PlacementTarget = anchor;
        downloads.HorizontalOffset = (anchor.ActualWidth - width - 24) / 2;
        downloads.VerticalOffset = -7;
        downloads.IsOpen = true;
        Entrance(frame, 6);
        var request = new CancellationTokenSource();
        loadingDownloads = request;
        try
        {
            var files = await Task.Run(() => FinderFiles.ReadFolder(folder, request.Token)
                .OrderByDescending(file => file.Modified).Take(60).ToArray());
            if (request.IsCancellationRequested || loadingDownloads != request) return;
            rows.Children.Clear();
            if (files.Length == 0) rows.Children.Add(Text("Папка пуста", 12, Muted));
            foreach (var file in files)
            {
                var row = new DockPanel();
                var image = new Image { Source = file.Icon, Width = 32, Height = 32, Margin = new Thickness(0, 0, 9, 0),
                    Stretch = Stretch.Uniform };
                DockPanel.SetDock(image, Dock.Left);
                row.Children.Add(image);
                var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                labels.Children.Add(Text(file.Name, 13, bold: true));
                labels.Children.Add(Text(file.IsDirectory ? file.Kind : $"{file.Kind} · {file.SizeLabel}", 11, Muted));
                row.Children.Add(labels);
                var entry = Button(row, file.Name, () =>
                {
                    downloads.IsOpen = false;
                    if (file.IsDirectory) openFinder(file.Path);
                    else if (!Launch(file.Path)) feedback.Text = "Не удалось открыть загрузку";
                }, Brushes.Transparent);
                entry.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                MakeDownloadDraggable(entry, file);
                entry.PreviewKeyDown += (_, args) =>
                {
                    if (args.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None) return;
                    args.Handled = true;
                    PreviewDownload(file);
                };
                rows.Children.Add(entry);
                if (rows.Children.Count <= 16 && QuickLook.IsImage(file.Extension))
                    _ = LoadDownloadThumbnail(file, image, frame);
            }
            if (rows.Children.OfType<Button>().FirstOrDefault() is { } first && !rows.IsKeyboardFocusWithin)
                first.Focus();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { if (loadingDownloads == request) pending.Text = "Папка недоступна"; }
        finally
        {
            if (loadingDownloads == request) loadingDownloads = null;
            request.Dispose();
        }
    }

    private void PreviewDownload(FinderEntry file)
    {
        if (downloadPreview is { } current) current.Close();
        downloads.IsOpen = false;
        var preview = new QuickLook(DockWindow, file, openFinder);
        downloadPreview = preview;
        preview.Closed += (_, _) => downloadPreview = null;
        preview.Show();
    }

    private void MakeDownloadDraggable(Button entry, FinderEntry file)
    {
        Point? start = null;
        entry.PreviewMouseLeftButtonDown += (_, args) => start = args.GetPosition(entry);
        entry.PreviewMouseLeftButtonUp += (_, _) => start = null;
        entry.PreviewMouseMove += (_, args) =>
        {
            if (start is not { } origin || args.LeftButton != MouseButtonState.Pressed) return;
            var delta = args.GetPosition(entry) - origin;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            start = null;
            args.Handled = true;
            var payload = FinderFiles.FileDropData(file.Path);
            if (entry.IsMouseCaptured) entry.ReleaseMouseCapture();
            downloads.IsOpen = false;
            try { DragDrop.DoDragDrop(DockWindow, payload, DragDropEffects.Copy | DragDropEffects.Move); }
            catch (System.Runtime.InteropServices.ExternalException) { feedback.Text = "Не удалось перетащить файл"; }
        };
    }

    private async Task LoadDownloadThumbnail(FinderEntry file, Image target, Border frame)
    {
        var image = await Task.Run(() => QuickLook.ReadImage(file.Path, 96));
        if (downloads.IsOpen && ReferenceEquals(downloads.Child, frame) && image is not null) target.Source = image;
    }

    private Grid Slot(FrameworkElement element, ScaleTransform? scale = null, TranslateTransform? lift = null)
    {
        var slot = new Grid();
        var offset = new TranslateTransform();
        element.RenderTransform = offset;
        slot.Children.Add(element);
        slots.Add(new(slot, scale, offset, lift));
        return slot;
    }

    private bool OpenPin(Pin pin)
    {
        if (OpensFinder(pin))
        {
            openFinder(pin.Arguments == "shell:Downloads" ? FinderFiles.Downloads : null);
            return true;
        }
        if (Launcher.IsShellApp(pin.Target)) return Launcher.LaunchTarget(pin.Target);
        if (pin.Target == BrowserLink && pin.Location is { } browserPath) return Launch(browserPath);
        if (pin.Arguments != "shell:AppsFolder") return Launch(pin.Target, pin.Arguments);
        openLauncher();
        return true;
    }

    private async Task<bool> Activate(Pin pin, Native.OpenWindow? selected = null)
    {
        if (!activatingPins.Add(pin.Target)) return false;
        feedback.Text = "";
        try
        {
            if (OpensFinder(pin) && selected is null)
            {
                bool newWindow = finderHandle == 0;
                OpenPin(pin);
                return newWindow;
            }

            // Let a Dock context menu finish releasing mouse and keyboard capture first.
            await DockWindow.Dispatcher.InvokeAsync(() =>
            {
                if (DockWindow.IsVisible) DockWindow.Activate();
            }, System.Windows.Threading.DispatcherPriority.Input);
            var match = selected is not null && Native.WindowMatches(selected) ? selected :
                windows.Where(item => MatchesWindow(pin, item) && Native.WindowMatches(item))
                    .OrderByDescending(item => recentWindows.TryGetValue(item.Path, out nint window) && window == item.Handle).FirstOrDefault();
            string executable = pin.Location ?? pin.Target;
            if (Native.IsTaskManager(executable) || Native.IsTaskManager(match?.Path))
            {
                await OpenTaskManager(match?.Handle ?? 0);
                return false;
            }
            if (Launcher.IsPrismTarget(pin.Target, executable) &&
                await Prism.OpenRunning(Prism.IsTrayClient(executable) ? executable : null,
                    match?.Handle ?? 0, InvokeTrayIcon) is { } prismOpened)
            {
                if (!prismOpened) feedback.Text = "Prism запущен. Открой окно через значок Windows.";
                return false;
            }
            if (match is null)
                match = await Task.Run(() => Native.OpenWindows().FirstOrDefault(item => pin.Matches(item.Path) ||
                    Launcher.IsShellApp(pin.Target) && Native.WindowApplicationId(item.Handle) is { } id &&
                    pin.Target.Equals(@"shell:AppsFolder\" + id, StringComparison.OrdinalIgnoreCase)));
            if (match is not null)
            {
                if (!Native.WindowMatches(match) || !await Native.FocusWindow(match.Handle))
                {
                    feedback.Text = "Windows не удалось вывести окно приложения";
                    SessionLog.Write($"Could not bring forward a Dock window for {Path.GetFileName(match.Path)}.");
                }
                return false;
            }
            bool opened = OpenPin(pin);
            if (!opened) feedback.Text = "Windows не удалось открыть приложение";
            return opened;
        }
        finally { activatingPins.Remove(pin.Target); }
    }

    private static bool OpensFinder(Pin pin) => pin.Target == "explorer.exe" && pin.Arguments is "" or "shell:Downloads";

    internal void SetFinderHandle(nint handle)
    {
        finderHandle = handle;
        UpdateIndicators();
    }

    private void UpdateIndicators()
    {
        foreach (var (pin, dot) in indicators)
        {
            bool finder = finderHandle != 0 && pin.Target == "explorer.exe" && pin.Arguments == "";
            bool running = finder || windows.Any(item => MatchesWindow(pin, item));
            dot.Visibility = running ? Visibility.Visible : Visibility.Hidden;
            bool active = finder && foreground == finderHandle || windows.Any(item => item.Handle == foreground && MatchesWindow(pin, item));
            dot.Opacity = active ? 1 : 0.62;
        }
    }

    private bool MatchesWindow(Pin pin, Native.OpenWindow window) => pin.Matches(window.Path) ||
        appIdentities.TryGetValue(window.Handle, out var app) && app.Process == window.Process &&
        pin.Target.Equals(@"shell:AppsFolder\" + app.Id, StringComparison.OrdinalIgnoreCase);

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
            double distance = hoverX is { } pointer ? (pointer - center) / SlotSize : double.PositiveInfinity;
            tile.Growth = 0.42 * Math.Exp(-0.5 * distance * distance);
            total += tile.Growth * IconSize;
        }
        double prefix = -total / 2;
        foreach (var tile in slots)
        {
            double extra = tile.Growth * IconSize;
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
        if (Mouse.LeftButton == MouseButtonState.Pressed) return;
        double blend = 1 - Math.Exp(-Math.Clamp(elapsed, 0, 64) / 58);
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
            changing |= Math.Abs(tile.Offset.X - x) > 0.1;
            tile.Offset.X = Approach(tile.Offset.X, x, 0.1, blend);
            if (tile.Lift is { } lift)
            {
                changing |= Math.Abs(lift.Y - y) > 0.1;
                lift.Y = Approach(lift.Y, y, 0.1, blend);
            }
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
            if (tile.Lift is { } lift) lift.Y = 0;
        }
    }

    internal void SetDockVisible(bool visible, bool immediate)
    {
        if (immediate && !visible) downloads.IsOpen = false;
        bool wasVisible = DockWindow.IsVisible;
        if (dockVisible == visible && !(immediate && !visible && wasVisible)) return;
        dockVisible = visible;
        double y = wasVisible ? dockSlide.Y : 13;
        double opacity = wasVisible ? dock.Opacity : 0;
        dockSlide.BeginAnimation(TranslateTransform.YProperty, null);
        dock.BeginAnimation(UIElement.OpacityProperty, null);
        dockSlide.Y = visible ? 0 : 13;
        dock.Opacity = visible ? 1 : 0;
        if (visible && !wasVisible) DockWindow.Show();
        if (!animate || immediate)
        {
            if (!visible && DockWindow.IsVisible) DockWindow.Hide();
            return;
        }
        var duration = TimeSpan.FromMilliseconds(visible ? 240 : 165);
        dockSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, dockSlide.Y, duration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        var fade = new DoubleAnimation(opacity, dock.Opacity, duration) { FillBehavior = FillBehavior.Stop };
        if (!visible) fade.Completed += (_, _) => { if (!dockVisible && DockWindow.IsVisible) DockWindow.Hide(); };
        dock.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    internal static bool Launch(string target, string arguments = "")
    {
        try { Process.Start(new ProcessStartInfo(target, arguments) { UseShellExecute = true })?.Dispose(); return true; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException) { return false; }
    }

    internal void RefreshClock() => tray.Refresh();
    internal void RefreshInputLanguage(nint window) => tray.RefreshInputLanguage(Native.InputLanguage(window));
    internal void RefreshTray() => trayIcons.Refresh();

    internal void PlaceMenu(Rect area)
    {
        MenuBar.Width = area.Width;
        MenuBar.Left = area.Left;
        MenuBar.Top = area.Top;
    }

    internal void PlaceDock(Rect area)
    {
        DockWindow.Width = Math.Min(1120, area.Width);
        DockWindow.Left = area.Left + (area.Width - DockWindow.Width) / 2;
        DockWindow.Top = area.Bottom - DockWindow.Height - 5;
        ResizeDock();
    }

    private void ResizeDock()
    {
        double desired = indicators.Count * SlotSize + (dynamicPaths.Length > 0 ? 34 : 17) + 18;
        double scale = Math.Clamp((DockWindow.Width - 12) / (desired + 82), 0.1, 1);
        dock.LayoutTransform = scale < 1 ? new ScaleTransform(scale, scale) : Transform.Identity;
    }

    private sealed record Pin(string Label, string Target, string Arguments, string[] Processes,
        string Light, string Dark, string Glyph, ImageSource? Image = null, string? Location = null)
    {
        internal bool Matches(string path) => string.Equals(Target, path, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Location, path, StringComparison.OrdinalIgnoreCase) ||
            Processes.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
    }

    private sealed record DockSlot(Grid Anchor, ScaleTransform? Scale, TranslateTransform Offset, TranslateTransform? Lift)
    {
        internal double Growth { get; set; }
        internal double TargetX { get; set; }
    }
}
