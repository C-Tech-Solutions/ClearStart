using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ClearStart;

/// <summary>
/// Keeps the Windows taskbar on the bottom edge while ClearStart runs in its place, and puts it back on the edges it
/// had when ClearStart stops.
///
/// Windows 11 has its taskbar on the bottom only. A taskbar that a registry change or a tool has put on the top or a
/// side cannot be auto-hidden there: Windows keeps a strip of the desktop for it even while ClearStart hides it, so
/// ClearStart's bar on that edge sits past an empty strip and maximized windows stop short of it. On the bottom it
/// auto-hides like any other and gives its strip up.
///
/// Windows 11 25H2 with 2026's updates lets the user choose the edge in Settings, and keeps the choice as a number of
/// its own (<see cref="LocationValue"/>). Explorer goes by that number as it starts, over the older record below, which
/// it writes from it. There, ClearStart changes the number, tells Explorer the way Settings does, and waits a few
/// seconds for the taskbar to move: no restart, and beside the early-startup loader's Explorer too. The user may
/// choose another edge in Settings while ClearStart runs; <see cref="TaskbarGuard"/> sees the taskbar leave the bottom,
/// and it is written down and moved again. What follows is for a Windows without the number, and for an Explorer that
/// does not move its taskbar when told.
///
/// Explorer reads the edge only as it starts, and writes its own as it closes, so the edge is changed while Explorer
/// is closed. The Restart Manager closes it the way Windows does for an update, the edge is written, and the Restart
/// Manager starts it again; folder windows stay open. This takes two or three seconds. An Explorer that does not close
/// when asked is ended and started afresh instead.
///
/// The Explorer the early-startup loader started is not restarted while the loader runs, whichever copy of ClearStart
/// is running: the loader watches it, and takes its closing for a failure. With early startup on, the loader puts the
/// bottom edge in Windows' record before it starts Explorer at each sign-in instead, whenever ClearStart has the
/// taskbar's own edges on record (native\TaskbarEdge.h). Turning early startup on writes them down
/// (<see cref="RecordForEarlyStartup"/>), so the first sign-in through the loader has the taskbar on the bottom too.
///
/// The edges the taskbar had are written down first. They are put back when ClearStart closes, fails, or is
/// uninstalled ("--restore"), and at the next sign-in when the user signs out and ClearStart does not start again.
/// While the loader watches Explorer, the taskbar is left on the bottom and the record kept instead. The loader goes
/// soon after the ClearStart it started has closed, and a "--restore" after that (an uninstall) restarts Explorer as
/// usual; otherwise the taskbar goes back at the first sign-in that ClearStart does not start at.
/// With early startup on, a ClearStart that closes and has put the taskbar back writes its edges down again
/// (<see cref="RecordForEarlyStartup"/>), for the loader at the next sign-in.
/// Self-contained like <see cref="ExplorerTaskbar"/>, for the same reasons.
/// </summary>
internal static class TaskbarEdge
{
    internal const string MainKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3";
    internal const string MainValue = "Settings";
    // One value for each monitor Windows has seen, for the taskbars on the other monitors.
    internal const string OtherMonitorsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\MMStuckRects3";
    // The edge chosen in Settings, as a number (the same edges), on a Windows that has that choice. It is missing on one
    // that does not, and until the choice is first changed. Explorer goes by it over the two records above.
    internal const string LocationKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    internal const string LocationValue = "TaskbarLocation";
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RunOnceValue = "ClearStart taskbar edge";
    // A line of the record that holds where a taskbar window was, rather than an edge from Windows' record.
    private const string BarLine = "bar";
    // Within each value: the edge (0 left, 1 top, 2 right, 3 bottom), and in the main one the auto-hide flag.
    private const int EdgeOffset = 12;
    private const int FlagsOffset = 8;
    private const byte AutoHideFlag = 0x01;
    private const byte Bottom = 3;
    private static readonly string[] EdgeNames = { "left", "top", "right", "bottom" };

    // Explorer closes in about two seconds; one that has not closed by then mostly never does. The Restart Manager would
    // wait half a minute for it, with the screen empty meanwhile.
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(5);
    // How long a new Explorer has to put up its taskbar. It normally takes a second or two.
    private static readonly TimeSpan TaskbarWait = TimeSpan.FromSeconds(30);
    // How long the early-startup loader has to go once the ClearStart it started has closed. It takes well under one.
    private static readonly TimeSpan LoaderWait = TimeSpan.FromSeconds(5);
    // How long Explorer has to move its taskbar after the edge chosen in Settings changed. It takes well under a second;
    // one that is still starting takes longer. Beside the loader's Explorer nothing else can be done, so that one gets
    // longer.
    private static readonly TimeSpan SettingWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SettingWaitBesideLoader = TimeSpan.FromSeconds(20);
    // Putting the taskbar back as the ClearStart the loader started closes: the loader has let the taskbar go already,
    // and stops a ClearStart that has not closed five seconds later, taking that for a failure (native\Protocol.h).
    private static readonly TimeSpan SettingWaitClosingBesideLoader = TimeSpan.FromSeconds(2);
    // After this long without the taskbar moving, every window is told the setting changed, not only Explorer's taskbar.
    private static readonly TimeSpan TellEveryoneAfter = TimeSpan.FromSeconds(2);

