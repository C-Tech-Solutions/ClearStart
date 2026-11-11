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
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\ClearStart.SingleInstance";
    private const string ExitEventName = @"Local\ClearStart.Exit";

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
                StopRunningInstance();
                ExplorerTaskbar.Restore();
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

    // ------------------------------------------------------------------ normal run

    private void RunShell(HostMode mode)
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
        _live = mode == HostMode.Live;
        WatchForErrors();

        // "ClearStart.exe --exit" and "--restore" set this event to close us cleanly.
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent,
            (_, _) => Dispatcher.InvokeAsync(() => Shutdown(0)), null, Timeout.Infinite, true);

        // Signing out or shutting down: put the Windows taskbar setting back before the session goes away.
        SessionEnding += (_, _) =>
        {
            Log.Info("The Windows session is ending");
            DisposeHost();
        };

        Settings.Load();
        Settings.Watch();

        _host = new ShellHost(mode);
        _host.Start();
    }

    private void DisposeHost()
    {
        var host = _host;
        _host = null;
        if (host == null) return;
        try
        {
            host.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown did not complete cleanly", ex);
        }
    }

    /// <summary>Asks a running copy to close and waits (briefly) until it has.</summary>
    private static void StopRunningInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ExitEventName, out var signal)) return;
            using (signal)
            {
                signal.Set();
            }

            using var mutex = new Mutex(false, MutexName);
            try
            {
                if (mutex.WaitOne(TimeSpan.FromSeconds(8))) mutex.ReleaseMutex();
                else Log.Warn("The running copy did not close in time");
            }
            catch (AbandonedMutexException)
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not signal the running copy: " + ex.Message);
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
            if (_live) ExplorerTaskbar.Restore();
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
