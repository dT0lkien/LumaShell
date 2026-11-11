using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace LumaShell;

internal static class FinderFiles
{
    private static readonly ConcurrentDictionary<string, ImageSource> FileIcons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly Lazy<ImageSource> FolderIcon = new(() => Drawing(
        ("M1,5 Q1,3 3,3 H8 L11,6 H17 Q19,6 19,8 V16 Q19,18 17,18 H3 Q1,18 1,16 Z", "#FF328BC8"),
        ("M1,9 Q1,7 3,7 H17 Q19,7 19,9 V16 Q19,18 17,18 H3 Q1,18 1,16 Z", "#FF70C5F2")));
    private static readonly Lazy<ImageSource> GenericFileIcon = new(() => Drawing(
        ("M4,1 H12 L17,6 V19 H4 Z", "#FFDCE5EA"), ("M12,1 V6 H17 Z", "#FF9AABB7")));

    internal static string Downloads
    {
        get
        {
            nint path = 0;
            try
            {
                if (SHGetKnownFolderPath(DownloadsId, 0, 0, out path) >= 0 && Marshal.PtrToStringUni(path) is { Length: > 0 } folder)
                    return folder;
            }
            finally { if (path != 0) Marshal.FreeCoTaskMem(path); }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    internal static FinderEntry[] ReadFolder(string folder, CancellationToken cancellation)
    {
        var entries = new List<FinderEntry>();
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, IgnoreInaccessible = false };
        foreach (var item in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
        {
            cancellation.ThrowIfCancellationRequested();
            bool directory = item is DirectoryInfo;
            string extension = directory ? "" : item.Extension;
            long? size = null;
            DateTime? modified = null;
            try
            {
                if (item is FileInfo file) size = file.Length;
                modified = item.LastWriteTime;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            var icon = directory ? FolderIcon.Value : IconFor(extension);
            entries.Add(new FinderEntry(item.Name, item.FullName, directory, modified, size, extension, icon));
        }
        return entries.ToArray();
    }

    private static ImageSource IconFor(string extension)
    {
        if (FileIcons.TryGetValue(extension, out var icon)) return icon;
        return FileIcons.Count < 256 ? FileIcons.GetOrAdd(extension, ReadFileIcon) : GenericFileIcon.Value;
    }

    private static ImageSource ReadFileIcon(string extension)
    {
        // Request the type icon without opening the user's file or resolving cloud placeholders.
        int apartment = CoInitializeEx(0, 0);
        if (apartment < 0 && apartment != unchecked((int)0x80010106)) return GenericFileIcon.Value;
        var info = new ShellFileInfo();
        try
        {
            SHGetFileInfo("file" + extension, 0x80, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x111);
            return info.Icon == 0 ? GenericFileIcon.Value : Launcher.IconImage(info.Icon) ?? GenericFileIcon.Value;
        }
        finally
        {
            if (info.Icon != 0) DestroyIcon(info.Icon);
            if (apartment >= 0) CoUninitialize();
        }
    }

    private static ImageSource Drawing(params (string Path, string Color)[] shapes)
    {
        var group = new DrawingGroup();
        using (var drawing = group.Open())
            foreach (var shape in shapes)
                drawing.DrawGeometry(Visuals.Paint(shape.Color), null, Geometry.Parse(shape.Path));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    internal static Task<bool> CopyAsync(nint owner, string[] sources, string target)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            nint from = 0, to = 0;
            try
            {
                if (!Directory.Exists(target) || sources.Length == 0 ||
                    sources.Any(path => !Path.IsPathFullyQualified(path) || path.Contains('\0')))
                {
                    done.TrySetResult(false);
                    return;
                }
                // SHFileOperation expects double-NUL-terminated lists. Windows owns conflict dialogs.
                from = Marshal.StringToHGlobalUni(string.Join('\0', sources) + '\0');
                to = Marshal.StringToHGlobalUni(target + '\0');
                var operation = new FileOperation { Window = owner, Operation = 2, From = from, To = to, Flags = 0x40 };
                done.TrySetResult(SHFileOperation(ref operation) == 0 && !operation.Aborted);
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or ExternalException)
            { done.TrySetResult(false); }
            finally
            {
                if (from != 0) Marshal.FreeHGlobal(from);
                if (to != 0) Marshal.FreeHGlobal(to);
            }
        }) { IsBackground = true, Name = "Luma Finder file copy" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int Index;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileOperation
    {
        public nint Window;
        public uint Operation;
        public nint From, To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public nint Mappings, Title;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid folder, uint flags, nint token, out nint path);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref FileOperation operation);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint apartment);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}

internal sealed record FinderEntry(string Name, string Path, bool IsDirectory, DateTime? Modified, long? Size, string Extension, ImageSource Icon)
{
    public string ModifiedLabel => Modified?.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture) ?? "—";
    public string Kind => IsDirectory ? "Папка" : Extension.Length == 0 ? "Файл" : Extension.TrimStart('.').ToUpperInvariant();
    public string SizeLabel => Size switch
    {
        null => "—",
        < 1024 => $"{Size} Б",
        < 1024 * 1024 => $"{Size / 1024d:0.#} КБ",
        < 1024L * 1024 * 1024 => $"{Size / (1024d * 1024):0.#} МБ",
        _ => $"{Size / (1024d * 1024 * 1024):0.#} ГБ"
    };
}
