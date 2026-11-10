namespace LumaShell;

internal sealed class WinTap
{
    private int candidate;

    internal bool Handle(int key, bool down, bool alreadyModified, bool repeating = false)
    {
        if (key is not (0x5B or 0x5C))
        {
            if (down) candidate = 0;
            return false;
        }
        if (down)
        {
            if (!repeating) candidate = alreadyModified ? 0 : key;
            return false;
        }
        bool tap = candidate == key;
        candidate = 0;
        return tap;
    }

    internal void Reset() => candidate = 0;
}
