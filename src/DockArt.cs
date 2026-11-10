using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static LumaShell.Visuals;

namespace LumaShell;

// Original vector artwork. All coordinates share a 64-unit canvas so optical sizes stay consistent.
internal static class DockArt
{
    private static readonly Dictionary<string, ImageSource> Artwork = new();
    internal static ImageSource? Builtin(string target, string arguments)
    {
        string kind = arguments.Length > 0 ? arguments : target.ToLowerInvariant();
        if (kind is not ("explorer.exe" or "shell:AppsFolder" or "https://www.bing.com/" or "notepad.exe" or
            "cmd.exe" or "ms-settings:" or "shell:Downloads" or "shell:RecycleBinFolder")) return null;
        if (Artwork.TryGetValue(kind, out var cached)) return cached;
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            // Transparent canvas fixes the dimensions even for the narrower trash and folder silhouettes.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 64, 64));
            void Shape(string path, Brush? fill, string? stroke = null, double thickness = 1) =>
                dc.DrawGeometry(fill, stroke is null ? null : new Pen(Paint(stroke), thickness), Geometry.Parse(path));
            void Line(string path, string color, double width) => Shape(path, null, color, width);
            void Base(string top, string bottom)
            {
                dc.DrawRoundedRectangle(Gradient(top, bottom), new Pen(Paint("#80FFFFFF"), 0.8), new Rect(2, 2, 60, 60), 14, 14);
                dc.DrawRoundedRectangle(null, new Pen(Paint("#18000000"), 0.8), new Rect(2.5, 2.5, 59, 59), 13.5, 13.5);
            }
            switch (kind)
            {
                case "explorer.exe":
                    Base("#FF7DE6FF", "#FF0874E8");
                    dc.PushClip(new RectangleGeometry(new Rect(2, 2, 60, 60), 14, 14));
                    Shape("M34,2 H63 V63 H30 Q37,43 29,33 Q24,23 34,2", Gradient("#FFF0F9FF", "#FF99D6FF"));
                    dc.Pop();
                    Line("M32,7 Q27,23 30,33 H37 L34,49", "#FF185B98", 1.5);
                    Line("M17,23 V30 M46,23 V30", "#FF114A80", 2);
                    Shape("M15,39 Q31,50 49,37 Q34,53 18,45 Z", Paint("#FF10487C"));
                    break;
                case "shell:AppsFolder":
                    Base("#FFFFFFFF", "#FFD8E3E9");
                    string[] colors = ["#FFFF7181", "#FFFFB64A", "#FF6BD1F4", "#FF81D479", "#FFB9A1F1", "#FF68B0F4", "#FFFFCB69", "#FF7CD2CF", "#FF91A6B9"];
                    for (int index = 0; index < 9; index++)
                        dc.DrawRoundedRectangle(Gradient("#ECFFFFFF", colors[index]), null,
                            new Rect(12 + index % 3 * 15, 12 + index / 3 * 15, 11, 11), 3, 3);
                    break;
                case "https://www.bing.com/":
                    Base("#FF6EDEFF", "#FF1977E9");
                    dc.DrawEllipse(Gradient("#FFEDFFFF", "#FFB4DEFF"), null, new Point(32, 32), 26, 26);
                    dc.DrawEllipse(Gradient("#FF5AD8FF", "#FF086AD2"), new Pen(Paint("#FF5CB8ED"), 0.6), new Point(32, 32), 24, 24);
                    for (int index = 0; index < 36; index++)
                    {
                        double angle = index * Math.PI / 18;
                        double inside = index % 3 == 0 ? 19 : 21;
                        dc.DrawLine(new Pen(White, index % 3 == 0 ? 1.1 : 0.7),
                            new Point(32 + Math.Sin(angle) * inside, 32 - Math.Cos(angle) * inside),
                            new Point(32 + Math.Sin(angle) * 23, 32 - Math.Cos(angle) * 23));
                    }
                    Shape("M19,46 L29,29 L45,17 L35,35 Z", Paint("#FFF4F8FC"));
                    Shape("M29,29 L45,17 L35,35 Z", Gradient("#FFFF7566", "#FFDC3240"));
                    break;
                case "notepad.exe":
                    Base("#FFFFFFFF", "#FFEEF0E8");
                    dc.PushClip(new RectangleGeometry(new Rect(2, 2, 60, 60), 14, 14));
                    dc.DrawRectangle(Gradient("#FFFFE75F", "#FFFFCA23"), null, new Rect(2, 2, 60, 17));
                    dc.Pop();
                    Line("M3,20 H61", "#FFDFB332", 1);
                    for (int line = 0; line < 4; line++) Line($"M3,{29 + line * 8} H61", "#FFCFD2D0", 0.8);
                    break;
                case "cmd.exe":
                    Base("#FF35373A", "#FF101113");
                    Line("M14,18 L25,26 L14,34 M29,35 H42", "#FFF3F4F7", 2.5);
                    break;
                case "ms-settings:":
                    Base("#FFDADDE1", "#FF737B84");
                    for (int index = 0; index < 16; index++)
                    {
                        dc.PushTransform(new RotateTransform(index * 22.5, 32, 32));
                        dc.DrawRoundedRectangle(Gradient("#FFF1F3F5", "#FF7B8591"), new Pen(Paint("#FF555E68"), 0.5), new Rect(28.5, 7, 7, 12), 1, 1);
                        dc.Pop();
                    }
                    dc.DrawEllipse(Gradient("#FFF2F4F8", "#FF535C68"), new Pen(Paint("#FF555E68"), 1), new Point(32, 32), 20, 20);
                    dc.DrawEllipse(Paint("#FF4A535D"), new Pen(Paint("#FFE6E9EB"), 2), new Point(32, 32), 13, 13);
                    dc.DrawEllipse(Gradient("#FFABB4C0", "#FF4D5662"), new Pen(Paint("#FF333D49"), 1), new Point(32, 32), 6, 6);
                    break;
                case "shell:Downloads":
                    Shape("M5,19 Q5,15 9,15 H25 L31,20 H55 Q59,20 59,24 V55 Q59,59 55,59 H9 Q5,59 5,55 Z", Gradient("#FF91E0FF", "#FF2188DA"), "#FF2A89C9", 0.8);
                    dc.DrawRoundedRectangle(Paint("#FFF3F5F7"), new Pen(Paint("#FFACBDC9"), 0.8), new Rect(17, 6, 32, 39), 2, 2);
                    Line("M23,15 H42 M23,21 H42 M23,27 H38", "#FFC1CBD5", 1);
                    Shape("M4,29 Q4,26 8,26 H56 Q60,26 60,30 L57,56 Q57,60 53,60 H11 Q7,60 7,56 Z", Gradient("#FF75D8FF", "#FF288FE0"), "#FF2783CA", 0.8);
                    Line("M32,34 V49 M25,43 L32,50 L39,43", "#FFD9F5FF", 2.5);
                    break;
                case "shell:RecycleBinFolder":
                    Shape("M16,16 L20,59 Q32,65 44,59 L49,16 Z", Gradient("#99FFFFFF", "#C0BBC9CD"), "#C0E9F0F0", 1.2);
                    dc.DrawEllipse(Gradient("#FFC4CDD0", "#FF77858B"), new Pen(Paint("#FFDCE4E6"), 1.5), new Point(32, 16), 17, 5);
                    Shape("M20,15 L23,5 L34,8 L32,18 Z", Paint("#FFF5F8FB"), "#FFC1C7CF", 0.5);
                    Shape("M29,17 L36,4 L44,10 L42,19 Z", Gradient("#FFFFC656", "#FFF39040"));
                    Shape("M36,19 L43,10 L49,16 L44,20 Z", Paint("#FF5DB9F1"));
                    dc.DrawEllipse(null, new Pen(Paint("#FFF0F5F5"), 1.3), new Point(32, 16), 17, 5);
                    Line("M23,25 L25,54 M31,26 V56 M40,25 L38,54", "#65FFFFFF", 1);
                    break;
            }
        }
        var image = new DrawingImage(group);
        image.Freeze();
        Artwork[kind] = image;
        return image;
    }

    internal static ImageSource? Extract(string path)
    {
        if (ExtractLarge(path) is { } image) return image;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            return icon is null ? null : Launcher.IconImage(icon.Handle);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    private static ImageSource? ExtractLarge(string path)
    {
        IShellItemImageFactory? factory = null;
        nint bitmap = 0;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, 0, ref iid, out factory) < 0 || factory is null ||
                factory.GetImage(new SizePixels { Width = 128, Height = 128 }, 0x5, out bitmap) < 0 || bitmap == 0) return null;
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException) { return null; }
        finally
        {
            if (bitmap != 0) DeleteObject(bitmap);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SizePixels { public int Width, Height; }
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SizePixels size, uint flags, out nint bitmap);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint context, ref Guid iid, out IShellItemImageFactory? item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}
