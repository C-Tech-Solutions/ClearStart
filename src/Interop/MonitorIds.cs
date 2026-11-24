using System.Runtime.InteropServices;

namespace ClearStart;

/// <summary>
/// Tells the monitors apart by the monitors themselves. Windows names each display "\\.\DISPLAY1", "\\.\DISPLAY2",
/// and so on, but it can hand those numbers out in another order when monitors are turned off and on again: the
/// same monitor then comes back under another number, and settings kept under the number land on another monitor.
/// The monitor's own device path names its model and the connector it is on, and stays the same.
/// </summary>
internal static class MonitorIds
{
    private const int AttachedToDesktop = 0x1; // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
    private const int Active = 0x1; // DISPLAY_DEVICE_ACTIVE (on a monitor)
    private const uint GetDeviceInterfaceName = 0x1; // EDD_GET_DEVICE_INTERFACE_NAME

    private static readonly object Gate = new();
    private static Dictionary<string, string>? _byDisplay;
    private static string? _logged;

    /// <summary>
    /// The ID of the monitor on a display, such as "ABC1234#5&amp;1a2b3c4d&amp;0&amp;UID4352" for "\\.\DISPLAY1", or
    /// null when Windows gives none.
    /// </summary>
    public static string? For(string? deviceName)
    {
        string display = TaskbarSettings.MonitorKey(deviceName);
        if (display.Length == 0) return null;
        lock (Gate)
        {
            _byDisplay ??= Read();
            return _byDisplay.TryGetValue(display, out var id) ? id : null;
        }
    }

    /// <summary>Reads which monitor is on which display again. Called whenever the displays may have changed.</summary>
    public static void Refresh()
    {
        var now = Read();
        string seen = now.Count == 0
            ? "none told apart"
            : string.Join(", ", now.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Key} = {p.Value}"));
        bool log;
        lock (Gate)
        {
            _byDisplay = now;
            log = seen != _logged;
            _logged = seen;
        }
        // Written when it changes, so the log shows the monitors keeping their names when Windows renumbers them.
        if (log) Log.Info("Monitors on the displays: " + seen);
    }

    /// <summary>
    /// For the self-test only: the displays have these monitors ("DISPLAY1" to its ID) until it is called again
    /// with null, which reads them from Windows again.
    /// </summary>
    internal static void UseForTest(Dictionary<string, string>? byDisplay)
    {
        lock (Gate) _byDisplay = byDisplay == null ? null : new(byDisplay, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "\\?\DISPLAY#ABC1234#5&amp;1a2b3c4d&amp;0&amp;UID4352#{e6f07b5f-...}" becomes "ABC1234#5&amp;1a2b3c4d&amp;0&amp;UID4352":
    /// the model and the instance, which holds the connector. Null for anything else.
    /// </summary>
    internal static string? FromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var parts = path.Split('#');
        if (parts.Length != 4 || !parts[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase)) return null;
        if (parts[1].Length == 0 || parts[2].Length == 0) return null;
        return parts[1] + "#" + parts[2];
    }

    private static Dictionary<string, string> Read()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (uint i = 0; ; i++)
            {
                var adapter = NewDevice();
                if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;
                if ((adapter.StateFlags & AttachedToDesktop) == 0) continue;
                for (uint j = 0; ; j++)
                {
                    var monitor = NewDevice();
                    if (!EnumDisplayDevices(adapter.DeviceName, j, ref monitor, GetDeviceInterfaceName)) break;
                    if ((monitor.StateFlags & Active) == 0) continue;
                    if (FromPath(monitor.DeviceID) is { } id) found[TaskbarSettings.MonitorKey(adapter.DeviceName)] = id;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not tell the monitors apart: " + ex.Message);
        }
        return WithoutTwins(found);
    }

    /// <summary>
    /// Two displays that name the same monitor cannot be told apart that way: both are left out, and keep their
    /// numbers.
    /// </summary>
    internal static Dictionary<string, string> WithoutTwins(Dictionary<string, string> byDisplay)
    {
        var twins = byDisplay.GroupBy(p => p.Value, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).SelectMany(g => g).Select(p => p.Key).ToList();
        foreach (string display in twins) byDisplay.Remove(display);
        return byDisplay;
    }

    private static DISPLAY_DEVICE NewDevice() => new() { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE info, uint flags);
}
