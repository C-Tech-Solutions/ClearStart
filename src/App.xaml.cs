using System.Reflection;
using System.Windows;

namespace ClearStart;

/// <summary>
/// Start-up, shut-down and the safety net.
///
///   ClearStart.exe                 run for real: hide the Windows taskbar and take over
///   ClearStart.exe --preview       show our bars next to the Windows taskbar; hide and take over nothing
///   ClearStart.exe --exit          ask the running copy to close (which brings the Windows taskbar back)
///   ClearStart.exe --restore       same, and then force the Windows taskbar back even if nothing was running
///   ClearStart.exe --selftest DIR  draw the UI into image files in DIR without showing anything
///   ClearStart.exe --screenshots DIR  draw pictures for the documentation, with stand-in apps and default settings
///   ClearStart.exe --start-with-windows on|off  switch "Start with Windows" for this copy (used by Setup)
///
/// A live run begins in <see cref="Program"/>, which hides the Windows taskbar before any of this loads.
/// </summary>
public partial class App : Application
{
    internal const string MutexName = @"Local\ClearStart.SingleInstance";
    internal const string ExitEventName = @"Local\ClearStart.Exit";
    private static readonly string[] OtherModes = { "--preview", "--restore", "--exit", "--selftest", "--screenshots" };

    private readonly Queue<DateTime> _recentErrors = new();
    private Mutex? _mutex;
    private EventWaitHandle? _exitEvent;
    private RegisteredWaitHandle? _exitWait;
    private ShellHost? _host;
    private bool _live;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            string[] args = e.Args;

            if (Has(args, "--restore"))
            {
                Log.Init();
                // A copy that is still running puts the taskbar's edges back as it closes. Explorer must not be
                // restarted under it meanwhile (see TaskbarEdge), so then only the taskbar is shown here.
                bool closed = StopRunningInstance();
                ExplorerTaskbar.Restore(keepEdge: !closed);
                Shutdown(0);
            }
            else if (Has(args, "--exit"))
            {
                Log.Init();
                StopRunningInstance();
                Shutdown(0);
            }
            else if (Array.FindIndex(args, a => IsFlag(a, "--selftest")) is var index and >= 0)
            {
                RunSelfTest(index + 1 < args.Length ? args[index + 1] : "selftest", SelfTest.RunAsync);
            }
            else if (Array.FindIndex(args, a => IsFlag(a, "--screenshots")) is var shots and >= 0)
            {
                RunSelfTest(shots + 1 < args.Length ? args[shots + 1] : "screenshots", SelfTest.ScreenshotsAsync);
            }
            else
            {
                RunShell(Has(args, "--preview") ? HostMode.Preview : HostMode.Live);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            DisposeHost();
            if (_live) ExplorerTaskbar.Restore();
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeHost();

        try
        {
            _exitWait?.Unregister(null);
            _exitEvent?.Dispose();
        }
        catch
        {
            // Nothing useful to do this late.
        }

        try
        {
            if (_mutex != null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
        }
        catch
        {
            // Released by the system when the process ends.
        }

        base.OnExit(e);
    }

    private static bool IsFlag(string argument, string flag) =>
        argument.Equals(flag, StringComparison.OrdinalIgnoreCase);

    private static bool Has(string[] args, string flag) => args.Any(a => IsFlag(a, flag));

    /// <summary>True when these arguments start ClearStart for real, in place of the Windows taskbar.</summary>
    internal static bool IsLiveRun(string[] args) => !OtherModes.Any(mode => Has(args, mode));

    // ------------------------------------------------------------------ normal run

    private void RunShell(HostMode mode)
    {
        _live = mode == HostMode.Live;
        if (_live && LiveStart.Mutex != null && LiveStart.ExitEvent != null)
        {
            // Program.Main got this far already: it holds the single-copy lock and has hidden the Windows taskbar.
            _mutex = LiveStart.Mutex;
            _exitEvent = LiveStart.ExitEvent;
        }
        else
        {
            var mutex = new Mutex(true, MutexName, out bool first);
            if (!first)
            {
                // Already running; one copy is enough.
                mutex.Dispose();
                Shutdown(0);
                return;
            }
            _mutex = mutex;

            Log.Init();
            Log.Info($"ClearStart {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} starting ({mode})");
            _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        }
        WatchForErrors();

        // "ClearStart.exe --exit" and "--restore" set this event to close us cleanly.
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent,
            (_, _) => Dispatcher.InvokeAsync(() => Shutdown(0)), null, Timeout.Infinite, true);

        // Signing out or shutting down: give the Windows taskbar back before the session goes away. (Windows
        // goes on to close ClearStart either way.)
        SessionEnding += (_, _) =>
        {
            Log.Info("The Windows session is ending");
            DisposeHost(sessionEnding: true);
        };

        long step = StartTiming.BeginStep();
        Settings.Load();
        Settings.Watch();
        StartTiming.EndStep("settings", step);

        step = StartTiming.BeginStep();
        _host = new ShellHost(mode);
        StartTiming.EndStep("shell host construction", step);
        _host.Start();
    }

