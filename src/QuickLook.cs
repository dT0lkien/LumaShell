using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using static LumaShell.Visuals;

namespace LumaShell;

internal sealed class QuickLook : Window
{
    private readonly ContentControl content = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch
    };
    private bool closed;
    private readonly CancellationTokenSource lifetime = new();
    private PdfDocument? pdf;
    private Image? pdfImage;
    private TextBlock? pageLabel;
    private Button? previousPage, nextPage;
    private ScrollViewer? pageScroll;
    private uint page;
    private bool renderingPage;
    private MediaElement? player;
    private DispatcherTimer? playback;

    internal QuickLook(Window owner, FinderEntry entry, Action<string>? openFolder = null)
    {
        Title = $"{entry.Name} • Быстрый просмотр";
        Owner = owner;
        WindowStartupLocation = owner is FinderWindow ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
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
            if (entry.IsDirectory && openFolder is not null) openFolder(entry.Path);
            else if (entry.IsDirectory && owner is FinderWindow finder) finder.OpenFolder(entry.Path);
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
            if (args.Key is Key.Space or Key.Escape) { args.Handled = true; Close(); }
            else if (pdf is not null && args.Key is Key.Left or Key.Right or Key.PageUp or Key.PageDown)
            {
                args.Handled = true;
                ChangePage(args.Key is Key.Left or Key.PageUp ? -1 : 1);
            }
        };
        Closed += (_, _) =>
        {
            closed = true;
            lifetime.Cancel();
            lifetime.Dispose();
            playback?.Stop();
            player?.Close();
            if (player is not null) player.Source = null;
            pdf = null;
        };
        if (!entry.IsDirectory) Loaded += async (_, _) => await LoadPreview(entry);
    }

    private async Task LoadPreview(FinderEntry entry)
    {
        if (entry.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            await LoadPdf(entry.Path);
            return;
        }
        if (entry.Extension.ToLowerInvariant() is ".mp4" or ".m4v" or ".mov" or ".avi" or ".wmv" or ".mkv" or ".webm" or
            ".mp3" or ".wav" or ".m4a" or ".wma" or ".aac")
        {
            LoadMedia(entry.Path);
            return;
        }
        object? data = await Task.Run(() => ReadPreview(entry));
        if (closed) return;
        if (data is ImageSource image)
            content.Content = CreateImagePreview(image);
        else if (data is string text)
            content.Content = new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                Foreground = White, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14)
            };
        else if (IsImage(entry.Extension)) ShowMessage("Не удалось прочитать изображение");
    }

    private static UIElement CreateImagePreview(ImageSource source)
    {
        var image = new Image { Source = source, Stretch = Stretch.Uniform, Margin = new Thickness(8) };
        var scroller = new ScrollViewer { Content = image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroller.SizeChanged += (_, _) =>
        {
            if (image.Tag is null) image.MaxWidth = Math.Max(1, scroller.ViewportWidth - 16);
        };
        scroller.PreviewMouseWheel += (_, args) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || image.ActualWidth <= 0) return;
            image.Tag = true;
            image.MaxWidth = Math.Clamp(image.ActualWidth * (args.Delta > 0 ? 1.2 : 1 / 1.2), 80, 3000);
            args.Handled = true;
        };
        return scroller;
    }

    private async Task LoadPdf(string path)
    {
        ShowMessage("Открываем PDF…");
        var token = lifetime.Token;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
            var document = await PdfDocument.LoadFromFileAsync(file).AsTask(token);
            if (closed) return;
            if (document.PageCount == 0) { ShowMessage("В PDF нет страниц"); return; }
            pdf = document;
            var layout = new DockPanel();
            var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 6) };
            previousPage = Button(Text("‹", 16), "Предыдущая страница", () => ChangePage(-1));
            nextPage = Button(Text("›", 16), "Следующая страница", () => ChangePage(1));
            pageLabel = Text("", 12);
            pageLabel.Margin = new Thickness(14, 0, 14, 0);
            controls.Children.Add(previousPage);
            controls.Children.Add(pageLabel);
            controls.Children.Add(nextPage);
            DockPanel.SetDock(controls, Dock.Bottom);
            layout.Children.Add(controls);
            pdfImage = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(8) };
            pageScroll = new ScrollViewer { Content = pdfImage, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            pageScroll.PreviewMouseWheel += (_, args) =>
            {
                if (args.Delta < 0 && pageScroll.VerticalOffset >= pageScroll.ScrollableHeight - 1 ||
                    args.Delta > 0 && pageScroll.VerticalOffset < 1)
                {
                    args.Handled = true;
                    ChangePage(args.Delta < 0 ? 1 : -1);
                }
            };
            layout.Children.Add(pageScroll);
            content.Content = layout;
            await RenderPage();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (PreviewError(error))
        { if (!closed) ShowMessage("Не удалось открыть PDF. Возможно, он защищён паролем."); }
    }

    private async void ChangePage(int direction)
    {
        if (pdf is null || renderingPage) return;
        long next = (long)page + direction;
        if (next < 0 || next >= pdf.PageCount) return;
        page = (uint)next;
        await RenderPage();
    }

    private async Task RenderPage()
    {
        if (closed || pdf is not { } document || pdfImage is null) return;
        var token = lifetime.Token;
        renderingPage = true;
        previousPage!.IsEnabled = nextPage!.IsEnabled = false;
        pageLabel!.Text = $"{page + 1} / {document.PageCount}";
        try
        {
            using var selected = document.GetPage(page);
            using var buffer = new InMemoryRandomAccessStream();
            double width = Math.Max(1, selected.Size.Width), height = Math.Max(1, selected.Size.Height);
            double longest = Math.Max(width, height);
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Clamp(Math.Round(1500 * width / longest), 1, 1500),
                DestinationHeight = (uint)Math.Clamp(Math.Round(1500 * height / longest), 1, 1500)
            };
            await selected.RenderToStreamAsync(buffer, options).AsTask(token);
            if (closed) return;
            buffer.Seek(0);
            using var stream = buffer.AsStreamForRead();
            pdfImage.Source = DecodeImage(stream, 0);
            pageScroll!.ScrollToTop();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (PreviewError(error))
        { if (!closed) { pdfImage.Source = null; pageLabel.Text = $"Страница {page + 1} недоступна"; } }
        finally
        {
            renderingPage = false;
            if (!closed)
            {
                previousPage.IsEnabled = page > 0;
                nextPage.IsEnabled = page + 1 < document.PageCount;
            }
        }
    }

    private void LoadMedia(string path)
    {
        var layout = new DockPanel();
        var controls = new DockPanel { Margin = new Thickness(8, 7, 8, 7) };
        var elapsed = Text("0:00", 11, Muted);
        elapsed.Margin = new Thickness(8, 0, 0, 0);
        var seek = new Slider { Minimum = 0, Maximum = 1, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(seek, "Позиция воспроизведения");
        var media = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform };
        player = media;
        bool playing = false, updating = false;
        var playLabel = Text("Пуск", 12, bold: true);
        var play = Button(playLabel, "Воспроизвести или приостановить", () =>
        {
            if (playing) media.Pause(); else media.Play();
            playing = !playing;
            playLabel.Text = playing ? "Пауза" : "Пуск";
        });
        play.IsEnabled = false;
        play.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(play, Dock.Left);
        DockPanel.SetDock(elapsed, Dock.Right);
        controls.Children.Add(play);
        controls.Children.Add(elapsed);
        controls.Children.Add(seek);
        DockPanel.SetDock(controls, Dock.Bottom);
        layout.Children.Add(controls);
        layout.Children.Add(media);
        content.Content = layout;
        playback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        playback.Tick += (_, _) =>
        {
            var position = media.Position;
            elapsed.Text = position.ToString(position.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
            if (seek.IsMouseCaptureWithin) return;
            updating = true;
            seek.Value = position.TotalSeconds;
            updating = false;
        };
        seek.ValueChanged += (_, _) => { if (!updating && seek.IsEnabled) media.Position = TimeSpan.FromSeconds(seek.Value); };
        media.MediaOpened += (_, _) =>
        {
            if (closed) return;
            play.IsEnabled = true;
            if (media.NaturalDuration.HasTimeSpan && media.NaturalDuration.TimeSpan.TotalSeconds > 0)
            {
                seek.Maximum = media.NaturalDuration.TimeSpan.TotalSeconds;
                seek.IsEnabled = true;
            }
            media.Pause();
            playback.Start();
        };
        media.MediaEnded += (_, _) => { playing = false; playLabel.Text = "Пуск"; media.Position = TimeSpan.Zero; media.Pause(); };
        media.MediaFailed += (_, _) =>
        {
            playback.Stop();
            if (!closed) ShowMessage("Windows не смогла воспроизвести этот файл");
        };
        try
        {
            media.ScrubbingEnabled = true;
            media.Source = new Uri(path, UriKind.Absolute);
            media.Pause();
        }
        catch (Exception error) when (PreviewError(error)) { ShowMessage("Не удалось открыть медиафайл"); }
    }

    private void ShowMessage(string message)
    {
        var label = Text(message, 13, Muted);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        content.Content = label;
    }

    internal static bool IsImage(string extension) => extension.ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".ico" or ".webp" or ".heic" or ".heif";

    internal static ImageSource? ReadImage(string path, int width)
    {
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return file.Length <= 40 * 1024 * 1024 ? DecodeImage(file, width) : null;
        }
        catch (Exception error) when (PreviewError(error)) { return null; }
    }

    private static ImageSource DecodeImage(Stream stream, int width)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (width > 0) bitmap.DecodePixelWidth = width;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static bool PreviewError(Exception error) => error is IOException or UnauthorizedAccessException or FileFormatException or
        NotSupportedException or ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException or InvalidOperationException;

    private static object? ReadPreview(FinderEntry entry)
    {
        string extension = entry.Extension.ToLowerInvariant();
        if (IsImage(extension)) return ReadImage(entry.Path, 1800);
        bool text = extension is ".txt" or ".md" or ".csv" or ".log" or ".json" or ".xml" or ".ini" or ".yaml" or ".yml";
        if (!text) return null;
        try
        {
            using var file = File.Open(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            var buffer = new char[16 * 1024];
            int length = reader.ReadBlock(buffer, 0, buffer.Length);
            var result = new string(buffer, 0, length);
            return result.Contains('\0') ? null : result + (reader.EndOfStream ? "" : "\n…");
        }
        catch (Exception error) when (PreviewError(error))
        { return null; }
    }
}
