namespace LumaShell;

internal sealed class WinTap
{
    internal enum Action { Pass, Hold, Launch, OpenFolder, Suppress, ReplayTap, ReplayChord }
    private int pending;
    private int consumedWin;
    private bool consumedE;

    internal Action Handle(int key, bool down, bool intercept, bool alreadyModified, bool openFolder = false)
    {
        if (key == 0x45 && consumedE)
        {
            if (!down) consumedE = false;
            return Action.Suppress;
        }
        if (consumedWin != 0)
        {
            if (key == consumedWin)
            {
                if (!down) consumedWin = 0;
                return Action.Suppress;
            }
            if (down)
            {
                consumedWin = 0;
                return Action.ReplayChord;
            }
        }
        if (pending == 0)
        {
            if ((!intercept && !openFolder) || !down || alreadyModified || key is not (0x5B or 0x5C)) return Action.Pass;
            pending = key;
            return Action.Hold;
        }

        if (key == pending)
        {
            if (down) return Action.Hold;
            pending = 0;
            return intercept ? Action.Launch : Action.ReplayTap;
        }

        if (!down) return Action.Pass;
        if (openFolder && key == 0x45)
        {
            consumedWin = pending;
            consumedE = true;
            pending = 0;
            return Action.OpenFolder;
        }
        pending = 0;
        return Action.ReplayChord;
    }
}
