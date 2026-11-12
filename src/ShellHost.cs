using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ManagedShell;
using ManagedShell.AppBar;
using ManagedShell.Common.Enums;
using ManagedShell.Common.Logging;
using ManagedShell.Common.Logging.Observers;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;
using Microsoft.Win32;

namespace ClearStart;

internal enum HostMode
{
    /// <summary>The real thing: hides the Windows taskbar, hosts the tray, takes the Win key.</summary>
    Live,
    /// <summary>Shows our bars alongside the Windows taskbar; hides and takes over nothing.</summary>
    Preview,
    /// <summary>No windows on the desktop at all; used to render the UI to image files.</summary>
    SelfTest,
}

/// <summary>What a change to the settings touches, so only that much is redone.</summary>
[Flags]
internal enum SettingsChange
{
    None = 0,
    /// <summary>The taskbars' transparency, backdrop and colours.</summary>
    Look = 1,
    IconSizes = 2,
    /// <summary>Whether the bars can be resized by dragging.</summary>
    Lock = 4,
    Weather = 8,
    /// <summary>Anything about the start menu; it is built again the next time it opens.</summary>
    StartMenu = 16,
    /// <summary>Something the bars only pick up by being reopened.</summary>
    Bars = 32,
    /// <summary>Whether ClearStart checks for new versions, and installs them, by itself.</summary>
    Updates = 64,
}

/// <summary>
/// Owns everything that runs for the lifetime of the app: the shell services, one taskbar per
/// monitor, the start menu and the Win key hook. Its job is to keep those alive through display
/// changes and settings edits, and to put Windows back the way it was on the way out.
/// </summary>
internal sealed class ShellHost : IDisposable
{
    private readonly List<TaskbarWindow> _bars = new();
    // Which edge the Windows taskbar is on, per monitor: that is where a bar set to "auto" goes.
    private readonly Dictionary<string, BarEdge> _windowsEdges = new(StringComparer.OrdinalIgnoreCase);
    // The monitors that are meant to have a bar, and the one among them whose bar is the main one.
    private readonly HashSet<IntPtr> _barMonitors = new();
    private string? _mainDevice;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _screenTimer;
    private readonly DispatcherTimer _saveTimer;
    private StartMenuWindow? _startMenu;
    private ThumbnailWindow? _previews;
    private SettingsWindow? _settingsWindow;
    // A change from the settings window that needs the bars reopened, once the changes stop coming.
    private bool _rebuildPending;
    private WinKeyHook? _winKey;
    private FileLog? _shellLog;
    private string _screenSignature = "";
    private int _rebuildRetries;
    private bool _started;
    private bool _trayStarted;
    private bool _disposed;

    public ShellHost(HostMode mode, string? logDirectory = null)
    {
        Mode = mode;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Updates = new UpdateService(mode);

        AttachShellLog(logDirectory ?? Log.DefaultDirectory);

        var config = ShellManager.DefaultShellConfig;
        config.EnableTasksService = true;
        config.AutoStartTasksService = true;
        config.MultiMonAwareTasksService = true;
        config.TaskIconSize = IconSize.Large;
        bool hostTray = mode == HostMode.Live;
        config.EnableTrayService = hostTray;
        // Not yet: StartTray does it, once the bars are up.
        config.AutoStartTrayService = false;
        // The tray icons kept on the bar; the rest wait behind the arrow beside them.
        config.PinnedNotifyIcons = Settings.Current.Taskbar.PinnedTrayIcons.ToArray();
        Shell = new ShellManager(config);

        // One shared, ungrouped list of windows for every bar.
        TaskView = Shell.Tasks.GroupedWindows ?? Shell.Tasks.CreateGroupedWindowsCollection();
        TaskView.GroupDescriptions?.Clear();
        TaskView.Filter = item => item is ApplicationWindow window && window.ShowInTaskbar;
        if (TaskView is ICollectionViewLiveShaping live && live.CanChangeLiveFiltering)
        {
            if (!live.LiveFilteringProperties.Contains(nameof(ApplicationWindow.ShowInTaskbar)))
            {
                live.LiveFilteringProperties.Add(nameof(ApplicationWindow.ShowInTaskbar));
            }
            live.IsLiveFiltering = true;
        }

        Pins.Reload();

        // Ask for the open windows' icons before the app list asks for its few hundred: they all load
        // through one queue, and at the back of it the taskbar can sit without icons for many seconds.
        try
        {
            foreach (var item in TaskView)
            {
                if (item is ApplicationWindow window) _ = window.Icon;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not start loading the window icons", ex);
        }

        // The self-test reports on the app list straight away. Otherwise it loads once the bars are on screen
        // (see AfterBarsShown): with its few hundred icons it is the slowest part of starting, and only the
        // start menu needs it.
        if (mode == HostMode.SelfTest) Apps.Load();

        _screenTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _screenTimer.Tick += (_, _) =>
        {
            _screenTimer.Stop();
            CheckScreens();
        };

        // A slider in the settings window changes a setting many times a second; the file is written once
        // the changes stop coming.
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _saveTimer.Tick += (_, _) => FlushEdits();
    }

    public HostMode Mode { get; }

    public ShellManager Shell { get; }

    public ICollectionView TaskView { get; }

    public PinStore Pins { get; } = new();

    public AppCatalog Apps { get; } = new();

    public WeatherService Weather { get; } = new();

    public NetworkWatcher Network { get; } = new();

    /// <summary>Looks for new versions of ClearStart. It waits a few minutes after the start before it asks.</summary>
    public UpdateService Updates { get; }

    /// <summary>The name the start menu shows above its list of places. The screenshot mode puts a made-up one here.</summary>
    public string UserName { get; set; } = Environment.UserName;

    public int BarCount => _bars.Count;

    public void Start()
    {
        if (_started) return;
        _started = true;

        if (Mode == HostMode.Live)
        {
            // Normally running already: the program starts it before anything else loads. It keeps the Windows
            // taskbar hidden from now until ClearStart closes, on a thread of its own.
            TaskbarGuard.Start();
            // Which edge each Windows taskbar is docked to. A hidden taskbar still has its place on the screen.
            FindWindowsTaskbars();
        }

        // Before the bars, so they open with the right network glyph. (The self-test never gets here: it
        // builds its bars itself, and asks nothing of the weather service.)
        Network.Start();
        RebuildBars();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Settings.Changed += OnSettingsChanged;
        UpdateWinKeyHook();

        Log.Info($"Started in {Mode} mode with {_bars.Count} taskbar(s)");

        // Queued behind the drawing of the bars, so it runs when they are on screen.
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(AfterBarsShown));
    }

