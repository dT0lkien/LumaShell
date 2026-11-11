using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static LumaShell.Visuals;

namespace LumaShell;

internal sealed class Launcher
{
    private readonly Action openFinder;
    private readonly Window window = Overlay("Luma • Приложения", 600, 470);
    private readonly TextBox query = new() { FontSize = 19, Foreground = White, Background = Brushes.Transparent,
        BorderThickness = new Thickness(0), CaretBrush = White, Padding = new Thickness(7, 7, 0, 7), MaxLength = 160 };
    private readonly ListBox results = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        Foreground = White, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock status = Text("Загружаем приложения…", 12, Muted);
    private readonly TextBlock placeholder = Text("Поиск приложений", 18, Muted);
    private CatalogItem[]? catalog;
    private bool loading;

    internal Launcher(Action openFinder)
    {
        this.openFinder = openFinder;
        window.ShowActivated = true;
        results.ItemContainerStyle = ItemStyle();
        var layout = new DockPanel();
        var heading = new DockPanel { Margin = new Thickness(4, 1, 4, 8) };
        var title = Text("ПРИЛОЖЕНИЯ", 11, Blue, true);
        title.Margin = new Thickness(3, 0, 0, 0);
        var apps = Button("Все приложения Windows", () => { Close(); Desktop.Launch("explorer.exe", "shell:AppsFolder"); });
        DockPanel.SetDock(apps, Dock.Right);
        heading.Children.Add(apps);
        heading.Children.Add(title);
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);
        var search = new DockPanel { Margin = new Thickness(9, 0, 7, 0) };
        var lens = Icon("M13,7 A6,6 0 1 1 1,7 A6,6 0 1 1 13,7 M11,11 L18,18", 16, Blue);
        DockPanel.SetDock(lens, Dock.Left);
        search.Children.Add(lens);
        var field = new Grid();
        placeholder.Margin = new Thickness(7, 0, 0, 0);
        placeholder.IsHitTestVisible = false;
        field.Children.Add(placeholder);
        field.Children.Add(query);
        search.Children.Add(field);
        var searchCard = Surface(Paint("#FF2A2B38"), 11, search);
        searchCard.Margin = new Thickness(2, 0, 2, 8);
        DockPanel.SetDock(searchCard, Dock.Top);
        layout.Children.Add(searchCard);
        status.Margin = new Thickness(7, 7, 7, 0);
        DockPanel.SetDock(status, Dock.Bottom);
        layout.Children.Add(status);
        layout.Children.Add(results);
        var panel = Surface(Gradient("#F5262836", "#FC15151E"), 23, layout);
        panel.BorderBrush = Paint("#68FFFFFF");
        panel.BorderThickness = new Thickness(1);
        panel.Padding = new Thickness(15);
        window.Content = panel;
        FocusManager.SetFocusedElement(window, query);
        window.Activated += (_, _) => Keyboard.Focus(query);
        window.Deactivated += (_, _) => Close();
        window.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { args.Handled = true; Close(); }
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
                ItemsControl.ContainerFromElement(results, source) is not ListBoxItem { Tag: CatalogItem item }) return;
            args.Handled = true;
            Run(item);
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
        selected.Setters.Add(new Setter(Border.BackgroundProperty, Paint("#FF394460"), "Row"));
        template.Triggers.Add(hover);
        template.Triggers.Add(selected);
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    internal void Toggle(Rect area)
    {
        if (window.IsVisible) { Close(); return; }
        window.Width = Math.Min(600, area.Width - 20);
        window.Height = Math.Min(470, area.Height - 20);
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

    internal void Close() { if (window.IsVisible) window.Hide(); }

    private async void Load()
    {
        loading = true;
        var complete = new TaskCompletionSource<CatalogItem[]>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            .OrderByDescending(item => item.Name.StartsWith(filter, StringComparison.CurrentCultureIgnoreCase)).Take(35).ToArray();
        foreach (var app in matches)
        {
            var row = new DockPanel();
            var icon = app.Icon is null ? (FrameworkElement)Icon("M2,3 H18 V17 H2 Z M3,7 H17", 19, Blue)
                : new Image { Source = app.Icon, Width = 24, Height = 24 };
            icon.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(Text(app.Name, 14, bold: true));
            results.Items.Add(new ListBoxItem { Content = row, Tag = app, Padding = new Thickness(9, 6, 9, 6),
                Foreground = White, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        if (results.Items.Count > 0) results.SelectedIndex = 0;
        status.Text = matches.Length > 0 ? "↑ ↓  Выбрать     Enter  Открыть     Esc  Закрыть" : "Приложения не найдены";
    }

    private void LaunchSelected()
    {
        if (results.SelectedItem is ListBoxItem { Tag: CatalogItem item }) Run(item);
    }

    private void Run(CatalogItem item)
    {
        if (item.Target.Equals(@"shell:AppsFolder\Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase) ||
            item.Target.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase))
        {
            Close();
            openFinder();
            return;
        }
        bool launched = item.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            ? Desktop.Launch("explorer.exe", $"\"{item.Target}\"") : Desktop.Launch(item.Target);
        if (launched) Close();
        else status.Text = "Windows не удалось открыть приложение";
    }

    private static CatalogItem[] ReadCatalog()
    {
        var list = new List<CatalogItem>();
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
                        list.Add(new CatalogItem(Label(name, 90), target, ShellIcon(target)));
                    }
                    catch (Exception error) when (error is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                    finally { Release(entry); }
                }
            }
        }
        catch (Exception error) when (error is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally { Release(items); Release(folder); Release(shell); }
        if (list.Count == 0)
        {
            foreach (var location in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
            {
                string path = Environment.GetFolderPath(location);
                if (!Directory.Exists(path)) continue;
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (string file in Directory.EnumerateFiles(path, "*.lnk", options).Take(250))
                    list.Add(new CatalogItem(Path.GetFileNameWithoutExtension(file), file, ShellIcon(file)));
            }
        }
        return list.DistinctBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static ImageSource? ShellIcon(string target)
    {
        if (SHParseDisplayName(target, 0, out nint id, 0, out _) < 0 || id == 0) return null;
        try
        {
            if (SHGetFileInfo(id, 0, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x109) == 0 || info.Icon == 0) return null;
            try { return IconImage(info.Icon); }
            finally { DestroyIcon(info.Icon); }
        }
        finally { Marshal.FreeCoTaskMem(id); }
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
    private sealed record CatalogItem(string Name, string Target, ImageSource? Icon);

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
}
