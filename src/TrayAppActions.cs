using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace LumaShell;

internal static class TrayAppActions
{
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar;
    private const string NvidiaControlPanelFamily = "NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj";

    internal static bool IsLuma(string path) => string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);

    internal static bool IsNvidiaControlPanel(TraySnapshot.Item item) => IsNvidiaControlPanel(item.Path, item.Title);

    internal static bool IsNvidiaControlPanel(string path, string title)
    {
        string file = Path.GetFileName(path);
        if (file.Equals("nvtray.exe", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("nvcplui.exe", StringComparison.OrdinalIgnoreCase)) return true;
        bool panelLabel = title.StartsWith("NVIDIA Settings", StringComparison.OrdinalIgnoreCase) ||
            title.StartsWith("Настройки NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) &&
            (title.Contains("Control Panel", StringComparison.OrdinalIgnoreCase) ||
             title.Contains("Панель управления", StringComparison.OrdinalIgnoreCase));
        return panelLabel && (string.IsNullOrEmpty(file) || file.Equals("nvcontainer.exe", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("nvdisplay.container.exe", StringComparison.OrdinalIgnoreCase));
    }

    internal static async Task<bool> OpenNvidiaControlPanel()
    {
        var existing = Native.OpenWindows().FirstOrDefault(window =>
            Path.GetFileName(window.Path).Equals("nvcplui.exe", StringComparison.OrdinalIgnoreCase));
        if (existing is not null && await Native.FocusWindow(existing.Handle)) return true;
        bool opened = await Task.Run(ActivateNvidiaPackage);
        if (opened) return true;
        var programs = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 };
        foreach (var folder in programs)
        {
            string path = Path.Combine(Environment.GetFolderPath(folder), "NVIDIA Corporation", "Control Panel Client", "nvcplui.exe");
            if (Path.IsPathFullyQualified(path) && File.Exists(path) && Desktop.Launch(path)) return true;
        }
        SessionLog.Write("NVIDIA Control Panel could not be activated from the tray.");
        return false;
    }

    private static async Task<bool> ActivateNvidiaPackage()
    {
        try
        {
            foreach (var package in new global::Windows.Management.Deployment.PackageManager().FindPackagesForUser("", NvidiaControlPanelFamily))
            {
                foreach (var app in package.GetAppListEntries())
                {
                    if (await app.LaunchAsync()) return true;
                }
            }
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or InvalidOperationException or
            IOException or ArgumentException or NotSupportedException)
        { SessionLog.Write($"NVIDIA package activation failed (0x{error.HResult:X8})."); }
        return false;
    }

    internal static bool CanEnd(string path) => Path.IsPathFullyQualified(path) && !IsLuma(path) &&
        !path.StartsWith(Windows, StringComparison.OrdinalIgnoreCase);

    internal static bool TryEnd(TraySnapshot.Item item)
    {
        if (!CanEnd(item.Path) || IsNvidiaControlPanel(item)) return false;
        try
        {
            uint pid = Native.WindowProcessId(item.Owner);
            if (Matches(pid, item.Path)) return End(pid, item.Path);
            var windows = Native.OpenWindows().Where(window => window.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase))
                .Select(window => Native.WindowProcessId(window.Handle)).Distinct().Where(id => Matches(id, item.Path)).ToArray();
            if (windows.Length == 1) return End(windows[0], item.Path);
            var processes = new List<uint>();
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(item.Path)))
            {
                using (process)
                    if (Matches((uint)process.Id, item.Path)) processes.Add((uint)process.Id);
            }
            uint root = processes.Count == 1 ? processes[0] : FindRoot(processes);
            return root != 0 && End(root, item.Path);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        { return false; }
    }

    private static bool Matches(uint pid, string path) => pid != 0 && pid != Environment.ProcessId &&
        string.Equals(path, Native.ProcessImage(pid), StringComparison.OrdinalIgnoreCase);

    private static bool End(uint pid, string path)
    {
        using var current = Process.GetCurrentProcess();
        using var target = Process.GetProcessById((int)pid);
        // Open a real process handle before revalidating so PID reuse cannot change the target of Kill.
        _ = target.Handle;
        if (target.HasExited || target.SessionId != current.SessionId || !Matches(pid, path)) return false;
        target.Kill();
        return target.WaitForExit(2000);
    }

    private static uint FindRoot(List<uint> candidates)
    {
        if (candidates.Count == 0) return 0;
        nint snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1) return 0;
        try
        {
            var ids = candidates.ToHashSet();
            var parents = new Dictionary<uint, uint>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (Process32First(snapshot, ref entry))
            {
                do { if (ids.Contains(entry.Process)) parents[entry.Process] = entry.Parent; }
                while (Process32Next(snapshot, ref entry));
            }
            return parents.Count == ids.Count ? UniqueRoot(parents) : 0;
        }
        finally { CloseHandle(snapshot); }
    }

    internal static uint UniqueRoot(IReadOnlyDictionary<uint, uint> parents)
    {
        uint root = 0;
        foreach (var (process, parent) in parents)
        {
            if (parents.ContainsKey(parent)) continue;
            if (root != 0) return 0;
            root = process;
        }
        return root;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Process;
        public nuint Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll")] private static extern nint CreateToolhelp32Snapshot(uint flags, uint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
