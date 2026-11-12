using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace LumaShell;

internal static class SessionLog
{
    private const int MaxLogSize = 256 * 1024;
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(Worker.Data, "diagnostics");
    private static readonly string SessionPath = Path.Combine(DirectoryPath, $"session-{Process.GetCurrentProcess().SessionId}");
    private static readonly string LogPath = SessionPath + ".log";
    private static readonly string ActivePath = SessionPath + ".active";
    private static readonly string RestartPath = SessionPath + ".restarts";
    private static bool watching;

    internal static bool Begin()
    {
        bool interrupted = false;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            if (File.Exists(ActivePath))
            {
                interrupted = true;
                Write("Previous session ended without a shutdown record.");
            }
            File.WriteAllText(ActivePath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Write($"Unhandled exception: {args.ExceptionObject}");
        Write($"Started Luma {typeof(SessionLog).Assembly.GetName().Version} (PID {Environment.ProcessId}).");
        return interrupted;
    }

    internal static void End(int exitCode)
    {
        Write($"Shutdown completed with code {exitCode}.");
        try { File.Delete(ActivePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal static bool Open() => File.Exists(LogPath) && Desktop.Launch("notepad.exe", $"\"{LogPath}\"");

    internal static void StartRecovery()
    {
        if (watching || Worker.Executable is not { } target) return;
        watching = Worker.Start("--watch-session", target);
        Write(watching ? "Started the recovery process." : "Could not start the recovery process.");
    }

    internal static void Watch(Process? parent, string target)
    {
        try
        {
            if (parent is null)
            {
                Write("Recovery process could not attach to the parent.");
                return;
            }
            if (!string.Equals(Native.ProcessImage((uint)parent.Id), target, StringComparison.OrdinalIgnoreCase))
            {
                Write("Recovery process found a different parent executable.");
                return;
            }
            string pid = parent.Id.ToString(CultureInfo.InvariantCulture);
            Write($"Recovery process attached to PID {pid}.");
            parent.WaitForExit();
            Thread.Sleep(750);
            if (!Preferences.Load().AutoStart || !File.Exists(ActivePath) || File.ReadAllText(ActivePath) != pid) return;
            Write($"Detected interrupted session (PID {pid}).");
            if (!PermitRestart())
            {
                Write("Automatic restart limit reached.");
                return;
            }
            using var restarted = Process.Start(new ProcessStartInfo(target) { UseShellExecute = false });
            Write(restarted is null ? "Could not restart Luma after an interrupted session." : $"Restarted Luma as PID {restarted.Id}.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { Write($"Recovery process failed: {error}"); }
    }

    private static bool PermitRestart()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var attempts = new System.Collections.Generic.List<string>(2);
        if (File.Exists(RestartPath))
        {
            foreach (string line in File.ReadLines(RestartPath))
            {
                if (long.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out long time) &&
                    time >= now - 600 && time <= now) attempts.Add(line);
            }
        }
        if (attempts.Count >= 2) return false;
        attempts.Add(now.ToString(CultureInfo.InvariantCulture));
        File.WriteAllLines(RestartPath, attempts);
        return true;
    }

    internal static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                if (new FileInfo(LogPath) is { Exists: true, Length: >= MaxLogSize })
                    File.Move(LogPath, LogPath + ".previous", overwrite: true);
                string entry = message.Length > 8192 ? message[..8192] : message;
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {entry}{Environment.NewLine}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
