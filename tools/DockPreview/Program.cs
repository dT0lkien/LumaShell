using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LumaShell;

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
        string output = args.Length == 0 ? "Dock-Preview.png" : args[0];
        RenderDock(output);
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

    private static void RenderDock(string output)
    {
        using var updates = new Updates();
        var desktop = new Desktop(new Preferences(), _ => { }, NoAction, NoAction, NoAction, NoAction, NoAction,
            NoAction, updates, NoAction, NoAction, NoAction, _ => { }, NoAction, NoAction, NoAction, NoAction);
        try
        {
            desktop.SetFinderHandle(1);
            var layout = (Grid)desktop.DockWindow.Content;
            desktop.DockWindow.Content = null;
            const int width = 640, height = 120;
            layout.Measure(new Size(width, height));
            layout.Arrange(new Rect(0, 0, width, height));
            layout.UpdateLayout();
            var icons = Descendants<Image>(layout).ToArray();
            if (icons.Length != 8 || icons.Any(icon => icon.Source is not { IsFrozen: true, Width: 64, Height: 64 }))
                throw new InvalidOperationException("Invalid rendered Dock icons.");

            var idle = Snapshot(layout, width, height);
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
            var row = (StackPanel)typeof(Desktop).GetField("dockRow", instance)!.GetValue(desktop)!;
            var centerIcon = icons[3];
            var center = centerIcon.TranslatePoint(new Point(centerIcon.ActualWidth / 2, 0), row).X;
            typeof(Desktop).GetMethod("CalculateTargets", instance)!.Invoke(desktop, [center]);
            typeof(Desktop).GetMethod("UpdateIconTransforms", instance)!.Invoke(desktop, [1d]);
            var bounds = new Rect(new Point(), centerIcon.RenderSize);
            if (centerIcon.TransformToAncestor(layout).TransformBounds(bounds).Width <= centerIcon.ActualWidth * 1.3)
                throw new InvalidOperationException("Dock magnification did not reach the hovered icon.");
            double previous = 0;
            foreach (var icon in icons)
            {
                var rect = icon.TransformToAncestor(layout).TransformBounds(new Rect(new Point(), icon.RenderSize));
                if (rect.Left < previous || rect.Top < 0 || rect.Right > width)
                    throw new InvalidOperationException("Dock icons overlapped or exceeded the available space.");
                previous = rect.Right;
            }

            var hovered = Snapshot(layout, width, height);
            var composite = new DrawingVisual();
            using (var dc = composite.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(36, 72, 78), Color.FromRgb(30, 38, 55), 18),
                    null, new Rect(0, 0, width, height * 2 + 12));
                dc.DrawImage(idle, new Rect(0, 0, width, height));
                dc.DrawImage(hovered, new Rect(0, height, width, height));
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Snapshot(composite, width, height * 2 + 12)));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            using var file = File.Create(output);
            encoder.Save(file);
            Console.WriteLine("Rendered the Dock at rest and with the hovered icon magnified: " + output);
        }
        finally
        {
            desktop.DockWindow.Close();
            desktop.MenuBar.Close();
        }
    }

    private static RenderTargetBitmap Snapshot(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void NoAction() { }

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
