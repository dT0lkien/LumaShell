namespace LumaShell;

internal sealed class WinTap
{
    internal enum Action { Pass, Hold, Launch, OpenFolder, Capture, Suppress, ReplayTap, ReplayChord }
    private int pending;
    private bool pendingModified;
    private bool pendingLauncher;
    private int consumedWin;
    private int consumedKey;
    private int passedWin;

    internal Action Handle(int key, bool down, bool intercept, bool alreadyModified, bool openFolder = false, bool capture = false)
    {
        int winBit = key switch { 0x5B => 1, 0x5C => 2, _ => 0 };
        if ((passedWin & winBit) != 0)
        {
            if (!down) passedWin &= ~winBit;
            return Action.Pass;
        }
        if (key == consumedKey)
        {
            if (!down) consumedKey = 0;
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
                passedWin |= (consumedWin == 0x5B ? 1 : 2) | winBit;
                consumedWin = 0;
                return Action.ReplayChord;
            }
        }
        if (pending == 0)
        {
            if (!down || winBit == 0) return Action.Pass;
            if (!intercept && (!openFolder || alreadyModified))
            {
                passedWin |= winBit;
                return Action.Pass;
            }
            pending = key;
            pendingModified = alreadyModified;
            pendingLauncher = intercept;
            return Action.Hold;
        }

        if (key == pending)
        {
            if (down) return Action.Hold;
            pending = 0;
            if (pendingModified) return Action.Suppress;
            if (intercept) return Action.Launch;
            return pendingLauncher ? Action.Suppress : Action.ReplayTap;
        }

        if (!down) return Action.Pass;
        if ((capture && key == 0x53) || (openFolder && !pendingModified && key == 0x45))
        {
            consumedWin = pending;
            consumedKey = key;
            pending = 0;
            return key == 0x53 ? Action.Capture : Action.OpenFolder;
        }
        passedWin |= (pending == 0x5B ? 1 : 2) | winBit;
        pending = 0;
        return Action.ReplayChord;
    }
}
