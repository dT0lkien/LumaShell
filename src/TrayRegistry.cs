using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using static LumaShell.Visuals;

namespace LumaShell;

internal static class TrayRegistry
{
    private const string Settings = @"Control Panel\NotifyIconSettings";
    private static readonly Dictionary<string, ImageSource> Icons = new(StringComparer.OrdinalIgnoreCase);

    internal static IEnumerable<TraySnapshot.Item> Read(TraySnapshot.Item[] live)
    {
        var missing = new Dictionary<string, SavedIcon>(StringComparer.OrdinalIgnoreCase);
        var livePaths = live.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Settings);
            if (root is null) return [];
            var folders = new Dictionary<Guid, string?>();
            foreach (string id in root.GetSubKeyNames().Take(800))
            {
                using var icon = root.OpenSubKey(id);
                if (icon?.GetValue("ExecutablePath") is not string value || NormalizePath(value, folders) is not { } path ||
                    livePaths.Contains(path)) continue;
                bool pinned = icon.GetValue("IsPromoted") is 1;
                if (missing.TryGetValue(path, out var existing) && (existing.Pinned || !pinned)) continue;
                string title = Label(icon.GetValue("InitialTooltip") as string ?? "", 85);
                if (title.Length == 0) title = System.IO.Path.GetFileNameWithoutExtension(path);
                Guid.TryParse(icon.GetValue("IconGuid") as string, out var guid);
                missing[path] = new(path, id, title, pinned, guid, icon.GetValue("UID") is int uid ? unchecked((uint)uid) : null);
            }
            if (missing.Count == 0) return [];
            var names = missing.Keys.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var found = new List<TraySnapshot.Item>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (!names.Contains(process.ProcessName) || Native.ProcessImage((uint)process.Id) is not { } executable ||
                            !missing.Remove(executable, out var saved)) continue;
                        if (GetIcon(root, saved) is { } image)
                            found.Add(new($"saved:{saved.Path}", saved.Path, saved.Title, saved.Pinned, image, true, Uid: saved.Uid ?? 0, IconGuid: saved.IconGuid, HasUid: saved.Uid.HasValue));
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
            }
            return found;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return []; }
    }

    private static string? NormalizePath(string value, Dictionary<Guid, string?> folders)
    {
        string path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (path.StartsWith('{') && path.IndexOf('}') is > 0 and var end && Guid.TryParse(path[..(end + 1)], out var id))
        {
            if (!folders.TryGetValue(id, out var folder))
            {
                nint pointer = 0;
                try { folder = SHGetKnownFolderPath(ref id, 0, 0, out pointer) >= 0 ? Marshal.PtrToStringUni(pointer) : null; }
                finally { if (pointer != 0) Marshal.FreeCoTaskMem(pointer); }
                folders[id] = folder;
            }
            if (folder is null) return null;
            path = System.IO.Path.Combine(folder, path[(end + 1)..].TrimStart('\\', '/'));
        }
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !System.IO.Path.IsPathFullyQualified(path)) return null;
        try { return System.IO.Path.GetFullPath(path); }
        catch (ArgumentException) { return null; }
    }

    private static ImageSource? GetIcon(RegistryKey root, SavedIcon saved)
    {
        if (Icons.TryGetValue(saved.Path, out var cached)) return cached;
        ImageSource? image = null;
        using var key = root.OpenSubKey(saved.Id);
        if (key?.GetValue("IconSnapshot") is byte[] { Length: >= 24 and <= 262144 } bytes &&
            bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                frame.Freeze();
                image = frame;
            }
            catch (Exception error) when (error is NotSupportedException or FileFormatException or ArgumentException) { }
        }
        if (image is null)
        {
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(saved.Path);
                if (icon is not null) image = Launcher.IconImage(icon.Handle);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { }
        }
        if (image is not null)
        {
            if (Icons.Count >= 128) Icons.Clear();
            Icons[saved.Path] = image;
        }
        return image;
    }

    private sealed record SavedIcon(string Path, string Id, string Title, bool Pinned, Guid IconGuid, uint? Uid);
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, nint token, out nint path);
}
