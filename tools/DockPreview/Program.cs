using System;
using System.Diagnostics;
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
        if (args.Length > 1 && args[0] != "--verify-child")
        {
            RunAndReportWindowsErrors(args);
            return;
        }
        if (args.Length > 0 && args[0] == "--verify-child") args = args[1..];
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
        if (args.Length > 1)
        {
            try { FinderPreview.VerifyAndRender(assembly, args[1]); }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                Environment.ExitCode = 1;
            }
        }
    }

    private static void RunAndReportWindowsErrors(string[] args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--verify-child");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        child.WaitForExit();
        Environment.ExitCode = child.ExitCode;
        if (child.ExitCode == 0) return;
        Console.Error.WriteLine($"The Windows preview process exited with {child.ExitCode} (0x{child.ExitCode:X8}).");

        var query = new ProcessStartInfo("wevtutil.exe") { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var arg in new[] { "qe", "Application", "/q:*[System[(EventID=1000 or EventID=1026) and TimeCreated[timediff(@SystemTime)<=180000]]]",
                     "/rd:true", "/c:8", "/f:text" }) query.ArgumentList.Add(arg);
        using var events = Process.Start(query)!;
        foreach (string entry in events.StandardOutput.ReadToEnd().Split("\r\n\r\n"))
            if (entry.Contains("DockPreview", StringComparison.OrdinalIgnoreCase)) Console.Error.WriteLine(entry);
        events.WaitForExit();
    }
}
