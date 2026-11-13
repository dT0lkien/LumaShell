using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
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
            VerifyLauncherMenu(assembly, Path.Combine(sandbox, "Sample.exe"));
            VerifyTrayActions(assembly);
            VerifyAppIdentification(assembly, sandbox);
            VerifyHiddenWindow(assembly);
            VerifyTaskManager(assembly, window);
            VerifyFolderShortcut(assembly);

            string plan = Path.Combine(projects, "План.txt");
            type.GetMethod("Reveal", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [plan]);
            PumpUntil(() => list.SelectedItem is { } item && (string?)item.GetType().GetProperty("Path")!.GetValue(item) == plan,
                "revealing a file from application search");
            type.GetMethod("ToggleQuickLook", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var preview = application.Windows.OfType<Window>().Single(candidate => candidate.Owner == window);
            PumpUntil(() => Find<TextBox>(preview, box => box.IsReadOnly && box.Text == "Luma") is not null, "rendering Quick Look");
            preview.Close();
            VerifyPdfPreview(assembly, window, projects, list);
            open.Invoke(window, [desktop]);
            PumpUntil(() => list.Items.Count == files.Length + 2, "returning to the Finder preview folder");

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

    private static void VerifyTaskManager(Assembly assembly, Window owner)
    {
        var native = assembly.GetType("LumaShell.Native", throwOnError: true)!;
        var open = native.GetMethod("OpenTaskManager", BindingFlags.Static | BindingFlags.NonPublic)!;
        var isTaskManager = native.GetMethod("IsTaskManager", BindingFlags.Static | BindingFlags.NonPublic)!;
        string path = Path.Combine(Environment.SystemDirectory, "Taskmgr.exe");
        if (!(bool)isTaskManager.Invoke(null, [path])! ||
            (bool)isTaskManager.Invoke(null, [Path.Combine(Path.GetTempPath(), "Taskmgr.exe")])!)
            throw new InvalidOperationException("Task Manager routing matched the wrong executable.");
        owner.Activate();
        var previous = Process.GetProcessesByName("Taskmgr");
        var previousIds = previous.Select(process => process.Id).ToHashSet();
        foreach (var process in previous) process.Dispose();
        try
        {
            var activation = (Task<bool>)open.Invoke(null, [(nint)0])!;
            PumpUntil(() => activation.IsCompleted, "opening Windows Task Manager");
            if (!activation.GetAwaiter().GetResult())
                throw new InvalidOperationException("The Task Manager command did not open a foreground window.");
            nint handle = GetForegroundWindow();
            ShowWindowAsync(handle, 6);
            PumpUntil(() => IsIconic(handle), "minimizing the Task Manager window");
            owner.Activate();
            // Exercise Windows' own shortcut while the Task Manager already exists and is minimized.
            activation = (Task<bool>)open.Invoke(null, [(nint)0])!;
            PumpUntil(() => activation.IsCompleted, "restoring the Task Manager window");
            if (!activation.GetAwaiter().GetResult() || IsIconic(handle) || GetForegroundWindow() != handle)
                throw new InvalidOperationException("The Task Manager command did not restore the same window.");
            Console.WriteLine("The Task Manager command opened and restored the same minimized Task Manager window.");
        }
        finally
        {
            foreach (var process in Process.GetProcessesByName("Taskmgr"))
                using (process) if (!previousIds.Contains(process.Id)) process.CloseMainWindow();
            owner.Activate();
        }
    }

    private static void VerifyPdfPreview(Assembly assembly, Window finder, string folder, ListView list)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        string path = Path.Combine(folder, "Две страницы.pdf");
        using (var file = File.Create(path))
        using (var writer = new StreamWriter(file, Encoding.ASCII, leaveOpen: true))
        {
            writer.NewLine = "\n";
            writer.WriteLine("%PDF-1.4");
            var objects = new[]
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 420] /Resources << >> /Contents 5 0 R >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 420] /Resources << >> /Contents 6 0 R >>",
                "<< /Length 26 >>\nstream\n0 0 1 rg 30 30 240 360 re f\nendstream",
                "<< /Length 26 >>\nstream\n1 0 0 rg 30 30 240 360 re f\nendstream"
            };
            long[] offsets = new long[objects.Length];
            for (int i = 0; i < objects.Length; i++)
            {
                writer.Flush();
                offsets[i] = file.Position;
                writer.WriteLine($"{i + 1} 0 obj\n{objects[i]}\nendobj");
            }
            writer.Flush();
            long crossReference = file.Position;
            writer.WriteLine($"xref\n0 {objects.Length + 1}\n0000000000 65535 f ");
            foreach (long offset in offsets) writer.WriteLine($"{offset:0000000000} 00000 n ");
            writer.WriteLine($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{crossReference}\n%%EOF");
        }
        var type = finder.GetType();
        type.GetMethod("Reload", instance)!.Invoke(finder, null);
        PumpUntil(() => list.Items.Count == 2, "reading the PDF fixture");
        type.GetMethod("Reveal", instance)!.Invoke(finder, [path]);
        type.GetMethod("ToggleQuickLook", instance)!.Invoke(finder, null);
        var preview = Application.Current.Windows.OfType<Window>().Single(window => window.Owner == finder);
        try
        {
            PumpUntil(() => Find<Image>(preview, image => image.Source is BitmapSource) is not null &&
                Find<TextBlock>(preview, label => label.Text == "1 / 2") is not null, "rendering the first PDF page");
            var first = Find<Image>(preview, image => image.Source is BitmapSource)!.Source;
            var next = Find<Button>(preview, button => AutomationProperties.GetName(button) == "Следующая страница")!;
            PumpUntil(() => next.IsEnabled, "enabling PDF pagination");
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Find<Image>(preview, image => image.Source is BitmapSource source && !ReferenceEquals(source, first)) is not null &&
                Find<TextBlock>(preview, label => label.Text == "2 / 2") is not null, "rendering the second PDF page");
        }
        finally { preview.Close(); }
        var files = assembly.GetType("LumaShell.FinderFiles", throwOnError: true)!;
        var data = (DataObject)files.GetMethod("FileDropData", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [new[] { path }])!;
        if (data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length != 1 || paths[0] != path)
            throw new InvalidOperationException("The Downloads stack did not publish a file-drop payload.");
        Console.WriteLine("Quick Look rendered two PDF pages, and the Downloads drag payload carried the original file path.");
    }

    private static void VerifyAppIdentification(Assembly assembly, string sandbox)
    {
        const BindingFlags internalStatic = BindingFlags.Static | BindingFlags.NonPublic;
        var actions = assembly.GetType("LumaShell.TrayAppActions", throwOnError: true)!;
        var panel = actions.GetMethod("IsNvidiaControlPanel", internalStatic, null, [typeof(string), typeof(string)], null)!;
        bool Nvidia(string path, string title) => (bool)panel.Invoke(null, [path, title])!;
        if (!Nvidia(@"C:\Windows\System32\DriverStore\NVDisplay.Container.exe", "NVIDIA Settings") ||
            !Nvidia(@"C:\NVIDIA\nvtray.exe", "NVIDIA") ||
            !Nvidia("", "Панель управления NVIDIA") ||
            Nvidia(@"C:\NVIDIA\nvcontainer.exe", "NVIDIA App") ||
            Nvidia(@"C:\Another App\helper.exe", "Control Panel"))
            throw new InvalidOperationException("NVIDIA tray identification included the wrong process or missed the Control Panel.");
        var launcher = assembly.GetType("LumaShell.Launcher", throwOnError: true)!;
        var prism = launcher.GetMethod("IsPrismTarget", internalStatic)!;
        bool Prism(string target, string? location = null) => (bool)prism.Invoke(null, [target, location])!;
        if (!Prism(@"C:\Prism\Prism.exe") || !Prism(@"shell:AppsFolder\com.prism.vpn") ||
            !Prism(@"C:\Start Menu\Prism.lnk") ||
            !Prism(@"shell:AppsFolder\different-catalog-id", @"C:\Prism\Prism.exe") ||
            Prism(@"C:\Prism Launcher\prismlauncher.exe"))
            throw new InvalidOperationException("Prism identification missed a Dock or launcher entry.");
        Console.WriteLine("The tray identifies NVIDIA Control Panel separately from NVIDIA App, and recognizes Prism shortcuts.");

        string probe = Path.Combine(sandbox, "Prism.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), probe);
        using var process = Process.Start(new ProcessStartInfo(probe, "-n 30 127.0.0.1")
        {
            UseShellExecute = false, CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the windowless Prism probe.");
        try
        {
            var type = assembly.GetType("LumaShell.Prism", throwOnError: true)!;
            var open = type.GetMethod("OpenRunning", internalStatic)!;
            string? forwarded = null;
            Func<string, Task<bool>> tray = path => { forwarded = path; return Task.FromResult(false); };
            var result = (Task<bool?>)open.Invoke(null, [probe, (nint)0, tray])!;
            PumpUntil(() => result.IsCompleted, "checking a running Prism without a window");
            if (result.GetAwaiter().GetResult() != false || !string.Equals(probe, forwarded, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A running Prism without a window did not take the tray path.");
            Console.WriteLine("A windowless Prism process used the tray callback without starting a second copy.");
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            process.WaitForExit();
        }
    }

    private static void VerifyTrayActions(Assembly assembly)
    {
        const BindingFlags internalInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = assembly.GetType("LumaShell.TrayIcons", throwOnError: true)!;
        var itemType = assembly.GetType("LumaShell.TraySnapshot+Item", throwOnError: true)!;
        bool exited = false;
        var tray = Activator.CreateInstance(type, internalInstance, null,
            new object[] { (Action)(() => exited = true), (Action)(() => { }) }, null)!;
        var icon = new DrawingImage(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 14, 14))));
        var snapshot = Array.CreateInstance(itemType, 1);
        snapshot.SetValue(Activator.CreateInstance(itemType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object?[] { "probe", Environment.ProcessPath, "Luma Shell", true, icon, false, (nint)0, 0u,
                Guid.Empty, false, null }, null), 0);
        type.GetField("snapshot", internalInstance)!.SetValue(tray, snapshot);
        var toggle = (FrameworkElement)type.GetProperty("View", internalInstance)!.GetValue(tray)!;
        var popup = (Popup)type.GetField("popup", internalInstance)!.GetValue(tray)!;
        var panel = (Border)type.GetField("actionPanel", internalInstance)!.GetValue(tray)!;
        var list = (StackPanel)type.GetField("entries", internalInstance)!.GetValue(tray)!;
        var window = new Window { Content = toggle, Width = 550, Height = 95, Top = 30, Left = 600, Topmost = true };
        try
        {
            window.Show();
            type.GetMethod("RenderPopup", internalInstance)!.Invoke(tray, null);
            popup.IsOpen = true;
            var row = (Button)list.Children[0];
            PumpUntil(() => row.IsVisible && row.ActualWidth > 0, "laying out the tray row");
            Point center = row.PointToScreen(new Point(row.ActualWidth / 2, row.ActualHeight / 2));
            SetCursorPos((int)center.X, (int)center.Y);
            mouse_event(0x0008, 0, 0, 0, 0);
            mouse_event(0x0010, 0, 0, 0, 0);
            PumpUntil(() => panel.IsVisible && panel.ActualWidth > 0, "opening the tray actions with a right click");
            if (!popup.IsOpen || panel.PointToScreen(new Point(panel.ActualWidth, 0)).X > row.PointToScreen(new Point()).X)
                throw new InvalidOperationException("The tray actions overlapped or dismissed the list.");
            var exit = Find<Button>(panel, button => Equals(AutomationProperties.GetName(button), "Выйти из Luma"))
                ?? throw new InvalidOperationException("The tray did not expose the app's exit action.");
            exit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!exited) throw new InvalidOperationException("The tray did not invoke the selected action.");
            Console.WriteLine("Right-clicking the tray opened working actions to the left and kept the list visible.");
        }
        finally
        {
            popup.IsOpen = false;
            window.Close();
        }
        var native = assembly.GetType("LumaShell.NativeTrayMenu", throwOnError: true)!;
        var position = native.GetMethod("MenuPosition", BindingFlags.Static | BindingFlags.NonPublic)!;
        var area = new System.Drawing.Rectangle(-1920, 40, 1920, 1040);
        var actual = (Point)position.Invoke(null, [new Point(-280, 1040), area, 210, 160])!;
        if (actual != new Point(-498, 920)) throw new InvalidOperationException("The native menu did not fit to the left on a secondary display.");
    }

    private static void VerifyLauncherMenu(Assembly assembly, string executable)
    {
        const BindingFlags internalInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = assembly.GetType("LumaShell.Launcher", throwOnError: true)!;
        var entryType = type.GetNestedType("AppEntry", BindingFlags.NonPublic)!;
        var argument = Expression.Parameter(entryType);
        var isPinned = Expression.Lambda(typeof(Func<,>).MakeGenericType(entryType, typeof(bool)),
            Expression.Constant(false), argument).Compile();
        var toggle = Expression.Lambda(typeof(Func<,>).MakeGenericType(entryType, typeof(string)),
            Expression.Constant(null, typeof(string)), argument).Compile();
        string? revealed = null;
        var launcher = Activator.CreateInstance(type, internalInstance, null,
            new object[] { (Action)(() => { }), (Action<string>)(path => revealed = path), isPinned, toggle }, null)!;
        var entries = Array.CreateInstance(entryType, 1);
        entries.SetValue(Activator.CreateInstance(entryType, internalInstance | BindingFlags.Public, null,
            new object?[] { "Sample App", executable, executable, null }, null), 0);
        type.GetField("catalog", internalInstance)!.SetValue(launcher, entries);
        var window = (Window)type.GetField("window", internalInstance)!.GetValue(launcher)!;
        try
        {
            type.GetMethod("Toggle", internalInstance)!.Invoke(launcher, [SystemParameters.WorkArea]);
            var list = (ListBox)type.GetField("results", internalInstance)!.GetValue(launcher)!;
            PumpUntil(() => list.Items.Count == 1 && ((ListBoxItem)list.Items[0]).ActualWidth > 0, "laying out the launcher result");
            var row = (ListBoxItem)list.Items[0];
            Point center = row.PointToScreen(new Point(row.ActualWidth / 2, row.ActualHeight / 2));
            SetCursorPos((int)center.X, (int)center.Y);
            mouse_event(0x0008, 0, 0, 0, 0);
            mouse_event(0x0010, 0, 0, 0, 0);
            var menuField = type.GetField("context", internalInstance)!;
            PumpUntil(() => menuField.GetValue(launcher) is ContextMenu { IsOpen: true }, "opening the launcher menu with a right click");
            var menu = (ContextMenu)menuField.GetValue(launcher)!;
            if (!window.IsVisible || !menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Закрепить в Dock")))
                throw new InvalidOperationException("The launcher lost its window or pin command during a right click.");
            var show = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Показать в Finder"));
            show.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (revealed != executable) throw new InvalidOperationException("The launcher did not reveal the clicked application.");
            Console.WriteLine("A right click opened the launcher menu and its reveal command used the selected application.");
        }
        finally { window.Close(); }
    }

    private static void VerifyFolderShortcut(Assembly assembly)
    {
        var type = assembly.GetType("LumaShell.WinTap", throwOnError: true)!;
        var tap = Activator.CreateInstance(type, nonPublic: true)!;
        var handle = type.GetMethod("Handle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string Key(int key, bool down, bool launcher, bool finder, bool modified = false, bool capture = false) =>
            handle.Invoke(tap, [key, down, launcher, modified, finder, capture])!.ToString()!;
        const int win = 0x5B, e = 0x45, d = 0x44, s = 0x53;
        if (Key(win, true, true, true) != "Hold" || Key(e, true, true, true) != "OpenFolder" ||
            Key(e, true, true, true) != "Suppress" || Key(e, false, true, true) != "Suppress" ||
            Key(win, false, true, true) != "Suppress" ||
            Key(win, true, true, true) != "Hold" || Key(win, false, true, true) != "Launch" ||
            Key(win, true, false, true) != "Hold" || Key(win, false, false, true) != "ReplayTap" ||
            Key(win, true, true, true) != "Hold" || Key(d, true, true, true) != "ReplayChord" ||
            Key(win, true, true, true) != "Pass" || Key(win, false, true, true) != "Pass" ||
            Key(win, true, true, true, modified: true) != "Hold" || Key(win, false, true, true) != "Suppress" ||
            Key(win, true, true, true) != "Hold" || Key(win, false, false, true) != "Suppress" ||
            Key(win, true, true, true, modified: true) != "Hold" ||
            Key(s, true, true, true, capture: true) != "Capture" || Key(s, false, true, true) != "Suppress" ||
            Key(win, false, true, true) != "Suppress")
            throw new InvalidOperationException("Win+E did not preserve single Win and other Win shortcuts.");
        Console.WriteLine("Win+E routing preserved the launcher and other Win chords.");
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
            Array.FindIndex(document, verb => verb.Equals("openas", StringComparison.OrdinalIgnoreCase)) < 0 ||
            Array.FindIndex(selection, verb => verb.Equals("delete", StringComparison.OrdinalIgnoreCase)) < 0 ||
            Array.FindIndex(program, verb => verb.Equals("runas", StringComparison.OrdinalIgnoreCase)) < 0)
            throw new InvalidOperationException("The Windows Shell did not supply the expected Finder commands.");

        using var wallpaper = assembly.GetManifestResourceStream("LumaShell.AlpineLake.jpg")
            ?? throw new InvalidOperationException("The bundled wallpaper is missing.");
        var bitmap = BitmapDecoder.Create(wallpaper, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        if (bitmap.PixelWidth < 1600 || bitmap.PixelHeight < 900)
            throw new InvalidOperationException("The bundled wallpaper has unexpected dimensions.");
    }

    private static void VerifyHiddenWindow(Assembly assembly)
    {
        var hidden = new Window { Title = "Luma tray restore probe", Width = 240, Height = 100 };
        try
        {
            hidden.Show();
            nint handle = new WindowInteropHelper(hidden).Handle;
            hidden.Hide();
            var name = new StringBuilder(128);
            if (GetClassName(handle, name, name.Capacity) == 0)
                throw new InvalidOperationException("Could not identify the hidden test window.");
            var type = assembly.GetType("LumaShell.Native", throwOnError: true)!;
            MethodInfo Method(string method) => type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!;
            var found = (nint)Method("FindTitledWindow").Invoke(null, [Environment.ProcessPath!, name.ToString(), hidden.Title, (uint)Environment.ProcessId])!;
            if (found != handle) throw new InvalidOperationException("The tray restore path did not find the hidden window.");
            var activation = (Task<bool>)Method("FocusWindow").Invoke(null, [found])!;
            PumpUntil(() => activation.IsCompleted, "showing a hidden application window");
            if (!activation.GetAwaiter().GetResult() || !(bool)Method("IsApplicationWindowShown").Invoke(null, [found])!)
                throw new InvalidOperationException("The tray restore path did not focus the hidden window.");
            hidden.WindowState = WindowState.Minimized;
            activation = (Task<bool>)Method("FocusWindow").Invoke(null, [found])!;
            PumpUntil(() => activation.IsCompleted, "restoring a minimized application window");
            if (!activation.GetAwaiter().GetResult() || !(bool)Method("IsApplicationWindowShown").Invoke(null, [found])!)
                throw new InvalidOperationException("The Dock restore path did not focus the minimized window.");
            Console.WriteLine("The window activation path restored and focused hidden and minimized windows.");
        }
        finally { hidden.Close(); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);

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
