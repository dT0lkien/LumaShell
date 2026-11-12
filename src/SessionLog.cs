using System;
using System.Diagnostics;
using System.IO;

namespace LumaShell;

internal static class SessionLog
{
    private const int MaxLogSize = 256 * 1024;
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(Worker.Data, "diagnostics");
    private static readonly string SessionPath = Path.Combine(DirectoryPath, $"session-{Process.GetCurrentProcess().SessionId}");
    private static readonly string LogPath = SessionPath + ".log";
    private static readonly string ActivePath = SessionPath + ".active";

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
            File.WriteAllText(ActivePath, Environment.ProcessId.ToString());
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
