namespace ClearStart;

/// <summary>
/// Keeps the Windows taskbar hidden for as long as ClearStart runs in its place.
///
/// Explorer shows its taskbar again whenever the docked bars on the desktop change: one of ours opening,
/// closing or moving, a monitor coming or going, Explorer itself restarting. Windows reports every window
/// that is shown; this listens for that on a thread of its own and hides the taskbar again at once, however
/// busy the UI thread is (building four bars can keep it busy for seconds). A look every two seconds catches
/// anything the reports missed.
///
/// Without code inside Explorer, which ClearStart does not put there, this is as soon as the taskbar can be
/// hidden: the report comes just after the window was shown, so Explorer may still draw it for a frame.
/// </summary>
internal static class TaskbarGuard
{
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_TIMER = 0x0113;
    private const uint SweepMilliseconds = 2000;

    private static readonly string[] TaskbarClasses = { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;
    private static readonly object Gate = new();
    // Held while the auto-hide setting is being changed; once Stop has it, no change starts. So none can land
    // after the taskbar has been put back.
    private static readonly object AutoHideGate = new();
    private static bool _stopped;
    private static Thread? _thread;
    private static uint _threadId;
    // Kept here so the garbage collector cannot take it while Windows still calls it.
    private static Native.WinEventProc? _callback;

    // The rest is only touched on the guard's own thread.
    private static readonly HashSet<IntPtr> Recorded = new();
    private static int _hiddenAgain;
    private static bool _firstHideLogged;
    private static DateTime _lastMissLogged = DateTime.MinValue;

    /// <summary>Hides the Windows taskbar now (if it is up yet) and keeps it hidden until <see cref="Stop"/>.</summary>
    public static void Start()
    {
        lock (Gate)
        {
            if (_thread != null) return;
            lock (AutoHideGate) _stopped = false;
            // Not disposed: if the wait below gives up, the thread still sets it later.
            var ready = new ManualResetEventSlim();
            var thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Windows taskbar guard" };
            thread.Start();
            // The first sweep hides what is there already; wait for it, but never for long.
            ready.Wait(TimeSpan.FromSeconds(5));
            _thread = thread;
        }
    }

    /// <summary>Stops hiding the Windows taskbar. Showing it again is up to the caller.</summary>
    public static void Stop()
    {
        Thread? thread;
        lock (Gate)
        {
            thread = _thread;
            _thread = null;
        }
        if (thread == null) return;

        Native.PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(2));
        // Waits for a change in progress to finish (it asks Explorer, as putting the taskbar back does next).
        lock (AutoHideGate) _stopped = true;
        Log.Info($"Stopped keeping the Windows taskbar hidden; it was hidden {_hiddenAgain} time(s) as it came back");
    }

