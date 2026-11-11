using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using Microsoft.Win32;

namespace LumaShell;

internal static class LogonStartup
{
    internal const string Argument = "--logon";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "LumaShell";
    private const int InteractiveToken = 3;
    private const int CreateOrUpdate = 6;
    private static readonly XNamespace Scheduler = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    internal static bool Configure(bool enabled)
    {
        string? executable = Worker.Executable;
        if (enabled && executable is null) return false;
        bool taskConfigured = ConfigureTask(enabled, executable);
        try
        {
            if (!enabled || taskConfigured)
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                run?.DeleteValue(RunName, throwOnMissingValue: false);
                return !enabled || taskConfigured;
            }
            string command = $"\"{executable}\" {Argument}";
            if (command.Length > 260) return false;
            using var fallback = Registry.CurrentUser.CreateSubKey(RunKey);
            fallback.SetValue(RunName, command, RegistryValueKind.String);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return enabled && taskConfigured; }
    }

    private static bool ConfigureTask(bool enabled, string? executable)
    {
        object? service = null, root = null, existing = null, created = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User?.Value is not { } user) return false;
            string name = $"LumaShell-{user}";
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null || (service = Activator.CreateInstance(type)) is null) return false;
            dynamic connection = service;
            connection.Connect();
            root = connection.GetFolder(@"\");
            dynamic folder = root!;
            try { existing = folder.GetTask(name); }
            // COM automation maps ERROR_FILE_NOT_FOUND to FileNotFoundException on .NET.
            catch (Exception error) when ((error is FileNotFoundException or COMException) &&
                error.HResult == unchecked((int)0x80070002)) { }
            if (!enabled)
            {
                if (existing is not null) folder.DeleteTask(name, 0);
                return true;
            }
            if (existing is not null)
            {
                // Preserve a task disabled by the user in Task Scheduler.
                string xml = ((dynamic)existing).Xml;
                var command = XElement.Parse(xml).Element(Scheduler + "Actions")?.Element(Scheduler + "Exec");
                if (string.Equals((string?)command?.Element(Scheduler + "Command"), executable, StringComparison.OrdinalIgnoreCase) &&
                    (string?)command?.Element(Scheduler + "Arguments") == Argument) return true;
            }
            created = folder.RegisterTask(name, Definition(user, executable!), CreateOrUpdate, user, null, InteractiveToken, null);
            return created is not null;
        }
        catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or System.Security.SecurityException or
            Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Xml.XmlException)
        { return false; }
        finally
        {
            Release(created);
            Release(existing);
            Release(root);
            Release(service);
        }
    }

    private static string Definition(string user, string executable)
    {
        XElement Element(string name, object value) => new(Scheduler + name, value);
        return new XElement(Scheduler + "Task", new XAttribute("version", "1.2"),
            Element("Triggers", Element("LogonTrigger", Element("UserId", user))),
            Element("Principals", new XElement(Scheduler + "Principal", new XAttribute("id", "User"),
                Element("UserId", user), Element("LogonType", "InteractiveToken"), Element("RunLevel", "LeastPrivilege"))),
            Element("Settings", new object[]
            {
                Element("MultipleInstancesPolicy", "IgnoreNew"),
                Element("DisallowStartIfOnBatteries", false),
                Element("StopIfGoingOnBatteries", false),
                Element("ExecutionTimeLimit", "PT0S"),
                Element("Priority", 5)
            }),
            new XElement(Scheduler + "Actions", new XAttribute("Context", "User"),
                Element("Exec", new object[]
                {
                    Element("Command", executable),
                    Element("Arguments", Argument),
                    Element("WorkingDirectory", Path.GetDirectoryName(executable)!)
                }))).ToString(SaveOptions.DisableFormatting);
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
