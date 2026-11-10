using System;
using System.Runtime.InteropServices;

namespace LumaShell;

internal static class Audio
{
    internal static (double Level, bool Muted)? Status() => WithDevice<(double, bool)>(endpoint =>
    {
        Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out float level));
        Marshal.ThrowExceptionForHR(endpoint.GetMute(out bool muted));
        return (level, muted);
    });

    internal static void SetLevel(double level) => WithDevice(endpoint => endpoint.SetMasterVolumeLevelScalar((float)Math.Clamp(level, 0, 1), 0));

    internal static void ToggleMute() => WithDevice(endpoint =>
    {
        Marshal.ThrowExceptionForHR(endpoint.GetMute(out bool muted));
        return endpoint.SetMute(!muted, 0);
    });

    private static T? WithDevice<T>(Func<IEndpointVolume, T> operation) where T : struct
    {
        object? enumerator = null, device = null, volume = null;
        try
        {
            enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!);
            Marshal.ThrowExceptionForHR(((IDeviceEnumerator)enumerator!).GetDefaultAudioEndpoint(0, 1, out device));
            Guid id = typeof(IEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(((IDevice)device).Activate(ref id, 23, 0, out volume));
            return operation((IEndpointVolume)volume);
        }
        catch (Exception error) when (error is COMException or InvalidCastException or UnauthorizedAccessException) { return null; }
        finally
        {
            if (volume is not null) Marshal.ReleaseComObject(volume);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, [MarshalAs(UnmanagedType.IUnknown)] out object device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        [PreserveSig] int Activate(ref Guid id, uint context, nint activation, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint callback);
        [PreserveSig] int UnregisterControlChangeNotify(nint callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, nint context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, nint context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, nint context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, nint context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, nint context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}
