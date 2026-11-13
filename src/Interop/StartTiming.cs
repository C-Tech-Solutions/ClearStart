using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ClearStart;

/// <summary>
/// How long ClearStart has been starting, and how long since the user signed in, for the log. These are the
/// numbers to look at when the Windows taskbar shows at sign-in, or the bars are slow to appear.
/// </summary>
internal static class StartTiming
{
    private static readonly DateTime ProcessStarted = ReadProcessStart();
    private static readonly DateTime? SignedIn = ReadSignIn();

    public static long BeginStep() => Stopwatch.GetTimestamp();

    public static void EndStep(string step, long started) =>
        Log.Info($"Startup step: {step}, {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");

    /// <summary>For example "0.4 s after ClearStart started, 6.1 s after sign-in".</summary>
    public static string Describe()
    {
        var now = DateTime.UtcNow;
        string text = Format(now - ProcessStarted) + " after ClearStart started";
        if (SignedIn is { } signedIn) text += ", " + Format(now - signedIn) + " after sign-in";
        return text;
    }

    private static string Format(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalSeconds < 120) return span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        if (span.TotalMinutes < 120) return span.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + " min";
        return span.TotalHours.ToString("0.0", CultureInfo.InvariantCulture) + " h";
    }

    private static DateTime ReadProcessStart()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    /// <summary>When the user signed in to this Windows session, or null if Windows does not say.</summary>
    private static DateTime? ReadSignIn()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, WTS_CURRENT_SESSION, WTSSessionInfo, out buffer, out int bytes)
                || buffer == IntPtr.Zero || bytes < Marshal.SizeOf<WTSINFO>())
            {
                return null;
            }
            long logon = Marshal.PtrToStructure<WTSINFO>(buffer).LogonTime;
            return logon > 0 ? DateTime.FromFileTimeUtc(logon) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    private const int WTS_CURRENT_SESSION = -1;
    private const int WTSSessionInfo = 24;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTSINFO
    {
        public int State;
        public uint SessionId;
        public uint IncomingBytes;
        public uint OutgoingBytes;
        public uint IncomingFrames;
        public uint OutgoingFrames;
        public uint IncomingCompressedBytes;
        public uint OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long LogonTime;
        public long CurrentTime;
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass,
        out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
