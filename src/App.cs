using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace LumaShell;

internal sealed class App : Application
{
    private Preferences preferences = null!;
    private Screenshots screenshots = null!;
    private ShellShortcuts shortcuts = null!;
    private Launcher launcher = null!;
    private Activity activity = null!;
    private Prism prism = null!;
    private PublicNetwork network = null!;
    private Island island = null!;
    private Desktop desktop = null!;
    private FinderWindow? finder;
    private TopBar topBar = null!;
    private Taskbar taskbar = null!;
    private Updates updates = null!;
    private Forms.NotifyIcon tray = null!;
    private Forms.ToolStripMenuItem? hideTaskbarMenu;
    private System.Drawing.Icon? trayIcon;
    private DispatcherTimer ticker = null!;
    private nint islandHandle;
    private nint lastForeground;
    private bool hotkeyRegistered;
    private bool screenshotHotkey;
    private bool nativeTrayHotkey;
    private bool fullscreen;
    private int ticks;
    private Rect lastArea = Rect.Empty;
    private DateTimeOffset keepDockUntil;
    private bool recoveredSession;
    private readonly SemaphoreSlim startupGate = new(1);
    private readonly CancellationTokenSource folderRequests = new();
    private string? initialFolder;

    [STAThread]
    private static int Main(string[] args)
    {
        if (Worker.Handle(args)) return Environment.ExitCode;
        string? folder = FolderRouting.ReadArgument(args);
        if (args.Length > 0 && args[0] == FolderRouting.Argument && folder is null) return 0;
        using var instance = new Mutex(true, @"Local\LumaShell.Overlay", out bool first);
        if (!first)
        {
            if (folder is not null && !FolderRouting.Forward(folder))
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                var window = new FinderWindow();
                app.Startup += (_, _) => window.OpenFolder(folder);
                app.Run();
            }
            return 0;
        }
        var preferences = Preferences.Load();
        if (args.Length > 0 && args[0] == LogonStartup.Argument && !preferences.AutoStart) return 0;
        bool recovered = SessionLog.Begin();
        int exitCode;
        using (var taskbar = new Taskbar())
        {
            Taskbar.Recover();
            taskbar.SetEnabled(preferences.Chrome && preferences.HideWindowsTaskbar);
            if (preferences.AutoStart) SessionLog.StartRecovery();
            exitCode = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, preferences = preferences,
                taskbar = taskbar, initialFolder = folder, recoveredSession = recovered }.Run();
        }
        SessionLog.End(exitCode);
        return exitCode;
    }

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        screenshots = new Screenshots(preferences);
        activity = new Activity();
        prism = new Prism(preferences);
        network = new PublicNetwork();
        updates = new Updates();
        launcher = new Launcher(() => OpenFinder(null), RevealFinderItem,
            app => desktop.IsPinned(app), app => desktop.TogglePin(app)) { InvokeTray = executable => desktop.InvokeTrayIcon(executable) };
        shortcuts = new ShellShortcuts(Dispatcher, screenshots.Begin, ToggleLauncher, OpenDefaultFinder);
        if (!shortcuts.Configure(preferences.ReplaceSnip, preferences.WinLauncher, preferences.RouteFolders))
            preferences.ReplaceSnip = preferences.WinLauncher = false;
        island = new Island(screenshots, activity, prism, network, preferences, ToggleSnip);
        desktop = new Desktop(preferences, tab => OpenIsland(tab), screenshots.Begin, screenshots.OpenFolder, CloseFromMenu,
            taskbar.Reveal, () => SetWindowsTaskbarHidden(!preferences.HideWindowsTaskbar), UpdateFromMenu, updates, ToggleLauncher,
            ToggleWin, ToggleStartup, OpenFinder, ApplyWallpaper, ToggleFolderRouting, ToggleAutoInstall, OpenSessionLog);
        prism.InvokeTray = desktop.InvokeTrayIcon;
        topBar = new TopBar(desktop.MenuBar);
        islandHandle = new WindowInteropHelper(island.Window).EnsureHandle();
        HwndSource.FromHwnd(islandHandle).AddHook(WindowMessage);
        hotkeyRegistered = Native.RegisterIslandHotkey(islandHandle);
        screenshotHotkey = Native.RegisterScreenshotHotkey(islandHandle);
        nativeTrayHotkey = Native.RegisterTrayHotkey(islandHandle);
        UpdateLauncherHotkey();
        UpdateFinderHotkey();
        island.ClipboardToggleRequested += ToggleClipboard;
        island.Collapsed += RestoreFocus;
        screenshots.StateChanged += RefreshVisibility;
        screenshots.Saved += _ => { island.RefreshScreenshots(); island.Flash("✓  Снимок сохранён в PNG"); };
        screenshots.Failed += message => island.Flash(message);
        prism.Feedback += island.Flash;
        prism.Updated += () => network.Refresh();
        taskbar.Failed += () => island.Flash("Не удалось скрыть панель задач Windows");
        updates.Changed += () => island.Flash(updates.Status);
        topBar.Enable(preferences.Chrome);
        if (taskbar.Faulted) island.Flash("Не удалось скрыть панель задач Windows");
        PlaceWindows(true);
        RefreshVisibility();
        island.RefreshClock(true);
        desktop.RefreshClock();
        island.RefreshMedia();
        prism.Refresh();
        network.Refresh();
        if (Updates.ReadResult() is { } updateResult) island.Flash(updateResult);
        else if (recoveredSession) island.Flash("Luma снова запущена · проверьте журнал в меню");
        CreateTray();
        desktop.RefreshTray();
        ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        ticker.Tick += (_, _) => Tick();
        ticker.Start();
        ConfigureStartup();
        _ = FolderRouting.Listen(path =>
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => OpenFinder(path)));
        }, folderRequests.Token);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!FolderRouting.Configure(preferences.RouteFolders) && preferences.RouteFolders && Worker.Executable is not null)
                island.Flash("Windows не подключила открытие папок в Finder");
        }));
        if (initialFolder is { } target) OpenFinder(target);
    }

    private void Tick()
    {
        if (preferences.Chrome && preferences.HideWindowsTaskbar) taskbar.SetEnabled(true);
        taskbar.Tick(desktop.UsingNativeTray);
        PlaceWindows();
        RefreshVisibility();
        if (++ticks % 5 == 0)
        {
            island.RefreshClock(ticks % 50 == 0);
            if (preferences.Chrome) desktop.RefreshWindows(lastForeground);
        }
        if (ticks % 10 == 0) island.RefreshMedia();
        if (ticks % 15 == 0) activity.Refresh();
        if (ticks % 20 == 0 && desktop.MenuBar.IsVisible) desktop.RefreshTray();
        if (ticks % 30 == 0) prism.Refresh();
        if (ticks % 300 == 0) network.Refresh();
        if (ticks % 50 == 0)
        {
            desktop.RefreshClock();
            if (preferences.AutoInstallUpdates && updates.CanInstallAutomatically &&
                finder is null && !fullscreen && !screenshots.Busy && !island.KeepSession && !taskbar.Revealing &&
                !desktop.TrayIsOpen && !launcher.IsOpen &&
                Native.IsDesktop(Native.GetForegroundWindow()) && Native.IdleFor(TimeSpan.FromMinutes(3)) &&
                updates.Schedule(restart: true, automatic: true))
            {
                Shutdown();
                return;
            }
        }
        if (ticks % 18000 == 75) updates.Check(autoInstall: preferences.AutoInstallUpdates);
    }

    private void ToggleStartup()
    {
        preferences.AutoStart = !preferences.AutoStart;
        if (!preferences.Save()) island.Flash("Не удалось сохранить настройку автозапуска");
        if (preferences.AutoStart) SessionLog.StartRecovery();
        ConfigureStartup(interactive: true);
    }

    private void ApplyWallpaper() => island.Flash(Wallpaper.Apply() ? "Обои Luma установлены" : "Не удалось установить обои");

    private void ToggleFolderRouting()
    {
        bool enabled = !preferences.RouteFolders;
        if (!FolderRouting.Configure(enabled))
        {
            island.Flash("Не удалось изменить приложение для папок");
            return;
        }
        preferences.RouteFolders = enabled;
        preferences.Save();
        shortcuts.Configure(preferences.ReplaceSnip, preferences.WinLauncher, enabled);
        UpdateFinderHotkey();
    }

    private void OpenDefaultFinder()
    {
        if (shortcuts.FinderAvailable) OpenFinder(null);
    }

    private void OpenFinder(string? path)
    {
        var window = EnsureFinder();
        window.OpenFolder(path);
        desktop.SetFinderHandle(window.Handle);
    }

    private void RevealFinderItem(string path)
    {
        var window = EnsureFinder();
        window.Reveal(path);
        desktop.SetFinderHandle(window.Handle);
    }

    private FinderWindow EnsureFinder()
    {
        if (finder is null)
        {
            finder = new FinderWindow();
            finder.Closed += (_, _) =>
            {
                finder = null;
                desktop.SetFinderHandle(0);
            };
        }
        return finder;
    }

    private async void ConfigureStartup(bool interactive = false)
    {
        await startupGate.WaitAsync();
        try
        {
            bool enabled = preferences.AutoStart;
            bool configured = await Task.Run(() => LogonStartup.Configure(enabled));
            if (!configured) island.Flash("Не удалось настроить автозапуск");
            else if (interactive) island.Flash(enabled ? "Автозапуск включён" : "Автозапуск выключен");
        }
        catch (Exception error)
        {
            // Registration is optional. Never let a scheduler failure escape onto the UI dispatcher.
            System.Diagnostics.Trace.TraceError("Luma logon registration: {0}", error);
            if (!Dispatcher.HasShutdownStarted) island.Flash("Не удалось настроить автозапуск");
        }
        finally { startupGate.Release(); }
    }

    private void PlaceWindows(bool force = false)
    {
        var area = SystemParameters.WorkArea;
        if (Native.PrimaryWorkArea() is { } physical)
        {
            var dpi = VisualTreeHelper.GetDpi(island.Window);
            area = new Rect(physical.Left / dpi.DpiScaleX, physical.Top / dpi.DpiScaleY,
                physical.Width / dpi.DpiScaleX, physical.Height / dpi.DpiScaleY);
        }
        if (!force && area == lastArea) return;
        lastArea = area;
        if (topBar.Registered) topBar.Place();
        else desktop.PlaceMenu(area);
        desktop.PlaceDock(area);
        island.Place(area, preferences.Chrome && !topBar.Registered);
    }

    private void RefreshVisibility()
    {
        nint foreground = Native.GetForegroundWindow();
        bool own = Native.BelongsToCurrentProcess(foreground) && foreground != finder?.Handle ||
            desktop.UsingNativeTray && NativeTrayMenu.IsNativeSurface(foreground);
        if (!own && foreground != 0) lastForeground = foreground;
        nint underlying = own ? lastForeground : foreground;
        bool onDesktop = Native.IsDesktop(underlying);
        fullscreen = !onDesktop && Native.CoversPrimaryMonitor(underlying);
        bool showChrome = preferences.Chrome && !fullscreen;
        shortcuts.LauncherAvailable = preferences.Chrome;
        shortcuts.FinderAvailable = preferences.RouteFolders && !screenshots.Busy;
        if (!shortcuts.LauncherAvailable || screenshots.Busy) launcher.Close();
        if (showChrome && !desktop.MenuBar.IsVisible)
        {
            desktop.MenuBar.Show();
            topBar.Place();
            desktop.RefreshClock();
        }
        else SetVisible(desktop.MenuBar, showChrome);
        if (showChrome) desktop.RefreshInputLanguage(foreground);
        if (showChrome && (NearDock() || desktop.Interacting)) keepDockUntil = DateTimeOffset.UtcNow.AddMilliseconds(850);
        bool showDock = showChrome && !screenshots.Busy && !taskbar.Revealing &&
            (preferences.DockAlways || onDesktop || DateTimeOffset.UtcNow < keepDockUntil);
        desktop.SetDockVisible(showDock, fullscreen || screenshots.Busy || !preferences.Chrome || taskbar.Revealing);
        if ((fullscreen || screenshots.Busy) && island.Window.IsVisible) island.Collapse();
        SetVisible(island.Window, !fullscreen && !screenshots.Busy);
    }

    private static bool NearDock()
    {
        if (Forms.Screen.PrimaryScreen is not { } screen) return false;
        var area = Native.PrimaryWorkArea() ?? screen.WorkingArea;
        var cursor = Forms.Cursor.Position;
        return cursor.Y >= area.Bottom - 24 && cursor.Y < screen.Bounds.Bottom &&
            Math.Abs(cursor.X - (area.Left + area.Width / 2)) <= Math.Min(500, area.Width / 2);
    }

    private static void SetVisible(Window window, bool visible)
    {
        if (window.IsVisible == visible) return;
        if (visible) window.Show();
        else window.Hide();
    }

    private void OpenIsland(IslandTab? tab = null)
    {
        if (fullscreen || screenshots.Busy) return;
        var foreground = Native.GetForegroundWindow();
        if (foreground != 0 && !Native.BelongsToCurrentProcess(foreground)) lastForeground = foreground;
        if (tab is { } selected) island.Select(selected);
        island.Open();
        island.Window.Activate();
    }

    private void RestoreFocus()
    {
        if (lastForeground != 0 && Native.GetForegroundWindow() == islandHandle)
            Native.SetForegroundWindow(lastForeground);
    }

    private void ToggleSnip()
    {
        if (!shortcuts.Configure(!preferences.ReplaceSnip, preferences.WinLauncher, preferences.RouteFolders))
        {
            island.Flash("Windows не дала подключить Win+Shift+S");
            return;
        }
        preferences.ReplaceSnip = shortcuts.Snip;
        preferences.Save();
        island.RefreshScreenshots();
    }

    private void ToggleLauncher()
    {
        if (shortcuts.LauncherAvailable && !screenshots.Busy) launcher.Toggle(lastArea);
    }

    private void ToggleWin()
    {
        if (!shortcuts.Configure(preferences.ReplaceSnip, !preferences.WinLauncher, preferences.RouteFolders))
        {
            island.Flash("Windows не дала подключить клавишу Win");
            return;
        }
        preferences.WinLauncher = shortcuts.Launcher;
        UpdateLauncherHotkey();
        preferences.Save();
    }

    private void UpdateLauncherHotkey()
    {
        if (shortcuts.LauncherHotkeyRegistered)
        {
            if (shortcuts.Launcher) return;
            shortcuts.LauncherHotkeyRegistered = false;
            Native.UnregisterHotKey(islandHandle, Native.LauncherHotkey);
        }
        else if (shortcuts.Launcher)
            shortcuts.LauncherHotkeyRegistered = Native.RegisterLauncherHotkey(islandHandle);
    }

    private void UpdateFinderHotkey()
    {
        if (shortcuts.FinderHotkeyRegistered && !shortcuts.Finder)
        {
            shortcuts.FinderHotkeyRegistered = false;
            Native.UnregisterHotKey(islandHandle, Native.FinderHotkey);
        }
        else if (!shortcuts.FinderHotkeyRegistered && shortcuts.Finder)
            shortcuts.FinderHotkeyRegistered = Native.RegisterFinderHotkey(islandHandle);
    }

    private void UpdateFromMenu()
    {
        if (!updates.Ready) updates.Check(interactive: true, autoInstall: preferences.AutoInstallUpdates);
        else if (updates.Schedule(true)) Shutdown();
    }

    private void ToggleAutoInstall()
    {
        preferences.AutoInstallUpdates = !preferences.AutoInstallUpdates;
        preferences.Save();
        updates.RefreshReadyStatus(preferences.AutoInstallUpdates);
        if (!updates.Ready) island.Flash(preferences.AutoInstallUpdates ?
            "Обновления устанавливаются в простое" : "Обновления устанавливаются по кнопке или при выходе");
    }

    private void OpenSessionLog()
    {
        if (!SessionLog.Open()) island.Flash("Не удалось открыть журнал Luma");
    }

    private void CloseFromMenu()
    {
        if (updates.Ready) updates.Schedule(false);
        Shutdown();
    }

    private void SetWindowsTaskbarHidden(bool hide)
    {
        if (preferences.HideWindowsTaskbar == hide) return;
        preferences.HideWindowsTaskbar = hide;
        preferences.Save();
        if (hideTaskbarMenu is not null) hideTaskbarMenu.Checked = hide;
        taskbar.SetEnabled(preferences.Chrome && hide);
    }

    private void ToggleClipboard()
    {
        if (island.ClipboardEnabled)
        {
            Native.RemoveClipboardFormatListener(islandHandle);
            island.SetClipboardEnabled(false);
        }
        else if (Native.AddClipboardFormatListener(islandHandle)) island.SetClipboardEnabled(true);
        else island.Flash("Windows не удалось подключить буфер");
    }

    private nint WindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == Native.ClipboardMessage) Dispatcher.BeginInvoke(DispatcherPriority.Background, island.CaptureClipboard);
        if (message == Native.HotkeyMessage && wParam == Native.IslandHotkey)
        {
            handled = true;
            if (island.Expanded) island.Collapse();
            else OpenIsland();
        }
        if (message == Native.HotkeyMessage && wParam == Native.ScreenshotHotkey)
        {
            handled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, screenshots.Begin);
        }
        if (message == Native.HotkeyMessage && wParam == Native.TrayHotkey)
        {
            handled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, taskbar.Reveal);
        }
        if (message == Native.HotkeyMessage && wParam == Native.LauncherHotkey)
        {
            handled = true;
            if (shortcuts.LauncherHotkeyRegistered) ToggleLauncher();
        }
        if (message == Native.HotkeyMessage && wParam == Native.FinderHotkey)
        {
            handled = true;
            if (shortcuts.FinderHotkeyRegistered) OpenDefaultFinder();
        }
        return 0;
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(hotkeyRegistered ? "Остров  Ctrl+Alt+Пробел" : "Остров", null, (_, _) => OpenIsland());
        menu.Items.Add(screenshotHotkey ? "Снимок  Ctrl+Alt+S" : "Снимок", null, (_, _) => screenshots.Begin());
        menu.Items.Add("Папка снимков", null, (_, _) => screenshots.OpenFolder());
        menu.Items.Add(nativeTrayHotkey ? "Значки Windows  Ctrl+Alt+B" : "Значки Windows", null, (_, _) => taskbar.Reveal());
        var chrome = new Forms.ToolStripMenuItem("Верхнее меню и Dock") { Checked = preferences.Chrome, CheckOnClick = true };
        chrome.CheckedChanged += (_, _) =>
        {
            preferences.Chrome = chrome.Checked;
            if (!preferences.Chrome) desktop.RestoreExplorerFrame();
            preferences.Save();
            topBar.Enable(preferences.Chrome);
            taskbar.SetEnabled(preferences.Chrome && preferences.HideWindowsTaskbar);
            PlaceWindows(true);
            RefreshVisibility();
        };
        menu.Items.Add(chrome);
        var dock = new Forms.ToolStripMenuItem("Всегда показывать Dock") { Checked = preferences.DockAlways, CheckOnClick = true };
        dock.CheckedChanged += (_, _) =>
        {
            preferences.DockAlways = dock.Checked;
            preferences.Save();
            RefreshVisibility();
        };
        menu.Items.Add(dock);
        hideTaskbarMenu = new Forms.ToolStripMenuItem("Скрывать панель задач Windows")
            { Checked = preferences.HideWindowsTaskbar, CheckOnClick = true };
        hideTaskbarMenu.CheckedChanged += (_, _) => SetWindowsTaskbarHidden(hideTaskbarMenu.Checked);
        menu.Items.Add(hideTaskbarMenu);
        var update = menu.Items.Add("Проверить обновления", null, (_, _) => UpdateFromMenu());
        updates.Changed += () => update.Text = updates.Ready ? "Установить обновление и перезапустить" : "Проверить обновления";
        menu.Items.Add("Журнал Luma", null, (_, _) => OpenSessionLog());
        menu.Items.Add("Настройки панели задач Windows", null, (_, _) => Desktop.Launch("ms-settings:taskbar"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выйти", null, (_, _) => CloseFromMenu());
        if (Environment.ProcessPath is { } path) trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(path);
        tray = new Forms.NotifyIcon { Text = "Luma Shell", Icon = trayIcon ?? System.Drawing.SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => OpenIsland();
    }

    protected override void OnExit(ExitEventArgs args)
    {
        ticker?.Stop();
        folderRequests.Cancel();
        desktop?.RestoreExplorerFrame();
        if (screenshots is not null) screenshots.StateChanged -= RefreshVisibility;
        shortcuts?.Dispose();
        topBar?.Dispose();
        taskbar?.Dispose();
        updates?.Dispose();
        prism?.Dispose();
        network?.Dispose();
        if (hotkeyRegistered) Native.UnregisterHotKey(islandHandle, Native.IslandHotkey);
        if (screenshotHotkey) Native.UnregisterHotKey(islandHandle, Native.ScreenshotHotkey);
        if (nativeTrayHotkey) Native.UnregisterHotKey(islandHandle, Native.TrayHotkey);
        if (shortcuts?.LauncherHotkeyRegistered == true) Native.UnregisterHotKey(islandHandle, Native.LauncherHotkey);
        if (shortcuts?.FinderHotkeyRegistered == true) Native.UnregisterHotKey(islandHandle, Native.FinderHotkey);
        if (island?.ClipboardEnabled == true) Native.RemoveClipboardFormatListener(islandHandle);
        if (tray is not null)
        {
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
        }
        trayIcon?.Dispose();
        base.OnExit(args);
    }
}
