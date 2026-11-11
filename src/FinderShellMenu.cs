using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace LumaShell;

internal static class FinderShellMenu
{
    private const uint FirstShellCommand = 1;
    private const uint LastShellCommand = 0x6FFF;
    private const uint CopyPathCommand = 0x7000;
    private const uint OpenFolderCommand = 0x7001;
    private static readonly Guid UiObject = new("3981E225-F559-11D3-8E3A-00C04F6837D5");

    internal enum Result { Unavailable, Cancelled, Invoked, Failed, CopyPath, OpenFolder }

    internal static Result Show(nint owner, Point location, string[] paths, bool canOpenFolder)
    {
        var source = HwndSource.FromHwnd(owner);
        if (source is null) return Result.Unavailable;
        var modifiers = Keyboard.Modifiers;
        using var prepared = Prepare(paths, modifiers.HasFlag(ModifierKeys.Shift));
        if (prepared is null) return Result.Unavailable;

        if (canOpenFolder) InsertMenu(prepared.Handle, 0, 0x400, OpenFolderCommand, "Открыть в Luma Finder");
        AppendMenu(prepared.Handle, 0x800, 0, null);
        AppendMenu(prepared.Handle, 0, CopyPathCommand, "Копировать путь");
        uint command;
        var menu3 = prepared.Context as IContextMenu3;
        var menu2 = menu3 is null ? prepared.Context as IContextMenu2 : null;
        source.AddHook(ForwardMenuMessage);
        try
        {
            command = TrackPopupMenuEx(prepared.Handle, 0x102, (int)location.X, (int)location.Y, owner, 0);
        }
        finally { source.RemoveHook(ForwardMenuMessage); }

        if (command == 0) return Result.Cancelled;
        if (command == CopyPathCommand) return Result.CopyPath;
        if (command == OpenFolderCommand) return Result.OpenFolder;
        if (command < FirstShellCommand || command > LastShellCommand) return Result.Failed;
        return Invoke(prepared.Context, owner, command - FirstShellCommand, location, paths[0], modifiers)
            ? Result.Invoked : Result.Failed;

        nint ForwardMenuMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
        {
            // Shell extensions populate and draw their own submenus (for example Open with).
            if (message is not (0x0117 or 0x002B or 0x002C or 0x0120)) return 0;
            if ((message is 0x002B or 0x002C) && wParam != 0) return 0;
            try
            {
                if (menu3 is not null && menu3.HandleMenuMsg2((uint)message, wParam, lParam, out nint result) == 0)
                {
                    handled = true;
                    return result;
                }
                if (message != 0x0120 && menu2 is not null && menu2.HandleMenuMsg((uint)message, wParam, lParam) == 0)
                {
                    handled = true;
                    return message is 0x002B or 0x002C ? 1 : 0;
                }
            }
            catch (COMException) { }
            return 0;
        }
    }

    // Used by the Windows smoke check to inspect the same menu the UI opens.
    internal static string[] ReadVerbs(string[] paths)
    {
        using var prepared = Prepare(paths, extended: false);
        if (prepared is null) return [];
        var verbs = new System.Collections.Generic.List<string>();
        var buffer = new StringBuilder(256);
        for (uint id = 0; id < prepared.CommandCount; id++)
        {
            buffer.Clear();
            if (prepared.Context.GetCommandString(id, 4, 0, buffer, (uint)buffer.Capacity) >= 0 && buffer.Length > 0)
                verbs.Add(buffer.ToString());
        }
        return verbs.ToArray();
    }