    private static readonly object Gate = new();
    // Set once the taskbar was moved, until it is seen on the bottom: a move that did not hold is not tried again.
    private static bool _tried;
    // Set when the edges were written for the next sign-in rather than put back now: the user signs out, or the taskbar
    // is beside the early-startup loader's Explorer.
    private static bool _writtenForSignIn;
    // Set while ClearStart's shell starts; Explorer is not restarted meanwhile (see StartingUp).
    private static bool _startingUp;
    private static volatile bool _moving;

    /// <summary>
    /// True from the moment the taskbar is found on another edge until it has been moved to the bottom, or the move was
    /// given up. ClearStart's tray waits behind Explorer's meanwhile (see <see cref="ShellHost"/>).
    /// </summary>
    public static bool Moving => _moving;

    /// <summary>
    /// Runs on the restarting thread just before Explorer is closed and started again while ClearStart runs. False holds
    /// the restart back: ClearStart's tray is not out of the new Explorer's way yet, or ClearStart is closing.
    /// </summary>
    public static Func<bool>? BeforeRestart { get; set; }

    /// <summary>
    /// Run by <see cref="TaskbarGuard"/> once the taskbar has moved to the bottom in the Explorer that is running, and
    /// is auto-hidden there: ClearStart's bars take their room on the desktop again. Runs on a background thread.
    /// </summary>
    public static Action? AfterMove { get; set; }

    /// <summary>True while ClearStart has the taskbar on the bottom and the edges it was moved from on record.</summary>
    public static bool Moved => File.Exists(RecordFile);

    /// <summary>
    /// ClearStart is starting: a restart of Explorer waits for <see cref="StartedUp"/>. An Explorer that is new as
    /// ClearStart's shell services start works only partly: windows no longer snap with the Win+arrow keys, and it no
    /// longer closes when asked.
    /// </summary>
    public static void StartingUp()
    {
        // Held by a restart under way.
        lock (Gate) _startingUp = true;
    }

    /// <summary>ClearStart's shell has started; Explorer may be restarted again.</summary>
    public static void StartedUp()
    {
        lock (Gate)
        {
            _startingUp = false;
            Monitor.PulseAll(Gate);
        }
    }

    private static string RecordFile => Path.Combine(Log.DataDirectory, "explorer-taskbar-edges.txt");

    // The Explorer the early-startup loader started at sign-in, as its process id and start time (see StartedByLoader).
    private static string LoaderExplorerFile => Path.Combine(Log.DataDirectory, "early-startup-explorer.txt");
    private static bool _notedLoaderExplorer;

    internal readonly record struct Entry(string Key, string Name, byte Edge);

