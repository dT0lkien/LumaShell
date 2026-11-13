using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static LumaShell.Visuals;

namespace LumaShell;

internal sealed class Launcher
{
    private readonly Action openFinder;
    private readonly Action<string> reveal;
    private readonly Func<AppEntry, bool> isPinned;
    private readonly Func<AppEntry, string?> togglePin;
    private readonly Window window = Overlay("Luma • Приложения", 620, 455);
    private readonly TextBox query = new() { FontSize = 23, Foreground = White, Background = Brushes.Transparent,
        BorderThickness = new Thickness(0), CaretBrush = White, Padding = new Thickness(7, 6, 7, 6), MaxLength = 160 };
    private readonly ListBox results = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        Foreground = White, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock status = Text("Загружаем приложения…", 12, Muted);
    private readonly TextBlock placeholder = Text("Поиск приложений", 22, Muted);
    private readonly TextBlock section = Text("Приложения", 11, Muted, true);
    private readonly List<string> recent = [];
    private AppEntry[]? catalog;
    private ContextMenu? context;
    private bool loading;
    private bool launching;
    internal Func<string, Task<bool>>? InvokeTray { get; init; }

    internal Launcher(Action openFinder, Action<string> reveal, Func<AppEntry, bool> isPinned, Func<AppEntry, string?> togglePin)
    {
        this.openFinder = openFinder;
        this.reveal = reveal;
        this.isPinned = isPinned;
        this.togglePin = togglePin;
        window.ShowActivated = true;
        results.ItemContainerStyle = ItemStyle();
        ScrollViewer.SetHorizontalScrollBarVisibility(results, ScrollBarVisibility.Disabled);
        var layout = new DockPanel();
        var search = new DockPanel { Margin = new Thickness(14, 4, 9, 4) };
        var lens = Icon("M13,7 A6,6 0 1 1 1,7 A6,6 0 1 1 13,7 M11,11 L18,18", 20, Muted);
        DockPanel.SetDock(lens, Dock.Left);
        search.Children.Add(lens);
        var field = new Grid();
        placeholder.Margin = new Thickness(7, 0, 0, 0);
        placeholder.IsHitTestVisible = false;
        field.Children.Add(placeholder);
        field.Children.Add(query);
        search.Children.Add(field);
        var searchCard = new Border { Background = Paint("#2CFFFFFF"), BorderBrush = Line,
            BorderThickness = new Thickness(0, 0, 0, 1), Child = search };
        DockPanel.SetDock(searchCard, Dock.Top);
        layout.Children.Add(searchCard);
        section.Margin = new Thickness(16, 10, 0, 5);
        DockPanel.SetDock(section, Dock.Top);
        layout.Children.Add(section);
        status.Margin = new Thickness(16, 8, 8, 10);
        DockPanel.SetDock(status, Dock.Bottom);
        layout.Children.Add(status);
        results.Margin = new Thickness(7, 0, 7, 0);
        layout.Children.Add(results);
        var panel = Surface(Gradient("#F62D2F36", "#FB202126"), 24, layout);
        panel.BorderBrush = Paint("#68FFFFFF");
        panel.BorderThickness = new Thickness(1);
        panel.Padding = new Thickness(2);
        window.Content = panel;
        System.Windows.Automation.AutomationProperties.SetName(query, "Поиск приложений");
        FocusManager.SetFocusedElement(window, query);
        window.Activated += (_, _) => Keyboard.Focus(query);
        window.Deactivated += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(CloseIfUnfocused));
        window.PreviewKeyDown += (_, args) =>
        {
            if (context?.IsOpen == true) return;
            if (args.Key == Key.Escape) { args.Handled = true; Close(); }
            else if (args.Key == Key.Apps || (args.Key == Key.F10 || args.SystemKey == Key.F10) &&
                Keyboard.Modifiers == ModifierKeys.Shift)
            {
                if (results.SelectedItem is not ListBoxItem row) return;
                args.Handled = true;
                OpenContext(row, PlacementMode.Bottom);
            }
            else if (args.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control &&
                results.SelectedItem is ListBoxItem { Tag: AppEntry { Location: { } path } })
            {
                args.Handled = true;
                Reveal(path);
            }
            else if (args.Key == Key.Enter && (query.IsKeyboardFocusWithin || results.IsKeyboardFocusWithin))
            { args.Handled = true; LaunchSelected(); }
            else if (query.IsKeyboardFocusWithin && args.Key is Key.Up or Key.Down && results.Items.Count > 0)
            {
                args.Handled = true;
                results.SelectedIndex = Math.Clamp(results.SelectedIndex + (args.Key == Key.Down ? 1 : -1), 0, results.Items.Count - 1);
                results.ScrollIntoView(results.SelectedItem);
            }
        };
        query.TextChanged += (_, _) => Render();
        results.PreviewMouseLeftButtonUp += (_, args) =>
        {
            if (args.OriginalSource is not DependencyObject source ||
                ItemsControl.ContainerFromElement(results, source) is not ListBoxItem { Tag: AppEntry item }) return;
            args.Handled = true;
            Run(item);
        };
        results.PreviewMouseRightButtonUp += (_, args) =>
        {
            if (args.OriginalSource is not DependencyObject source ||
                ItemsControl.ContainerFromElement(results, source) is not ListBoxItem row) return;
            args.Handled = true;
            OpenContext(row, PlacementMode.MousePoint);
        };
    }

    private static Style ItemStyle()
    {
        var frame = new FrameworkElementFactory(typeof(Border)) { Name = "Row" };
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        frame.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        frame.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        frame.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = frame };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Chip, "Row"));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, Paint("#FF2858A5"), "Row"));
        template.Triggers.Add(hover);
        template.Triggers.Add(selected);
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    internal void Toggle(Rect area)
    {
        if (window.IsVisible) { Close(); return; }
        window.Width = Math.Max(120, Math.Min(620, area.Width - 20));
        window.Height = Math.Max(140, Math.Min(455, area.Height - 20));
        window.Left = area.Left + (area.Width - window.Width) / 2;
        window.Top = area.Top + Math.Max(10, (area.Height - window.Height) * 0.24);
        window.Show();
        window.Activate();
        if (window.IsActive) Keyboard.Focus(query);
        if (query.Text.Length == 0) Render();
        else query.Clear();
        if (window.Content is FrameworkElement panel) Entrance(panel, 10, 0.98);
        if (catalog is null && !loading) Load();
    }

    internal void Close()
    {
        if (context is { } menu) menu.IsOpen = false;
        if (window.IsVisible) window.Hide();
    }

    internal bool IsOpen => window.IsVisible;

    private void CloseIfUnfocused()
    {
        if (!window.IsActive && context?.IsOpen != true) Close();
    }

    private void OpenContext(ListBoxItem row, PlacementMode placement)
    {
        if (row.Tag is not AppEntry app) return;
        results.SelectedItem = row;
        var menu = PopupMenu(row, placement);
        context = menu;
        menu.Items.Add(MenuEntry("Открыть", () => Run(app), "Enter"));
        if (app.Location is { } location)
        {
            menu.Items.Add(MenuEntry("Показать в Finder", () => Reveal(location), "Ctrl+R"));
            if (CanElevate(location)) menu.Items.Add(MenuEntry("Запуск от имени администратора", () => RunElevated(app, location)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry(isPinned(app) ? "Открепить от Dock" : "Закрепить в Dock", () =>
            status.Text = togglePin(app) ?? (isPinned(app) ? "Приложение закреплено" : "Приложение откреплено")));
        menu.Items.Add(MenuEntry(app.Location is null ? "Копировать имя" : "Копировать путь", () =>
        {
            try { Clipboard.SetText(app.Location ?? app.Name); status.Text = "Скопировано"; }
            catch (ExternalException) { status.Text = "Буфер обмена занят"; }
        }));
        menu.Closed += (_, _) =>
        {
            if (context != menu) return;
            context = null;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (window.IsActive && window.IsVisible) Keyboard.Focus(query);
                else CloseIfUnfocused();
            }));
        };
        menu.IsOpen = true;
    }

    private void Reveal(string path)
    {
        Close();
        reveal(path);
    }

    private static bool CanElevate(string location) =>
        Path.GetExtension(location).ToLowerInvariant() is ".exe" or ".lnk";

    private void RunElevated(AppEntry app, string location)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(location) { UseShellExecute = true, Verb = "runas" });
            Remember(app);
            Close();
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException)
        { status.Text = "Windows не удалось запустить приложение"; }
    }

    private async void Load()
    {
        loading = true;
        var complete = new TaskCompletionSource<AppEntry[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { complete.TrySetResult(ReadCatalog()); }
            catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
            { complete.TrySetResult([]); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        catalog = await complete.Task;
        loading = false;
        Render();
    }

    private void Render()
    {
        string filter = query.Text.Trim();
        placeholder.Visibility = query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        results.Items.Clear();
        if (catalog is null) return;
        var matches = catalog.Where(item => item.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(item => item.Name.StartsWith(filter, StringComparison.CurrentCultureIgnoreCase))
            .ThenBy(item => filter.Length == 0 && recent.IndexOf(item.Target) is var index && index >= 0 ? index : int.MaxValue)
            .Take(35).ToArray();
        section.Text = filter.Length == 0 && recent.Count > 0 ? "Недавние и другие приложения" : "Приложения";
        foreach (var app in matches)
        {
            var row = new DockPanel();
            var icon = app.Icon is null ? (FrameworkElement)Icon("M2,3 H18 V17 H2 Z M3,7 H17", 19, Blue)
                : new Image { Source = app.Icon, Width = 28, Height = 28 };
            icon.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(Text(app.Name, 14, bold: true));
            var result = new ListBoxItem { Content = row, Tag = app, Padding = new Thickness(9, 5, 9, 5),
                Foreground = White, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            System.Windows.Automation.AutomationProperties.SetName(result, app.Name);
            results.Items.Add(result);
        }
        if (results.Items.Count > 0) results.SelectedIndex = 0;
        status.Text = matches.Length > 0 ? "↑ ↓  Выбрать     Enter  Открыть     Shift+F10  Действия" : "Приложения не найдены";
    }

    private void LaunchSelected()
    {
        if (results.SelectedItem is ListBoxItem { Tag: AppEntry item }) Run(item);
    }

    private async void Run(AppEntry item)
    {
        if (launching) return;
        if (OpensFinder(item.Target))
        {
            Remember(item);
            Close();
            openFinder();
            return;
        }
        launching = true;
        try
        {
            if (Native.IsTaskManager(item.Target) || Native.IsTaskManager(item.Location))
            {
                if (await Native.OpenTaskManager()) { Remember(item); Close(); }
                else if (window.IsVisible) status.Text = "Не удалось открыть Диспетчер задач. Нажми Ctrl+Shift+Esc.";
                return;
            }
            if (IsPrismTarget(item.Target, item.Location) &&
                await Prism.OpenRunning(Prism.IsTrayClient(item.Location ?? "") ? item.Location : null, invokeTray: InvokeTray) is { } opened)
            {
                if (opened) { Remember(item); Close(); }
                else status.Text = "Prism запущен, но его окно недоступно";
                return;
            }
            if (LaunchTarget(item.Target)) { Remember(item); Close(); }
            else status.Text = "Windows не удалось открыть приложение";
        }
        finally { launching = false; }
    }

    private void Remember(AppEntry app)
    {
        recent.Remove(app.Target);
        recent.Insert(0, app.Target);
        if (recent.Count > 8) recent.RemoveAt(8);
    }

    internal static bool LaunchTarget(string target) => IsShellApp(target)
        ? Desktop.Launch("explorer.exe", $"\"{target}\"") : Desktop.Launch(target);

    internal static bool IsPrismTarget(string target, string? location) => Prism.IsTrayClient(target) ||
        location is not null && Prism.IsTrayClient(location) ||
        target.EndsWith(@"\com.prism.vpn", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(target).Equals("Prism.lnk", StringComparison.OrdinalIgnoreCase) ||
        location is not null && Path.GetFileName(location).Equals("Prism.lnk", StringComparison.OrdinalIgnoreCase);

    internal static bool OpensFinder(string target) =>
        target.Equals(@"shell:AppsFolder\Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase) ||
        target.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            StringComparison.OrdinalIgnoreCase);

    internal static bool IsShellApp(string target) =>
        target.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase) &&
        target.Length > @"shell:AppsFolder\".Length && !target.Contains('"') && !target.Any(char.IsControl);

    private static AppEntry[] ReadCatalog()
    {
        var list = new List<AppEntry>();
        var shortcuts = StartMenuShortcuts();
        object? shell = null, folder = null, items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)!);
            folder = ((dynamic)shell!).NameSpace("shell:AppsFolder");
            if (folder is not null)
            {
                items = ((dynamic)folder).Items();
                int count = ((dynamic)items!).Count;
                for (int index = 0; index < Math.Min(400, count); index++)
                {
                    object? entry = null;
                    try
                    {
                        entry = ((dynamic)items!).Item(index);
                        if (entry is null) continue;
                        string name = ((dynamic)entry).Name;
                        string path = ((dynamic)entry).Path;
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path) || path.Contains('"')) continue;
                        string target = File.Exists(path) ? path : @"shell:AppsFolder\" + path;
                        string? location = File.Exists(path) ? path : AppLocation(entry) ?? shortcuts.GetValueOrDefault(name);
                        list.Add(new AppEntry(Label(name, 90), target, location, ShellIcon(target)));
                    }
                    catch (Exception error) when (error is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                    finally { Release(entry); }
                }
            }
        }
        catch (Exception error) when (error is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally { Release(items); Release(folder); Release(shell); }
        if (list.Count == 0)
            foreach (var (name, file) in shortcuts)
                list.Add(new AppEntry(Label(name, 90), file, file, ShellIcon(file)));
        return list.DistinctBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static Dictionary<string, string> StartMenuShortcuts()
    {
        var shortcuts = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var location in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
        {
            string path = Environment.GetFolderPath(location);
            if (!Directory.Exists(path)) continue;
            try
            {
                foreach (string file in Directory.EnumerateFiles(path, "*.lnk", options).Take(250))
                    shortcuts.TryAdd(Path.GetFileNameWithoutExtension(file), file);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return shortcuts;
    }

    private static string? AppLocation(object entry)
    {
        try
        {
            return ((dynamic)entry).ExtendedProperty("System.Link.TargetParsingPath") is string path && File.Exists(path) ? path : null;
        }
        catch (Exception error) when (error is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { return null; }
    }

    internal static ImageSource? ShellIcon(string target)
    {
        int apartment = CoInitializeEx(0, 0);
        if (apartment < 0 && apartment != unchecked((int)0x80010106)) return null;
        nint id = 0;
        try
        {
            if (SHParseDisplayName(target, 0, out id, 0, out _) < 0 || id == 0) return null;
            if (SHGetFileInfo(id, 0, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x109) == 0 || info.Icon == 0) return null;
            try { return IconImage(info.Icon); }
            finally { DestroyIcon(info.Icon); }
        }
        finally
        {
            if (id != 0) Marshal.FreeCoTaskMem(id);
            if (apartment >= 0) CoUninitialize();
        }
    }

    internal static ImageSource? IconImage(nint icon)
    {
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is ArgumentException or COMException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    internal sealed record AppEntry(string Name, string Target, string? Location, ImageSource? Icon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int Index;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string Type;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint bind, out nint id, uint mask, out uint attributes);
    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(nint id, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint apartment);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