    /// <summary>Everything the bars do not need in order to appear, started once they are on screen.</summary>
    private void AfterBarsShown()
    {
        if (_disposed) return;
        Log.Info($"Taskbars on screen, {StartTiming.Describe()}");

        if (Mode == HostMode.Live) StartTray();
        Weather.Start();
        Updates.Start();

        // Once the bars have nothing else to do. Opening the start menu before then loads it on the spot.
        _dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (_disposed) return;
            Apps.Load();
            Log.Info($"App list read ({Apps.Apps.Count} apps so far), {StartTiming.Describe()}");
        }));
        if (Mode == HostMode.Live)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                // Off this thread: it asks Task Scheduler, which can take a moment.
                if (!_disposed) ThreadPool.QueueUserWorkItem(_ => AutoStart.MoveToTask());
            }));
        }
    }

    /// <summary>
    /// Starts hosting the tray. That tells every app a new taskbar is here, and each one answers by handing
    /// over its icon, giving up after about four seconds. So it waits until the bars are up and this thread
    /// is free to answer: started while they are still being built, it loses most of the icons for good.
    /// </summary>
    private void StartTray()
    {
        if (_disposed || _trayStarted || Shell.NotificationArea is not { } area) return;
        _trayStarted = true;
        try
        {
            area.Initialize();
            if (area.IsFailed)
            {
                Log.Warn("The tray could not be started; the bars will have no tray icons");
                return;
            }

            // The tray's two lists, without the icons that do not belong in it.
            area.PinnedIcons.Filter = item => item is NotifyIcon { IsPinned: true } icon && BelongsInTray(icon);
            area.UnpinnedIcons.Filter = item => item is NotifyIcon { IsPinned: false } icon && BelongsInTray(icon);

            // The tray's own window exists only now: say again where the main bar is.
            foreach (var bar in _bars) bar.ReportTrayPosition();
        }
        catch (Exception ex)
        {
            Log.Error("Could not start the tray", ex);
            return;
        }

        // How many apps answered, for the log: the number to look at when icons are missing.
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_disposed) return;
            int shown = area.TrayIcons.Count(BelongsInTray);
            int onBar = area.TrayIcons.Count(icon => icon.IsPinned && BelongsInTray(icon));
            int leftOut = area.TrayIcons.Count(icon => !icon.IsHidden && !BelongsInTray(icon));
            Log.Info($"Tray icons: {shown}, {onBar} of them on the taskbar"
                + (leftOut > 0 ? $"; {leftOut} of Windows' own left out" : ""));
        };
        timer.Start();
    }

    // Windows' own volume and network icons.
    private static readonly Guid[] WindowsOwnIcons =
    {
        new(NotificationArea.VOLUME_GUID),
        new(NotificationArea.NETWORK_GUID),
    };

    /// <summary>
    /// False for an icon its app has hidden, and for Windows' own volume and network icons while the
    /// network and volume button is on the bars: that button does their job already.
    /// </summary>
    private static bool BelongsInTray(NotifyIcon icon) =>
        !icon.IsHidden
        && !(Settings.Current.Taskbar.ShowQuickSettingsButton && Array.IndexOf(WindowsOwnIcons, icon.GUID) >= 0);

    /// <summary>Looks again at which icons belong in the tray, after a setting it goes by may have changed.</summary>
    private void RefreshTrayLists()
    {
        if (Shell.NotificationArea is not { } area) return;
        area.PinnedIcons?.Refresh();
        area.UnpinnedIcons?.Refresh();
    }

    // ------------------------------------------------------------------ start menu

    /// <summary>All bars accompany the start menu, including bars behind fullscreen windows.</summary>
    internal void SetStartMenuVisible(bool visible)
    {
        foreach (var bar in _bars) bar.SetStartMenuVisible(visible);
    }

    /// <param name="monitor">Monitor to open on; zero = the one the mouse is on.</param>
    public void ToggleStartMenu(IntPtr monitor)
    {
        try
        {
            if (_disposed) return;
            HidePreview();
            if (monitor == IntPtr.Zero) monitor = Native.MonitorFromCursor();
            if (_startMenu is { IsClosed: true }) _startMenu = null;
            // Normally loaded by now; if the menu is opened in the first moments, load it here.
            Apps.Load();
            _startMenu ??= new StartMenuWindow(this);
            _startMenu.Toggle(monitor);
        }
        catch (Exception ex)
        {
            Log.Error("Could not toggle the start menu", ex);
            // Start over with a fresh window next time.
            try { _startMenu?.Close(); } catch { /* already broken */ }
            _startMenu = null;
        }
    }

    // ------------------------------------------------------------------ window previews

    /// <summary>The mouse came to rest on a running app's button: open its window previews after a moment.</summary>
    public void HoverPreview(AppButton app, PreviewAnchor anchor) => WithPreviews(p => p.Hover(app, anchor), create: true);

    /// <summary>The mouse left the button, or pressed it, before the previews opened.</summary>
    public void CancelPreviewHover() => WithPreviews(p => p.CancelHover(), create: false);

    /// <summary>Open an app's window previews now.</summary>
    public void ShowPreview(AppButton app, PreviewAnchor anchor) => WithPreviews(p => p.ShowNow(app, anchor), create: true);

    public void HidePreview() => WithPreviews(p => p.HidePreview(), create: false);

    /// <summary>The app whose window previews are showing right now, if any.</summary>
    public AppButton? PreviewedApp => _previews is { IsClosed: false } previews ? previews.ShownApp : null;

    private void WithPreviews(Action<ThumbnailWindow> action, bool create)
    {
        try
        {
            if (_disposed) return;
            if (_previews is { IsClosed: true }) _previews = null;
            if (_previews == null)
            {
                // The self-test draws its pictures off-screen and must never put a popup on the desktop.
                if (!create || Mode == HostMode.SelfTest) return;
                _previews = new ThumbnailWindow();
            }
            action(_previews);
        }
        catch (Exception ex)
        {
            Log.Error("Window previews failed", ex);
            // Start over with a fresh popup next time.
            try { _previews?.Close(); } catch { /* already broken */ }
            _previews = null;
        }
    }

    private void ClosePreviews()
    {
        try
        {
            _previews?.Close();
        }
        catch (Exception ex)
        {
            Log.Error("Could not close the window previews", ex);
        }
        _previews = null;
    }

    private void UpdateWinKeyHook()
    {
        bool want = Mode == HostMode.Live && Settings.Current.StartMenu.TakeOverWinKey;
        if (want && _winKey == null)
        {
            _winKey = new WinKeyHook(() => _dispatcher.InvokeAsync(() => ToggleStartMenu(IntPtr.Zero)));
            if (_winKey.Start())
            {
                Log.Info("Win key hook installed");
            }
        }
        else if (!want && _winKey != null)
        {
            _winKey.Dispose();
            _winKey = null;
            Log.Info("Win key hook removed");
        }
    }

    // ------------------------------------------------------------------ taskbars and screens

    /// <summary>
    /// Called whenever something about the displays may have changed. Changes arrive in bursts
    /// (a monitor waking up can fire a dozen notifications), so wait for them to settle first.
    /// </summary>
    public void QueueScreenCheck()
    {
        if (_disposed) return;
        _screenTimer.Stop();
        _screenTimer.Start();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _dispatcher.InvokeAsync(QueueScreenCheck);

    private static string ScreenSignature()
    {
        try
        {
            return string.Join("|", AppBarScreen.FromAllScreens()
                .Select(s => $"{s.DeviceName}:{s.Bounds.X},{s.Bounds.Y},{s.Bounds.Width},{s.Bounds.Height}:{s.Primary}"));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not enumerate screens: " + ex.Message);
            return "";
        }
    }

    private void CheckScreens()
    {
        if (_disposed || !_started) return;
        try
        {
            string signature = ScreenSignature();
            if (signature != _screenSignature || _bars.Count != ExpectedBarCount())
            {
                Log.Info("Display layout changed; rebuilding taskbars");
                RebuildBars();
            }
            else
            {
                foreach (var bar in _bars)
                {
                    bar.UpdatePosition();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Screen check failed", ex);
            RetryRebuildLater();
        }
    }

    private static int ExpectedBarCount()
    {
        try
        {
            return ScreensWithBars(AppBarScreen.FromAllScreens(), Settings.Current.Taskbar).Count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// The monitors that get a bar, in the order Windows lists them. If the settings ask for none at all,
    /// the main display keeps its bar: with no bar anywhere there would be no way back to the settings.
    /// </summary>
    internal static List<AppBarScreen> ScreensWithBars(IEnumerable<AppBarScreen> screens, TaskbarSettings taskbar)
    {
        var all = screens.ToList();
        var wanted = all.Where(s => taskbar.WantsBarOn(s.DeviceName, s.Primary)).ToList();
        if (wanted.Count == 0)
        {
            var keep = all.FirstOrDefault(s => s.Primary) ?? all.FirstOrDefault();
            if (keep != null) wanted.Add(keep);
        }
        return wanted;
    }

    /// <summary>Works out which monitors get a bar and which of those bars is the main one.</summary>
    private List<AppBarScreen> PlanBars()
    {
        var wanted = ScreensWithBars(AppBarScreen.FromAllScreens(), Settings.Current.Taskbar);
        _mainDevice = (wanted.FirstOrDefault(s => s.Primary) ?? wanted.FirstOrDefault())?.DeviceName;
        _barMonitors.Clear();
        foreach (var screen in wanted) _barMonitors.Add(screen.HMonitor);
        return wanted;
    }

    /// <summary>
    /// The main bar is the main display's, or the first one there is when that display has none. It is the
    /// one that carries the tray, clock and weather when those are not on every bar, and it lists the
    /// windows of the displays that have no bar of their own.
    /// </summary>
    public bool IsMainBar(AppBarScreen screen) =>
        _mainDevice == null ? screen.Primary : SameDevice(screen.DeviceName, _mainDevice);

    /// <summary>False for a monitor that has been left without a bar. (True for all before any bars are planned.)</summary>
    public bool HasBarOn(IntPtr monitor) => _barMonitors.Count == 0 || _barMonitors.Contains(monitor);

    /// <summary>The connected displays whose bar the user has hidden.</summary>
    public IReadOnlyList<string> HiddenBars()
    {
        try
        {
            var taskbar = Settings.Current.Taskbar;
            return AppBarScreen.FromAllScreens()
                .Where(s => taskbar.HiddenOn(s.DeviceName) && !HasBarOn(s.HMonitor))
                .Select(s => s.DeviceName)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not list the hidden taskbars: " + ex.Message);
            return Array.Empty<string>();
        }
    }

    /// <summary>True when some other display would still have a bar with this one's hidden.</summary>
    public bool CanHideBar(string device)
    {
        try
        {
            var taskbar = Settings.Current.Taskbar;
            return AppBarScreen.FromAllScreens()
                .Any(s => !SameDevice(s.DeviceName, device) && taskbar.WantsBarOn(s.DeviceName, s.Primary));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Takes the bar off one display, or puts it back, and remembers the choice.</summary>
    public void SetBarHidden(string device, bool hidden)
    {
        var taskbar = Settings.Current.Taskbar;
        if (taskbar.HiddenOn(device) == hidden) return;
        if (hidden && !CanHideBar(device))
        {
            Log.Info($"Taskbar on {TaskbarSettings.MonitorKey(device)} stays: it is the only one");
            return;
        }

        taskbar.EnsureMonitor(device).Hidden = hidden ? true : null;
        SaveSettings();
        Log.Info($"Taskbar on {TaskbarSettings.MonitorKey(device)}: {(hidden ? "hidden" : "shown again")}");
        _dispatcher.BeginInvoke(new Action(SyncBars));
    }

    /// <summary>
    /// Closes the bars of displays that should no longer have one and opens the missing ones, leaving the
    /// rest alone. If a different bar becomes the main one, they are all reopened: what a bar carries
    /// (tray, clock, weather) is decided when it is made.
    /// </summary>
    private void SyncBars()
    {
        if (_disposed || !_started) return;
        CloseStartMenu();
        try
        {
            string? mainBefore = _mainDevice;
            var wanted = PlanBars();
            if (SameDevice(mainBefore, _mainDevice))
            {
                foreach (var bar in _bars.Where(b => !wanted.Any(s => SameDevice(s.DeviceName, b.Device))).ToList())
                {
                    _bars.Remove(bar);
                    CloseBar(bar);
                }

                if (Mode != HostMode.Live) FindWindowsTaskbars();
                for (int i = 0; i < wanted.Count; i++)
                {
                    var screen = wanted[i];
                    if (_bars.Any(b => SameDevice(b.Device, screen.DeviceName))) continue;
                    _bars.Insert(Math.Min(i, _bars.Count), OpenBar(screen));
                }

                // The main bar lists the windows of displays without a bar, and that set has just changed.
                foreach (var bar in _bars) bar.RefreshApps();
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not show or hide a taskbar", ex);
        }
        RebuildBars();
    }

    /// <summary>
    /// Notes which edge of each monitor the Windows taskbar is on. While it is visible (preview) the
    /// answer is refreshed every time; once we have hidden it, only monitors not seen before are added.
    /// </summary>
    private void FindWindowsTaskbars()
    {
        try
        {
            foreach (var screen in AppBarScreen.FromAllScreens())
            {
                if (Mode == HostMode.Live && _started && _windowsEdges.ContainsKey(screen.DeviceName)) continue;

                var bounds = screen.Bounds;
                var edge = ExplorerTaskbar.EdgeOn(new Native.RECT
                {
                    Left = bounds.Left,
                    Top = bounds.Top,
                    Right = bounds.Right,
                    Bottom = bounds.Bottom,
                });
                if (edge != null) _windowsEdges[screen.DeviceName] = edge.Value;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not look up the Windows taskbar positions: " + ex.Message);
        }
    }

    /// <summary>The edge a monitor's bar goes on: the user's choice, or else where the Windows taskbar is.</summary>
    private AppBarEdge ResolveEdge(string device)
    {
        var edge = Settings.Current.Taskbar.EdgeFor(device);
        if (edge == BarEdge.Auto)
        {
            edge = _windowsEdges.TryGetValue(device, out var found) ? found : BarEdge.Bottom;
        }
        return edge switch
        {
            BarEdge.Top => AppBarEdge.Top,
            BarEdge.Left => AppBarEdge.Left,
            BarEdge.Right => AppBarEdge.Right,
            _ => AppBarEdge.Bottom,
        };
    }

    /// <summary>Where a monitor's bar would be docked right now (for the self-test report).</summary>
    public AppBarEdge PlannedEdge(string device)
    {
        if (_windowsEdges.Count == 0) FindWindowsTaskbars();
        return ResolveEdge(device);
    }

    private static bool IsSideBar(AppBarEdge edge) => edge is AppBarEdge.Left or AppBarEdge.Right;

    private TaskbarWindow OpenBar(AppBarScreen screen)
    {
        var edge = ResolveEdge(screen.DeviceName);
        double size = Settings.Current.Taskbar.SizeFor(screen.DeviceName, IsSideBar(edge));
        var bar = new TaskbarWindow(this, screen, edge, size);
        bar.Show();
        bar.SetStartMenuVisible(_startMenu?.IsVisible == true);
        Log.Info($"Taskbar on {TaskbarSettings.MonitorKey(screen.DeviceName)}: {edge} edge, {size} thick");
        return bar;
    }

    private void RebuildBars()
    {
        CloseBars();
        FindWindowsTaskbars();
        RefreshTrayLists();

        _screenSignature = ScreenSignature();
        int failures = 0;

        foreach (var screen in PlanBars())
        {
            try
            {
                _bars.Add(OpenBar(screen));
            }
            catch (Exception ex)
            {
                failures++;
                Log.Error($"Could not open a taskbar on {screen.DeviceName}", ex);
            }
        }

        if (failures > 0 || _bars.Count == 0)
        {
            RetryRebuildLater();
        }
        else
        {
            _rebuildRetries = 0;
        }
        if (Mode == HostMode.Live && _bars.Count > 0)
        {
            // A display can wake after the initial build failed. Every successful retry gets this check;
            // EarlySession only signals once, after the queued render work has completed.
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!_disposed && _bars.Count > 0) EarlySession.BarsReady();
            }));
        }
    }

    /// <summary>A display that is still waking up can make a rebuild fail; try again shortly, a limited number of times.</summary>
    private void RetryRebuildLater()
    {
        if (_disposed || _rebuildRetries >= 10) return;
        _rebuildRetries++;
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_disposed) return;
            Log.Info($"Retrying taskbar rebuild (attempt {_rebuildRetries})");
            _screenSignature = "";
            CheckScreens();
        };
        timer.Start();
    }

    private void CloseBars()
    {
        foreach (var bar in _bars)
        {
            CloseBar(bar);
        }
        _bars.Clear();
    }

    private static void CloseBar(TaskbarWindow bar)
    {
        try
        {
            bar.AllowClose = true;
            bar.Close();
        }
        catch (Exception ex)
        {
            Log.Error("Could not close a taskbar", ex);
        }
    }

    /// <summary>
    /// Docks one monitor's bar again after its edge, thickness or Start button changed, leaving the others alone.
    /// The bar stays open: closing and reopening it is what made the Windows taskbar flash up during a move.
    /// </summary>
    private void ReplaceBar(string device)
    {
        if (_disposed) return;
        CloseStartMenu();
        try
        {
            int index = _bars.FindIndex(b => SameDevice(b.Device, device));
            var screen = AppBarScreen.FromAllScreens().FirstOrDefault(s => SameDevice(s.DeviceName, device));
            // A display without a bar (hidden, or not the main one when only that has a bar): the choice is
            // saved and waits there for the bar to come back.
            if (index < 0 && (screen == null || !HasBarOn(screen.HMonitor))) return;
            if (index >= 0 && screen != null)
            {
                if (Mode != HostMode.Live) FindWindowsTaskbars();
                var bar = _bars[index];
                var edge = ResolveEdge(screen.DeviceName);
                double size = Settings.Current.Taskbar.SizeFor(screen.DeviceName, IsSideBar(edge));
                try
                {
                    bar.MoveTo(edge, size);
                    Log.Info($"Taskbar on {TaskbarSettings.MonitorKey(screen.DeviceName)}: {edge} edge, {size} thick");
                }
                catch (Exception ex)
                {
                    Log.Error("Could not move the taskbar in place; opening it again", ex);
                    _bars.RemoveAt(index);
                    CloseBar(bar);
                    _bars.Insert(index, OpenBar(screen));
                }
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not reopen the taskbar on {device}", ex);
        }
        RebuildBars();
    }

    private static bool SameDevice(string? a, string? b) =>
        string.Equals(TaskbarSettings.MonitorKey(a), TaskbarSettings.MonitorKey(b), StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ what the bars ask for

    /// <summary>The name Windows gives a monitor ("\\.\DISPLAY1"), or null if no bar is on it.</summary>
    public string? DeviceOf(IntPtr monitor) => BarOn(monitor)?.Device;

    /// <summary>Which edge the bar on a monitor is docked to; the start menu opens next to it.</summary>
    public AppBarEdge BarEdgeOn(IntPtr monitor) => BarOn(monitor)?.Edge ?? AppBarEdge.Bottom;

    /// <summary>The bar on a monitor, or the main bar when that monitor has none of its own.</summary>
    private TaskbarWindow? BarOn(IntPtr monitor) =>
        _bars.FirstOrDefault(b => b.Monitor == monitor)
        ?? _bars.FirstOrDefault(b => SameDevice(b.Device, _mainDevice))
        ?? _bars.FirstOrDefault();

    /// <summary>Moves one monitor's bar to another edge and remembers the choice.</summary>
    public void MoveBar(string device, BarEdge edge)
    {
        var taskbar = Settings.Current.Taskbar;
        bool wasSideBar = IsSideBar(ResolveEdge(device));

        var monitor = taskbar.EnsureMonitor(device);
        monitor.Edge = edge == taskbar.Edge ? null : edge;
        // A thickness chosen for a bar that lies flat means nothing once it stands on its side.
        if (IsSideBar(ResolveEdge(device)) != wasSideBar) monitor.Size = null;

        SaveSettings();
        Log.Info($"Taskbar on {TaskbarSettings.MonitorKey(device)} moved to: {edge}");
        _dispatcher.BeginInvoke(() => ReplaceBar(device));
    }

    /// <summary>Puts the Start button of one monitor's bar somewhere else.</summary>
    public void SetStartButton(string device, StartButtonPlacement placement)
    {
        var taskbar = Settings.Current.Taskbar;
        taskbar.EnsureMonitor(device).StartButton = placement == taskbar.StartButton ? null : placement;
        SaveSettings();
        _dispatcher.BeginInvoke(() => ReplaceBar(device));
    }

    /// <summary>Puts the Start button in the same place on every bar.</summary>
    public void SetStartButtonEverywhere(StartButtonPlacement placement)
    {
        var taskbar = Settings.Current.Taskbar;
        taskbar.StartButton = placement;
        foreach (var monitor in taskbar.Monitors.Values)
        {
            monitor.StartButton = null;
        }
        SaveSettings();
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            CloseStartMenu();
            RebuildBars();
        });
    }

    // ------------------------------------------------------------------ pinned apps, bar by bar
    //
    // "main" in these says whether the monitor's bar is the main one: the bar that keeps the pinned apps
    // when they are not on every bar.

    /// <summary>The pinned apps on every bar, or only on the main one.</summary>
    public void SetPinsOnAllBars(bool everyBar)
    {
        var taskbar = Settings.Current.Taskbar;
        taskbar.PinsOnAllMonitors = everyBar;
        // A bar that was told to show all or none goes by the new choice; one with pins chosen for it keeps them.
        foreach (var monitor in taskbar.Monitors.Values)
        {
            if (monitor.Pins != PinsShown.Chosen) monitor.Pins = null;
        }
        PinsShownChanged();
        Log.Info("Pinned apps: " + (everyBar ? "on every taskbar" : "on the main taskbar only"));
    }

    /// <summary>Chooses which of the pinned apps one monitor's bar shows.</summary>
    public void SetPinsShown(string device, bool main, PinsShown shown)
    {
        var taskbar = Settings.Current.Taskbar;
        var monitor = taskbar.EnsureMonitor(device);
        // The chosen ones start out as what the bar shows now, so nothing moves until one is switched on or off.
        if (shown == PinsShown.Chosen) monitor.ChosenPins ??= PinsShownOn(device, main);
        monitor.Pins = shown == taskbar.GeneralPins(main) ? null : shown;
        PinsShownChanged();
        Log.Info($"Pinned apps on {TaskbarSettings.MonitorKey(device)}: {shown}");
    }

    /// <summary>
    /// Puts one pinned app on a monitor's bar or takes it off. From then on that bar shows only the pinned
    /// apps chosen for it: the ones it showed before, with this one added or left out.
    /// </summary>
    public void ShowPinOn(string device, bool main, string pin, bool show)
    {
        var taskbar = Settings.Current.Taskbar;
        if (taskbar.ShowsPin(device, main, pin) == show) return;

        var chosen = PinsShownOn(device, main);
        chosen.RemoveAll(path => string.Equals(path, pin, StringComparison.OrdinalIgnoreCase));
        if (show) chosen.Add(pin);

        var monitor = taskbar.EnsureMonitor(device);
        monitor.ChosenPins = chosen;
        monitor.Pins = PinsShown.Chosen;
        PinsShownChanged();
    }

    /// <summary>Pins an app from one monitor's bar, and sees to it that this bar is one that shows the pin.</summary>
    public void PinToTaskbar(string device, bool main, string path)
    {
        Pins.PinToTaskbar(path);
        if (Pins.IsPinnedToTaskbar(path)) ShowPinOn(device, main, path, true);
    }

    /// <summary>The pinned apps a monitor's bar shows now, named the way the settings file names them.</summary>
    private List<string> PinsShownOn(string device, bool main)
    {
        var taskbar = Settings.Current.Taskbar;
        return Pins.Taskbar.Select(pin => pin.Path).Where(path => taskbar.ShowsPin(device, main, path)).ToList();
    }

    private void PinsShownChanged()
    {
        SaveSettings();
        foreach (var bar in _bars) bar.RefreshApps();
    }

    /// <summary>
    /// The same as Win+D: puts every window away to show the desktop, and brings them back the next time.
    /// </summary>
    public void ShowDesktop()
    {
        HidePreview();
        var type = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new InvalidOperationException("Windows shell automation is not available");
        object shell = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Windows shell automation did not start");
        try
        {
            type.InvokeMember("ToggleDesktop", BindingFlags.InvokeMethod, null, shell, null);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>Opens the settings window, or brings it forward; on the given page if one is named.</summary>
    public void ShowSettings(SettingsPage? page = null)
    {
        // The self-test draws the pages off-screen and must never open a window on the desktop.
        if (_disposed || Mode == HostMode.SelfTest) return;
        try
        {
            HidePreview();
            if (_settingsWindow == null)
            {
                var window = new SettingsWindow(this);
                window.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_settingsWindow, window)) _settingsWindow = null;
                    FlushEdits();
                };
                _settingsWindow = window;
            }
            _settingsWindow.Open(page);
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the settings window", ex);
            try { _settingsWindow?.Close(); } catch { /* already broken */ }
            _settingsWindow = null;
        }
    }

    /// <summary>
    /// A setting was changed in memory, by the settings window or a menu. What can change in place does so
    /// now; the file is written, and the bars reopened if they have to be, once the changes stop coming.
    /// </summary>
    public void Edited(SettingsChange what)
    {
        if (_disposed) return;

        if (what.HasFlag(SettingsChange.Look))
        {
            foreach (var bar in _bars) bar.RefreshLook();
        }
        if (what.HasFlag(SettingsChange.IconSizes))
        {
            foreach (var bar in _bars) bar.RefreshIconSize();
        }
        if (what.HasFlag(SettingsChange.Lock))
        {
            foreach (var bar in _bars) bar.RefreshLock();
        }
        if (what.HasFlag(SettingsChange.Weather))
        {
            Weather.SettingsChanged();
            foreach (var bar in _bars) bar.RefreshWeather();
        }
        if (what.HasFlag(SettingsChange.StartMenu))
        {
            CloseStartMenu();
            UpdateWinKeyHook();
        }
        if (what.HasFlag(SettingsChange.Bars)) _rebuildPending = true;
        if (what.HasFlag(SettingsChange.Updates)) Updates.SettingsChanged();

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes out what <see cref="Edited"/> has been holding back.</summary>
    private void FlushEdits()
    {
        if (!_saveTimer.IsEnabled && !_rebuildPending) return;
        _saveTimer.Stop();
        SaveSettings();

        if (!_rebuildPending || _disposed) return;
        _rebuildPending = false;
        CloseStartMenu();
        RebuildBars();
    }

    /// <summary>Remembers the thickness the user dragged a bar to. The bar already has it.</summary>
    public void SaveBarSize(string device, int size)
    {
        Settings.Current.Taskbar.EnsureMonitor(device).Size = size;
        SaveSettings();
        Log.Info($"Taskbar on {TaskbarSettings.MonitorKey(device)} resized to {size}");
    }

    /// <summary>Locks or unlocks resizing by dragging, on every bar.</summary>
    public void SetLocked(bool locked)
    {
        Settings.Current.Taskbar.Locked = locked;
        SaveSettings();
        foreach (var bar in _bars)
        {
            bar.RefreshLock();
        }
    }

    /// <summary>Shows or hides the search button next to Start, on every bar, in place.</summary>
    public void SetSearchButton(bool show)
    {
        Settings.Current.Taskbar.ShowSearchButton = show;
        SaveSettings();
        foreach (var bar in _bars)
        {
            bar.RefreshSearchButton();
        }
        Log.Info("Search button: " + (show ? "shown" : "hidden"));
    }

    /// <summary>What the search button opens: ClearStart's menu, or Windows Search. Asked at each click.</summary>
    public void SetSearchOpens(SearchOpens opens)
    {
        Settings.Current.Taskbar.SearchButtonOpens = opens;
        SaveSettings();
        Log.Info("Search button opens: " + (opens == SearchOpens.Windows ? "Windows Search" : "the ClearStart menu"));
    }

    /// <summary>The search button on this monitor's bar was clicked.</summary>
    public void OpenSearch(IntPtr monitor)
    {
        if (Settings.Current.Taskbar.SearchButtonOpens != SearchOpens.Windows)
        {
            // The menu opens with the cursor in its search box, so the next thing typed is the search.
            ToggleStartMenu(monitor);
            return;
        }

        // The self-test draws off-screen and must never put anything of Windows' on the desktop.
        if (_disposed || Mode == HostMode.SelfTest) return;
        HidePreview();
        // Windows has no call that opens its search; Win+S is its key, and pressing it again closes the
        // search. Our own Win key hook leaves keys sent this way alone, so it does not open the menu too.
        Ui.Safe(() => Native.TapShortcut(Native.VK_LWIN, Native.VK_S), "Windows Search");
    }

    /// <summary>The Cast button was clicked: opens Windows' Cast panel, the way Win+K does.</summary>
    public void OpenCast()
    {
        // As with the search: nothing of Windows' on the desktop during a self-test, and Win+K is the only way in.
        if (_disposed || Mode == HostMode.SelfTest) return;
        HidePreview();
        Ui.Safe(() => Native.TapShortcut(Native.VK_LWIN, Native.VK_K), "Cast");
    }

    /// <summary>
    /// Keeps a tray icon on the bar, just before another one there (null: after the last), or moves it there
    /// when it is on the bar already. Every bar shows the same icons, so this changes them all.
    /// </summary>
    public void PinTrayIcon(NotifyIcon icon, NotifyIcon? before)
    {
        if (Shell.NotificationArea is not { } area) return;
        Ui.Safe(() =>
        {
            // Places count in the saved list, which also holds icons whose apps are not running now.
            int count = area.PinnedNotifyIcons.Length;
            int position = before is { IsPinned: true } ? before.PinOrder : count;
            icon.Pin(Math.Clamp(position, 0, count));
            SaveTrayPins(area);
        }, "Keep a tray icon on the taskbar");
    }

    /// <summary>Takes a tray icon off the bar, into the panel behind the arrow.</summary>
    public void UnpinTrayIcon(NotifyIcon icon)
    {
        if (Shell.NotificationArea is not { } area) return;
        Ui.Safe(() =>
        {
            icon.Unpin();
            SaveTrayPins(area);
        }, "Hide a tray icon");
    }

    private void SaveTrayPins(NotificationArea area)
    {
        Settings.Current.Taskbar.PinnedTrayIcons = area.PinnedNotifyIcons.ToList();
        SaveSettings();
        Log.Info($"Tray icons on the taskbar: {area.PinnedNotifyIcons.Length}");
    }

    /// <summary>Writes the settings file. Our own saves do not come back as a "settings changed" event.</summary>
    private void SaveSettings()
    {
        // Nothing is written during a self-test: it must leave the user's settings alone.
        if (Mode == HostMode.SelfTest) return;
        Settings.Current.Taskbar.Tidy();
        Settings.Save();
    }

    // ------------------------------------------------------------------ settings

    private void OnSettingsChanged()
    {
        if (_disposed) return;
        try
        {
            // The file on disk has taken over: anything still waiting to be written was for the old settings.
            _saveTimer.Stop();
            _rebuildPending = false;

            Pins.Reload();
            Shell.NotificationArea?.SetPinnedIcons(Settings.Current.Taskbar.PinnedTrayIcons.ToArray());
            CloseStartMenu();
            RebuildBars();
            UpdateWinKeyHook();
            Weather.SettingsChanged();
            Updates.SettingsChanged();
            _settingsWindow?.Reload();
        }
        catch (Exception ex)
        {
            Log.Error("Could not apply the changed settings", ex);
        }
    }

    private void CloseStartMenu()
    {
        try
        {
            _startMenu?.Close();
        }
        catch (Exception ex)
        {
            Log.Error("Could not close the start menu", ex);
        }
        _startMenu = null;
    }

    // ------------------------------------------------------------------ lifetime

    public void RequestExit() => Application.Current?.Shutdown();

    private void AttachShellLog(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            ShellLogger.Severity = LogSeverity.Info;
            _shellLog = new FileLog(Path.Combine(directory, "shell-library.log"));
            _shellLog.Open();
            ShellLogger.Attach(_shellLog, true);
        }
        catch (Exception ex)
        {
            Log.Warn("Shell library log not available: " + ex.Message);
        }
    }

    public void Dispose() => Close(sessionEnding: false);

    /// <summary>
    /// Closes everything and gives the Windows taskbar back.
    /// </summary>
    /// <param name="sessionEnding">
    /// The user is signing out or shutting down. When ClearStart starts again at the next sign-in, the Windows
    /// taskbar is left on auto-hide (see <see cref="ExplorerTaskbar.ShowForSignOut"/>), so that Explorer starts it
    /// with reduced visibility until ClearStart is back; this cannot guarantee no taskbar frame is drawn.
    /// </param>
    public void Close(bool sessionEnding)
    {
        if (_disposed) return;
        _disposed = true;
        bool keepAutoHide = Mode == HostMode.Live && !EarlySession.Active && sessionEnding
            && StartsAgainAtSignIn() && !EarlyStartup.IsEnabled;
        Log.Info(keepAutoHide ? "Shutting down; ClearStart starts again at the next sign-in" : "Shutting down");

        Step("remove the Win key hook", () => { _winKey?.Dispose(); _winKey = null; });
        Step("stop watching displays", () =>
        {
            _screenTimer.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            Settings.Changed -= OnSettingsChanged;
        });
        Step("save the settings", () =>
        {
            if (!_saveTimer.IsEnabled) return;
            _saveTimer.Stop();
            SaveSettings();
        });
        Step("close the settings window", () => { _settingsWindow?.Close(); _settingsWindow = null; });
        Step("stop the weather and network watchers", () => { Weather.Dispose(); Network.Dispose(); });
        Step("stop the update checks", Updates.Dispose);
        Step("close the start menu", CloseStartMenu);
        Step("close the window previews", ClosePreviews);
        Step("signal shutdown", () => Shell.AppBarManager.SignalGracefulShutdown());
        Step("close the taskbars", CloseBars);
        if (Mode == HostMode.Live)
        {
            Step("show the Windows taskbar", () => GiveBackWindowsTaskbar(keepAutoHide));
        }
        Step("release the app list", Apps.Dispose);
        Step("stop the shell services", Shell.Dispose);
        if (Mode == HostMode.Live)
        {
            // Again, now that our tray is gone: that is when the apps can hand their tray icons back to Windows.
            GiveBackWindowsTaskbar(keepAutoHide);
        }
        Step("close the shell library log", () => _shellLog?.Dispose());
    }

    private static void GiveBackWindowsTaskbar(bool keepAutoHide)
    {
        if (keepAutoHide)
        {
            ExplorerTaskbar.ShowForSignOut();
        }
        else
        {
            ExplorerTaskbar.Restore();
        }
    }

    private static bool StartsAgainAtSignIn()
    {
        try
        {
            return AutoStart.IsEnabled;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not tell whether ClearStart starts with Windows: " + ex.Message);
            return false;
        }
    }

    /// <summary>One step of shutting down. A failed step is logged and the rest go ahead; a slow one is logged too.</summary>
    private static void Step(string what, Action action)
    {
        long started = Environment.TickCount64;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown step failed: " + what, ex);
        }
        long took = Environment.TickCount64 - started;
        if (took >= 300) Log.Info($"Shutdown step took {took} ms: {what}");
    }
}
