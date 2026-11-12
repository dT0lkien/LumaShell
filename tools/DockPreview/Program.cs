using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var assembly = Assembly.Load("LumaShell");
        var builtin = assembly.GetType("LumaShell.DockArt", throwOnError: true)!
            .GetMethod("Builtin", BindingFlags.Static | BindingFlags.NonPublic)!;
        (string Target, string Arguments)[] items =
        [
            ("explorer.exe", ""), ("explorer.exe", "shell:AppsFolder"), ("https://www.bing.com/", ""),
            ("notepad.exe", ""), ("cmd.exe", ""), ("ms-settings:", ""),
            ("explorer.exe", "shell:Downloads"), ("explorer.exe", "shell:RecycleBinFolder")
        ];
        // Exercise the actual frozen WPF drawings and rasterizer, not a second copy of the artwork.
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new LinearGradientBrush(Color.FromRgb(24, 80, 64), Color.FromRgb(16, 35, 53), 90), null, new Rect(0, 0, 584, 124));
            context.DrawRoundedRectangle(new LinearGradientBrush(Color.FromArgb(157, 133, 137, 145), Color.FromArgb(170, 57, 59, 67), 45),
                new Pen(new SolidColorBrush(Color.FromArgb(106, 255, 255, 255)), 1), new Rect(14, 23, 556, 84), 23, 23);
            for (int index = 0; index < items.Length; index++)
            {
                var image = builtin.Invoke(null, [items[index].Target, items[index].Arguments]) as ImageSource
                    ?? throw new InvalidOperationException("Missing artwork for " + items[index]);
                if (!image.IsFrozen || image.Width != 64 || image.Height != 64) throw new InvalidOperationException("Invalid icon canvas");
                context.DrawImage(image, new Rect(25 + index * 66, 31, 56, 56));
                if (index < 6) context.DrawEllipse(Brushes.White, null, new Point(53 + index * 66, 96), 2.5, 2.5);
            }
            context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(96, 255, 255, 255)), 1), new Point(415, 47), new Point(415, 82));
        }
        var bitmap = new RenderTargetBitmap(1168, 248, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string output = args.Length == 0 ? "Dock-Preview.png" : args[0];
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using (var file = File.Create(output)) encoder.Save(file);
        Console.WriteLine("Rendered all eight Dock icons: " + output);
        if (args.Length > 1) FinderPreview.VerifyAndRender(assembly, args[1]);
    }
}
