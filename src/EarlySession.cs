using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ClearStart;

/// <summary>The optional native loader's per-launch handshake. Normal launches never create these objects.</summary>
internal static class EarlySession
{
    private static MemoryMappedFile? _mapping;
    private static MemoryMappedViewAccessor? _state;
    private static EventWaitHandle? _ready, _release;
    private static DispatcherTimer? _heartbeat;

    public static bool Active => _state != null;
    public static bool Released => _state != null && _state.ReadInt32(4) == 0;

    public static void Connect()
    {
        string? token = Environment.GetEnvironmentVariable("CLEARSTART_EARLY_TOKEN");
        Environment.SetEnvironmentVariable("CLEARSTART_EARLY_TOKEN", null);
        Environment.SetEnvironmentVariable("CLEARSTART_EARLY_OWNER", null);
        if (token == null) return;
        if (!Guid.TryParseExact(token, "D", out _)) throw new InvalidOperationException("Invalid early-startup handshake");
        string prefix = @"Local\ClearStart.Early." + token;
        _mapping = MemoryMappedFile.OpenExisting(prefix + ".State");
        _state = _mapping.CreateViewAccessor(0, 40);
        if (_state.ReadInt32(0) != 1 || _state.ReadInt32(8) != Environment.ProcessId || Released)
            throw new InvalidOperationException("The early-startup loader has withdrawn this launch");
        _ready = EventWaitHandle.OpenExisting(prefix + ".Ready");
        _release = EventWaitHandle.OpenExisting(prefix + ".Release");
        Log.Info("Connected to the early-startup loader");
    }

    public static void BarsReady()
    {
        if (_state == null || _heartbeat != null) return;
        if (Released) { System.Windows.Application.Current.Shutdown(1); return; }
        Beat();
        _state.Write(24, 1);
        _ready!.Set();
        _heartbeat = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _heartbeat.Tick += (_, _) =>
        {
            if (Released) System.Windows.Application.Current.Shutdown(1);
            else Beat();
        };
        _heartbeat.Start();
    }

    private static void Beat()
    {
        QueryUnbiasedInterruptTime(out ulong time);
        _state!.Write(16, (long)(time / 10000));
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong time);

    public static void RecordTaskbarState(int state)
    {
        if (_state == null) return;
        _state.Write(28, state);
        _state.Write(32, 1);
    }

    // Called before either normal or emergency restoration, including a cancelled sign-out.
    public static void Release()
    {
        if (_state == null) return;
        _state.Write(4, 0);
        _release?.Set();
        // The mapping stays open until process exit; the guard thread reads Released concurrently.
    }
}