    /// <summary>
    /// Moves the Windows taskbar to the bottom edge if it is on another edge of any monitor. Runs on a background
    /// thread: it waits for Explorer to move its taskbar, or for a newly started Explorer to settle, and gives up as
    /// soon as ClearStart stops keeping the taskbar hidden.
    /// </summary>
    /// <param name="primary">Explorer's main taskbar window.</param>
    /// <param name="stopping">True once ClearStart has stopped keeping the taskbar hidden.</param>
    /// <returns>
    /// True if the taskbar moved to the bottom in the Explorer that is running: it can be auto-hidden there now. False
    /// if it was there already, was left where it is, or Explorer was started again (its new taskbar is looked at afresh).
    /// </returns>
    public static bool KeepAtBottom(IntPtr primary, Func<bool> stopping)
    {
        bool moving = false;
        try
        {
            List<Entry> entries;
            bool setting, besideLoader;
            string from;
            lock (Gate)
            {
                if (EarlySession.Active) NoteLoaderExplorer(primary);
                if (stopping()) return false;
                entries = ReadEntries();
                // Windows' record decides for the main taskbar. The windows decide for the others: the record keeps
                // an edge for every monitor ever connected, and old ones do not matter.
                if (MainOnBottom(entries) && ExplorerTaskbar.AllOnBottom())
                {
                    _tried = false;
                    return false;
                }
                if (_tried) return false;
                _tried = true;
                var record = ReadRecord();
                setting = LocationOf(entries) != null;
                besideLoader = EarlySession.Active || StartedByLoader(primary);
                if (!setting && besideLoader)
                {
                    // That Explorer stays as it is. With the edges on record, the loader puts the bottom edge in Windows'
                    // record before it starts Explorer at the next sign-in. It is written now too, for an Explorer that
                    // ends without writing its own.
                    if (EarlyStartup.IsEnabled && (record != null || entries.Any(entry => entry.Edge != Bottom)))
                    {
                        record = Remember(entries, record);
                        WriteEdges(OnBottom(entries), autoHide: null);
                        Log.Info($"The Windows taskbar is on the {Describe(record)}, where Windows cannot auto-hide it. " +
                            "It stays there until the next sign-in: moving it now restarts Explorer, and the early-startup " +
                            "loader takes the Explorer it started closing for a failure. The loader puts it on the bottom " +
                            "before it starts Explorer then");
                    }
                    else
                    {
                        Log.Warn("The Windows taskbar is not on the bottom edge. It is left there: moving it restarts " +
                            "Explorer, and the early-startup loader takes the Explorer it started closing for a failure");
                    }
                    return false;
                }
                // With the edge chosen in Settings, Explorer is told the setting again below, as it may not have
                // read it since it was written.
                if (!setting && entries.All(entry => entry.Edge == Bottom))
                {
                    Log.Warn("The Windows taskbar is not on the bottom edge, but Windows' record says it is; it is left there");
                    return false;
                }
                if (setting && !MainOnBottom(entries))
                {
                    // The edge chosen in Settings is the user's own, as it is now: an older record is out of date.
                    WriteRecord(entries);
                    record = entries;
                }
                else
                {
                    record = Remember(entries, record);
                }
                from = Describe(record);
                // Under the lock: closing ClearStart puts the edges back under the same lock, so that comes after this.
                if (setting) WriteEdges(OnBottom(entries), autoHide: null);
                _moving = moving = true;
            }
            Log.Info(setting && MainOnBottom(entries)
                ? "The Windows taskbar is not on the bottom edge yet, though the edge chosen in Settings is the bottom; " +
                    "waiting for Explorer to move it"
                : $"The Windows taskbar is on the {from}, where Windows cannot auto-hide it; " +
                    "moving it to the bottom while ClearStart runs");

            if (setting)
            {
                if (FollowsSetting(bottom: true, besideLoader ? SettingWaitBesideLoader : SettingWait, stopping))
                    return MovedToBottom();
                if (stopping()) return false;
                if (besideLoader)
                {
                    Log.Warn("Explorer has not moved its taskbar to the bottom, though its setting says so now. It is not " +
                        "started again: the early-startup loader takes the Explorer it started closing for a failure. " +
                        "The taskbar is on the bottom from the next sign-in");
                    return false;
                }
                Log.Warn("Explorer has not moved its taskbar to the bottom, though its setting says so now; " +
                    "starting Explorer again instead");
            }

            if (!WaitUntilSettled(primary, stopping)) return false;
            // It may have moved meanwhile, a little late.
            if (setting && FollowsSetting(bottom: true, TimeSpan.Zero, stopping, tell: false))
                return MovedToBottom();
            if (!RestartWhenFree(primary, stopping, () => WriteEdges(OnBottom(ReadEntries()), autoHide: true))) return false;
            // The new Explorer has its main taskbar up; the others follow in a moment.
            if (FollowsSetting(bottom: true, SettingWait, stopping, tell: false))
            {
                Log.Info("Windows taskbar moved to the bottom; Explorer started again");
            }
            else if (!stopping())
            {
                Log.Warn("Explorer started again, but its taskbar is still not on the bottom edge; it is left there");
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Could not move the Windows taskbar to the bottom", ex);
            return false;
        }
        finally
        {
            if (moving) _moving = false;
        }
    }

    /// <summary>
    /// The taskbar is on the bottom now, so a later move off it in Settings is tried again. The sweep that would
    /// otherwise make that possible may not have seen the taskbar before it moved.
    /// </summary>
    private static bool MovedToBottom()
    {
        lock (Gate) _tried = false;
        Log.Info("Windows taskbar moved to the bottom");
        return true;
    }

    /// <summary>
    /// Waits for Explorer's taskbars to be where the edge chosen in Settings puts them, now that ClearStart has changed
    /// it: on the bottom, or off it. Explorer's taskbar is told of the change first, the way Settings tells it; if it
    /// has still not moved after <see cref="TellEveryoneAfter"/>, every window is told.
    /// </summary>
    /// <param name="bottom">Wait for every taskbar to be on the bottom (true), or for them to have left it (false).</param>
    /// <param name="tell">False only looks: nothing is told to an Explorer that read the edge as it started.</param>
    private static bool FollowsSetting(bool bottom, TimeSpan wait, Func<bool> stopping, bool tell = true)
    {
        var waited = Stopwatch.StartNew();
        bool toldEveryone = !tell;
        // A short wait tells everyone halfway, leaving time to see the taskbar move.
        var everyoneAfter = wait < TellEveryoneAfter * 2 ? wait / 2 : TellEveryoneAfter;
        if (tell) TellSettingChanged(ExplorerTaskbar.FindPrimary());
        while (true)
        {
            // With no taskbar up there is nothing to see yet.
            if (ExplorerTaskbar.IsUp() && ExplorerTaskbar.AllOnBottom() == bottom) return true;
            if (stopping() || waited.Elapsed >= wait) return false;
            if (!toldEveryone && waited.Elapsed >= everyoneAfter)
            {
                toldEveryone = true;
                // On another thread: each slow window can hold the message for a second, and this wait keeps its own
                // deadline (two seconds when closing beside the loader, which stops ClearStart after five).
                ThreadPool.QueueUserWorkItem(_ => TellSettingChanged(HWND_BROADCAST));
            }
            Thread.Sleep(100);
        }
    }

    /// <summary>Tells a window that the taskbar's settings have changed, as Settings does after it changes one.</summary>
    private static void TellSettingChanged(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        // Waits for the window, a second at most: the text has to stay in place while it is read.
        SendMessageTimeout(window, WM_SETTINGCHANGE, IntPtr.Zero, "TraySettings", SMTO_ABORTIFHUNG, 1000, out _);
    }

    /// <summary>
    /// <see cref="Restart"/>, once ClearStart's shell is not starting (see <see cref="StartingUp"/>), and after
    /// <see cref="BeforeRestart"/> has moved ClearStart's tray out of the new Explorer's way.
    /// </summary>
    private static bool RestartWhenFree(IntPtr primary, Func<bool> stopping, Action whileClosed)
    {
        while (true)
        {
            lock (Gate)
            {
                while (_startingUp && !stopping()) Monitor.Wait(Gate, 250);
            }
            if (stopping()) return false;
            // Not under the lock: it waits for the window thread, which may be waiting for the lock.
            if (BeforeRestart is { } before && !before())
            {
                Thread.Sleep(1000);
                continue;
            }
            lock (Gate)
            {
                // Closing ClearStart puts the edges back under the same lock, so this cannot come after that.
                if (stopping()) return false;
                if (_startingUp) continue;
                return Restart(primary, whileClosed);
            }
        }
    }

    /// <summary>
    /// Puts the Windows taskbar back on the edges it had before <see cref="KeepAtBottom"/> moved it. Does nothing if
    /// it was not moved.
    /// </summary>
    /// <param name="sessionEnding">
    /// The user is signing out or shutting down, and ClearStart does not start again. Explorer is closing too, so the
    /// edges are only written for it to read at the next sign-in. Explorer may write its own as it closes, so
    /// "ClearStart.exe --restore" is also set to run once at the next sign-in, to finish the job.
    /// </param>
    /// <returns>True if Explorer was started again: its taskbar windows are new ones.</returns>
    public static bool PutBack(bool sessionEnding)
    {
        lock (Gate)
        {
            try
            {
                // Closing puts the taskbar back twice; the second time must not take back what the first one set up.
                if (_writtenForSignIn) return false;
                var record = ReadRecord();
                if (record == null) return false;
                IntPtr primary = ExplorerTaskbar.FindPrimary();
                // Explorer keeps its edges in memory and writes them as it closes. One that still has its taskbar on
                // the bottom (a sign-out that was cancelled after the edges were written) would write the bottom back.
                if (Matches(record) && !(primary != IntPtr.Zero && ExplorerTaskbar.AllOnBottom()))
                {
                    Forget();
                    return false;
                }

                if (sessionEnding)
                {
                    WriteEdges(record, autoHide: null);
                    RunOnceAtSignIn();
                    _writtenForSignIn = true;
                    Log.Info($"Windows taskbar edges written back ({Describe(record)}); checked again at the next sign-in");
                    return false;
                }

                if (primary == IntPtr.Zero)
                {
                    // Explorer is not running: it reads the edges when it starts.
                    WriteEdges(record, autoHide: null);
                    Forget();
                    Log.Info($"Windows taskbar edges written back ({Describe(record)})");
                    return false;
                }

                bool wroteSetting = false;
                if (LocationOf(record) is { } own && LocationOf(ReadEntries()) != null)
                {
                    // Explorer follows the edge chosen in Settings while it runs: no restart, and the early-startup
                    // loader's Explorer does too.
                    WriteEdges(record, autoHide: null);
                    wroteSetting = true;
                    // On a timeout beside the loader's Explorer, the record stays for later (see below).
                    var wait = EarlySession.Active ? SettingWaitClosingBesideLoader : SettingWait;
                    if (FollowsSetting(bottom: own.Edge == Bottom, wait, () => false))
                    {
                        Forget();
                        Log.Info($"Windows taskbar put back on the {Describe(record)}");
                        return false;
                    }
                    Log.Warn("Explorer has not moved its taskbar back, though its setting says so now");
                }

                if (StartedByLoader(primary))
                {
                    // That Explorer is not restarted (see KeepAtBottom).
                    if (!ExplorerTaskbar.AllOnBottom())
                    {
                        // Its taskbar never left its own edge. The next Explorer reads the edges written now.
                        WriteEdges(record, autoHide: null);
                        Forget();
                        Log.Info($"Windows taskbar edges written back ({Describe(record)}); the taskbar is still there");
                        return false;
                    }
                    // It started on the bottom and writes that edge into Windows' record while it runs, so nothing is
                    // written here: a copy of ClearStart started after this one would find the two disagreeing, and
                    // restart Explorer for nothing. The record stays. With early startup on, the loader puts the taskbar
                    // on the bottom again at the next sign-in; a "--restore" once the loader has gone (an uninstall) puts
                    // it back now. With ClearStart not starting at the next sign-in, a run then puts it back; not when it
                    // starts then, as the run would close it.
                    if (wroteSetting) WriteEdges(OnBottom(record), autoHide: null);
                    bool startsAgain = AutoStart.StartsAtSignIn();
                    if (!startsAgain) RunOnceAtSignIn();
                    _writtenForSignIn = true;
                    Log.Warn("The Windows taskbar stays on the bottom edge for now: putting it back restarts Explorer, and " +
                        "the early-startup loader takes the Explorer it started closing for a failure. " + (startsAgain
                            ? "ClearStart starts again at the next sign-in, and keeps it on the bottom"
                            : $"It goes back on the {Describe(record)} at the next sign-in"));
                    return false;
                }

                WaitUntilSettled(primary, () => false);
                // On failure the record stays, and the next close tries again.
                if (!Restart(primary, () => WriteEdges(record, autoHide: null))) return false;
                Forget();
                Log.Info($"Windows taskbar put back on the {Describe(record)}; Explorer started again");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Could not put the Windows taskbar back on its own edge", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Early startup is being turned on, or ClearStart is closing with it on and has put the taskbar back. If the
    /// taskbar is on another edge, its edges are written down now, so that the loader puts it on the bottom before it
    /// starts Explorer at the next sign-in (native\TaskbarEdge.h), and ClearStart can put it back later. The taskbar is
    /// not moved now.
    /// </summary>
    public static void RecordForEarlyStartup()
    {
        lock (Gate)
        {
            try
            {
                var entries = ReadEntries();
                // An edge chosen in Settings that is not the bottom is the user's own, as it is now: it replaces an
                // older record. Otherwise a record that is there holds the true edges already.
                bool chosen = LocationOf(entries) != null && !MainOnBottom(entries);
                if (!chosen && ReadRecord() != null) return;
                // As in KeepAtBottom: Windows' record decides for the main taskbar, the windows for the others.
                if (entries.All(entry => entry.Edge == Bottom) || (MainOnBottom(entries) && ExplorerTaskbar.AllOnBottom())) return;
                WriteRecord(entries);
                Log.Info($"The Windows taskbar is on the {Describe(entries)}, where Windows cannot auto-hide it. Its edges " +
                    "are written down: at the next sign-in, the early-startup loader puts it on the bottom before it " +
                    "starts Explorer");
            }
            catch (Exception ex)
            {
                Log.Warn("Could not write down the Windows taskbar's edges for early startup: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Where Explorer's taskbar windows were before they were moved to the bottom, so that each monitor's bar can go
    /// on the edge that monitor's taskbar had. Empty if the taskbar was not moved.
    /// </summary>
    public static List<Native.RECT> OriginalBars()
    {
        var bars = new List<Native.RECT>();
        try
        {
            if (!File.Exists(RecordFile)) return bars;
            foreach (string line in File.ReadAllLines(RecordFile))
            {
                string[] parts = line.Split('\t');
                if (parts.Length == 5 && parts[0] == BarLine
                    && int.TryParse(parts[1], out int left) && int.TryParse(parts[2], out int top)
                    && int.TryParse(parts[3], out int right) && int.TryParse(parts[4], out int bottom))
                {
                    bars.Add(new Native.RECT { Left = left, Top = top, Right = right, Bottom = bottom });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read where the Windows taskbar was: " + ex.Message);
        }
        return bars;
    }

    /// <summary>The edge chosen in Settings, on a Windows that has that choice.</summary>
    private static Entry? LocationOf(List<Entry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Key == LocationKey) return entry;
        }
        return null;
    }

    /// <summary>
    /// True if Windows has the main taskbar down for the bottom: by the edge chosen in Settings where there is one,
    /// which Explorer goes by, and by the older record otherwise.
    /// </summary>
    private static bool MainOnBottom(List<Entry> entries) => LocationOf(entries) is { } location
        ? location.Edge == Bottom
        : entries.All(entry => entry.Key != MainKey || entry.Edge == Bottom);

    private static IEnumerable<Entry> OnBottom(IEnumerable<Entry> entries) =>
        entries.Select(entry => entry with { Edge = Bottom });

    /// <summary>
    /// The edges other than the bottom, the main taskbar's first, as in "top edge". Where there is an edge chosen in
    /// Settings, every taskbar is on that one.
    /// </summary>
    internal static string Describe(List<Entry> entries)
    {
        if (LocationOf(entries) is { } location) return EdgeNames[location.Edge] + " edge";
        var edges = entries.OrderBy(entry => entry.Key == MainKey ? 0 : 1).Select(entry => entry.Edge)
            .Where(edge => edge != Bottom).Distinct().Select(edge => EdgeNames[edge]).ToList();
        return edges.Count == 0 ? "bottom edge" : string.Join(" and ", edges) + " edge";
    }

    /// <summary>The edge Windows has written down for each taskbar, the one chosen in Settings first if there is one.</summary>
    private static List<Entry> ReadEntries()
    {
        var entries = new List<Entry>();
        using (var advanced = Registry.CurrentUser.OpenSubKey(LocationKey))
        {
            if (advanced?.GetValue(LocationValue) is int location && location >= 0 && location <= Bottom)
            {
                entries.Add(new Entry(LocationKey, LocationValue, (byte)location));
            }
        }
        using (var main = Registry.CurrentUser.OpenSubKey(MainKey))
        {
            if (main?.GetValue(MainValue) is byte[] bytes && bytes.Length > EdgeOffset && bytes[EdgeOffset] <= Bottom)
            {
                entries.Add(new Entry(MainKey, MainValue, bytes[EdgeOffset]));
            }
        }
        using (var others = Registry.CurrentUser.OpenSubKey(OtherMonitorsKey))
        {
            foreach (string name in others?.GetValueNames() ?? Array.Empty<string>())
            {
                if (others!.GetValue(name) is byte[] bytes && bytes.Length > EdgeOffset && bytes[EdgeOffset] <= Bottom)
                {
                    entries.Add(new Entry(OtherMonitorsKey, name, bytes[EdgeOffset]));
                }
            }
        }
        return entries;
    }

    /// <param name="autoHide">Also switches auto-hide on (true) in the main value; null leaves it as it is.</param>
    /// <summary>
    /// Writes the auto-hide setting into Windows' record of the main taskbar. Explorer writes its own only as it closes:
    /// one that is ended, crashes or loses power before then leaves the record as it was, and the next Explorer starts
    /// from that.
    /// </summary>
    public static void WriteAutoHide(bool on)
    {
        try
        {
            lock (Gate)
            {
                using var key = Registry.CurrentUser.OpenSubKey(MainKey, writable: true);
                if (key?.GetValue(MainValue) is not byte[] bytes || bytes.Length <= FlagsOffset) return;
                byte flags = on ? (byte)(bytes[FlagsOffset] | AutoHideFlag) : (byte)(bytes[FlagsOffset] & ~AutoHideFlag);
                if (flags == bytes[FlagsOffset]) return;
                bytes[FlagsOffset] = flags;
                key.SetValue(MainValue, bytes, RegistryValueKind.Binary);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not write the taskbar's auto-hide setting into Windows' record: " + ex.Message);
        }
    }

    private static void WriteEdges(IEnumerable<Entry> entries, bool? autoHide)
    {
        foreach (var entry in entries)
        {
            using var key = Registry.CurrentUser.OpenSubKey(entry.Key, writable: true);
            if (entry.Key == LocationKey)
            {
                // Only where Windows keeps that number: it is never made here.
                if (key?.GetValue(entry.Name) is int) key.SetValue(entry.Name, (int)entry.Edge, RegistryValueKind.DWord);
                continue;
            }
            if (key?.GetValue(entry.Name) is not byte[] bytes || bytes.Length <= EdgeOffset) continue;
            bytes[EdgeOffset] = entry.Edge;
            if (autoHide == true && entry.Key == MainKey) bytes[FlagsOffset] |= AutoHideFlag;
            key.SetValue(entry.Name, bytes, RegistryValueKind.Binary);
        }
    }

    /// <summary>
    /// True when Windows' record already holds these edges. Where both have the edge chosen in Settings, that one
    /// decides: Explorer writes the rest from it.
    /// </summary>
    private static bool Matches(List<Entry> record)
    {
        var now = ReadEntries();
        if (LocationOf(record) is { } saved && LocationOf(now) is { } location) return saved.Edge == location.Edge;
        return record.All(entry => now.Any(current =>
            current.Key == entry.Key && current.Name == entry.Name && current.Edge == entry.Edge));
    }

    private static List<Entry>? ReadRecord()
    {
        try
        {
            if (!File.Exists(RecordFile)) return null;
            return ParseRecord(File.ReadAllLines(RecordFile));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the record of the Windows taskbar's edges: " + ex.Message);
            return null;
        }
    }

    /// <summary>The edges in the lines of the record; the lines that hold where a taskbar window was are skipped.</summary>
    internal static List<Entry> ParseRecord(IEnumerable<string> lines)
    {
        var record = new List<Entry>();
        foreach (string line in lines)
        {
            string[] parts = line.Split('\t');
            if (parts.Length == 3 && byte.TryParse(parts[2], out byte edge) && edge <= Bottom
                && (parts[0] == MainKey || parts[0] == OtherMonitorsKey
                    || (parts[0] == LocationKey && parts[1] == LocationValue)))
            {
                record.Add(new Entry(parts[0], parts[1], edge));
            }
        }
        return record;
    }

    internal static string RecordLine(Entry entry) => $"{entry.Key}\t{entry.Name}\t{entry.Edge}";

    /// <summary>Writes down the edges in Windows' record, and where each taskbar window is (see <see cref="OriginalBars"/>).</summary>
    private static void WriteRecord(List<Entry> entries)
    {
        Directory.CreateDirectory(Log.DataDirectory);
        File.WriteAllLines(RecordFile, entries.Select(RecordLine)
            .Concat(ExplorerTaskbar.BarRects().Select(bar =>
                $"{BarLine}\t{bar.Left}\t{bar.Top}\t{bar.Right}\t{bar.Bottom}")));
    }

    /// <summary>
    /// Writes the record if there is none. A record that is there already holds the true edges, from an earlier copy;
    /// Windows adds an entry for a monitor the first time it is connected, and one added since is written down with the
    /// edge it has now, or moving it would lose that edge.
    /// </summary>
    /// <returns>The edges on record now.</returns>
    private static List<Entry> Remember(List<Entry> entries, List<Entry>? record)
    {
        if (record == null)
        {
            WriteRecord(entries);
            return entries;
        }
        var added = entries.Where(entry => !record.Any(saved => saved.Key == entry.Key && saved.Name == entry.Name)).ToList();
        if (added.Count > 0) File.AppendAllLines(RecordFile, added.Select(RecordLine));
        return record.Concat(added).ToList();
    }

    /// <summary>Drops the record, and the run at the next sign-in that would have finished putting it back.</summary>
    private static void Forget()
    {
        File.Delete(RecordFile);
        using var key = Registry.CurrentUser.OpenSubKey(RunOnceKey, writable: true);
        key?.DeleteValue(RunOnceValue, throwOnMissingValue: false);
    }

    private static void RunOnceAtSignIn()
    {
        if (Environment.ProcessPath is not { } exe) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunOnceKey);
        key.SetValue(RunOnceValue, $"\"{exe}\" --restore");
    }

    /// <summary>
    /// For a ClearStart that is ending on an error, with its tray perhaps still up: an Explorer started now would find
    /// that tray in front of its own. A new copy ("--restore") puts the edges back instead, once this one has closed.
    /// </summary>
    public static void PutBackAfterExit()
    {
        if (!Moved || Environment.ProcessPath is not { } exe) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe, "--restore") { UseShellExecute = false })?.Dispose();
            Log.Info("The Windows taskbar goes back on its own edge once this copy of ClearStart has closed");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not start ClearStart again to put the Windows taskbar back on its own edge: " + ex.Message);
        }
    }

    /// <summary>
    /// Writes down which Explorer the early-startup loader started, for <see cref="StartedByLoader"/>: the ClearStart the
    /// loader started is the only one that knows. That is the first Explorer it sees; one started after it is a plain one.
    /// </summary>
    private static void NoteLoaderExplorer(IntPtr primary)
    {
        if (_notedLoaderExplorer) return;
        _notedLoaderExplorer = true;
        try
        {
            Native.GetWindowThreadProcessId(primary, out uint pid);
            if (pid == 0) return;
            using var shell = Process.GetProcessById((int)pid);
            Directory.CreateDirectory(Log.DataDirectory);
            File.WriteAllText(LoaderExplorerFile, $"{pid}\t{shell.StartTime.ToFileTime()}");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not write down which Explorer the early-startup loader started: " + ex.Message);
        }
    }

    /// <summary>
    /// True if <paramref name="primary"/> belongs to the Explorer the early-startup loader started at sign-in, and the
    /// loader is still there. The loader watches that Explorer for as long as it runs and takes its closing for a
    /// failure, so no copy of ClearStart restarts it meanwhile. The loader goes soon after the ClearStart it started
    /// has closed; any other copy waits a few seconds for that ("--restore" right after closing it, as an uninstall does).
    /// </summary>
    private static bool StartedByLoader(IntPtr primary)
    {
        try
        {
            if (!File.Exists(LoaderExplorerFile)) return false;
            string[] parts = File.ReadAllText(LoaderExplorerFile).Trim().Split('\t');
            if (parts.Length != 2 || !uint.TryParse(parts[0], out uint noted) || !long.TryParse(parts[1], out long started))
            {
                return false;
            }
            Native.GetWindowThreadProcessId(primary, out uint pid);
            if (pid == 0 || pid != noted) return false;
            using (var shell = Process.GetProcessById((int)pid))
            {
                if (shell.StartTime.ToFileTime() != started) return false;
            }
            return LoaderRunning(wait: !EarlySession.Active);
        }
        catch
        {
            // Gone, or not readable: not the loader's Explorer as far as anything here can tell.
            return false;
        }
    }

    /// <summary>True while an early-startup loader runs in this session; waits up to <see cref="LoaderWait"/> for it to go.</summary>
    private static bool LoaderRunning(bool wait)
    {
        int session;
        using (var self = Process.GetCurrentProcess()) session = self.SessionId;
        var waited = Stopwatch.StartNew();
        while (true)
        {
            bool running = false;
            foreach (var loader in Process.GetProcessesByName("ClearStart.Loader"))
            {
                using (loader)
                {
                    try
                    {
                        running |= loader.SessionId == session;
                    }
                    catch
                    {
                        // Gone meanwhile.
                    }
                }
            }
            if (!running || !wait || waited.Elapsed >= LoaderWait) return running;
            Thread.Sleep(250);
        }
    }

    /// <summary>Waits until Explorer has run for <see cref="ExplorerTaskbar.Settle"/>. False if <paramref name="stopping"/> turned true.</summary>
    private static bool WaitUntilSettled(IntPtr primary, Func<bool> stopping)
    {
        DateTime started;
        try
        {
            Native.GetWindowThreadProcessId(primary, out uint pid);
            using var shell = Process.GetProcessById((int)pid);
            started = shell.StartTime;
        }
        catch
        {
            return !stopping();
        }

        while (DateTime.Now - started < ExplorerTaskbar.Settle)
        {
            if (stopping()) return false;
            Thread.Sleep(250);
        }
        return !stopping();
    }

    /// <summary>
    /// Closes the Explorer that shows the taskbar, runs <paramref name="whileClosed"/>, and starts Explorer again.
    /// Only that one Explorer: folder windows in Explorers of their own stay as they are.
    /// </summary>
    /// <returns>
    /// True if Explorer was closed and started again; false if it was not touched, or could not be ended (it is left
    /// running).
    /// </returns>
    private static bool Restart(IntPtr primary, Action whileClosed)
    {
        Native.GetWindowThreadProcessId(primary, out uint pid);
        if (pid == 0) return false;
        using var shell = Process.GetProcessById((int)pid);
        long startTime = shell.StartTime.ToFileTime();
        var target = new RM_UNIQUE_PROCESS
        {
            dwProcessId = (int)pid,
            startLow = (uint)startTime,
            startHigh = (uint)(startTime >> 32),
        };

        int error = RmStartSession(out uint session, 0, new StringBuilder(CCH_RM_SESSION_KEY + 1));
        if (error != 0)
        {
            Log.Warn($"Windows would not restart Explorer (Restart Manager error {error})");
            return false;
        }
        try
        {
            error = RmRegisterResources(session, 0, null, 1, new[] { target }, 0, null);
            if (error != 0)
            {
                Log.Warn($"Windows would not restart Explorer (Restart Manager error {error})");
                return false;
            }

            DateTime closed = DateTime.Now;
            error = ShutDown(session);
            if (error != 0)
            {
                // Asked to close, Explorer may be half-way there: its taskbar gone and its windows no longer answering.
                // Ended and started afresh it is whole again, and then it has not written its own edges either.
                Log.Warn($"Explorer did not close when asked (Restart Manager error {error}); ending it and starting it again");
                if (!End(shell)) return false;
                try
                {
                    whileClosed();
                }
                finally
                {
                    // Windows starts its shell again by itself when it ends this way; otherwise it is started here.
                    BringBack(closed, notBack: null);
                }
                return true;
            }

            closed = DateTime.Now;
            try
            {
                whileClosed();
            }
            finally
            {
                error = RmRestart(session, 0, IntPtr.Zero);
                // Even after an error: an Explorer that started anyway must not get a second one.
                BringBack(closed, $"Explorer did not start again by itself (Restart Manager error {error}); starting it");
            }
            return true;
        }
        finally
        {
            RmEndSession(session);
        }
    }

    /// <summary>
    /// Asks Explorer to close, the way Windows does for an update, and waits for it at most <see cref="CloseWait"/>.
    /// The asking runs on a thread of its own: waiting for that thread keeps this one answering the messages
    /// Explorer sends as it closes, which it waits for when this is ClearStart's window thread.
    /// </summary>
    /// <returns>Zero once Explorer has closed; otherwise an error code.</returns>
    private static int ShutDown(uint session)
    {
        int error = ERROR_TIMEOUT;
        var asking = new Thread(() => error = RmShutdown(session, 0, IntPtr.Zero))
        {
            IsBackground = true,
            Name = "Explorer restart",
        };
        asking.Start();
        if (asking.Join(CloseWait)) return error;

        RmCancelCurrentTask(session);
        asking.Join(TimeSpan.FromSeconds(1));
        return ERROR_TIMEOUT;
    }

    /// <summary>Ends Explorer outright. False if it is still running.</summary>
    private static bool End(Process shell)
    {
        try
        {
            if (!shell.HasExited) shell.Kill();
            if (shell.WaitForExit(5000)) return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not end Explorer: " + ex.Message);
        }
        Log.Warn("Explorer is still running; its taskbar is left as it is");
        return false;
    }

    /// <summary>
    /// After Explorer was closed at <paramref name="closed"/>: starts it if it has not started again by itself, and waits
    /// for its taskbar. Now and then a new Explorer puts up the desktop but never its taskbar, and stays that way; that
    /// one is ended and Explorer started afresh, once.
    /// The waiting runs on a thread of its own, like <see cref="ShutDown"/>'s: waiting for that thread keeps this one
    /// answering the messages the new Explorer sends as it starts.
    /// </summary>
    /// <param name="notBack">Logged if Explorer has to be started here, or null when that is to be expected.</param>
    private static void BringBack(DateTime closed, string? notBack)
    {
        var waiting = new Thread(() =>
        {
            try
            {
                if (!CameBack(closed))
                {
                    if (notBack != null) Log.Warn(notBack);
                    StartExplorer();
                }
                if (TaskbarCameBack(closed)) return;

                // The desktop's window is the shell's: an Explorer with no taskbar but that window is the stuck one.
                Native.GetWindowThreadProcessId(GetShellWindow(), out uint pid);
                using var shell = pid == 0 ? null : Process.GetProcessById((int)pid);
                if (shell != null && shell.StartTime < closed)
                {
                    Log.Warn("Explorer did not put its taskbar up again; it is left as it is");
                    return;
                }
                Log.Warn("Explorer started again without its taskbar; ending it and starting it once more");
                DateTime again = DateTime.Now;
                if (shell != null && !End(shell)) return;
                if (!CameBack(again)) StartExplorer();
                if (!TaskbarCameBack(again)) Log.Warn("Explorer has still not put its taskbar up");
            }
            catch (Exception ex)
            {
                Log.Error("Could not check that Explorer started again with its taskbar", ex);
            }
        })
        {
            IsBackground = true,
            Name = "Explorer start",
        };
        waiting.Start();
        waiting.Join();
    }

    /// <summary>True once an Explorer started since <paramref name="since"/> has put up its taskbar, waiting for it.</summary>
    private static bool TaskbarCameBack(DateTime since)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TaskbarWait)
        {
            IntPtr taskbar = ExplorerTaskbar.FindPrimary();
            Native.GetWindowThreadProcessId(taskbar, out uint pid);
            if (pid != 0)
            {
                try
                {
                    using var shell = Process.GetProcessById((int)pid);
                    if (shell.StartTime >= since) return true;
                }
                catch
                {
                    // Gone again: wait for the next one.
                }
            }
            Thread.Sleep(250);
        }
        return false;
    }

    /// <summary>True once an Explorer has started since <paramref name="closed"/>, waiting a few seconds for it.</summary>
    private static bool CameBack(DateTime closed)
    {
        for (int i = 0; i < 20; i++)
        {
            foreach (var process in Process.GetProcessesByName("explorer"))
            {
                using (process)
                {
                    try
                    {
                        if (process.StartTime >= closed) return true;
                    }
                    catch
                    {
                        // Gone again, or not ours to look at.
                    }
                }
            }
            Thread.Sleep(250);
        }
        return false;
    }

    private static void StartExplorer()
    {
        try
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Could not start Explorer again", ex);
        }
    }

    private const int CCH_RM_SESSION_KEY = 32;
    private const int ERROR_TIMEOUT = 1460;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        // The process's start time, as a FILETIME: two halves, so the struct keeps Windows' layout.
        public uint startLow;
        public uint startHigh;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint fileCount, string[]? files, uint processCount,
        RM_UNIQUE_PROCESS[] processes, uint serviceCount, string[]? services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmShutdown(uint session, uint flags, IntPtr progress);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmRestart(uint session, int reserved, IntPtr progress);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmCancelCurrentTask(uint session);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags,
        uint timeout, out IntPtr result);
}
