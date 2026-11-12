using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
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
    private const int IgnoreRegistrationTriggers = 0x20;
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
            root = Connect(out service);
            if (root is null) return false;
            dynamic folder = root;
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
                var current = XElement.Parse((string)((dynamic)existing).Xml);
                if (Matches(current, executable)) return true;
            }
            created = folder.RegisterTask(name, Definition(user, executable!), CreateOrUpdate | IgnoreRegistrationTriggers,
                user, null, InteractiveToken, null);
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

    internal static bool StartRegistered(string executable)
    {
        if (!Preferences.Load().AutoStart) return false;
        object? service = null, root = null, existing = null, started = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User?.Value is not { } user) return false;
            root = Connect(out service);
            if (root is null) return false;
            existing = ((dynamic)root).GetTask($"LumaShell-{user}");
            dynamic task = existing!;
            if (!(bool)task.Enabled || !Matches(XElement.Parse((string)task.Xml), executable)) return false;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                if ((int)task.State == 3) // TASK_STATE_READY
                {
                    started = task.Run(null);
                    return started is not null;
                }
                if (attempt < 5) Thread.Sleep(250);
            }
            return false;
        }
        catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or System.Security.SecurityException or
            Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Xml.XmlException)
        { return false; }
        finally
        {
            Release(started);
            Release(existing);
            Release(root);
            Release(service);
        }
    }

    private static object? Connect(out object? service)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        service = type is null ? null : Activator.CreateInstance(type);
        if (service is null) return null;
        dynamic connection = service;
        connection.Connect();
        return connection.GetFolder(@"\");
    }

    private static bool Matches(XElement task, string? executable)
    {
        var command = task.Element(Scheduler + "Actions")?.Element(Scheduler + "Exec");
        return string.Equals((string?)command?.Element(Scheduler + "Command"), executable, StringComparison.OrdinalIgnoreCase) &&
            (string?)command?.Element(Scheduler + "Arguments") == Argument;
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