    private static Prepared? Prepare(string[] paths, bool extended)
    {
        if (paths.Length == 0) return null;
        var ids = new nint[paths.Length];
        IShellItemArray? array = null;
        IContextMenu? context = null;
        nint menu = 0;
        bool keep = false;
        try
        {
            for (int i = 0; i < paths.Length; i++)
                if (SHParseDisplayName(paths[i], 0, out ids[i], 0, out _) < 0 || ids[i] == 0) return null;
            if (SHCreateShellItemArrayFromIDLists((uint)ids.Length, ids, out array) < 0 || array is null) return null;
            var iid = typeof(IContextMenu).GUID;
            if (array.BindToHandler(0, in UiObject, in iid, out context) < 0 || context is null) return null;
            menu = CreatePopupMenu();
            if (menu == 0) return null;
            int result = context.QueryContextMenu(menu, 0, FirstShellCommand, LastShellCommand, extended ? 0x100u : 0u);
            if (result < 0 || GetMenuItemCount(menu) <= 0) return null;
            keep = true;
            return new Prepared(menu, context, array, ids, (uint)result & 0xFFFF);
        }
        catch (Exception error) when (error is COMException or InvalidCastException or ArgumentException)
        { return null; }
        finally { if (!keep) Release(menu, context, array, ids); }
    }

    private static bool Invoke(IContextMenu menu, nint owner, uint offset, Point location, string path, ModifierKeys modifiers)
    {
        string? folder = Path.GetDirectoryName(path);
        nint directory = Marshal.StringToCoTaskMemAnsi(folder);
        nint directoryW = Marshal.StringToCoTaskMemUni(folder);
        try
        {
            var command = new InvokeInfo
            {
                Size = (uint)Marshal.SizeOf<InvokeInfo>(), Mask = 0x20004000, Window = owner, Show = 1,
                Verb = (nint)offset, VerbW = (nint)offset, Directory = directory, DirectoryW = directoryW,
                X = (int)location.X, Y = (int)location.Y
            };
            if (modifiers.HasFlag(ModifierKeys.Shift)) command.Mask |= 0x10000000;
            if (modifiers.HasFlag(ModifierKeys.Control)) command.Mask |= 0x40000000;
            int result = menu.InvokeCommand(ref command);
            return result >= 0 || result == unchecked((int)0x800704C7); // User cancelled a Windows dialog.
        }
        catch (COMException) { return false; }
        finally { Marshal.FreeCoTaskMem(directory); Marshal.FreeCoTaskMem(directoryW); }
    }

    private sealed class Prepared(nint menu, IContextMenu context, IShellItemArray array, nint[] ids, uint count) : IDisposable
    {
        internal nint Handle => menu;
        internal IContextMenu Context => context;
        internal uint CommandCount => count;
        public void Dispose() => Release(menu, context, array, ids);
    }

    private static void Release(nint menu, IContextMenu? context, IShellItemArray? array, nint[] ids)
    {
        if (menu != 0) DestroyMenu(menu);
        if (context is not null) Marshal.ReleaseComObject(context);
        if (array is not null) Marshal.ReleaseComObject(array);
        foreach (nint id in ids) if (id != 0) Marshal.FreeCoTaskMem(id);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InvokeInfo
    {
        public uint Size, Mask;
        public nint Window, Verb, Parameters, Directory;
        public int Show;
        public uint Hotkey;
        public nint Icon, Title, VerbW, ParametersW, DirectoryW, TitleW;
        public int X, Y;
    }

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(nint bindContext, in Guid handler, in Guid iid, out IContextMenu? context);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo command);
        [PreserveSig] int GetCommandString(nuint id, uint flags, nint reserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, uint capacity);
    }

    // Inherited COM slots are repeated explicitly so the message handlers keep their native indices.
    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo command);
        [PreserveSig] int GetCommandString(nuint id, uint flags, nint reserved, nint text, uint capacity);
        [PreserveSig] int HandleMenuMsg(uint message, nint wParam, nint lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo command);
        [PreserveSig] int GetCommandString(nuint id, uint flags, nint reserved, nint text, uint capacity);
        [PreserveSig] int HandleMenuMsg(uint message, nint wParam, nint lParam);
        [PreserveSig] int HandleMenuMsg2(uint message, nint wParam, nint lParam, out nint result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string path, nint context, out nint id, uint attributes, out uint outputAttributes);
    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint count, [In] nint[] ids, out IShellItemArray? array);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(nint menu);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? label);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool InsertMenu(nint menu, uint index, uint flags, nuint id, string label);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);
}
