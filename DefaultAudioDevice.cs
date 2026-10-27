using System.Runtime.InteropServices;

namespace MultiSpeaker;

/// <summary>
/// Sets the Windows default output device, like picking one in the taskbar's sound menu.
/// Windows has no public API for this; IPolicyConfig is the undocumented interface the
/// Sound control panel itself uses, and it has been stable since Windows 7.
/// </summary>
static class DefaultAudioDevice
{
    const int RoleConsole = 0;     // games, system sounds
    const int RoleMultimedia = 1;  // music, video
    // The communications role (calls) is left alone so a running call doesn't jump devices.

    public static void Set(string deviceId)
    {
        var config = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            Marshal.ThrowExceptionForHR(config.SetDefaultEndpoint(deviceId, RoleConsole));
            Marshal.ThrowExceptionForHR(config.SetDefaultEndpoint(deviceId, RoleMultimedia));
        }
        finally
        {
            Marshal.ReleaseComObject(config);
        }
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    /// <summary>Only SetDefaultEndpoint is called; the earlier methods just hold their vtable slots.</summary>
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, IntPtr defaultPeriodOut, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