    private static void Run(ManualResetEventSlim ready)
    {
        IntPtr hook = IntPtr.Zero;
        UIntPtr timer = UIntPtr.Zero;
        try
        {
            _threadId = Native.GetCurrentThreadId();
            _callback = OnWinEvent;
            hook = Native.SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _callback, 0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            if (hook == IntPtr.Zero)
            {
                Log.Warn("Windows will not report the taskbar being shown; it is looked for every two seconds instead");
            }
            timer = Native.SetTimer(IntPtr.Zero, UIntPtr.Zero, SweepMilliseconds, IntPtr.Zero);
            Sweep(first: true);
        }
        catch (Exception ex)
        {
            Log.Error("Could not start keeping the Windows taskbar hidden", ex);
        }
        finally
        {
            ready.Set();
        }

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_TIMER && msg.hwnd == IntPtr.Zero)
            {
                Sweep(first: false);
                continue;
            }
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        if (timer != UIntPtr.Zero) Native.KillTimer(IntPtr.Zero, timer);
        if (hook != IntPtr.Zero) Native.UnhookWinEvent(hook);
    }

    /// <summary>Windows calls this, on the guard's thread, each time a window in another process is shown.</summary>
    private static void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint eventThread, uint eventTime)
    {
        if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF || hwnd == IntPtr.Zero) return;
        try
        {
            if (KindOf(hwnd) is not { } primary) return;
            Hide(hwnd);
            _hiddenAgain++;
            if (!_firstHideLogged)
            {
                _firstHideLogged = true;
                Log.Info($"Windows taskbar hidden as it appeared, {StartTiming.Describe()}");
            }
            if (primary) KeepAutoHidden(hwnd);
        }
        catch
        {
            // A failure in here must not take the process down; the sweep tries again.
        }
    }

    /// <summary>Hides every Windows taskbar that is showing. The first sweep runs before anything else loads.</summary>
    private static void Sweep(bool first)
    {
        int hidden = 0;
        foreach (string name in TaskbarClasses)
        {
            IntPtr hwnd = IntPtr.Zero;
            while ((hwnd = Native.FindWindowEx(IntPtr.Zero, hwnd, name, null)) != IntPtr.Zero)
            {
                if (KindOf(hwnd) is not { } primary) continue;
                if (primary) KeepAutoHidden(hwnd);
                if (!Native.IsWindowVisible(hwnd)) continue;
                Hide(hwnd);
                hidden++;
            }
        }

        if (first)
        {
            _firstHideLogged = hidden > 0;
            Log.Info(hidden > 0
                ? $"Windows taskbar hidden ({hidden} bar(s)), {StartTiming.Describe()}"
                : $"The Windows taskbar is not up yet; it will be hidden as it appears ({StartTiming.Describe()})");
        }
        else if (hidden > 0)
        {
            _hiddenAgain += hidden;
            if (DateTime.UtcNow - _lastMissLogged > TimeSpan.FromMinutes(1))
            {
                _lastMissLogged = DateTime.UtcNow;
                Log.Info($"The Windows taskbar was showing without Windows reporting it ({hidden} bar(s)); hidden again");
            }
        }
    }

    /// <summary>True for Windows' main taskbar, false for one on another monitor, null for any other window.</summary>
    private static bool? KindOf(IntPtr hwnd)
    {
        string name = Native.ClassNameOf(hwnd);
        bool primary = name == TaskbarClasses[0];
        if (!primary && name != TaskbarClasses[1]) return null;
        // Our own tray window has the main taskbar's class name too.
        Native.GetWindowThreadProcessId(hwnd, out uint process);
        return process == OwnProcessId ? null : primary;
    }

    /// <summary>
    /// Asks the window's own thread to hide it and leaves at once, so a busy Explorer cannot hold this thread up.
    /// It goes to the bottom of the pile, so that if Explorer shows it again it is behind every window until it is
    /// hidden. Putting it back puts it on top again (<see cref="ExplorerTaskbar.Restore"/>).
    /// </summary>
    private static void Hide(IntPtr hwnd) =>
        Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
            Native.SWP_HIDEWINDOW | Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_ASYNCWINDOWPOS);

    /// <summary>
    /// Auto-hide makes the hidden taskbar give up its strip of the desktop. The setting it had first is written
    /// down, so that it can be put back. Done once for each taskbar Explorer makes (it makes a new one when it
    /// restarts); Explorer keeps the setting in between.
    /// </summary>
    private static void KeepAutoHidden(IntPtr primary)
    {
        if (!Recorded.Add(primary)) return;
        // Asking Explorer waits for its taskbar's thread, which can be busy for a while just after it starts. This
        // thread has to stay free to hide the other taskbars as they appear, so the asking is done on another.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (AutoHideGate)
            {
                if (_stopped) return;
                try
                {
                    ExplorerTaskbar.RememberState(primary);
                    ExplorerTaskbar.EnsureAutoHide(primary);
                }
                catch (Exception ex)
                {
                    Log.Error("Could not switch the Windows taskbar to auto-hide", ex);
                }
            }
        });
    }
}
