using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace LumaShell;

internal static class FolderRouting
{
    internal const string Argument = "--open-folder";
    private const string Verb = "LumaShell.Finder";
    private const string OwnerMarker = "LumaShellRegistration";
    private const string PreviousDefault = "LumaShellPreviousDefault";
    private const int MaximumPayload = 128 * 1024;
    private static readonly string[] Types = ["Directory", "Drive"];
    private static readonly PipeOptions PrivatePipe = PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous;
    private static readonly string PipeName = SessionPipeName();

    internal static string? ReadArgument(string[] args) =>
        args.Length == 2 && args[0] == Argument ? Normalize(args[1]) : null;

    private static string? Normalize(string path)
    {
        if (path.Length is 0 or > 32767 || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    internal static bool Forward(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { return Send(path, timeout.Token).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException)
        { return false; }
    }

    private static async Task<bool> Send(string path, CancellationToken cancellation)
    {
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PrivatePipe);
        await client.ConnectAsync(cancellation).ConfigureAwait(false);
        if (GetNamedPipeServerProcessId(client.SafePipeHandle, out uint process)) AllowSetForegroundWindow(process);
        byte[] payload = Encoding.UTF8.GetBytes(path);
        await client.WriteAsync(BitConverter.GetBytes(payload.Length), cancellation).ConfigureAwait(false);
        await client.WriteAsync(payload, cancellation).ConfigureAwait(false);
        byte[] ack = new byte[1];
        await client.ReadExactlyAsync(ack, cancellation).ConfigureAwait(false);
        return ack[0] == 1;
    }

    internal static async Task Listen(Action<string> open, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PrivatePipe);
                await server.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                byte[] header = new byte[sizeof(int)];
                await server.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                int length = BitConverter.ToInt32(header);
                if (length is <= 0 or > MaximumPayload) continue;
                byte[] payload = new byte[length];
                await server.ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);
                if (Normalize(Encoding.UTF8.GetString(payload)) is not { } path) continue;
                open(path);
                await server.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                if (!cancellation.IsCancellationRequested) await Task.Delay(150).ConfigureAwait(false);
            }
        }
    }

    internal static bool Configure(bool enable)
    {
        string? executable = Worker.Executable;
        if (enable && executable is null) return false;
        try
        {
            foreach (string type in Types)
            {
                if (enable) Register(type, executable!);
                else Restore(type);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            if (enable)
                foreach (string type in Types)
                {
                    try { Restore(type); }
                    catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                }
            return false;
        }
        finally { SHChangeNotify(0x08000000, 0, 0, 0); }
    }

    private static string ShellKey(string type) => $@"Software\Classes\{type}\shell";

    private static void Register(string type, string executable)
    {
        using var shell = Registry.CurrentUser.CreateSubKey(ShellKey(type));
        object? previous = shell.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (previous is not null && previous is not string)
            throw new InvalidOperationException("The folder handler default is not a string.");
        using var existing = shell.OpenSubKey(Verb);
        if (existing is not null && existing.GetValue(OwnerMarker) is not 1)
            throw new InvalidOperationException("The folder verb is owned by another app.");
        using var verb = shell.CreateSubKey(Verb);
        verb.SetValue(OwnerMarker, 1);
        verb.SetValue(null, "Открыть в Luma Finder");
        verb.SetValue("Icon", $"\"{executable}\",0");
        using var command = verb.CreateSubKey("command");
        // The final dot keeps a drive-root backslash away from the closing command-line quote.
        command.SetValue(null, $"\"{executable}\" {Argument} \"%1\\.\"");
        if (previous is string current && current.Equals(Verb, StringComparison.OrdinalIgnoreCase)) return;
        if (previous is string text) verb.SetValue(PreviousDefault, text, shell.GetValueKind(null));
        else verb.DeleteValue(PreviousDefault, throwOnMissingValue: false);
        shell.SetValue(null, Verb);
    }

    private static void Restore(string type)
    {
        using var shell = Registry.CurrentUser.OpenSubKey(ShellKey(type), writable: true);
        if (shell is null) return;
        using (var verb = shell.OpenSubKey(Verb))
        {
            if (verb?.GetValue(OwnerMarker) is not 1) return;
            if (string.Equals(shell.GetValue(null) as string, Verb, StringComparison.OrdinalIgnoreCase))
            {
                if (verb.GetValue(PreviousDefault, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string previous)
                    shell.SetValue(null, previous, verb.GetValueKind(PreviousDefault));
                else shell.DeleteValue("", throwOnMissingValue: false);
            }
        }
        shell.DeleteSubKeyTree(Verb, throwOnMissingSubKey: false);
    }

    private static string SessionPipeName()
    {
        using var process = Process.GetCurrentProcess();
        return $"LumaShell.Folders.{process.SessionId}";
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, nint item, nint other);
    [DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint process);
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint process);
}
