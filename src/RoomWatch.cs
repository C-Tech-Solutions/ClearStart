using System.Windows.Threading;

namespace ClearStart;

/// <summary>A bar as <see cref="RoomWatch"/> sees it: whether it has its room on the desktop, and two ways to dock it.</summary>
internal interface IRoomBar
{
    /// <summary>The display it is on, for the log.</summary>
    string Name { get; }

    /// <summary>Whether the desktop leaves the bar's strip to it, so maximized windows stop beside the bar.</summary>
    bool HasRoom { get; }

    /// <summary>Docks the bar again where it is, which makes Windows work its room out again.</summary>
    void Redock();

    /// <summary>Docks the bar anew through Explorer's taskbar, which works the room out from the start.</summary>
    void DockAgain();
}

/// <summary>
/// Keeps the bars' room on the desktop after the displays change. When monitors come back, after being turned off or
/// after sleep, Windows can give a monitor's whole area to the desktop again, so maximized windows go under its bar,
/// and it can do that some seconds after the change has settled, while Explorer is still making its own taskbars
/// for those monitors. Docking a bar again does not always win its room back at once.
///
/// So after each change the bars are looked at every two seconds for half a minute. A bar without its room is docked
/// again each time, and once anew through Explorer if that has not helped after ten seconds. The watch stops as soon
/// as every bar has its room. Windows also says when it changes a monitor's area: if a bar has lost its room then,
/// the watch starts again. A bar that has no room at the end of the watch is left as it is until the displays change
/// again, so that a bar that never gets its room is not docked again and again for good.
/// </summary>
internal sealed class RoomWatch
{
    /// <summary>How many looks one watch takes, one every <see cref="Interval"/>.</summary>
    public const int Looks = 15;

    /// <summary>The look at which a bar that is still without its room is docked anew through Explorer.</summary>
    public const int DockAnewAt = 5;

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly Func<IReadOnlyCollection<IRoomBar>> _bars;
    private readonly DispatcherTimer? _timer;
    // The bars found without their room in this watch, and how many looks each has been without it.
    private readonly Dictionary<IRoomBar, int> _without = new();
    private int _looks;

    /// <param name="dispatcher">Where the looks run. Without one, nothing looks by itself, and the self-test calls
    /// <see cref="Look"/>.</param>
    public RoomWatch(Func<IReadOnlyCollection<IRoomBar>> bars, Dispatcher? dispatcher)
    {
        _bars = bars;
        if (dispatcher == null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = Interval };
        _timer.Tick += (_, _) => Look();
    }

    /// <summary>Whether the bars are being looked at.</summary>
    public bool Running { get; private set; }

    /// <summary>
    /// True once a watch has ended with a bar still without its room: changes Windows makes to the monitors' areas
    /// do not start another watch until the displays change again.
    /// </summary>
    public bool GaveUp { get; private set; }

    /// <summary>
    /// The displays changed, or the bars were docked anew: watch them for half a minute from now. A watch that is
    /// already running starts its half minute again.
    /// </summary>
    /// <param name="lookNow">Looks once straight away as well, before the first look the timer takes.</param>
    public void Watch(bool lookNow)
    {
        GaveUp = false;
        Begin();
        if (lookNow) Look();
    }

    /// <summary>
    /// Windows changed the area a monitor leaves to the desktop. A bar may have lost its room with it; if one has,
    /// the watch starts. Changes that come while a watch runs, as the bars' own docking makes them, are left to it.
    /// </summary>
    public void WorkAreaChanged()
    {
        if (Running || GaveUp) return;
        if (_bars().All(bar => bar.HasRoom)) return;
        Begin();
    }

    /// <summary>Stops looking, as ClearStart closes or the self-test ends.</summary>
    public void Stop()
    {
        _timer?.Stop();
        Running = false;
    }

    /// <summary>
    /// One look: each bar without its room is docked again, and logged when it is first found so and when it has its
    /// room back. Taken by the timer, and by the self-test.
    /// </summary>
    public void Look()
    {
        if (!Running) return;
        _looks++;
        var bars = _bars();
        // Bars that went away (closed for a new layout) are no longer looked for.
        foreach (var gone in _without.Keys.Where(bar => !bars.Contains(bar)).ToList()) _without.Remove(gone);

        foreach (var bar in bars)
        {
            if (bar.HasRoom)
            {
                if (_without.Remove(bar, out int looks)) LogBack(bar, looks);
                continue;
            }
            _without.TryGetValue(bar, out int without);
            without++;
            _without[bar] = without;
            if (without == 1) Log.Info($"Taskbar on {bar.Name} has no room on the desktop: windows go under it. Docking it again until it has");
            try
            {
                if (without == DockAnewAt) bar.DockAgain();
                else bar.Redock();
            }
            catch (Exception ex)
            {
                Log.Error($"Could not dock the taskbar on {bar.Name} again", ex);
            }
            if (bar.HasRoom && _without.Remove(bar, out int tries)) LogBack(bar, tries);
        }

        if (_without.Count == 0)
        {
            Stop();
            return;
        }
        if (_looks < Looks) return;

        foreach (var bar in _without.Keys)
        {
            Log.Warn($"Taskbar on {bar.Name} still has no room on the desktop after {_without[bar]} tries; " +
                "it is tried again when the displays change");
        }
        _without.Clear();
        Stop();
        GaveUp = true;
    }

    // Each watch counts afresh, so a bar still without its room when the displays change again is docked anew again.
    private void Begin()
    {
        _looks = 0;
        _without.Clear();
        Running = true;
        if (_timer == null) return;
        _timer.Stop();
        _timer.Start();
    }

    private static void LogBack(IRoomBar bar, int tries) =>
        Log.Info($"Taskbar on {bar.Name} has its room on the desktop again, after {tries} {(tries == 1 ? "try" : "tries")}");
}
