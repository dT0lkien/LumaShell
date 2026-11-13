using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace LumaShell;

internal static class Worker
{
    internal static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Luma Shell");
    private static string? helper;

    internal static string? Executable => Environment.ProcessPath is { } path &&
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.ChangeExtension(path, ".dll")) ? path : null;

    internal static bool Start(string operation, params string[] args)
    {
        if (Executable is not { } current) return false;
        try
        {
            if (helper is null)
            {
                string directory = Path.Combine(Data, "workers");
                Directory.CreateDirectory(directory);
                foreach (string old in Directory.EnumerateFiles(directory, "helper-*.exe"))
                {
                    if (File.GetLastWriteTimeUtc(old) >= DateTime.UtcNow.AddDays(-1)) continue;
                    try { File.Delete(old); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
                string copy = Path.Combine(directory, $"helper-{Environment.ProcessId}-{Guid.NewGuid():N}.exe");
                File.Copy(current, copy);
                File.SetLastWriteTimeUtc(copy, DateTime.UtcNow);
                helper = copy;
            }
            using var parent = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(operation);
            start.ArgumentList.Add(parent.Id.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
            foreach (string arg in args) start.ArgumentList.Add(arg);
            using var child = Process.Start(start);
            return child is not null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        { return false; }
    }

    internal static bool Handle(string[] args)
    {
        if (args.Length > 0 && args[0] == "--show-prism") return true;
        if (args.Length == 0 || args[0] is not ("--watch-taskbar" or "--watch-session" or "--apply-update")) return false;
        if (args.Length < 4 || !int.TryParse(args[1], out int pid) || !long.TryParse(args[2], out long started)) return true;
        using var parent = FindParent(pid, started);
        if (args[0] == "--watch-taskbar" && args.Length == 4 && Guid.TryParseExact(args[3], "N", out _))
            Taskbar.Watch(parent, args[3]);
        if (args[0] == "--watch-session" && args.Length == 4)
            SessionLog.Watch(parent, args[3]);
        if (args[0] == "--apply-update" && args.Length == 8)
            Updates.Apply(parent, args[3], args[4], args[5], args[6] == "restart", args[7]);
        return true;
    }

    private static Process? FindParent(int id, long started)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(id);
            if (process.StartTime.ToUniversalTime().Ticks == started) return process;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
        process?.Dispose();
        return null;
    }
}
