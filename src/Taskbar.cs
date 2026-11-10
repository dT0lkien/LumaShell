using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LumaShell;

internal sealed class Taskbar : IDisposable
{
    private static readonly string LeasePath = Path.Combine(Worker.Data, $"taskbar-{Session()}.json");
    private const string LockName = @"Local\LumaShell.TaskbarLease";
    private string? lease;
    private EventWaitHandle? ready;
    private DateTime readyDeadline;
    private DateTime revealUntil;
    private DateTime nextStateCheck;
    private nint currentBar;
    private bool faulted;
    internal bool Active { get; private set; }
    internal bool Revealing => Active && DateTime.UtcNow < revealUntil;
    internal event Action? Failed;

    internal void SetEnabled(bool enabled)
    {
        if (!enabled) { Dispose(); faulted = false; return; }
        if (lease is not null || faulted) return;
        var bar = FindWindow("Shell_TrayWnd", null);
        if (bar == 0) return;
        try
        {
            Restore();
            string token = Guid.NewGuid().ToString("N");
            ready = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(token));
            Directory.CreateDirectory(Worker.Data);
            File.WriteAllText(LeasePath, JsonSerializer.Serialize(new Lease(token, GetState())));
            lease = token;
            readyDeadline = DateTime.UtcNow.AddSeconds(20);
            if (!Worker.Start("--watch-taskbar", token)) Fail();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { Fail(); }
    }

    internal void Tick()
    {
        if (ready is not null)
        {
            if (!ready.WaitOne(0))
            {
                if (DateTime.UtcNow >= readyDeadline) Fail();
                return;
            }
            ready.Dispose();
            ready = null;
            Active = true;
        }
        var now = DateTime.UtcNow;
        if (!Active || now < revealUntil) return;
        nint primary = FindWindow("Shell_TrayWnd", null);
        if (primary == 0) return;
        if (currentBar != primary || now >= nextStateCheck)
        {
            currentBar = primary;
            nextStateCheck = now.AddSeconds(2);
            uint state = GetState();
            if ((state & 1) == 0) SetState(primary, state | 1);
        }
        foreach (var window in Bars(primary))
        {
            if (IsWindowVisible(window)) ShowWindowAsync(window, 0);
        }
    }

    internal async void Reveal()
    {
        revealUntil = DateTime.UtcNow.AddSeconds(15);
        var primary = FindWindow("Shell_TrayWnd", null);
        if (primary == 0) return;
        if (Active)
        {
            SetState(primary, GetState() & ~1u);
            nextStateCheck = DateTime.MinValue;
        }
        foreach (var window in Bars(primary)) Native.RestoreWindow(window);
        await Task.Delay(170);
        for (int attempt = 0; attempt < 10 && Native.ShortcutModifiersDown(); attempt++) await Task.Delay(70);
        if (Native.ShortcutModifiersDown()) return;
        Native.Shortcut(0x5B, 0x42);
    }

    private void Fail()
    {
        Dispose();
        faulted = true;
        Failed?.Invoke();
    }

    public void Dispose()
    {
        if (lease is { } token) Restore(token);
        lease = null;
        Active = false;
        currentBar = 0;
        ready?.Dispose();
        ready = null;
    }

    internal static void Watch(Process? parent, string token)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(EventName(token), out var eventHandle))
            {
                using (eventHandle) eventHandle.Set();
            }
            while (parent is not null && !parent.WaitForExit(2000) && File.Exists(LeasePath)) { }
        }
        catch (InvalidOperationException) { }
        finally { Restore(token); }
    }

    internal static void Recover() => Restore();

    private static void Restore(string? token = null)
    {
        using var guard = new Mutex(false, LockName);
        try { guard.WaitOne(); }
        catch (AbandonedMutexException) { }
        try
        {
            if (!File.Exists(LeasePath)) return;
            var lease = JsonSerializer.Deserialize<Lease>(File.ReadAllText(LeasePath));
            if (lease is null || token is not null && token != lease.Token) return;
            nint primary = FindWindow("Shell_TrayWnd", null);
            if (primary == 0) return;
            foreach (var window in Bars(primary)) Native.RestoreWindow(window);
            SetState(primary, lease.State);
            File.Delete(LeasePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        finally { guard.ReleaseMutex(); }
    }

    private static uint GetState()
    {
        var data = new AppBarData { Size = (uint)Marshal.SizeOf<AppBarData>() };
        return (uint)SHAppBarMessage(4, ref data);
    }

    private static void SetState(nint window, uint state)
    {
        var data = new AppBarData { Size = (uint)Marshal.SizeOf<AppBarData>(), Window = window, State = (nint)state };
        SHAppBarMessage(10, ref data);
    }

    private static IEnumerable<nint> Bars(nint primary)
    {
        yield return primary;
        nint window = 0;
        while ((window = FindWindowEx(0, window, "Shell_SecondaryTrayWnd", null)) != 0) yield return window;
    }

    private static int Session() { using var process = Process.GetCurrentProcess(); return process.SessionId; }
    private static string EventName(string token) => $@"Local\LumaShell.Taskbar.{token}";
    private sealed record Lease(string Token, uint State);

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public nint Window;
        public uint Message, Edge;
        public int Left, Top, Right, Bottom;
        public nint State;
    }

    [DllImport("shell32.dll")]
    private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);
}
