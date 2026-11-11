using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LumaShell;

internal static class Wallpaper
{
    private const string Resource = "LumaShell.AlpineLake.jpg";
    private const string FileName = "Luma-Alpine-Lake.jpg";
    private static readonly Guid DesktopWallpaperClass = new("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD");

    internal static bool Apply()
    {
        object? desktop = null;
        string? temporary = null;
        try
        {
            using var resource = typeof(Wallpaper).Assembly.GetManifestResourceStream(Resource);
            if (resource is null) return false;
            var folder = Path.Combine(Worker.Data, "wallpapers");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, FileName);
            temporary = path + ".tmp";
            using (var file = File.Create(temporary)) resource.CopyTo(file);
            File.Move(temporary, path, overwrite: true);
            temporary = null;

            var type = Type.GetTypeFromCLSID(DesktopWallpaperClass, throwOnError: true)!;
            desktop = Activator.CreateInstance(type);
            return desktop is IDesktopWallpaper wallpaper && wallpaper.SetWallpaper(null, path) >= 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException or
            InvalidCastException or System.Security.SecurityException)
        { return false; }
        finally
        {
            if (desktop is not null && Marshal.IsComObject(desktop)) Marshal.ReleaseComObject(desktop);
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        [PreserveSig]
        int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitor, [MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
