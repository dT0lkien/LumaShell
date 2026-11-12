using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using static LumaShell.Visuals;

namespace LumaShell;

internal sealed class QuickLook : Window
{
    private readonly ContentControl content = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch
    };
    private bool closed;

    internal QuickLook(Window owner, FinderEntry entry)
    {
        Title = $"{entry.Name} • Быстрый просмотр";
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;
        Width = Math.Min(760, SystemParameters.WorkArea.Width - 28);
        Height = Math.Min(540, SystemParameters.WorkArea.Height - 35);
        MinWidth = 260;
        MinHeight = 200;
        Background = Paint("#FF202226");
        Foreground = White;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 49, CornerRadius = new CornerRadius(15), ResizeBorderThickness = new Thickness(5),
            GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false
        });

        var layout = new DockPanel();
        var header = new DockPanel { Height = 49, Margin = new Thickness(11, 0, 10, 0) };
        var close = Button(new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = Paint("#FFFF605C") },
            "Закрыть быстрый просмотр", Close, Brushes.Transparent, 6);
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        DockPanel.SetDock(close, Dock.Left);
        header.Children.Add(close);
        var open = Button(entry.IsDirectory ? "Перейти к папке" : "Открыть", () =>
        {
            if (entry.IsDirectory && owner is FinderWindow finder) finder.OpenFolder(entry.Path);
            else if (!Desktop.Launch(entry.Path)) return;
            Close();
        });
        WindowChrome.SetIsHitTestVisibleInChrome(open, true);
        DockPanel.SetDock(open, Dock.Right);
        header.Children.Add(open);
        var heading = Text(entry.Name, 14, bold: true);
        heading.Margin = new Thickness(8, 0, 10, 0);
        header.Children.Add(heading);
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        var metadata = Text($"{entry.Kind}   ·   {entry.SizeLabel}   ·   {entry.ModifiedLabel}", 11, Muted);
        metadata.Margin = new Thickness(13, 7, 10, 9);
        DockPanel.SetDock(metadata, Dock.Bottom);
        layout.Children.Add(metadata);
        var stage = Surface(Paint("#FF17191D"), 11, content);
        stage.Margin = new Thickness(9, 0, 9, 0);
        layout.Children.Add(stage);
        var frame = Surface(Background, 15, layout);
        frame.BorderThickness = new Thickness(1);
        frame.BorderBrush = Line;
        Content = frame;
        content.Content = new Image { Source = entry.Icon, Width = 74, Height = 74, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key is not (Key.Space or Key.Escape)) return;
            args.Handled = true;
            Close();
        };
        Closed += (_, _) => closed = true;
        if (!entry.IsDirectory) Loaded += async (_, _) => await LoadPreview(entry);
    }

    private async Task LoadPreview(FinderEntry entry)
    {
        object? data = await Task.Run(() => ReadPreview(entry));
        if (closed) return;
        if (data is ImageSource image)
            content.Content = new Image { Source = image, Stretch = Stretch.Uniform, Margin = new Thickness(8) };
        else if (data is string text)
            content.Content = new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                Foreground = White, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14)
            };
    }

    private static object? ReadPreview(FinderEntry entry)
    {
        string extension = entry.Extension.ToLowerInvariant();
        bool image = extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff";
        bool text = extension is ".txt" or ".md" or ".csv" or ".log" or ".json" or ".xml" or ".ini" or ".yaml" or ".yml";
        if (!image && !text || image && entry.Size > 40 * 1024 * 1024) return null;
        try
        {
            using var file = File.Open(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (image)
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 1200;
                bitmap.StreamSource = file;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            using var reader = new StreamReader(file);
            var buffer = new char[16 * 1024];
            int length = reader.ReadBlock(buffer, 0, buffer.Length);
            var result = new string(buffer, 0, length);
            return result.Contains('\0') ? null : result + (reader.EndOfStream ? "" : "\n…");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FileFormatException or
            NotSupportedException or ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        { return null; }
    }
}
