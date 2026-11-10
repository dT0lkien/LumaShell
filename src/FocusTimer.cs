using System;

namespace LumaShell;

internal sealed class FocusTimer
{
    private DateTimeOffset? deadline;
    private TimeSpan paused;

    internal bool Running => deadline.HasValue;
    internal bool Paused => paused > TimeSpan.Zero;
    internal bool Finished { get; private set; }

    internal TimeSpan Remaining(DateTimeOffset now) => deadline is { } end ? MaxZero(end - now) : paused;

    internal void Start(TimeSpan duration, DateTimeOffset now)
    {
        Reset();
        deadline = now + duration;
    }

    internal void TogglePause(DateTimeOffset now)
    {
        if (Running)
        {
            var remaining = Remaining(now);
            if (remaining == TimeSpan.Zero) return;
            paused = remaining;
            deadline = null;
        }
        else if (Paused) Start(paused, now);
    }

    internal bool Tick(DateTimeOffset now)
    {
        if (deadline is not { } end || end > now) return false;
        deadline = null;
        Finished = true;
        return true;
    }

    internal void Reset()
    {
        deadline = null;
        paused = TimeSpan.Zero;
        Finished = false;
    }

    private static TimeSpan MaxZero(TimeSpan value) => value > TimeSpan.Zero ? value : TimeSpan.Zero;
}
