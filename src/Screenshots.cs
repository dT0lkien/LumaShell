using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using static LumaShell.Visuals;

namespace LumaShell;

internal sealed class Screenshots(Preferences settings)
{
    private bool busy;
    internal bool Busy => busy;
    internal string Folder => settings.ScreenshotFolder ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Luma Screenshots");
    internal event Action? StateChanged;
    internal event Action<string>? Saved;
    internal event Action<string>? Failed;

    internal string[] Recent()
    {
        try { return Directory.EnumerateFiles(Folder, "Luma-*.png").OrderDescending().Take(4).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    internal void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Desktop.Launch(Folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { Failed?.Invoke("Не удалось открыть папку снимков"); }
    }

    internal bool ChooseFolder()
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Папка для снимков Luma" };
        if (Directory.Exists(Folder)) picker.InitialDirectory = Folder;
        if (picker.ShowDialog() != true) return false;
        settings.ScreenshotFolder = picker.FolderName;
        if (!settings.Save()) Failed?.Invoke("Папка выбрана до перезапуска Luma");
        return true;
    }

    internal async void Begin()
    {
        if (busy) return;
        busy = true;
        try
        {
            var bounds = Forms.Screen.FromPoint(Forms.Cursor.Position).Bounds;
            StateChanged?.Invoke();
            await Task.Delay(110);
            var snapshot = Capture(bounds);
            var crop = await Select(snapshot, bounds);
            if (crop is not { } pixels) return;
            string folder = Folder;
            string path = await Task.Run(() => Save(snapshot, pixels, folder));
            Saved?.Invoke(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ExternalException or
            InvalidOperationException or ArgumentException or OutOfMemoryException)
        { Failed?.Invoke("Windows не удалось сохранить снимок"); }
        finally
        {
            busy = false;
            StateChanged?.Invoke();
        }
    }

    private static BitmapSource Capture(Drawing.Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.Width * bounds.Height > 100_000_000)
            throw new InvalidOperationException("Unsupported monitor size");
        using var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Location, Drawing.Point.Empty, bounds.Size);
        nint handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(handle, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var rgb = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
            rgb.Freeze();
            return rgb;
        }
        finally { DeleteObject(handle); }
    }

    private Task<Int32Rect?> Select(BitmapSource source, Drawing.Rectangle bounds)
    {
        var finished = new TaskCompletionSource<Int32Rect?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = Overlay("Luma • Снимок экрана", bounds.Width, bounds.Height);
        window.AllowsTransparency = false;
        window.Background = Brushes.Black;
        window.ShowActivated = true;
        window.Cursor = Cursors.Cross;
        var root = new Grid();
        var scene = new Grid { Background = Brushes.Black, ClipToBounds = true };
        scene.Children.Add(new Image { Source = source, Stretch = Stretch.Fill, IsHitTestVisible = false });
        var outside = new RectangleGeometry();
        var inside = new RectangleGeometry();
        var dark = new GeometryGroup { FillRule = FillRule.EvenOdd };
        dark.Children.Add(outside);
        dark.Children.Add(inside);
        scene.Children.Add(new System.Windows.Shapes.Path { Data = dark, Fill = Paint("#80000000"), IsHitTestVisible = false });
        scene.Children.Add(new System.Windows.Shapes.Path { Data = inside, Stroke = Brushes.White, StrokeThickness = 1, IsHitTestVisible = false });
        scene.SizeChanged += (_, _) => outside.Rect = new Rect(0, 0, scene.ActualWidth, scene.ActualHeight);
        root.Children.Add(scene);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var instruction = Text("Выделите область мышью", 13, bold: true);
        instruction.Margin = new Thickness(10, 0, 12, 0);
        actions.Children.Add(instruction);
        var toolbar = Surface(Paint("#F5202029"), 15, actions);
        toolbar.Padding = new Thickness(5);
        toolbar.HorizontalAlignment = HorizontalAlignment.Center;
        toolbar.VerticalAlignment = VerticalAlignment.Top;
        toolbar.Margin = new Thickness(12);
        root.Children.Add(toolbar);
        window.Content = root;
        Int32Rect? result = null;
        Point? anchor = null;
        bool closing = false;

        void Complete(Int32Rect? value)
        {
            if (closing) return;
            closing = true;
            result = value;
            window.Close();
        }
        void FullScreen() => Complete(new Int32Rect(0, 0, source.PixelWidth, source.PixelHeight));
        actions.Children.Add(Button("Весь экран", FullScreen));
        actions.Children.Add(Button("Отмена", () => Complete(null), Brushes.Transparent));
        scene.MouseLeftButtonDown += (_, args) =>
        {
            anchor = args.GetPosition(scene);
            scene.CaptureMouse();
            toolbar.Visibility = Visibility.Collapsed;
        };
        Rect DragBounds(Point start, Point end) => new(start,
            new Point(Math.Clamp(end.X, 0, scene.ActualWidth), Math.Clamp(end.Y, 0, scene.ActualHeight)));
        scene.MouseMove += (_, args) =>
        {
            if (anchor is { } start) inside.Rect = DragBounds(start, args.GetPosition(scene));
        };
        scene.MouseLeftButtonUp += (_, args) =>
        {
            if (anchor is not { } start) return;
            var rect = DragBounds(start, args.GetPosition(scene));
            anchor = null;
            scene.ReleaseMouseCapture();
            if (rect.Width < 2 || rect.Height < 2)
            {
                inside.Rect = Rect.Empty;
                toolbar.Visibility = Visibility.Visible;
                return;
            }
            double sx = source.PixelWidth / scene.ActualWidth, sy = source.PixelHeight / scene.ActualHeight;
            int left = (int)Math.Floor(rect.Left * sx), top = (int)Math.Floor(rect.Top * sy);
            int right = Math.Min(source.PixelWidth, (int)Math.Ceiling(rect.Right * sx));
            int bottom = Math.Min(source.PixelHeight, (int)Math.Ceiling(rect.Bottom * sy));
            Complete(new Int32Rect(left, top, right - left, bottom - top));
        };
        window.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) Complete(null);
            else if (args.Key == Key.Enter && anchor is null) FullScreen();
        };
        window.Deactivated += (_, _) => window.Dispatcher.BeginInvoke(new Action(() => Complete(null)));
        window.Closed += (_, _) => { closing = true; finished.TrySetResult(result); };
        nint hwnd = new WindowInteropHelper(window).EnsureHandle();
        Native.MoveWindowPixels(hwnd, bounds);
        window.Show();
        Native.MoveWindowPixels(hwnd, bounds);
        window.Activate();
        root.Focusable = true;
        root.Focus();
        return finished.Task;
    }

    private static string Save(BitmapSource snapshot, Int32Rect crop, string folder)
    {
        Directory.CreateDirectory(folder);
        string name = $"Luma-{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}-{Guid.NewGuid():N}.png";
        string path = Path.Combine(folder, name);
        string temp = path + ".tmp";
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(snapshot, crop)));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) encoder.Save(stream);
            File.Move(temp, path);
            return path;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);
}
