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
                    Base("#FFEAF5FF", "#FFA7C5E5");
                    Shape("M9,24 Q9,20 13,20 H23 Q25,20 27,22 L30,25 H50 Q55,25 55,30 V49 Q55,54 50,54 H14 Q9,54 9,49 Z",
                        Paint("#33284F86"));
                    Shape("M9,19 Q9,15 13,15 H23 Q25,15 27,17 L30,20 H51 Q55,20 55,24 V45 Q55,50 50,50 H14 Q9,50 9,45 Z",
                        Gradient("#FF67C9FE", "#FF1361CC"), "#FF3187D9", 0.8);
                    Shape("M13,24 Q13,22 15,22 H50 Q52,22 52,24 V30 H13 Z", Paint("#FFDDF5FF"));
                    Shape("M8,29 Q8,25 12,25 H51 Q56,25 56,30 L54,46 Q53,50 49,50 H15 Q11,50 10,46 Z",
                        Gradient("#FF5DDBFF", "#FF228BEB"), "#FF238ADC", 0.8);
                    Line("M12,28 H52", "#A9D8F7FF", 1);
                    dc.DrawRoundedRectangle(Paint("#A9E0F5FF"), null, new Rect(27, 36, 10, 3), 1.5, 1.5);
                    break;
                case "shell:AppsFolder":
                    Base("#FFFFFFFF", "#FFD8E3E9");
                    string[] colors = ["#FFFF7181", "#FFFFB64A", "#FF6BD1F4", "#FF81D479", "#FFB9A1F1", "#FF68B0F4", "#FFFFCB69", "#FF7CD2CF", "#FF91A6B9"];
                    for (int index = 0; index < 9; index++)
                        dc.DrawRoundedRectangle(Gradient("#ECFFFFFF", colors[index]), null,
                            new Rect(12 + index % 3 * 15, 12 + index / 3 * 15, 11, 11), 3, 3);
                    break;
                case "https://www.bing.com/":
                    Base("#FF59738E", "#FF304357");
                    Line("M51,32 A19,19 0 1 1 13,32 A19,19 0 1 1 51,32 M13,32 H51 M32,13 C20,23 20,41 32,51 M32,13 C44,23 44,41 32,51",
                        "#FFF1F6FD", 2.1);
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
                    Base("#FF626D80", "#FF262C39");
                    var gear = new StreamGeometry { FillRule = FillRule.EvenOdd };
                    using (var path = gear.Open())
                    {
                        // Eight broad teeth read clearly at Dock sizes.
                        double[] radii = [18.5, 18.5, 23, 23, 18.5];
                        double[] angles = [-0.5, -0.28, -0.18, 0.18, 0.28];
                        for (int tooth = 0; tooth < 8; tooth++)
                        {
                            for (int corner = 0; corner < angles.Length; corner++)
                            {
                                double angle = (tooth + angles[corner]) * Math.PI / 4;
                                var point = new Point(32 + radii[corner] * Math.Sin(angle), 32 - radii[corner] * Math.Cos(angle));
                                if (tooth == 0 && corner == 0) path.BeginFigure(point, true, true);
                                else path.LineTo(point, true, false);
                            }
                        }
                    }
                    var center = new Point(32, 32);
                    var metal = new CombinedGeometry(GeometryCombineMode.Exclude, gear, new EllipseGeometry(center, 12, 12));
                    dc.PushTransform(new TranslateTransform(0, 2));
                    dc.DrawGeometry(Paint("#750B1019"), null, metal);
                    dc.Pop();
                    dc.DrawGeometry(Gradient("#FFF8FCFF", "#FF9CAABF"), new Pen(Paint("#AFF8FCFF"), 0.8) { LineJoin = PenLineJoin.Round }, metal);
                    dc.DrawEllipse(null, new Pen(Paint("#94F1F6FF"), 0.9), center, 15.4, 15.4);
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
