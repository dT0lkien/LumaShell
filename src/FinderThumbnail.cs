using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;

namespace LumaShell;

internal sealed class FinderThumbnail : Image
{
    private static readonly SemaphoreSlim Decoders = new(3);
    private CancellationTokenSource? pending;

    public FinderThumbnail()
    {
        Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) => Refresh();
        DataContextChanged += (_, _) => Refresh();
        Unloaded += (_, _) => { pending?.Cancel(); pending = null; };
    }

    private async void Refresh()
    {
        pending?.Cancel();
        pending = null;
        var entry = DataContext as FinderEntry;
        Source = entry?.Icon;
        if (!IsLoaded || entry is null || entry.IsDirectory || !QuickLook.IsImage(entry.Extension)) return;
        using var request = new CancellationTokenSource();
        pending = request;
        try
        {
            await Decoders.WaitAsync(request.Token);
            try
            {
                var image = await Task.Run(() => QuickLook.ReadImage(entry.Path, 96), request.Token);
                if (!request.IsCancellationRequested && image is not null) Source = image;
            }
            finally { Decoders.Release(); }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally { if (ReferenceEquals(pending, request)) pending = null; }
    }
}
