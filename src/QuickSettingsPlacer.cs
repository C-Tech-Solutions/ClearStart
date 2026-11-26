using System.Runtime.InteropServices;
using ManagedShell.AppBar;

namespace ClearStart;

/// <summary>
/// Opens Windows' quick settings next to the bar whose button was clicked.
///
/// Windows opens the panel on the main screen whichever bar asks for it (its own taskbars on the other screens have no
/// button for it), in the corner of the work area by the taskbar. So when the button is clicked, the panel is watched
/// for: when it appears on another screen than the clicked bar's, it is moved to the same corner of that bar's screen,
/// as far in from the edges (scaled to that screen), and to the top or left corner for a bar on that edge. If Windows
/// puts it back while it is open, for example as the panel grows to list Wi-Fi networks, it is moved again. Opened by
/// a bar on the bottom or the right of the main screen, it is left where Windows put it.
///
/// Only the panel's place is changed: never its size, which window is in front, or which is active. Each click gets
/// a line in the log saying where the panel opened and where it was moved.
/// </summary>
internal static class QuickSettingsPlacer
{
    /// <summary>The class of Windows' quick settings window.</summary>
    public const string PanelClass = "ControlCenterWindow";

    /// <summary>How long the panel is waited for after the button is clicked.</summary>
    public static readonly TimeSpan WaitToOpen = TimeSpan.FromSeconds(3);

    /// <summary>How long the panel stays hidden before the watch ends: it may be hidden for a moment as it opens.</summary>
    public static readonly TimeSpan ClosedFor = TimeSpan.FromSeconds(1);

    /// <summary>The longest the panel is watched once it is open.</summary>
    private static readonly TimeSpan LongestOpen = TimeSpan.FromMinutes(10);

    /// <summary>The most times the panel is moved in one opening, in case Windows keeps putting it back.</summary>
    private const int MostMoves = 20;

    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint EVENT_OBJECT_CLOAKED = 0x8017;
    private const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_APP = 0x8000;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    private static readonly object Gate = new();
    private static Thread? _thread;
    private static uint _threadId;
    // A click while the watch runs, for its thread to take up.
    private static Expectation? _next;
    private static string? _lastReport;

    /// <summary>Whether a panel is being waited for or watched.</summary>
    public static bool Watching
    {
        get { lock (Gate) return _thread != null; }
    }

    /// <summary>The log line of the last watch that ended.</summary>
    public static string? LastReport
    {
        get { lock (Gate) return _lastReport; }
    }

