using System.Runtime.InteropServices;
using Windows.Media.Audio;
using Windows.Media.Devices;

namespace ClearStart;

/// <summary>One place sound can come out of: speakers, headphones, a monitor's audio.</summary>
internal sealed record AudioOutput(string Id, string Name, bool IsDefault);

/// <summary>One spatial sound format the current output can use. An empty id means "off".</summary>
internal sealed record SpatialFormat(string Id, string Name, bool IsActive);

/// <summary>What the "Spatial sound" menu needs to know about the current output.</summary>
internal sealed record SpatialSound(bool Supported, string ActiveName, IReadOnlyList<SpatialFormat> Formats);

/// <summary>
/// The sound outputs of this PC: which exist, which one is in use, switching to another, and the
/// spatial sound format of the one in use. Every call asks Windows afresh; nothing is cached.
/// </summary>
internal static class Audio
{
    private const int Render = 0;          // EDataFlow.eRender
    private const int ActiveDevices = 1;   // DEVICE_STATE_ACTIVE
    private const int RoleConsole = 0;     // system sounds and most apps
    private const int RoleMultimedia = 1;  // music, video, games
    private const int ReadOnly = 0;        // STGM_READ
    private const ushort VT_LPWSTR = 31;

    // ------------------------------------------------------------------ outputs

    /// <summary>The outputs that are plugged in and enabled, in the order Windows lists them.</summary>
    public static IReadOnlyList<AudioOutput> Outputs()
    {
        var result = new List<AudioOutput>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();

            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(Render, RoleMultimedia, out var current) == 0 && current != null)
            {
                try
                {
                    current.GetId(out defaultId);
                }
                finally
                {
                    Marshal.ReleaseComObject(current);
                }
            }

            if (enumerator.EnumAudioEndpoints(Render, ActiveDevices, out devices) != 0 || devices == null) return result;
            devices.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (devices.Item(i, out var device) != 0 || device == null) continue;
                try
                {
                    if (device.GetId(out string id) != 0 || string.IsNullOrEmpty(id)) continue;
                    result.Add(new AudioOutput(id, NameOf(device) ?? "Sound output " + (i + 1),
                        string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not list the sound outputs: " + ex.Message);
        }
        finally
        {
            if (devices != null) Marshal.ReleaseComObject(devices);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
        return result;
    }

    /// <summary>
    /// Makes an output the one Windows plays through. The device used for calls ("communications") is a
    /// separate choice in Windows and is left alone.
    /// </summary>
    public static bool SetDefaultOutput(string id)
    {
        IPolicyConfig? policy = null;
        try
        {
            // Windows has no documented call for this; this is the interface its own sound settings use,
            // unchanged since Windows 7 and relied on by every audio switcher.
            policy = (IPolicyConfig)new PolicyConfigClient();
            int console = policy.SetDefaultEndpoint(id, RoleConsole);
            int multimedia = policy.SetDefaultEndpoint(id, RoleMultimedia);
            if (console == 0 && multimedia == 0) return true;
            Log.Warn($"Windows refused to switch the sound output (0x{console:X8}, 0x{multimedia:X8})");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not switch the sound output: " + ex.Message);
            return false;
        }
        finally
        {
            if (policy != null) Marshal.ReleaseComObject(policy);
        }
    }

    private static string? NameOf(IMMDevice device)
    {
        if (device.OpenPropertyStore(ReadOnly, out var store) != 0 || store == null) return null;
        try
        {
            // PKEY_Device_FriendlyName, e.g. "Speakers (Realtek Audio)".
            var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
            if (store.GetValue(ref key, out var value) != 0) return null;
            try
            {
                return value.vt == VT_LPWSTR && value.pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.pointer) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    // ------------------------------------------------------------------ spatial sound

    /// <summary>The spatial sound state of the output in use, or null if Windows would not say.</summary>
    public static SpatialSound? Spatial()
    {
        try
        {
            var device = SpatialAudioDeviceConfiguration.GetForDeviceId(
                MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default));
            if (!device.IsSpatialAudioSupported) return new SpatialSound(false, "Off", Array.Empty<SpatialFormat>());

            string active = device.ActiveSpatialAudioFormat ?? "";
            var known = KnownFormats();

            // Windows Sonic comes with Windows. The others need their own app and licence, and Windows does not
            // say whether that is installed, so one of those is listed only while it is the one in use.
            var formats = new List<SpatialFormat> { new("", "Off", active.Length == 0) };
            string sonic = SpatialAudioFormatSubtype.WindowsSonic;
            if (device.IsSpatialAudioFormatSupported(sonic))
            {
                formats.Add(new SpatialFormat(sonic, known[sonic], Same(active, sonic)));
            }
            if (active.Length > 0 && !Same(active, sonic))
            {
                formats.Add(new SpatialFormat(active, known.GetValueOrDefault(active, "Another format"), true));
            }

            return new SpatialSound(true, formats.First(f => f.IsActive).Name, formats);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the spatial sound setting: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Asks Windows to use a spatial sound format on the output in use (an empty id turns it off).
    /// False means Windows did not do it; the caller then opens the Settings page instead.
    /// </summary>
    public static async Task<bool> SetSpatialAsync(string formatId)
    {
        try
        {
            var device = SpatialAudioDeviceConfiguration.GetForDeviceId(
                MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default));
            var result = await device.SetDefaultSpatialAudioFormatAsync(formatId);
            if (result.Status == SetDefaultSpatialAudioFormatStatus.Succeeded) return true;
            Log.Info("Windows did not change the spatial sound format: " + result.Status);
            return false;
        }
        catch (Exception ex)
        {
            Log.Info("Windows did not change the spatial sound format: " + ex.Message);
            return false;
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> KnownFormats() => new(StringComparer.OrdinalIgnoreCase)
    {
        [SpatialAudioFormatSubtype.WindowsSonic] = "Windows Sonic for Headphones",
        [SpatialAudioFormatSubtype.DolbyAtmosForHeadphones] = "Dolby Atmos for Headphones",
        [SpatialAudioFormatSubtype.DolbyAtmosForHomeTheater] = "Dolby Atmos for home theater",
        [SpatialAudioFormatSubtype.DolbyAtmosForSpeakers] = "Dolby Atmos for built-in speakers",
        [SpatialAudioFormatSubtype.DTSHeadphoneX] = "DTS Headphone:X",
        [SpatialAudioFormatSubtype.DTSXUltra] = "DTS:X Ultra",
    };

    // ------------------------------------------------------------------ Core Audio declarations

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    /// <summary>Only the string case is read; the two pointer-sized fields give the struct its real size.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr pointer;
        public IntPtr more;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT value);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection? devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);

        // The rest of the interface is not used.
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, int context, IntPtr activationParams, out IntPtr instance);

        [PreserveSig]
        int OpenPropertyStore(int access, out IPropertyStore? properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PROPERTYKEY key);

        [PreserveSig]
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);

        // SetValue and Commit are not used.
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient
    {
    }

    /// <summary>Only SetDefaultEndpoint is called; the entries before it are there to keep its position.</summary>
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    }
}
