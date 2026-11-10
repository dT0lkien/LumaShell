using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace LumaShell;

internal static class Zapret
{
    internal enum Mode { Unavailable, Off, On, Pending, AppOwned }
    private static readonly string Packs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Prism", "zapret", "packs") + Path.DirectorySeparatorChar;

    internal static Mode Read()
    {
        if (OwnsService())
        {
            nint manager = OpenSCManager(null, null, 1);
            if (manager != 0)
            {
                try
                {
                    nint service = OpenService(manager, "zapret", 4);
                    if (service != 0)
                    {
                        try
                        {
                            if (QueryServiceStatus(service, out var status)) return status.State switch
                            {
                                1 => Mode.Off,
                                4 => Mode.On,
                                _ => Mode.Pending
                            };
                        }
                        finally { CloseServiceHandle(service); }
                    }
                }
                finally { CloseServiceHandle(manager); }
            }
        }
        foreach (var process in Process.GetProcessesByName("winws"))
        {
            using (process)
                if (Native.ProcessImage((uint)process.Id) is { } path && IsPrismBinary(path)) return Mode.AppOwned;
        }
        return Mode.Unavailable;
    }

    internal static async Task<string?> Toggle()
    {
        var state = Read();
        if (state is not (Mode.On or Mode.Off)) return "В Prism установите zapret как службу Windows";
        string tool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(tool)
            {
                UseShellExecute = true, Verb = "runas", Arguments = state == Mode.On ? "stop zapret" : "start zapret",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null) return "Не удалось обратиться к службе zapret";
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) return "Windows не смогла переключить службу zapret";
            return null;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { return "Переключение zapret отменено"; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { return "Не удалось переключить zapret"; }
    }

    private static bool OwnsService()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret");
            if (key?.GetValue("ImagePath") is not string image) return false;
            image = image.TrimStart();
            if (!image.StartsWith('"')) return false;
            int end = image.IndexOf('"', 1);
            return end > 1 && IsPrismBinary(Environment.ExpandEnvironmentVariables(image[1..end]));
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    private static bool IsPrismBinary(string path)
    {
        string full = Path.GetFullPath(path);
        return full.StartsWith(Packs, StringComparison.OrdinalIgnoreCase) &&
            full.EndsWith(@"\bin\winws.exe", StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus { public uint Type, State, Controls, ExitCode, ServiceExitCode, Checkpoint, WaitHint; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll")]
    private static extern bool QueryServiceStatus(nint service, out ServiceStatus status);
    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(nint handle);
}