    /// <summary>
    /// Called just before the panel is asked for: it is watched for, and moved next to the bar on
    /// <paramref name="monitor"/> at <paramref name="edge"/>.
    /// </summary>
    /// <param name="className">The class of the window to watch for; the self-test gives one of its own.</param>
    /// <param name="processId">Only a window of this process, if not 0; the self-test gives its own.</param>
    public static void Expect(IntPtr monitor, AppBarEdge edge, string className = PanelClass, uint processId = 0)
    {
        if (monitor == IntPtr.Zero) return;
        var expectation = new Expectation(monitor, edge, className, processId, Environment.TickCount64);
        ManualResetEventSlim ready;
        lock (Gate)
        {
            if (_thread != null)
            {
                // The timer takes it up too, if the thread has no message queue yet.
                _next = expectation;
                Native.PostThreadMessage(_threadId, WM_APP, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            ready = new ManualResetEventSlim();
            var started = ready;
            _thread = new Thread(() => new Watch(expectation).Run(started))
            {
                IsBackground = true,
                Name = "Quick settings placer",
            };
            _thread.Start();
        }
        // Windows must be reporting windows before the panel is asked for, or its showing could be missed.
        ready.Wait(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Where the panel sits in its screen's work area: how far in it is from the right, and from the bottom, at that
    /// screen's scale.
    /// </summary>
    internal readonly record struct Anchor(int FromSide, int FromEnd, double Scale)
    {
        public static Anchor Of(Native.RECT panel, Native.RECT work, double scale) =>
            new(work.Right - panel.Right, work.Bottom - panel.Bottom, scale);
    }

    /// <summary>
    /// Where a panel of this size goes in a work area: in the corner by a bar on <paramref name="edge"/> (the bottom
    /// right for a bar on the bottom or the right, the top right for one on the top, the bottom left for one on the
    /// left), as far in as <paramref name="anchor"/> says, and inside the work area.
    /// </summary>
    internal static (int X, int Y) Spot(Anchor anchor, int width, int height, Native.RECT work, double scale,
        AppBarEdge edge)
    {
        double k = anchor.Scale > 0 && scale > 0 ? scale / anchor.Scale : 1;
        int side = (int)Math.Round(anchor.FromSide * k);
        int end = (int)Math.Round(anchor.FromEnd * k);
        int x = edge == AppBarEdge.Left ? work.Left + side : work.Right - side - width;
        int y = edge == AppBarEdge.Top ? work.Top + end : work.Bottom - end - height;
        x = Math.Max(work.Left, Math.Min(x, work.Right - width));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
        return (x, y);
    }

    private sealed record Expectation(IntPtr Monitor, AppBarEdge Edge, string ClassName, uint ProcessId, long Since);

    /// <summary>One watch, on a thread of its own: Windows reports windows shown, moved and hidden to it.</summary>
    private sealed class Watch
    {
        private readonly Native.WinEventProc _callback;
        private readonly List<string> _trail = new();
        private readonly HashSet<string> _otherShown = new();
        private Expectation _expect;
        private IntPtr _panel;
        private Anchor? _anchor;
        private Native.RECT? _placed;
        private int _moves;
        private long _openedAt, _closedAt;
        private bool _leftAlone;

        public Watch(Expectation first)
        {
            _expect = first;
            _callback = OnWinEvent;
        }

        public void Run(ManualResetEventSlim ready)
        {
            var hooks = new List<IntPtr>();
            UIntPtr timer = UIntPtr.Zero;
            try
            {
                _threadId = Native.GetCurrentThreadId();
                foreach (var (first, last) in new[]
                {
                    (EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE),
                    (EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE),
                    (EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED),
                })
                {
                    IntPtr hook = Native.SetWinEventHook(first, last, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
                    if (hook != IntPtr.Zero) hooks.Add(hook);
                }
                timer = Native.SetTimer(IntPtr.Zero, UIntPtr.Zero, 200, IntPtr.Zero);
                if (hooks.Count < 3) Note("Windows would not report windows shown and moved, so it cannot be moved");
            }
            catch (Exception ex)
            {
                Log.Error("Could not watch for the quick settings panel", ex);
            }
            finally
            {
                ready.Set();
            }

            try
            {
                bool watching = hooks.Count == 3 && timer != UIntPtr.Zero;
                while (watching && Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if ((msg.message == WM_TIMER && msg.hwnd == IntPtr.Zero) || msg.message == WM_APP)
                    {
                        watching = Tick();
                        continue;
                    }
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Watching for the quick settings panel failed", ex);
            }
            finally
            {
                // Ended by Tick already, unless something failed.
                lock (Gate)
                {
                    if (_thread == Thread.CurrentThread)
                    {
                        _lastReport = Report();
                        _thread = null;
                        Log.Info(_lastReport);
                    }
                }
                if (timer != UIntPtr.Zero) Native.KillTimer(IntPtr.Zero, timer);
                foreach (IntPtr hook in hooks) Native.UnhookWinEvent(hook);
            }
        }

        /// <returns>False when the watch is over.</returns>
        private bool Tick()
        {
            Expectation? next;
            lock (Gate)
            {
                next = _next;
                _next = null;
            }
            if (next != null)
            {
                // Clicked again: what was seen so far goes to the log, and the watch starts over.
                string report = Report();
                lock (Gate) _lastReport = report;
                Log.Info(report);
                _expect = next;
                _panel = IntPtr.Zero;
                _anchor = null;
                _placed = null;
                _moves = 0;
                _openedAt = _closedAt = 0;
                _leftAlone = false;
                _trail.Clear();
                _otherShown.Clear();
                return true;
            }

            long now = Environment.TickCount64;
            bool over = _panel == IntPtr.Zero ? now - _expect.Since > WaitToOpen.TotalMilliseconds
                : _closedAt != 0 ? now - _closedAt > ClosedFor.TotalMilliseconds
                : now - _openedAt > LongestOpen.TotalMilliseconds;
            if (!over) return true;
            lock (Gate)
            {
                // A click that came in just now is taken up at the next tick.
                if (_next != null) return true;
                _lastReport = Report();
                _thread = null;
                Log.Info(_lastReport);
            }
            return false;
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
            uint eventThread, uint eventTime)
        {
            if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF || hwnd == IntPtr.Zero) return;
            try
            {
                if (_panel != IntPtr.Zero && hwnd != _panel) return;
                bool closing = eventType is EVENT_OBJECT_DESTROY or EVENT_OBJECT_HIDE or EVENT_OBJECT_CLOAKED;
                if (_panel == IntPtr.Zero)
                {
                    if (closing) return;
                    string className = Native.ClassNameOf(hwnd);
                    if (className != _expect.ClassName || !OfTheProcess(hwnd))
                    {
                        // Kept for the log, in case Windows names its panel otherwise one day.
                        if (eventType != EVENT_OBJECT_LOCATIONCHANGE && _otherShown.Count < 8) _otherShown.Add(className);
                        return;
                    }
                    _panel = hwnd;
                    _openedAt = Environment.TickCount64;
                }

                if (closing)
                {
                    if (_closedAt == 0)
                    {
                        _closedAt = Environment.TickCount64;
                        Note(eventType == EVENT_OBJECT_DESTROY ? "closed" : "hidden");
                    }
                    return;
                }
                if (eventType == EVENT_OBJECT_LOCATIONCHANGE && _closedAt != 0) return;
                if (eventType != EVENT_OBJECT_LOCATIONCHANGE && _closedAt != 0)
                {
                    _closedAt = 0;
                    Note("shown again");
                }
                Place();
            }
            catch (Exception ex)
            {
                Note("failed: " + ex.Message);
            }
        }

        private bool OfTheProcess(IntPtr hwnd)
        {
            if (_expect.ProcessId == 0) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint processId);
            return processId == _expect.ProcessId;
        }

        /// <summary>Moves the panel next to the clicked bar, unless it is there.</summary>
        private void Place()
        {
            if (!Native.GetWindowRect(_panel, out var rect)) return;
            if (_placed is { } placed && Same(placed, rect)) return;
            if (!Native.TryGetMonitor(_expect.Monitor, out _, out var work)) return;
            double scale = Native.ScaleOf(_expect.Monitor);
            IntPtr on = Native.MonitorFromWindow(_panel, 2 /* MONITOR_DEFAULTTONEAREST */);

            if (on != _expect.Monitor)
            {
                // Where Windows put it: the place on the clicked bar's screen is worked out from that.
                if (!Native.TryGetMonitor(on, out var bounds, out var onWork)) return;
                _anchor = Anchor.Of(rect, onWork, Native.ScaleOf(on));
                Note($"at {Where(rect)} on the screen at {bounds.Left},{bounds.Top}");
            }
            else if (_anchor == null)
            {
                if (_expect.Edge is AppBarEdge.Bottom or AppBarEdge.Right)
                {
                    // On the clicked bar's screen, in the corner by it: Windows' own place is right.
                    if (!_leftAlone) Note($"at {Where(rect)} on the clicked bar's screen, left there");
                    _leftAlone = true;
                    _placed = rect;
                    return;
                }
                _anchor = Anchor.Of(rect, work, scale);
                Note($"at {Where(rect)} on the clicked bar's screen");
            }

            var (x, y) = Spot(_anchor!.Value, rect.Width, rect.Height, work, scale, _expect.Edge);
            if (x == rect.Left && y == rect.Top)
            {
                _placed = rect;
                return;
            }
            if (_moves >= MostMoves)
            {
                if (_moves++ == MostMoves) Note($"moved back {MostMoves} times; left there");
                return;
            }
            _moves++;
            if (!Native.SetWindowPos(_panel, IntPtr.Zero, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | SWP_NOOWNERZORDER))
            {
                Note($"could not be moved (error {Marshal.GetLastPInvokeError()})");
                _moves = MostMoves + 1;
                return;
            }
            if (Native.GetWindowRect(_panel, out var now)) _placed = now;
            Note($"moved to {x},{y}");
        }

        private void Note(string what)
        {
            if (_trail.Count >= 16) return;
            _trail.Add($"{Environment.TickCount64 - _expect.Since} ms {what}");
        }

        private string Report()
        {
            string target = Native.TryGetMonitor(_expect.Monitor, out var bounds, out _)
                ? $"the bar on the screen at {bounds.Left},{bounds.Top} ({_expect.Edge.ToString().ToLowerInvariant()})"
                : "a bar whose screen is gone";
            if (_panel == IntPtr.Zero)
            {
                string others = _otherShown.Count == 0 ? "" : "; shown meanwhile: " + string.Join(", ", _otherShown);
                return $"Quick settings, asked for by {target}: did not open within {WaitToOpen.TotalSeconds:0} s " +
                    $"(clicked while it was open closes it){others}" + (_trail.Count > 0 ? "; " + string.Join("; ", _trail) : "");
            }
            return $"Quick settings, asked for by {target}: {string.Join("; ", _trail)}";
        }

        private static string Where(Native.RECT rect) => $"{rect.Left},{rect.Top} ({rect.Width}x{rect.Height})";

        private static bool Same(Native.RECT a, Native.RECT b) =>
            a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
    }
}
