using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class FinderPreview
{
    internal static void VerifyAndRender(Assembly assembly, string output)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        string sandbox = Directory.CreateTempSubdirectory("Luma-Finder-").FullName;
        string desktop = Directory.CreateDirectory(Path.Combine(sandbox, "Рабочий стол")).FullName;
        string projects = Directory.CreateDirectory(Path.Combine(desktop, "Проекты")).FullName;
        File.WriteAllText(Path.Combine(projects, "План.txt"), "Luma");
        Directory.CreateDirectory(Path.Combine(desktop, "Архив"));
        string[] files =
        [
            "Обложка.png", "Референсы.pdf", "Заметки.txt", "Подборка.jpg", "Макет.svg", "Палитра.png",
            "Презентация.pdf", "Документы.zip", "Скриншот 2026-11-11.png", "Скриншот 2026-11-10.png",
            "Фото рабочего стола.jpg", "Список задач.txt", "Тема проекта.json", "Черновик.md", "Чертёж.pdf"
        ];
        for (int i = 0; i < files.Length; i++)
        {
            string path = Path.Combine(desktop, files[i]);
            using (var file = File.Create(path)) file.SetLength((i + 1) * 208471);
            File.SetLastWriteTime(path, new DateTime(2026, 11, 11, 10, 20, 0).AddHours(-3 * i));
        }

        var type = assembly.GetType("LumaShell.FinderWindow", throwOnError: true)!;
        var window = (Window)Activator.CreateInstance(type, nonPublic: true)!;
        try
        {
            var open = type.GetMethod("OpenFolder", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var history = type.GetMethod("GoHistory", BindingFlags.Instance | BindingFlags.NonPublic)!;
            open.Invoke(window, [desktop]);
            var content = (FrameworkElement)window.Content;
            var list = Find<ListView>(content) ?? throw new InvalidOperationException("Finder list is missing");
            PumpUntil(() => list.Items.Count == files.Length + 2, "the Desktop folder");
            open.Invoke(window, [projects]);
            PumpUntil(() => list.Items.Count == 1 && window.Title.StartsWith("Проекты"), "the Projects folder");
            history.Invoke(window, [-1]);
            PumpUntil(() => list.Items.Count == files.Length + 2 && window.Title.StartsWith("Рабочий стол"), "back navigation");
            var search = Find<TextBox>(content, box => AutomationProperties.GetName(box) == "Поиск в папке")
                ?? throw new InvalidOperationException("Finder search is missing");
            search.Text = "скриншот";
            if (list.Items.Count != 2) throw new InvalidOperationException("Folder search did not filter the displayed rows.");
            search.Clear();

            VerifyShellMenu(assembly, desktop, sandbox);

            list.SelectedIndex = 0;
            window.UpdateLayout();
            PumpUntil(() => Find<TextBlock>(list, label => label.Text == "Архив") is not null, "rendered file names");
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(output);
            encoder.Save(stream);
            Console.WriteLine("Finder opened both folders, returned through history, filtered files and rendered the list: " + output);
        }
        finally
        {
            window.Close();
            Directory.Delete(sandbox, recursive: true);
            application.Shutdown();
        }
    }

    private static void VerifyShellMenu(Assembly assembly, string folder, string sandbox)
    {
        var read = assembly.GetType("LumaShell.FinderShellMenu", throwOnError: true)!
            .GetMethod("ReadVerbs", BindingFlags.Static | BindingFlags.NonPublic)!;
        string[] Read(params string[] files) => (string[])read.Invoke(null, [files])!;
        var document = Read(Path.Combine(folder, "Заметки.txt"));
        var selection = Read(Path.Combine(folder, "Заметки.txt"), Path.Combine(folder, "Список задач.txt"));
        string executable = Path.Combine(sandbox, "Sample.exe");
        File.Copy(Environment.ProcessPath!, executable);
        var program = Read(executable);
        Console.WriteLine($"Document commands: {string.Join(", ", document)}");
        Console.WriteLine($"Selection commands: {string.Join(", ", selection)}");
        Console.WriteLine($"Executable commands: {string.Join(", ", program)}");
        if (Array.FindIndex(document, verb => verb.Equals("delete", StringComparison.OrdinalIgnoreCase)) < 0 ||
            Array.FindIndex(selection, verb => verb.Equals("delete", StringComparison.OrdinalIgnoreCase)) < 0 ||
            Array.FindIndex(program, verb => verb.Equals("runas", StringComparison.OrdinalIgnoreCase)) < 0)
            throw new InvalidOperationException("The Windows Shell did not supply the expected Finder commands.");

        using var wallpaper = assembly.GetManifestResourceStream("LumaShell.AlpineLake.jpg")
            ?? throw new InvalidOperationException("The bundled wallpaper is missing.");
        var bitmap = BitmapDecoder.Create(wallpaper, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        if (bitmap.PixelWidth < 1600 || bitmap.PixelHeight < 900)
            throw new InvalidOperationException("The bundled wallpaper has unexpected dimensions.");
    }

    private static void PumpUntil(Func<bool> finished, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (finished() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        if (!finished()) throw new TimeoutException("Finder did not complete " + description);
    }

    private static T? Find<T>(DependencyObject root, Func<T, bool>? matches = null) where T : DependencyObject
    {
        if (root is T item && (matches is null || matches(item))) return item;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (Find(VisualTreeHelper.GetChild(root, index), matches) is { } match) return match;
        return null;
    }
}
