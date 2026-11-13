using System.Reflection;

namespace ClearStart;

/// <summary>
/// Where ClearStart begins. A live run hides the Windows taskbar here, first of all, before the settings, WPF or
/// the shell library are loaded: at sign-in every moment counts. Everything else is up to <see cref="App"/>.
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
            if (App.IsLiveRun(args) && !LiveStart.Begin()) return 0;
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
        catch (Exception ex) when (LiveStart.Mutex != null)
        {
            // The Windows taskbar is hidden by now; an error that ends the program must not leave it that way.
            Log.Error("ClearStart stopped with an error", ex);
            ExplorerTaskbar.Restore();
            return 1;
        }
    }
}

/// <summary>The first moments of a live run, before anything else is loaded.</summary>
internal static class LiveStart
{
    // Explorer can take a while at sign-in on a slow or busy machine. After this long, go ahead without it.
    private static readonly TimeSpan ExplorerWait = TimeSpan.FromMinutes(2);

    /// <summary>The single-copy lock, held from here until ClearStart closes. Null until <see cref="Begin"/> has it.</summary>
    public static Mutex? Mutex { get; private set; }

    /// <summary>Set by "ClearStart.exe --exit" and "--restore" to close this copy.</summary>
    public static EventWaitHandle? ExitEvent { get; private set; }

    /// <summary>
    /// Takes the single-copy lock, hides the Windows taskbar and keeps it hidden from now on, and waits for Explorer
    /// if it is not up yet. False when ClearStart should go no further: another copy is running, or it was asked to
    /// close while it waited.
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
        TaskbarGuard.Start();

        if (WaitForExplorer()) return true;

        // Asked to close before there was anything to take over: put back what was touched, and stop here.
        ExplorerTaskbar.Restore();
        ExitEvent.Dispose();
        mutex.ReleaseMutex();
        mutex.Dispose();
        return false;
    }

    /// <summary>
    /// Started at sign-in, ClearStart can be up before Explorer. Its bars dock through Explorer's taskbar, and its tray
    /// would make Explorer think another taskbar is already running, so it waits for Explorer's taskbar to exist
    /// (the guard hides it as it appears). False if it was asked to close in the meantime.
    /// </summary>
    private static bool WaitForExplorer()
    {
        if (EarlySession.Released) return false;
        if (ExplorerTaskbar.IsUp()) return !EarlySession.Released;

        Log.Info("Waiting for Explorer to put up the Windows taskbar");
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!ExplorerTaskbar.IsUp())
        {
            if (EarlySession.Released) return false;
            if (ExitEvent!.WaitOne(250))
            {
                Log.Info("Asked to close while waiting for Explorer");
                return false;
            }
            if (waited.Elapsed > ExplorerWait)
            {
                Log.Warn($"Explorer's taskbar did not appear in {ExplorerWait.TotalMinutes:0} minutes; starting without it");
                return true;
            }
        }
        Log.Info($"Explorer's taskbar is up, {StartTiming.Describe()}");
        return !EarlySession.Released;
    }
}
