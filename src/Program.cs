using System.Reflection;

namespace ClearStart;

/// <summary>
/// Where ClearStart begins. A live run hides the Windows taskbar here, first of all, before the settings, WPF or
/// the shell library are loaded: at sign-in every moment counts. For the same reason it starts WPF, and warms up
/// what the bars will need (<see cref="StartupWarmUp"/>), before it waits for Explorer rather than after.
/// Everything else is up to <see cref="App"/>.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        int command = Array.FindIndex(args, a => a.Equals("--start-with-windows", StringComparison.OrdinalIgnoreCase));
        if (command >= 0) return AutoStart.RunCommand(command + 1 < args.Length ? args[command + 1] : null);
        int early = Array.FindIndex(args, a => a.Equals("--early-startup", StringComparison.OrdinalIgnoreCase));
        if (early >= 0) return EarlyStartup.RunCommand(early + 1 < args.Length ? args[early + 1] : null);

        try
        {
            bool live = App.IsLiveRun(args);
            if (live && !LiveStart.Begin()) return 0;
            long step = StartTiming.BeginStep();
            var app = new App();
            app.InitializeComponent();
            if (live)
            {
                StartTiming.EndStep("WPF start-up", step);
                if (!LiveStart.WaitForExplorer()) return 0;
            }
            return app.Run();
        }
        catch (Exception ex) when (LiveStart.Mutex != null)
        {
            // The Windows taskbar is hidden by now; an error that ends the program must not leave it that way. Its edges
            // go back once this process has gone, with ClearStart's tray if it was up (see TaskbarEdge.PutBackAfterExit).
            Log.Error("ClearStart stopped with an error", ex);
            ExplorerTaskbar.Restore(keepEdge: true);
            TaskbarEdge.PutBackAfterExit();
            return 1;
        }
    }
}

/// <summary>The first moments of a live run, before anything else is loaded.</summary>
internal static class LiveStart
{
    // Explorer can take a while at sign-in on a slow or busy machine. After this long, go ahead without it.
    private static readonly TimeSpan ExplorerWait = TimeSpan.FromMinutes(2);
    // How long Explorer is given to start its own parts before ClearStart's shell services start beside it. One that was
    // given two seconds or less has lost its window snapping.
    private static readonly TimeSpan ExplorerAge = TimeSpan.FromSeconds(3);

    /// <summary>The single-copy lock, held from here until ClearStart closes. Null until <see cref="Begin"/> has it.</summary>
    public static Mutex? Mutex { get; private set; }

    /// <summary>Set by "ClearStart.exe --exit" and "--restore" to close this copy.</summary>
    public static EventWaitHandle? ExitEvent { get; private set; }

    /// <summary>
    /// Takes the single-copy lock, hides the Windows taskbar and keeps it hidden from now on, and starts the warm-up.
    /// False when ClearStart should go no further: another copy is running, or the early-startup loader has already
    /// withdrawn this launch.
    /// </summary>
    public static bool Begin()
    {
        var mutex = new Mutex(true, App.MutexName, out bool first);
        if (!first)
        {
            // Already running; one copy is enough.
            mutex.Dispose();
            return false;
        }
        Mutex = mutex;
        ExitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, App.ExitEventName);

        Log.Init();
        EarlySession.Connect();
        Log.Info($"ClearStart {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} starting (Live), {StartTiming.Describe()}");
        // Before the guard, which may find the taskbar on another edge and restart Explorer to move it: that restart now
        // waits until ClearStart has started, rather than leave a new Explorer coming up beside ClearStart's start-up.
        TaskbarEdge.StartingUp();
        TaskbarGuard.Start();
        if (EarlySession.Released) return GiveUp();

        StartupWarmUp.Start();
        return true;
    }

    /// <summary>
    /// Started at sign-in, ClearStart can be up before Explorer. Its bars dock through Explorer's taskbar, and its tray
    /// would make Explorer think another taskbar is already running, so it waits for Explorer's taskbar to exist
    /// (the guard hides it as it appears). False if it was asked to close in the meantime; what was touched is then
    /// put back.
    /// An Explorer in its first moments works only partly if ClearStart's shell services start beside it: windows no
    /// longer snap with the Win+arrow keys. At sign-in ClearStart is often ready first, so it also gives Explorer
    /// <see cref="ExplorerAge"/>.
    /// </summary>
    public static bool WaitForExplorer()
    {
        if (EarlySession.Released) return GiveUp();
        if (!ExplorerTaskbar.IsUp())
        {
            Log.Info("Waiting for Explorer to put up the Windows taskbar");
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!ExplorerTaskbar.IsUp())
            {
                if (EarlySession.Released) return GiveUp();
                if (AskedToClose(250))
                {
                    Log.Info("Asked to close while waiting for Explorer");
                    return GiveUp();
                }
                if (waited.Elapsed > ExplorerWait)
                {
                    Log.Warn($"Explorer's taskbar did not appear in {ExplorerWait.TotalMinutes:0} minutes; starting without it");
                    return true;
                }
            }
            Log.Info($"Explorer's taskbar is up, {StartTiming.Describe()}");
        }

        if (ExplorerTaskbar.Age(ExplorerTaskbar.FindPrimary()) is { } age && age < ExplorerAge)
        {
            if (AskedToClose((int)Math.Ceiling((ExplorerAge - age).TotalMilliseconds)))
            {
                Log.Info("Asked to close while waiting for Explorer");
                return GiveUp();
            }
            Log.Info($"Explorer had only just started; gave it {ExplorerAge.TotalSeconds:0} s, {StartTiming.Describe()}");
        }
        return EarlySession.Released ? GiveUp() : true;
    }

    /// <summary>
    /// Waits for the exit signal, and meanwhile answers the messages other programs send to this thread's windows: a new
    /// Explorer that sends to every window would otherwise wait on ClearStart's. Nothing else is handled. WPF has started
    /// on this thread by now, and the usual wait (WaitHandle.WaitOne) would also run its queued work, which could start
    /// the app before Explorer is ready.
    /// </summary>
    private static bool AskedToClose(int milliseconds)
    {
        var handles = new[] { ExitEvent!.SafeWaitHandle.DangerousGetHandle() };
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            long left = milliseconds - waited.ElapsedMilliseconds;
            if (left <= 0) return false;
            uint result = Native.MsgWaitForMultipleObjectsEx(1, handles, (uint)left, Native.QS_SENDMESSAGE, 0);
            if (result == Native.WAIT_OBJECT_0) return true;
            if (result == Native.WAIT_TIMEOUT) return false;
            if (result != Native.WAIT_OBJECT_0 + 1)
            {
                // Should the wait itself fail, keep to the same pace rather than spin.
                Thread.Sleep((int)left);
                return false;
            }
            Native.PeekMessage(out _, IntPtr.Zero, 0, 0, Native.PM_NOREMOVE | Native.PM_QS_SENDMESSAGE);
        }
    }

    /// <summary>Asked to close before there was anything to take over: puts back what was touched, and stops here.</summary>
    private static bool GiveUp()
    {
        ExplorerTaskbar.Restore();
        ExitEvent?.Dispose();
        ExitEvent = null;
        Mutex?.ReleaseMutex();
        Mutex?.Dispose();
        Mutex = null;
        return false;
    }
}
