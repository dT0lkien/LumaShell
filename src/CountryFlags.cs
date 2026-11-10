using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LumaShell;

internal static class CountryFlags
{
    private static readonly Dictionary<string, ImageSource?> Loaded = new(StringComparer.Ordinal);

    internal static ImageSource? Get(string code)
    {
        if (Loaded.TryGetValue(code, out var cached)) return cached;
        using var stream = typeof(CountryFlags).Assembly.GetManifestResourceStream("LumaShell.Flags.zip");
        if (stream is null) return null;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        ImageSource? flag = null;
        if (archive.GetEntry(code.ToLowerInvariant() + ".png") is { } entry)
        {
            using var image = entry.Open();
            using var data = new MemoryStream();
            image.CopyTo(data);
            data.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = data;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            flag = bitmap;
        }
        Loaded[code] = flag;
        return flag;
    }
}
