using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace LumaShell;

internal sealed class Media
{
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private GlobalSystemMediaTransportControlsSession? session;
    private bool refreshing;
    internal Track? Current { get; private set; }

    internal async Task RefreshAsync()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            session = manager.GetCurrentSession();
            if (session is null)
            {
                Current = null;
                return;
            }

            var properties = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var controls = playback.Controls;
            var timeline = session.GetTimelineProperties();
            bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            Current = new Track(properties.Title, properties.Artist,
                playing, controls.IsPreviousEnabled,
                controls.IsPlayPauseToggleEnabled || (playing ? controls.IsPauseEnabled : controls.IsPlayEnabled), controls.IsNextEnabled,
                timeline.Position - timeline.StartTime, timeline.EndTime - timeline.StartTime, timeline.LastUpdatedTime);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            session = null;
            manager = null;
            Current = null;
        }
        finally { refreshing = false; }
    }

    internal async Task<bool> ControlAsync(string action)
    {
        var target = session;
        if (target is null) return false;
        try
        {
            return action switch
            {
                "previous" => await target.TrySkipPreviousAsync(),
                "toggle" => await PlayPause(target),
                "next" => await target.TrySkipNextAsync(),
                _ => false
            };
        }
        catch (Exception exception) when (IsUnavailable(exception)) { return false; }
    }

    private static async Task<bool> PlayPause(GlobalSystemMediaTransportControlsSession target)
    {
        var playback = target.GetPlaybackInfo();
        if (playback.Controls.IsPlayPauseToggleEnabled) return await target.TryTogglePlayPauseAsync();
        return playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            ? await target.TryPauseAsync() : await target.TryPlayAsync();
    }

    private static bool IsUnavailable(Exception exception) =>
        exception is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException;

    internal sealed record Track(string Title, string Artist, bool Playing, bool Previous, bool Toggle, bool Next,
        TimeSpan Position, TimeSpan Duration, DateTimeOffset ReportedAt);
}
