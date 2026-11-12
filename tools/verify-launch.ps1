param([Parameter(Mandatory)][string]$Executable)

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'This test uses a disposable Windows desktop and user profile.' }

$app = (Resolve-Path $Executable).Path
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskName = "LumaShell-$sid"
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runCommand = '"{0}" --logon' -f $app
$folderRoot = Join-Path $env:RUNNER_TEMP ('Luma-Folder-' + [Guid]::NewGuid().ToString('N'))
$folder = Join-Path $folderRoot 'Папка для проверки'
[IO.Directory]::CreateDirectory($folder) | Out-Null

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class LumaLaunchProbe
{
    delegate bool Callback(IntPtr window, IntPtr state);

    [DllImport("user32")] static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder title, int capacity);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32")] public static extern uint SetErrorMode(uint mode);
    [DllImport("user32")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("shell32", CharSet = CharSet.Unicode)] public static extern IntPtr ShellExecute(
        IntPtr owner, string verb, string file, string parameters, string directory, int show);

    public static bool WindowVisible(uint process, string expected)
    {
        bool found = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint pid);
            if (pid != process || !IsWindowVisible(window)) return true;
            var title = new StringBuilder(160);
            GetWindowText(window, title, title.Capacity);
            found = title.ToString() == expected;
            return !found;
        }, IntPtr.Zero);
        return found;
    }

    public static bool DesktopVisible(uint process)
    {
        bool dock = false, island = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint pid);
            if (pid != process || !IsWindowVisible(window)) return true;
            var title = new StringBuilder(128);
            GetWindowText(window, title, title.Capacity);
            dock |= title.ToString() == "Luma • Dock";
            island |= title.ToString() == "Luma • Dynamic Island";
            return !(dock && island);
        }, IntPtr.Zero);
        return dock && island;
    }
}
'@

[LumaLaunchProbe]::SetErrorMode(3) | Out-Null
$started = Get-Date
$process = Start-Process -FilePath $app -PassThru
try {
    $visibleFor = 0
    for ($i = 0; $i -lt 46 -and $visibleFor -lt 14; $i++) {
        if ($process.WaitForExit(500)) { throw "Luma exited during startup with code $($process.ExitCode)" }
        if ([LumaLaunchProbe]::DesktopVisible($process.Id)) { $visibleFor++ }
        else { $visibleFor = 0 }
    }
    if ($visibleFor -lt 14) { throw 'The Dock and Island did not stay visible for seven seconds.' }

    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($task -and $task.Actions.Execute -contains $app) {
        Write-Host 'Fresh-profile startup registered a Task Scheduler logon task.'
    } elseif ((Get-ItemPropertyValue $runKey -Name LumaShell -ErrorAction SilentlyContinue) -eq $runCommand) {
        Write-Host 'Fresh-profile startup registered the Windows Run fallback.'
    } else {
        throw 'Luma started but did not register for logon.'
    }
    Write-Host 'The published executable kept the Dock and Island visible through startup.'

    foreach ($win in 0x5B, 0x5C) {
        [LumaLaunchProbe]::keybd_event($win, 0, 1, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 80
        [LumaLaunchProbe]::keybd_event($win, 0, 3, [UIntPtr]::Zero)
        $launcher = $false
        for ($i = 0; $i -lt 30 -and !$launcher; $i++) {
            Start-Sleep -Milliseconds 100
            $launcher = [LumaLaunchProbe]::WindowVisible($process.Id, 'Luma • Приложения')
        }
        if (!$launcher) { throw "The Win key ($win) did not open the Luma launcher." }
        [LumaLaunchProbe]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)
        [LumaLaunchProbe]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero)
        for ($i = 0; $i -lt 30 -and $launcher; $i++) {
            Start-Sleep -Milliseconds 100
            $launcher = [LumaLaunchProbe]::WindowVisible($process.Id, 'Luma • Приложения')
        }
        if ($launcher) { throw 'Escape did not close the Luma launcher.' }
    }
    Write-Host 'Both Win keys opened the Luma launcher, which received Escape.'

    $result = [LumaLaunchProbe]::ShellExecute([IntPtr]::Zero, $null, $folder, $null, $null, 1)
    if ($result.ToInt64() -le 32) { throw "Windows failed to open the sample folder ($result)." }
    $found = $false
    for ($i = 0; $i -lt 32 -and !$found; $i++) {
        if ($process.WaitForExit(500)) { throw 'Luma exited while Windows opened a folder.' }
        $found = [LumaLaunchProbe]::WindowVisible($process.Id, 'Папка для проверки • Luma Finder')
    }
    if (!$found) { throw 'The Windows folder request did not reach the running Luma Finder.' }
    Write-Host 'Windows opened a Unicode folder with spaces in the running Luma Finder.'
} catch {
    $exeName = [IO.Path]::GetFileName($app)
    Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$started; Id=1000,1026} -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -like "*$exeName*" } |
        Select-Object -ExpandProperty Message | Write-Host
    throw
} finally {
    if (!$process.HasExited) { Stop-Process -Id $process.Id -Force }
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($task -and $task.Actions.Execute -contains $app) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    if ((Get-ItemPropertyValue $runKey -Name LumaShell -ErrorAction SilentlyContinue) -eq $runCommand) {
        Remove-ItemProperty $runKey -Name LumaShell
    }
    foreach ($type in 'Directory', 'Drive') {
        $shell = Get-Item "HKCU:\Software\Classes\$type\shell" -ErrorAction SilentlyContinue
        if (!$shell) { continue }
        $verb = $shell.OpenSubKey('LumaShell.Finder')
        if ($verb -and $verb.GetValue('LumaShellRegistration') -eq 1) {
            $previous = $verb.GetValue('LumaShellPreviousDefault', $null, 'DoNotExpandEnvironmentNames')
            $previousKind = if ($null -ne $previous) { $verb.GetValueKind('LumaShellPreviousDefault') }
            $verb.Close()
            $writable = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("Software\Classes\$type\shell", $true)
            if ($writable.GetValue('') -eq 'LumaShell.Finder') {
                if ($null -ne $previous) { $writable.SetValue('', $previous, $previousKind) }
                else { $writable.DeleteValue('', $false) }
            }
            $writable.DeleteSubKeyTree('LumaShell.Finder', $false)
            $writable.Close()
        } elseif ($verb) { $verb.Close() }
        $shell.Close()
    }
    Remove-Item -LiteralPath $folderRoot -Recurse -Force -ErrorAction SilentlyContinue
}
