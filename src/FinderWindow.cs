using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using static LumaShell.Visuals;

namespace LumaShell;

internal sealed class FinderWindow : Window
{
    private static readonly Brush BackgroundColor = Paint("#FF202720");
    private static readonly Brush SidebarColor = Paint("#FF1B211C");
    private static readonly Brush Stroke = Paint("#FF3B463D");
    private static readonly Brush ToolColor = Paint("#FF29332B");
    private static readonly Brush Stripe = Paint("#78364137");
    private static readonly Brush Selection = Paint("#FF274D68");
    private static readonly Brush Secondary = Paint("#FFADB8AF");
    private static readonly Brush Accent = Paint("#FF84C8FF");
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private const string FolderGlyph = "M1,5 H8 L10,7 H19 V17 H1 Z";
    private readonly ListView list = new()
    {
        Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        SelectionMode = SelectionMode.Extended, Margin = new Thickness(10, 0, 10, 0),
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBlock heading = Text("Проводник", 19, bold: true);
    private readonly TextBlock status = Text("", 11, Secondary);
    private readonly StackPanel pathBar = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox search = Field("Поиск в папке");
    private readonly TextBox address = Field("Путь к папке");
    private readonly StackPanel places = new();
    private readonly List<(Button Button, string Path)> placeButtons = [];
    private readonly List<string> history = [];
    private readonly Button back;
    private readonly Button forward;
    private readonly Button up;
    private readonly GridViewColumn nameColumn = new() { Width = 300 };
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private FinderEntry[] entries = [];
    private CancellationTokenSource? load;
    private FileSystemWatcher? watcher;
    private string? folder;
    private int historyIndex = -1;
    private SortBy sort = SortBy.Name;
    private bool descending;
    private bool copying;
    private bool showingItemMenu;
    private bool closed;
    private QuickLook? preview;

    internal FinderWindow()
    {
        Title = "Luma Finder";
        Width = 1060;
        Height = 650;
        MinWidth = 730;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Background = BackgroundColor;
        Foreground = White;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        UseLayoutRounding = true;
        var chrome = new WindowChrome
        {
            CaptionHeight = 48, ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(17), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false
        };
        WindowChrome.SetWindowChrome(this, chrome);

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(184), MinWidth = 146, MaxWidth = 260 });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new DockPanel();
        var lights = new StackPanel { Orientation = Orientation.Horizontal, Height = 43, Margin = new Thickness(12, 0, 0, 0) };
        lights.Children.Add(Light("#FFFF605C", "Закрыть окно", Close));
        lights.Children.Add(Light("#FFFFBD44", "Свернуть окно", () => WindowState = WindowState.Minimized));
        lights.Children.Add(Light("#FF29C951", "Развернуть или восстановить окно",
            () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized));
        DockPanel.SetDock(lights, Dock.Top);
        sidebar.Children.Add(lights);
        sidebar.Children.Add(new ScrollViewer { Content = places, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var sideSurface = Surface(SidebarColor, 15, sidebar);
        sideSurface.BorderBrush = Stroke;
        sideSurface.BorderThickness = new Thickness(1);
        sideSurface.Margin = new Thickness(8, 8, 0, 8);
        layout.Children.Add(sideSurface);
        var sidebarDivider = new GridSplitter
        {
            Width = 7, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.CurrentAndNext,
            Background = Brushes.Transparent, Margin = new Thickness(0, 49, 0, 8)
        };
        Panel.SetZIndex(sidebarDivider, 1);
        AutomationProperties.SetName(sidebarDivider, "Ширина боковой панели");
        layout.Children.Add(sidebarDivider);
        AddSection("Избранное");
        AddPlace("Рабочий стол", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "M2,3 H18 V15 H2 Z M6,18 H14 M10,15 V18");
        AddPlace("Документы", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "M5,2 H12 L16,6 V18 H5 Z M12,2 V6 H16");
        AddPlace("Загрузки", FinderFiles.Downloads, "M10,3 V13 M6,9 L10,13 L14,9 M4,16 H16");
        AddPlace("Изображения", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "M2,3 H18 V17 H2 Z M3,15 L8,9 L12,14 L15,11 L18,14");
        AddSection("Расположения");
        AddPlace("Домашняя папка", Home, "M2,9 L10,2 L18,9 M5,8 V17 H15 V8 M9,17 V12 H12 V17");
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
                AddPlace($"Диск {drive.Name.TrimEnd('\\')}", drive.RootDirectory.FullName, "M3,5 H17 L19,13 V17 H1 V13 Z M1,13 H19 M5,15 H6");
        }
        catch (IOException) { }

        var content = new DockPanel();
        Grid.SetColumn(content, 1);
        layout.Children.Add(content);
        var toolbar = new DockPanel { Height = 50, Margin = new Thickness(12, 3, 10, 2) };
        var navigation = new StackPanel { Orientation = Orientation.Horizontal };
        back = Tool("M13,3 L6,10 L13,17", "Назад", () => GoHistory(-1));
        forward = Tool("M7,3 L14,10 L7,17", "Вперёд", () => GoHistory(1));
        up = Tool("M3,12 L10,5 L17,12", "На уровень выше", GoUp);
        navigation.Children.Add(back);
        navigation.Children.Add(forward);
        navigation.Children.Add(up);
        var navSurface = Pill(navigation);
        DockPanel.SetDock(navSurface, Dock.Left);
        toolbar.Children.Add(navSurface);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
        actions.Children.Add(Tool("M2,4 H18 M2,10 H14 M2,16 H10", "Сортировать файлы", ShowSortMenu));
        actions.Children.Add(Tool("M3,10 H4 M9,10 H10 M15,10 H16", "Действия с папкой", ShowFolderMenu));
        var actionSurface = Pill(actions);
        DockPanel.SetDock(actionSurface, Dock.Right);
        var searchLayout = new DockPanel();
        var lens = Icon("M13,7 A6,6 0 1 1 1,7 A6,6 0 1 1 13,7 M11,11 L18,18", 15, Secondary);
        lens.Margin = new Thickness(9, 0, 0, 0);
        DockPanel.SetDock(lens, Dock.Left);
        searchLayout.Children.Add(lens);
        searchLayout.Children.Add(WithPlaceholder(search, "Поиск"));
        var searchSurface = Pill(searchLayout);
        searchSurface.Width = 150;
        WindowChrome.SetIsHitTestVisibleInChrome(searchSurface, true);
        DockPanel.SetDock(searchSurface, Dock.Right);
        toolbar.Children.Add(searchSurface);
        toolbar.Children.Add(actionSurface);

        var nameArea = new Grid { Margin = new Thickness(11, 0, 8, 0) };
        nameArea.Children.Add(heading);
        address.Visibility = Visibility.Collapsed;
        WindowChrome.SetIsHitTestVisibleInChrome(address, true);
        address.Background = ToolColor;
        nameArea.Children.Add(address);
        toolbar.Children.Add(nameArea);
        DockPanel.SetDock(toolbar, Dock.Top);
        content.Children.Add(toolbar);
        status.Margin = new Thickness(10, 0, 12, 0);
        var footerContent = new DockPanel { Margin = new Thickness(7, 0, 0, 0) };
        DockPanel.SetDock(status, Dock.Right);
        footerContent.Children.Add(status);
        var pathScroll = new ScrollViewer { Content = pathBar, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
        pathBar.SizeChanged += (_, _) => pathScroll.ScrollToRightEnd();
        footerContent.Children.Add(pathScroll);
        var footer = new Border { Height = 32, BorderBrush = Stroke, BorderThickness = new Thickness(0, 1, 0, 0), Child = footerContent };
        DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(footer);
        content.Children.Add(list);
        var frame = new Border { Background = BackgroundColor, BorderBrush = Stroke, BorderThickness = new Thickness(1), Child = layout };
        Content = frame;
        SourceInitialized += (_, _) => ApplyBackdrop(chrome, frame, sideSurface);

        ConfigureList();
        search.TextChanged += (_, _) => RenderEntries();
        address.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                args.Handled = true;
                string target = address.Text;
                EndAddress();
                Navigate(target);
            }
            else if (args.Key == Key.Escape) { args.Handled = true; EndAddress(); }
        };
        address.LostKeyboardFocus += (_, _) => { if (address.IsVisible) EndAddress(); };
        PreviewKeyDown += OnKey;
        refreshTimer.Tick += (_, _) => { refreshTimer.Stop(); Reload(); };
        Closed += (_, _) =>
        {
            closed = true;
            load?.Cancel();
            watcher?.Dispose();
            refreshTimer.Stop();
        };
        UpdateNavigation();
    }

    internal nint Handle => new WindowInteropHelper(this).Handle;

    private void ApplyBackdrop(WindowChrome chrome, Border frame, Border sidebar)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || SystemParameters.HighContrast ||
            HwndSource.FromHwnd(Handle)?.CompositionTarget is not { } target) return;
        try
        {
            const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            if (Microsoft.Win32.Registry.GetValue(key, "EnableTransparency", 1) is 0) return;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return; }

        int acrylic = 3;
        if (DwmSetWindowAttribute(Handle, 38, ref acrylic, sizeof(int)) < 0) return;
        int dark = 1, rounded = 2;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
        chrome.GlassFrameThickness = new Thickness(-1);
        target.BackgroundColor = Colors.Transparent;
        Background = Brushes.Transparent;
        frame.Background = Paint("#B21C2821");
        sidebar.Background = Paint("#A117211A");
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    internal void OpenFolder(string? target)
    {
        BringForward();
        if (target is not null) Navigate(target);
        else if (folder is null && load is null)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Navigate(string.IsNullOrWhiteSpace(desktop) ? Home : desktop);
        }
    }

    internal void Reveal(string path)
    {
        if (Path.GetDirectoryName(path) is not { Length: > 0 } parent) return;
        BringForward();
        Navigate(parent, reveal: path);
    }

    private void BringForward()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private static TextBox Field(string label)
    {
        var field = new TextBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = White,
            CaretBrush = White, VerticalContentAlignment = VerticalAlignment.Center, FontSize = 13,
            Padding = new Thickness(8, 3, 8, 3)
        };
        AutomationProperties.SetName(field, label);
        return field;
    }

    private static Grid WithPlaceholder(TextBox field, string text)
    {
        var placeholder = Text(text, 13, Secondary);
        placeholder.IsHitTestVisible = false;
        placeholder.Margin = new Thickness(8, 0, 0, 0);
        field.TextChanged += (_, _) => placeholder.Visibility = field.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var grid = new Grid();
        grid.Children.Add(placeholder);
        grid.Children.Add(field);
        return grid;
    }

    private static Button Light(string color, string label, Action action)
    {
        var light = new Grid { Width = 13, Height = 13 };
        light.Children.Add(new System.Windows.Shapes.Ellipse { Fill = Paint(color), Stroke = Paint("#4D000000"), StrokeThickness = 0.6 });
        string symbol = color switch
        {
            "#FFFF605C" => "M4,4 L12,12 M12,4 L4,12",
            "#FFFFBD44" => "M3,8 H13",
            _ => "M4,11 V5 H10 M6,13 H12 V7"
        };
        var glyph = Icon(symbol, 7, Paint("#B825241F"));
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.Visibility = Visibility.Hidden;
        light.Children.Add(glyph);
        var button = Button(light, label, action, Brushes.Transparent, 0);
        button.Width = 23;
        button.FocusVisualStyle = null;
        button.MouseEnter += (_, _) => glyph.Visibility = Visibility.Visible;
        button.MouseLeave += (_, _) => glyph.Visibility = Visibility.Hidden;
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        return button;
    }

    private static Button Tool(string path, string label, Action action)
    {
        var button = Button(Icon(path, 16), label, action, Brushes.Transparent, 7);
        button.Width = 36;
        button.Height = 32;
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        return button;
    }

    private static Border Pill(UIElement content)
    {
        var border = Surface(ToolColor, 16, content);
        border.VerticalAlignment = VerticalAlignment.Center;
        border.BorderBrush = Stroke;
        border.BorderThickness = new Thickness(1);
        border.MinHeight = 34;
        return border;
    }

    private void AddSection(string label)
    {
        var title = Text(label, 11, Secondary, true);
        title.Margin = new Thickness(15, 14, 0, 4);
        places.Children.Add(title);
    }

    private void AddPlace(string label, string path, string glyph)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Icon(glyph, 16);
        icon.Margin = new Thickness(0, 0, 9, 0);
        row.Children.Add(icon);
        row.Children.Add(Text(label, 13));
        var button = Button(row, label, () => Navigate(path), Brushes.Transparent, 9);
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(7, 1, 7, 1);
        button.Height = 33;
        places.Children.Add(button);
        placeButtons.Add((button, path));
    }

    private void ConfigureList()
    {
        var view = new GridView { AllowsColumnReorder = false };
        nameColumn.Header = ColumnHeader("Имя", SortBy.Name);
        nameColumn.CellTemplate = NameTemplate();
        view.Columns.Add(nameColumn);
        view.Columns.Add(Column("Дата изменения", nameof(FinderEntry.ModifiedLabel), 148, SortBy.Modified));
        view.Columns.Add(Column("Размер", nameof(FinderEntry.SizeLabel), 78, SortBy.Size));
        view.Columns.Add(Column("Тип", nameof(FinderEntry.Kind), 82, SortBy.Kind));
        view.ColumnHeaderContainerStyle = HeaderStyle();
        list.View = view;
        list.ItemContainerStyle = RowStyle();
        list.AlternationCount = 2;
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.SizeChanged += (_, _) => nameColumn.Width = Math.Max(130, list.ActualWidth - 330);
        list.MouseDoubleClick += (_, args) =>
        {
            if (RowUnder(args.OriginalSource) is not null) OpenSelected();
        };
        list.PreviewMouseRightButtonDown += (_, args) =>
        {
            if (RowUnder(args.OriginalSource) is { IsSelected: false } row)
            {
                list.UnselectAll();
                row.IsSelected = true;
            }
        };
        list.SelectionChanged += (_, _) => UpdateStatus();
        list.PreviewMouseRightButtonUp += (_, args) =>
        {
            args.Handled = true;
            if (RowUnder(args.OriginalSource) is not null) ShowSelectedMenu(list.PointToScreen(args.GetPosition(list)));
            else ShowBackgroundMenu();
        };
    }

    private ListViewItem? RowUnder(object source) => source is DependencyObject visual
        ? ItemsControl.ContainerFromElement(list, visual) as ListViewItem : null;

    private GridViewColumnHeader ColumnHeader(string label, SortBy column)
    {
        var header = new GridViewColumnHeader { Content = label };
        header.Click += (_, _) => ChangeSort(column);
        return header;
    }

    private GridViewColumn Column(string label, string property, double width, SortBy column)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(property));
        text.SetValue(TextBlock.ForegroundProperty, Secondary);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        return new GridViewColumn
        {
            Header = ColumnHeader(label, column), Width = width, CellTemplate = new DataTemplate { VisualTree = text }
        };
    }

    private static DataTemplate NameTemplate()
    {
        var dock = new FrameworkElementFactory(typeof(DockPanel));
        dock.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        var icon = new FrameworkElementFactory(typeof(Image));
        icon.SetBinding(Image.SourceProperty, new Binding(nameof(FinderEntry.Icon)));
        icon.SetValue(WidthProperty, 16d);
        icon.SetValue(HeightProperty, 16d);
        icon.SetValue(MarginProperty, new Thickness(0, 0, 9, 0));
        dock.AppendChild(icon);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(FinderEntry.Name)));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        dock.AppendChild(text);
        return new DataTemplate { VisualTree = dock };
    }

    private static Style HeaderStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.BorderBrushProperty, Stroke);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
        border.SetValue(Border.PaddingProperty, new Thickness(7, 0, 2, 0));
        var text = new FrameworkElementFactory(typeof(ContentPresenter));
        text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(text);
        var style = new Style(typeof(GridViewColumnHeader));
        style.Setters.Add(new Setter(ForegroundProperty, Secondary));
        style.Setters.Add(new Setter(FontSizeProperty, 11d));
        style.Setters.Add(new Setter(HeightProperty, 28d));
        style.Setters.Add(new Setter(TemplateProperty, new ControlTemplate(typeof(GridViewColumnHeader)) { VisualTree = border }));
        return style;
    }

    private static Style RowStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "RowBackground" };
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(GridViewRowPresenter));
        presenter.SetValue(GridViewRowPresenter.ContentProperty, new TemplateBindingExtension(ContentProperty));
        presenter.SetValue(GridViewRowPresenter.ColumnsProperty, new TemplateBindingExtension(GridView.ColumnCollectionProperty));
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(ListViewItem)) { VisualTree = border };
        AddRowTrigger(ItemsControl.AlternationIndexProperty, 1, Stripe);
        AddRowTrigger(IsMouseOverProperty, true, ToolColor);
        AddRowTrigger(ListBoxItem.IsSelectedProperty, true, Selection);
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(HeightProperty, 26d));
        style.Setters.Add(new Setter(ForegroundProperty, White));
        style.Setters.Add(new Setter(TemplateProperty, template));
        return style;

        void AddRowTrigger(DependencyProperty property, object value, Brush brush)
        {
            var trigger = new Trigger { Property = property, Value = value };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, brush, "RowBackground"));
            template.Triggers.Add(trigger);
        }
    }

    private async void Navigate(string target, int? index = null, bool reload = false, string? reveal = null)
    {
        string destination;
        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
            destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded, folder ?? Home));
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        { status.Text = "Проверь путь к папке"; return; }
        if (!reload && index is null && destination.Equals(folder, StringComparison.OrdinalIgnoreCase))
        {
            if (reveal is not null)
            {
                load?.Cancel();
                SelectPath(reveal);
            }
            return;
        }
        if (reload && load is not null) return;
        load?.Cancel();
        var request = new CancellationTokenSource();
        load = request;
        if (!reload) status.Text = "Открываем папку…";
        try
        {
            var loaded = await Task.Run(() => FinderFiles.ReadFolder(destination, request.Token));
            if (closed || load != request || request.IsCancellationRequested) return;
            var selectedPaths = reload ? SelectedPaths() : [];
            entries = loaded;
            if (!reload)
            {
                folder = destination;
                if (index is int position) historyIndex = position;
                else
                {
                    if (historyIndex + 1 < history.Count) history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
                    history.Add(destination);
                    historyIndex = history.Count - 1;
                }
                string name = Path.GetFileName(destination);
                heading.Text = string.IsNullOrEmpty(name) ? destination : name;
                heading.ToolTip = destination;
                Title = $"{heading.Text} • Luma Finder";
                UpdateNavigation();
                UpdatePathBar(destination);
                WatchFolder(destination);
                if (search.Text.Length != 0) search.Clear();
                else RenderEntries();
                if (reveal is not null) SelectPath(reveal);
                else if (list.Items.Count > 0) list.ScrollIntoView(list.Items[0]);
            }
            else
            {
                RenderEntries();
                if (selectedPaths.Length > 0)
                    foreach (var item in list.Items.OfType<FinderEntry>())
                        if (selectedPaths.Contains(item.Path, StringComparer.OrdinalIgnoreCase)) list.SelectedItems.Add(item);
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            if (!closed && load == request) status.Text = error is UnauthorizedAccessException
                ? "Нет доступа к этой папке" : "Папка недоступна";
        }
        finally
        {
            if (load == request) load = null;
            request.Dispose();
        }
    }

    private void SelectPath(string path)
    {
        if (search.Text.Length != 0) search.Clear();
        if (list.Items.OfType<FinderEntry>().FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) is not { } found)
            return;
        list.SelectedItem = found;
        list.ScrollIntoView(found);
        list.Focus();
    }

    private void UpdatePathBar(string destination)
    {
        var components = new Stack<string>();
        for (string? current = destination; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            components.Push(current);
        pathBar.Children.Clear();
        foreach (string path in components)
        {
            if (pathBar.Children.Count != 0)
                pathBar.Children.Add(Icon("M7,4 L13,10 L7,16", 8, Secondary));
            string name = Path.GetFileName(path);
            var crumb = Button(Text(string.IsNullOrEmpty(name) ? path : name, 11, Secondary), path,
                () => Navigate(path), Brushes.Transparent, 6);
            crumb.MaxWidth = 160;
            pathBar.Children.Add(crumb);
        }
    }

    private void ToggleQuickLook()
    {
        if (preview is { IsVisible: true })
        {
            preview.Close();
            return;
        }
        if (list.SelectedItem is not FinderEntry item) return;
        preview = new QuickLook(this, item);
        preview.Closed += (_, _) => preview = null;
        preview.Show();
    }

    private void WatchFolder(string path)
    {
        watcher?.Dispose();
        watcher = null;
        refreshTimer.Stop();
        try
        {
            watcher = new FileSystemWatcher(path);
            watcher.Created += QueueRefresh;
            watcher.Deleted += QueueRefresh;
            watcher.Changed += QueueRefresh;
            watcher.Renamed += QueueRefresh;
            watcher.Error += (_, _) => QueueRefresh(this, EventArgs.Empty);
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            watcher?.Dispose();
            watcher = null;
        }
    }

    private void QueueRefresh(object sender, EventArgs args) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
    {
        if (closed) return;
        refreshTimer.Stop();
        refreshTimer.Start();
    }));

    private void Reload() { if (folder is { } path) Navigate(path, reload: true); }
    private void GoHistory(int delta)
    {
        int next = historyIndex + delta;
        if (next >= 0 && next < history.Count) Navigate(history[next], next);
    }
    private void GoUp()
    {
        if (folder is not null && Path.GetDirectoryName(folder) is { } parent) Navigate(parent);
    }

    private void UpdateNavigation()
    {
        back.IsEnabled = historyIndex > 0;
        forward.IsEnabled = historyIndex >= 0 && historyIndex < history.Count - 1;
        up.IsEnabled = folder is not null && Path.GetDirectoryName(folder) is not null;
        foreach (var (button, path) in placeButtons)
        {
            bool selected = string.Equals(Path.TrimEndingDirectorySeparator(path), folder, StringComparison.OrdinalIgnoreCase);
            button.Background = selected ? Stripe : Brushes.Transparent;
            if (button.Content is StackPanel row)
            {
                if (row.Children[0] is System.Windows.Shapes.Path icon) icon.Stroke = selected ? Accent : White;
                if (row.Children[1] is TextBlock text) text.Foreground = selected ? Accent : White;
            }
        }
    }

    private void RenderEntries()
    {
        string filter = search.Text.Trim();
        var matching = entries.Where(entry => filter.Length == 0 || entry.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(entry => entry.IsDirectory);
        var ordered = sort switch
        {
            SortBy.Modified => descending ? matching.ThenByDescending(entry => entry.Modified) : matching.ThenBy(entry => entry.Modified),
            SortBy.Size => descending ? matching.ThenByDescending(entry => entry.Size) : matching.ThenBy(entry => entry.Size),
            SortBy.Kind => descending ? matching.ThenByDescending(entry => entry.Kind, StringComparer.CurrentCultureIgnoreCase)
                : matching.ThenBy(entry => entry.Kind, StringComparer.CurrentCultureIgnoreCase),
            _ => descending ? matching.ThenByDescending(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                : matching.ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        list.ItemsSource = ordered.ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        int count = list.Items.Count;
        string total = search.Text.Length == 0 ? $"Объектов: {count}" : $"Найдено: {count} из {entries.Length}";
        status.Text = list.SelectedItems.Count == 0 ? total : $"{total}   ·   Выбрано: {list.SelectedItems.Count}";
    }

    private void ChangeSort(SortBy column)
    {
        descending = sort == column ? !descending : column is SortBy.Modified or SortBy.Size;
        sort = column;
        RenderEntries();
    }

    private void OpenSelected()
    {
        if (list.SelectedItem is not FinderEntry item) return;
        if (item.IsDirectory) Navigate(item.Path);
        else if (!Desktop.Launch(item.Path)) status.Text = "Не удалось открыть файл";
    }

    private string[] SelectedPaths() => list.SelectedItems.OfType<FinderEntry>().Select(item => item.Path).ToArray();

    private void ShowSelectedMenu(Point location)
    {
        if (showingItemMenu || list.SelectedItem is not FinderEntry selected) return;
        string[] paths = SelectedPaths();
        showingItemMenu = true;
        try
        {
            switch (FinderShellMenu.Show(Handle, location, paths, paths.Length == 1 && selected.IsDirectory))
            {
                case FinderShellMenu.Result.Preview:
                    ToggleQuickLook();
                    break;
                case FinderShellMenu.Result.OpenFolder:
                    Navigate(selected.Path);
                    break;
                case FinderShellMenu.Result.CopyPath:
                    CopyPath(string.Join(Environment.NewLine, paths));
                    break;
                case FinderShellMenu.Result.Invoked:
                    Reload();
                    break;
                case FinderShellMenu.Result.Failed:
                    status.Text = "Windows не удалось выполнить команду";
                    break;
                case FinderShellMenu.Result.Unavailable:
                    ShowFallbackMenu(selected, paths);
                    break;
            }
        }
        finally { showingItemMenu = false; }
    }

    private void ShowFallbackMenu(FinderEntry selected, string[] paths)
    {
        var commands = new List<(string, Action)> { ("Открыть", OpenSelected) };
        if (paths.Length == 1) commands.Add(("Быстрый просмотр", ToggleQuickLook));
        if (paths.Length == 1 && !selected.IsDirectory)
        {
            commands.Add(("Открыть с помощью…", () => RunVerb(selected.Path, "openas")));
            if (selected.Extension.ToLowerInvariant() is ".exe" or ".com" or ".cmd" or ".bat")
                commands.Add(("Запуск от имени администратора", () => RunVerb(selected.Path, "runas")));
        }
        commands.Add(("Копировать", CopySelected));
        commands.Add(("Копировать путь", () => CopyPath(string.Join(Environment.NewLine, paths))));
        commands.Add(("Удалить…", () => Delete(paths)));
        if (paths.Length == 1) commands.Add(("Свойства", () => RunVerb(selected.Path, "properties")));
        Menu(commands.ToArray()).IsOpen = true;
    }

    private void ShowBackgroundMenu() => Menu([
        ("Вставить", Paste), ("Обновить", Reload), ("Открыть в Проводнике Windows", OpenInExplorer)
    ]).IsOpen = true;

    private void RunVerb(string path, string verb)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = verb })?.Dispose(); }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException)
        { status.Text = "Windows не удалось выполнить команду"; }
    }

    private async void Delete(string[] paths)
    {
        if (copying) return;
        copying = true;
        try
        {
            bool deleted = await FinderFiles.DeleteAsync(Handle, paths);
            if (!closed)
            {
                if (deleted) Reload();
                else status.Text = "Удаление не завершено";
            }
        }
        finally { copying = false; }
    }

    private void CopySelected()
    {
        var paths = SelectedPaths();
        if (paths.Length == 0) return;
        try
        {
            var files = new StringCollection();
            files.AddRange(paths);
            Clipboard.SetFileDropList(files);
            status.Text = $"Скопировано: {paths.Length}";
        }
        catch (ExternalException) { status.Text = "Буфер обмена занят"; }
    }

    private async void Paste()
    {
        if (copying || folder is not { } target) return;
        string[] paths;
        bool move = false;
        uint sequence = FinderFiles.GetClipboardSequenceNumber();
        try
        {
            var clipboard = Clipboard.GetDataObject();
            paths = clipboard?.GetData(DataFormats.FileDrop) as string[] ?? [];
            byte[]? effect = clipboard?.GetData("Preferred DropEffect", false) switch
            {
                MemoryStream stream => stream.ToArray(),
                byte[] data => data,
                _ => null
            };
            move = effect is { Length: >= 4 } && BitConverter.ToUInt32(effect) == 2;
        }
        catch (ExternalException) { status.Text = "Буфер обмена занят"; return; }
        if (paths.Length == 0) return;
        copying = true;
        status.Text = move ? "Перемещаем файлы…" : "Копируем файлы…";
        try
        {
            bool copied = await FinderFiles.TransferAsync(Handle, paths, target, move);
            if (copied && move && sequence != 0 && FinderFiles.GetClipboardSequenceNumber() == sequence)
            {
                try { Clipboard.Clear(); }
                catch (ExternalException) { }
            }
            if (!closed && folder == target)
            {
                if (copied) Reload();
                else status.Text = move ? "Перемещение не завершено" : "Копирование не завершено";
            }
        }
        finally { copying = false; }
    }

    private void CopyPath(string path)
    {
        try { Clipboard.SetText(path); status.Text = "Путь скопирован"; }
        catch (ExternalException) { status.Text = "Буфер обмена занят"; }
    }

    private void OpenInExplorer()
    {
        if (folder is { } path) Desktop.Launch("explorer.exe", $"/e,\"{Path.TrimEndingDirectorySeparator(path)}\\.\"");
    }

    private void StartAddress()
    {
        address.Text = folder ?? Home;
        heading.Visibility = Visibility.Collapsed;
        address.Visibility = Visibility.Visible;
        Keyboard.Focus(address);
        address.SelectAll();
    }

    private void EndAddress()
    {
        address.Visibility = Visibility.Collapsed;
        heading.Visibility = Visibility.Visible;
    }

    private void OnKey(object sender, KeyEventArgs args)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (args.Key == Key.L) StartAddress();
            else if (args.Key == Key.F) { Keyboard.Focus(search); search.SelectAll(); }
            else if (args.Key == Key.C && !search.IsKeyboardFocusWithin && !address.IsKeyboardFocusWithin) CopySelected();
            else if (args.Key == Key.V && !search.IsKeyboardFocusWithin && !address.IsKeyboardFocusWithin) Paste();
            else return;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && args.SystemKey is Key.Left or Key.Right or Key.Up)
        {
            if (args.SystemKey == Key.Up) GoUp();
            else GoHistory(args.SystemKey == Key.Left ? -1 : 1);
        }
        else if (args.Key == Key.F5) Reload();
        else if (args.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && list.IsKeyboardFocusWithin) ToggleQuickLook();
        else if (list.IsKeyboardFocusWithin && (args.Key == Key.Apps || args.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            if (list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is ListViewItem row && row.IsVisible)
                ShowSelectedMenu(row.PointToScreen(new Point(16, row.ActualHeight)));
            else ShowBackgroundMenu();
        }
        else if (args.Key == Key.Enter && list.IsKeyboardFocusWithin) OpenSelected();
        else return;
        args.Handled = true;
    }

    private static ContextMenu Menu((string Label, Action Action)[] commands)
    {
        var menu = PopupMenu();
        foreach (var (label, action) in commands)
            menu.Items.Add(MenuEntry(label, action));
        return menu;
    }

    private void ShowSortMenu() => Menu([
        ("Имя", () => ChangeSort(SortBy.Name)), ("Дата изменения", () => ChangeSort(SortBy.Modified)),
        ("Размер", () => ChangeSort(SortBy.Size)), ("Тип", () => ChangeSort(SortBy.Kind))
    ]).IsOpen = true;

    private void ShowFolderMenu() => Menu([
        ("Перейти к папке…", StartAddress), ("Обновить", Reload), ("Открыть в Проводнике Windows", OpenInExplorer)
    ]).IsOpen = true;

    private enum SortBy { Name, Modified, Size, Kind }
}
