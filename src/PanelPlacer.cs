using System.Diagnostics;
using System.Runtime.InteropServices;
using ManagedShell.AppBar;

namespace ClearStart;

/// <summary>
/// Opens Windows' quick settings and notification center next to the bar whose button was clicked.
///
/// Windows opens these panels on the main screen whichever bar asks for them (its own taskbars on the other screens
/// have no buttons for them), in the corner of the work area by the taskbar. So when the button is clicked, the panel
/// is watched for and put in the corner of the clicked bar's work area, as Windows puts it: the bottom right for a bar
/// on the bottom or the right, the top right for one on the top, and the bottom left for one on the left. That place
/// comes from the work area alone, not from where the panel is, so it is the same at every opening, though Windows
/// opens the notification center again where it was left. By a bar on the bottom or the right, a panel already inside
/// the clicked bar's work area is left where it is: it is in Windows' own corner, a little in from the bar if Windows'
/// taskbar is taller than the bar. If Windows puts a panel back while it is open, for example as quick settings grows
/// to list Wi-Fi networks, it is moved again.
///
/// Windows makes the panels for the main screen, so on a screen with less room they would run off the edge. Quick
/// settings is then made as tall, or as wide, as the work area. The notification center keeps its size, because it
/// does not lay itself out again when its window is made smaller: it is lined up with the bottom of the work area,
/// where its calendar is, and its top, empty unless there are many notifications, runs off the screen. Otherwise only
/// the panel's place is changed: never which window is in front, or which is active. Each click gets a line in the
/// log saying where the panel opened and where it was moved.
/// </summary>
internal static class PanelPlacer
{
    /// <summary>A panel to watch for, told apart by its window.</summary>
    /// <param name="Name">What the log calls it.</param>
    /// <param name="ClassName">The class of its window.</param>
    /// <param name="ProcessName">The program it belongs to, when its class is a common one.</param>
    /// <param name="ProcessId">Only a window of this process, if not 0; the self-test gives its own.</param>
    /// <param name="Resizable">Whether it may be made smaller to fit a screen: it lays itself out again.</param>
    internal sealed record Panel(string Name, string ClassName, string? ProcessName = null, uint ProcessId = 0,
        bool Resizable = false);

    /// <summary>Windows' quick settings: Wi-Fi, volume and the like. A window of its own class, made as it opens.</summary>
    public static readonly Panel QuickSettings = new("Quick settings", "ControlCenterWindow", Resizable: true);

    /// <summary>
    /// Windows' notifications and calendar. Its window is kept, hidden, while it is closed, and has the class every
    /// window of a Store-style app has; a window of Windows' shell experience host that size is it. Made smaller, it
    /// is not laid out again and the calendar at its bottom is cut off, so it is not resizable.
    /// </summary>
    public static readonly Panel NotificationCenter = new("Notification center", "Windows.UI.Core.CoreWindow",
        "ShellExperienceHost");

    /// <summary>How long the panel is waited for after the button is clicked.</summary>
    public static readonly TimeSpan WaitToOpen = TimeSpan.FromSeconds(3);

    /// <summary>How long the panel stays hidden before the watch ends: it may be hidden for a moment as it opens.</summary>
    public static readonly TimeSpan ClosedFor = TimeSpan.FromSeconds(1);

    /// <summary>The longest the panel is watched once it is open.</summary>
    private static readonly TimeSpan LongestOpen = TimeSpan.FromMinutes(10);

    /// <summary>The most times the panel is moved in one opening, in case Windows keeps putting it back.</summary>
    private const int MostMoves = 20;

