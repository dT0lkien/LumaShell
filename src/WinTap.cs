namespace LumaShell;

internal sealed class WinTap
{
    internal enum Action { Pass, Hold, Launch, ReplayTap, ReplayChord }
    private int pending;

    internal Action Handle(int key, bool down, bool intercept, bool alreadyModified)
    {
        if (pending == 0)
        {
            if (!intercept || !down || alreadyModified || key is not (0x5B or 0x5C)) return Action.Pass;
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
        pending = 0;
        return Action.ReplayChord;
    }
}
