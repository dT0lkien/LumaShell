using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace LumaShell;

internal static class SessionPower
{
    internal enum Action { Sleep, Lock, Restart, ShutDown, SignOut }

    private static bool busy;

    internal static async void Run(Window owner, Action action)
    {
        if (busy) return;
        busy = true;
        string title = action switch
        {
            Action.Sleep => "Перевести компьютер в сон",
            Action.Lock => "Заблокировать экран",
            Action.Restart => "Перезагрузить компьютер",
            Action.ShutDown => "Завершить работу",
            Action.SignOut => "Выйти из сеанса Windows",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        try
        {
            await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            if (action is Action.Restart or Action.ShutDown or Action.SignOut &&
                MessageBox.Show(owner, $"{title}? Сохрани открытые документы перед продолжением.", "Luma · Сеанс Windows",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            if (action == Action.Lock)
            {
                if (!LockWorkStation()) throw new Win32Exception();
            }
            else if (action == Action.Sleep)
            {
                if (!await Task.Run(() => Forms.Application.SetSuspendState(Forms.PowerState.Suspend, false, false)))
                    throw new InvalidOperationException("Windows rejected the sleep request.");
            }
            else
            {
                var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
                {
                    Arguments = action switch
                    {
                        Action.Restart => "/r /t 0",
                        Action.ShutDown => "/s /t 0",
                        _ => "/l"
                    },
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start shutdown.exe.");
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) throw new Win32Exception(process.ExitCode);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            SessionLog.Write($"Windows session action {action} failed. {ex.Message}");
            MessageBox.Show(owner, $"Windows не удалось выполнить действие «{title}».", "Luma · Сеанс Windows",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { busy = false; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();
}