    private void DisposeHost(bool sessionEnding = false)
    {
        var host = _host;
        _host = null;
        if (host == null) return;
        try
        {
            host.Close(sessionEnding);
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown did not complete cleanly", ex);
        }
    }

    /// <summary>
    /// Asks a running copy to close and waits until it has. Closing can take a while when it puts the Windows taskbar
    /// back on another edge: Explorer is restarted for that, after it has run long enough (see TaskbarEdge).
    /// </summary>
    /// <returns>False if a copy is still running, or may be.</returns>
    private static bool StopRunningInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ExitEventName, out var signal)) return true;
            using (signal)
            {
                signal.Set();
            }

            using var mutex = new Mutex(false, MutexName);
            try
            {
                if (mutex.WaitOne(TimeSpan.FromSeconds(45)))
                {
                    mutex.ReleaseMutex();
                    return true;
                }
                Log.Warn("The running copy did not close in time");
                return false;
            }
            catch (AbandonedMutexException)
            {
                mutex.ReleaseMutex();
                return true;
            }
        }
        catch (Exception ex)
        {
            // A copy may still be running (one started as administrator can refuse the signal), so Explorer must not be
            // restarted under it.
            Log.Warn("Could not signal the running copy: " + ex.Message);
            return false;
        }
    }

    // ------------------------------------------------------------------ errors

    /// <summary>
    /// A taskbar must not disappear because of one bad click, so an unexpected error is logged and
    /// the app carries on. If errors keep coming it gives up cleanly, and whatever happens the
    /// Windows taskbar is put back before the process ends.
    /// </summary>
    private void WatchForErrors()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            Log.Error("Unexpected error (carrying on)", args.Exception);
            if (TooManyErrors())
            {
                Log.Error("Errors keep repeating; closing so the Windows taskbar comes back");
                Shutdown(1);
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error("Fatal error", args.ExceptionObject as Exception);
            if (!_live) return;
            // ClearStart's tray is still up: Explorer is not restarted under it (see TaskbarEdge.PutBackAfterExit).
            ExplorerTaskbar.Restore(keepEdge: true);
            TaskbarEdge.PutBackAfterExit();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warn("Background task error: " + args.Exception.GetBaseException().Message);
            args.SetObserved();
        };
    }

    private bool TooManyErrors()
    {
        var now = DateTime.UtcNow;
        _recentErrors.Enqueue(now);
        while (_recentErrors.Count > 0 && (now - _recentErrors.Peek()).TotalSeconds > 10)
        {
            _recentErrors.Dequeue();
        }
        return _recentErrors.Count > 25;
    }

    // ------------------------------------------------------------------ self-test

    /// <summary>Runs the self-test or the screenshot mode: both draw into a folder and show nothing.</summary>
    private void RunSelfTest(string directory, Func<string, Task<int>> run)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        Log.Init(directory);
        WatchForErrors();

        Dispatcher.InvokeAsync(async () =>
        {
            int code = 1;
            try
            {
                code = await run(directory);
            }
            catch (Exception ex)
            {
                Log.Error("Self-test failed", ex);
            }
            Shutdown(code);
        });
    }
}
