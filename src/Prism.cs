using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace LumaShell;

internal sealed class Prism(Preferences settings) : IDisposable
{
    private readonly HttpClient client = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(2), MaxResponseContentBufferSize = 1024 * 1024 };
    private bool refreshing;
    private bool changingZapret;
    internal string Status { get; private set; } = "Проверяем Prism…";
    internal Zapret.Mode ZapretStatus { get; private set; }
    internal bool ChangingZapret => changingZapret;
    internal event Action? Updated;
    internal event Action<string>? Feedback;

    internal void Toggle()
    {
        settings.Prism = !settings.Prism;
        settings.Save();
        Status = settings.Prism ? "Проверяем Prism…" : "Статус выключен";
        Updated?.Invoke();
        Refresh();
    }

    internal void ChooseProfile()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Папка профиля Prism с store.json или sing-box.json" };
        if (settings.PrismProfile is { } folder && Directory.Exists(folder)) dialog.InitialDirectory = folder;
        if (dialog.ShowDialog() != true) return;
        settings.PrismProfile = dialog.FolderName;
        settings.Prism = true;
        settings.Save();
        Updated?.Invoke();
        Refresh();
    }

    internal async void ToggleZapret()
    {
        if (changingZapret) return;
        changingZapret = true;
        Updated?.Invoke();
        try
        {
            var error = await Zapret.Toggle();
            if (error is not null) Feedback?.Invoke(error);
            else Feedback?.Invoke("Windows приняла команду для zapret");
        }
        finally
        {
            changingZapret = false;
            ZapretStatus = Zapret.Read();
            Updated?.Invoke();
        }
    }

    internal async void Refresh()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var zapret = Zapret.Read();
            string status = settings.Prism ? await ReadStatus() : "Статус выключен";
            if (!settings.Prism) status = "Статус выключен";
            if (status == Status && zapret == ZapretStatus) return;
            Status = status;
            ZapretStatus = zapret;
            Updated?.Invoke();
        }
        finally { refreshing = false; }
    }

    private async Task<string> ReadStatus()
    {
        bool processRunning = KernelRunning();
        bool hasProfile = false;
        foreach (string folder in ProfileFolders())
        {
            if (!Directory.Exists(folder)) continue;
            hasProfile = true;
            try
            {
                var connection = await ReadConnection(folder);
                if (connection is null) continue;
                using var version = await Request(connection, "/version");
                if (!version.IsSuccessStatusCode) continue;
                using var selector = await Request(connection, "/proxies/proxy");
                if (!selector.IsSuccessStatusCode) return "●  Ядро Prism запущено";
                using var result = JsonDocument.Parse(await selector.Content.ReadAsStringAsync());
                return result.RootElement.TryGetProperty("now", out var selected) && selected.ValueKind == JsonValueKind.String &&
                    selected.GetString() is { Length: > 0 } node ? $"●  {Visuals.Label(node, 60)}" : "●  Ядро Prism запущено";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or
                OperationCanceledException or JsonException or InvalidOperationException or FormatException or ArgumentException) { }
        }
        if (processRunning) return "●  Ядро Prism работает · API недоступен";
        return hasProfile ? "Ядро Prism не обнаружено" : "Укажите папку профиля Prism";
    }

    private IEnumerable<string> ProfileFolders()
    {
        if (settings.PrismProfile is { Length: > 0 } selected) yield return selected;
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (string folder in new[] { "Prism", "prism-vpn", "Prism VPN" }) yield return Path.Combine(roaming, folder);
    }

    private static bool KernelRunning()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcessesByName("Prism"))
        {
            using (process)
                if (Native.ProcessImage((uint)process.Id) is { } app)
                    foreach (string core in CoreLocations(app)) roots.Add(core);
        }
        if (roots.Count == 0) return false;
        foreach (var process in Process.GetProcessesByName("sing-box"))
        {
            using (process)
                if (Native.ProcessImage((uint)process.Id) is { } path && roots.Contains(path)) return true;
        }
        return false;
    }

    private static IEnumerable<string> CoreLocations(string executable)
    {
        string folder = Path.Combine(Path.GetDirectoryName(executable)!, "resources", "core");
        yield return Path.Combine(folder, "sing-box.exe");
        yield return Path.Combine(folder, "win", "sing-box.exe");
    }

    internal static bool IsTrayClient(string path) => Path.IsPathFullyQualified(path) &&
        Path.GetFileName(path).Equals("Prism.exe", StringComparison.OrdinalIgnoreCase) &&
        (File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "resources", "app.asar")) ||
            CoreLocations(path).Any(File.Exists));

    internal static async Task<bool> OpenFromTray(string path)
    {
        if (!IsTrayClient(path)) return false;
        nint window = FindWindow(path);
        if (window != 0)
            return await ActivateWindow(window) || await RequestElevatedActivation(Native.WindowProcessId(window));

        // Prism's second-instance handler shows the existing window. Only use it
        // for the exact running installation in this session.
        using var current = Process.GetCurrentProcess();
        bool running = false;
        foreach (var process in Process.GetProcessesByName("Prism"))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId == current.SessionId &&
                        string.Equals(Native.ProcessImage((uint)process.Id), path, StringComparison.OrdinalIgnoreCase))
                        running = true;
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return running && Desktop.Launch(path);
    }

    private static nint FindWindow(string path)
    {
        foreach (string title in new[] { "Prism", "Prism VPN" })
            foreach (string className in new[] { "Chrome_WidgetWin_1", "Chrome_WidgetWin_0" })
                if (Native.FindTitledWindow(path, className, title) is var window && window != 0) return window;
        return 0;
    }

    private static async Task<bool> ActivateWindow(nint window)
    {
        if (!Native.IsApplicationWindowShown(window) && !Native.ShowApplicationWindow(window)) return false;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (Native.IsApplicationWindowShown(window) &&
                (Native.GetForegroundWindow() == window || Native.FocusWindow(window))) return true;
            await Task.Delay(45);
        }
        return false;
    }

    private static async Task<bool> RequestElevatedActivation(uint processId)
    {
        if (processId == 0 || Worker.Executable is not { } luma) return false;
        try
        {
            if (Native.ProcessImage(processId) is not { } path || !IsTrayClient(path)) return false;
            long started = Native.ProcessCreationTime(processId);
            if (started == 0) return false;
            var start = new ProcessStartInfo(luma) { UseShellExecute = true, Verb = "runas" };
            start.ArgumentList.Add("--show-prism");
            start.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(started.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var helper = Process.Start(start);
            if (helper is null) return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await helper.WaitForExitAsync(timeout.Token);
            return helper.ExitCode == 0;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { return false; }
    }

    internal static bool ActivateElevated(uint processId, long started)
    {
        try
        {
            using var prism = Process.GetProcessById(checked((int)processId));
            using var current = Process.GetCurrentProcess();
            if (prism.SessionId != current.SessionId || started == 0 || Native.ProcessCreationTime(processId) != started ||
                Native.ProcessImage(processId) is not { } path || !IsTrayClient(path)) return false;
            nint window = FindWindow(path);
            return window != 0 && Native.WindowProcessId(window) == processId && ActivateWindow(window).GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException)
        { return false; }
    }

    private static async Task<Endpoint?> ReadConnection(string folder)
    {
        using var runtime = await ReadJson(Path.Combine(folder, "sing-box.json"));
        if (runtime is not null && runtime.RootElement.TryGetProperty("experimental", out var experimental) &&
            experimental.TryGetProperty("clash_api", out var api) && api.TryGetProperty("external_controller", out var controller) &&
            api.TryGetProperty("secret", out var secret) && secret.GetString() is { Length: > 0 and <= 256 } runtimeKey &&
            Uri.TryCreate($"http://{controller.GetString()}", UriKind.Absolute, out var uri) &&
            uri.Host is "127.0.0.1" or "localhost" or "[::1]" && uri.AbsolutePath == "/" && uri.UserInfo.Length == 0)
        {
            string host = uri.Host == "[::1]" ? "[::1]" : "127.0.0.1";
            return new Endpoint($"http://{host}:{uri.Port}", runtimeKey);
        }
        using var profile = await ReadJson(Path.Combine(folder, "store.json"));
        if (profile is null || !profile.RootElement.TryGetProperty("clashSecret", out var key) ||
            key.GetString() is not { Length: > 0 and <= 256 } token) return null;
        int port = 9291;
        if (profile.RootElement.TryGetProperty("settings", out var config) && config.TryGetProperty("clashPort", out var value) &&
            value.TryGetInt32(out int configured) && configured is > 0 and <= 65535) port = configured;
        return new Endpoint($"http://127.0.0.1:{port}", token);
    }

    private static async Task<JsonDocument?> ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.Length > 8 * 1024 * 1024 ? null : await JsonDocument.ParseAsync(stream);
    }

    private async Task<HttpResponseMessage> Request(Endpoint endpoint, string route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Url + route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Key);
        return await client.SendAsync(request);
    }

    private sealed record Endpoint(string Url, string Key);

    internal static bool Open()
    {
        foreach (var process in Process.GetProcessesByName("Prism"))
        {
            using (process)
            {
                try { if (process.MainWindowHandle is { } handle && handle != 0 && Native.FocusWindow(handle)) return true; }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        string[] roots = [Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)];
        foreach (string root in roots)
        {
            foreach (string name in new[] { "Prism.lnk", @"Prism\Prism.lnk", @"Prism\Prism.exe" })
            {
                string path = Path.Combine(root, name);
                if (File.Exists(path) && Desktop.Launch(path)) return true;
            }
        }
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Укажите Prism для Windows", Filter = "Приложение|*.exe" };
        return picker.ShowDialog() == true && Desktop.Launch(picker.FileName);
    }

    public void Dispose() => client.Dispose();
}
