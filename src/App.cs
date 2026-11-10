using System;
using System.Threading;
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

    [STAThread]
    private static void Main(string[] args)
    {
        if (Worker.Handle(args)) return;
        using var instance = new Mutex(true, @"Local\LumaShell.Overlay", out bool first);
        if (!first) return;
        new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }.Run();
    }

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        preferences = Preferences.Load();
        screenshots = new Screenshots(preferences);
        activity = new Activity();
        prism = new Prism(preferences);
        network = new PublicNetwork();
        taskbar = new Taskbar();
        Taskbar.Recover();
        updates = new Updates();
        launcher = new Launcher();
        shortcuts = new ShellShortcuts(Dispatcher, screenshots.Begin, ToggleLauncher);
        if (!shortcuts.Configure(preferences.ReplaceSnip, preferences.WinLauncher)) preferences.ReplaceSnip = preferences.WinLauncher = false;
        island = new Island(screenshots, activity, prism, network, preferences, ToggleSnip);
        desktop = new Desktop(preferences, tab => OpenIsland(tab), screenshots.Begin, screenshots.OpenFolder, CloseFromMenu,
            taskbar.Reveal, () => SetWindowsTaskbarHidden(!preferences.HideWindowsTaskbar), UpdateFromMenu, updates, ToggleLauncher, ToggleWin);
        topBar = new TopBar(desktop.MenuBar);
        islandHandle = new WindowInteropHelper(island.Window).EnsureHandle();
        HwndSource.FromHwnd(islandHandle).AddHook(WindowMessage);
        hotkeyRegistered = Native.RegisterIslandHotkey(islandHandle);
        screenshotHotkey = Native.RegisterScreenshotHotkey(islandHandle);
        nativeTrayHotkey = Native.RegisterTrayHotkey(islandHandle);
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
        taskbar.SetEnabled(preferences.Chrome && preferences.HideWindowsTaskbar);
        PlaceWindows(true);
        RefreshVisibility();
        island.RefreshClock(true);
        desktop.RefreshClock();
        island.RefreshMedia();
        prism.Refresh();
        network.Refresh();
        if (Updates.ReadResult() is { } updateResult) island.Flash(updateResult);
        CreateTray();
        desktop.RefreshTray();
        ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        ticker.Tick += (_, _) => Tick();
        ticker.Start();
    }

    private void Tick()
    {
        taskbar.Tick();
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
        if (ticks % 25 == 0 && preferences.Chrome && preferences.HideWindowsTaskbar) taskbar.SetEnabled(true);
        if (ticks % 30 == 0) prism.Refresh();
        if (ticks % 300 == 0) network.Refresh();
        if (ticks % 50 == 0) desktop.RefreshClock();
        if (ticks % 108000 == 75) updates.Check();
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
        bool own = Native.BelongsToCurrentProcess(foreground);
        if (!own && foreground != 0) lastForeground = foreground;
        nint underlying = own ? lastForeground : foreground;
        bool onDesktop = Native.IsDesktop(underlying);
        fullscreen = !onDesktop && Native.CoversPrimaryMonitor(underlying);
        bool showChrome = preferences.Chrome && !fullscreen;
        shortcuts.LauncherAvailable = preferences.Chrome && !screenshots.Busy;
        if (!shortcuts.LauncherAvailable) launcher.Close();
        if (showChrome && !desktop.MenuBar.IsVisible)
        {
            desktop.MenuBar.Show();
            topBar.Place();
            desktop.RefreshClock();
        }
        else SetVisible(desktop.MenuBar, showChrome);
        if (showChrome) desktop.RefreshInputLanguage(underlying);
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
        if (!shortcuts.Configure(!preferences.ReplaceSnip, preferences.WinLauncher))
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
        if (shortcuts.LauncherAvailable) launcher.Toggle(lastArea);
    }

    private void ToggleWin()
    {
        if (!shortcuts.Configure(preferences.ReplaceSnip, !preferences.WinLauncher))
        {
            island.Flash("Windows не дала подключить клавишу Win");
            return;
        }
        preferences.WinLauncher = shortcuts.Launcher;
        preferences.Save();
    }

    private void UpdateFromMenu()
    {
        if (!updates.Ready) updates.Check(true);
        else if (updates.Schedule(true)) Shutdown();
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