    /// <summary>Smaller windows of the panel's program are not the panel: Store-style apps keep a tiny one too.</summary>
    private const int SmallestPanel = 100;

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
    /// Called just before <paramref name="panel"/> is asked for: it is watched for, and moved next to the bar on
    /// <paramref name="monitor"/> at <paramref name="edge"/>.
    /// </summary>
    public static void Expect(Panel panel, IntPtr monitor, AppBarEdge edge)
    {
        if (monitor == IntPtr.Zero) return;
        var expectation = new Expectation(panel, monitor, edge, Environment.TickCount64);
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
                Name = "Panel placer",
            };
            _thread.Start();
        }
        // Windows must be reporting windows before the panel is asked for, or its showing could be missed.
        ready.Wait(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Where a panel of this size goes in a work area, and its size there: in the corner by a bar on
    /// <paramref name="edge"/>, the bottom right for a bar on the bottom or the right, the top right for one on the
    /// top, the bottom left for one on the left. A panel taller or wider than the work area is made as tall or as wide
    /// as it if it is <paramref name="resizable"/>; otherwise it keeps its size, lined up with the work area's bottom
    /// and with its side by the bar.
    /// </summary>
    internal static (int X, int Y, int Width, int Height) Spot(int width, int height, Native.RECT work,
        AppBarEdge edge, bool resizable)
    {
        if (resizable)
        {
            width = Math.Min(width, work.Width);
            height = Math.Min(height, work.Height);
        }
        int x = edge == AppBarEdge.Left ? work.Left : work.Right - width;
        int y = edge == AppBarEdge.Top && height <= work.Height ? work.Top : work.Bottom - height;
        return (x, y, width, height);
    }

    private sealed record Expectation(Panel Panel, IntPtr Monitor, AppBarEdge Edge, long Since);

    /// <summary>One watch, on a thread of its own: Windows reports windows shown, moved and hidden to it.</summary>
    private sealed class Watch
    {
        private readonly Native.WinEventProc _callback;
        private readonly List<string> _trail = new();
        private readonly HashSet<string> _otherShown = new();
        private readonly Dictionary<uint, string> _processNames = new();
        private Expectation _expect;
        private IntPtr _panel;
        private Native.RECT? _placed;
        private int _moves;
        private long _openedAt, _closedAt;

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
                Log.Error("Could not watch for Windows' panels", ex);
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
                Log.Error("Watching for Windows' panels failed", ex);
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
                _placed = null;
                _moves = 0;
                _openedAt = _closedAt = 0;
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
                    if (!IsThePanel(hwnd, className))
                    {
                        // Kept for the log, in case Windows makes its panel otherwise one day.
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

        private bool IsThePanel(IntPtr hwnd, string className)
        {
            var panel = _expect.Panel;
            if (className != panel.ClassName) return false;
            if (panel.ProcessId != 0 || panel.ProcessName != null)
            {
                Native.GetWindowThreadProcessId(hwnd, out uint processId);
                if (panel.ProcessId != 0 && processId != panel.ProcessId) return false;
                if (panel.ProcessName != null &&
                    !string.Equals(ProcessNameOf(processId), panel.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return Native.GetWindowRect(hwnd, out var rect) && rect.Width >= SmallestPanel && rect.Height >= SmallestPanel;
        }

        private string ProcessNameOf(uint processId)
        {
            if (_processNames.TryGetValue(processId, out var name)) return name;
            try
            {
                using var process = Process.GetProcessById((int)processId);
                name = process.ProcessName;
            }
            catch (Exception)
            {
                name = "";
            }
            _processNames[processId] = name;
            return name;
        }

        /// <summary>Moves the panel into the corner of the clicked bar's work area, unless it is there.</summary>
        private void Place()
        {
            if (!Native.GetWindowRect(_panel, out var rect)) return;
            if (_placed is { } placed && Same(placed, rect)) return;
            if (!Native.TryGetMonitor(_expect.Monitor, out _, out var work)) return;
            IntPtr on = Native.MonitorFromWindow(_panel, 2 /* MONITOR_DEFAULTTONEAREST */);
            string where = on == _expect.Monitor ? "on the clicked bar's screen"
                : Native.TryGetMonitor(on, out var bounds, out _) ? $"on the screen at {bounds.Left},{bounds.Top}"
                : "on another screen";

            var (x, y, width, height) = Spot(rect.Width, rect.Height, work, _expect.Edge, _expect.Panel.Resizable);
            bool resize = width != rect.Width || height != rect.Height;
            // Inside the work area, by a bar on the bottom or the right, it is in Windows' own corner: moving it the few
            // pixels to the bar would only make it jump at every opening.
            bool inCorner = on == _expect.Monitor && _expect.Edge is AppBarEdge.Bottom or AppBarEdge.Right &&
                            Inside(rect, work);
            if (inCorner || x == rect.Left && y == rect.Top && !resize)
            {
                Note($"at {Where(rect)} {where}, left there");
                _placed = rect;
                return;
            }
            if (_moves >= MostMoves)
            {
                if (_moves++ == MostMoves) Note($"moved back {MostMoves} times; left there");
                return;
            }
            Note($"at {Where(rect)} {where}");
            _moves++;
            if (!Native.SetWindowPos(_panel, IntPtr.Zero, x, y, width, height,
                (resize ? 0 : Native.SWP_NOSIZE) | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | SWP_NOOWNERZORDER))
            {
                Note($"could not be moved (error {Marshal.GetLastPInvokeError()})");
                _moves = MostMoves + 1;
                return;
            }
            // Kept once it is on the clicked bar's screen (Windows may nudge it a little); if Windows has already put it
            // back on another screen, the next event moves it again.
            _placed = Native.GetWindowRect(_panel, out var now) &&
                      Native.MonitorFromWindow(_panel, 2 /* MONITOR_DEFAULTTONEAREST */) == _expect.Monitor
                ? now
                : null;
            Note(resize ? $"moved to {x},{y} and made {width}x{height}" : $"moved to {x},{y}");
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
            string name = _expect.Panel.Name;
            if (_panel == IntPtr.Zero)
            {
                string others = _otherShown.Count == 0 ? "" : "; shown meanwhile: " + string.Join(", ", _otherShown);
                return $"{name}, asked for by {target}: did not open within {WaitToOpen.TotalSeconds:0} s " +
                    $"(clicked while it was open closes it){others}" + (_trail.Count > 0 ? "; " + string.Join("; ", _trail) : "");
            }
            return $"{name}, asked for by {target}: {string.Join("; ", _trail)}";
        }

        private static string Where(Native.RECT rect) => $"{rect.Left},{rect.Top} ({rect.Width}x{rect.Height})";

        private static bool Inside(Native.RECT rect, Native.RECT work) =>
            rect.Left >= work.Left && rect.Top >= work.Top && rect.Right <= work.Right && rect.Bottom <= work.Bottom;

        private static bool Same(Native.RECT a, Native.RECT b) =>
            a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
    }
}
