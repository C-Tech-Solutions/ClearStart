using System.Windows.Media;
using ManagedShell.AppBar;
using ManagedShell.Interop;
using ManagedShell.WindowsTray;

namespace ClearStart;

/// <summary>
/// One taskbar, docked to one edge of one monitor. Docking, work-area reservation and
/// full-screen handling come from the base class; this adds the see-through window and our content.
/// </summary>
internal sealed class TaskbarWindow : AppBarWindow
{
    // Sent to every window when a taskbar starts: by Explorer when it has started again, and by ClearStart itself.
    private static readonly int TaskbarCreatedMessage = (int)Native.RegisterWindowMessage("TaskbarCreated");

    private readonly ShellHost _host;
    private TaskbarView _view;
    private bool _sideBar;
    private double _size;
    private bool _startMenuVisible;

    /// <param name="size">Thickness: the height of a top or bottom bar, the width of a side bar.</param>
    public TaskbarWindow(ShellHost host, AppBarScreen screen, AppBarEdge edge, double size)
        : base(host.Shell.AppBarManager, host.Shell.ExplorerHelper, host.Shell.FullScreenHelper, screen,
            edge, AppBarMode.Normal, size)
    {
        _host = host;
        _sideBar = edge is AppBarEdge.Left or AppBarEdge.Right;
        _size = size;
        Title = "ClearStart";
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        _view = CreateView(edge, size);
        Closed += (_, _) => _view.Detach();
    }

    public string Device => Screen.DeviceName;

    public IntPtr Monitor => Screen.HMonitor;

    public AppBarEdge Edge => AppBarEdge;

    /// <summary>Temporarily raises every bar with the menu, then resumes normal fullscreen handling.</summary>
    public void SetStartMenuVisible(bool visible)
    {
        _startMenuVisible = visible;
        if (visible)
        {
            base.OnFullScreenLeave();
            return;
        }

        var fullscreen = _fullScreenHelper.FullScreenApps.FirstOrDefault(app =>
            app.screen.DeviceName == Screen.DeviceName || app.screen.IsVirtualScreen);
        if (fullscreen != null) base.OnFullScreenEnter(fullscreen);
        else base.OnFullScreenLeave();
    }

    protected override void OnFullScreenEnter(FullScreenApp app)
    {
        if (!_startMenuVisible) base.OnFullScreenEnter(app);
    }

    /// <summary>Shows or hides the resize handle after the "lock taskbar size" setting changed.</summary>
    public void RefreshLock() => _view.ApplyLock();

    public void RefreshIconSize() => _view.ApplyIconSize();

    /// <summary>Shows or hides the search button after its setting changed.</summary>
    public void RefreshSearchButton() => _view.ApplySearchButton();

    public void RefreshWeather() => _view.RefreshWeather();

    /// <summary>Looks again at which windows belong on this bar, after another display gained or lost its bar.</summary>
    public void RefreshApps() => _view.RefreshApps();

    /// <summary>
    /// Takes the handle Windows gave the bar's monitor when it woke from sleep: the same monitor in the same place,
    /// under a new handle. The bar stays open and where it is.
    /// </summary>
    public void UseScreen(AppBarScreen screen)
    {
        Screen = screen;
        _view.UseMonitor(screen.HMonitor);
    }

    /// <summary>
    /// Docks the bar again where it is. A monitor that wakes from sleep can come back with all of its area given to
    /// the desktop, so windows go under the bar, and docking the bar at the same place again does not make Windows
    /// work the area out again. When that happened, the bar is docked one pixel thinner for a moment and then at its
    /// own size: each is a change, so Windows makes room for the bar again. The bar itself does not move.
    /// </summary>
    /// <returns>True if the bar had lost its room on the desktop and was given it back.</returns>
    public bool Redock()
    {
        if (DeferWorkArea) return false;
        UpdatePosition();
        if (HasItsRoom() || !Native.GetWindowRect(Handle, out var rect)) return false;

        switch (AppBarEdge)
        {
            case AppBarEdge.Left:
                rect.Right--;
                break;
            case AppBarEdge.Right:
                rect.Left++;
                break;
            case AppBarEdge.Top:
                rect.Bottom--;
                break;
            default:
                rect.Top++;
                break;
        }
        Native.SetAppBarPosition(Handle, (uint)AppBarEdge, rect);
        UpdatePosition();
        Log.Info($"Taskbar on {TaskbarSettings.MonitorKey(Device)} had lost its room on the desktop; docked it again, " +
            (HasItsRoom() ? "and it has its room back" : "but it has no room yet"));
        return true;
    }

    /// <summary>
    /// Docks the bar anew through Explorer's taskbar, after Explorer started again: the new Explorer does not know
    /// the bar, so it gave the bar's strip back to the desktop. The bar stays open and where it is.
    /// </summary>
    public void DockAgain()
    {
        // Taken off ClearStart's own list first: while the bar is on it, registering it again does nothing.
        // Explorer is told to forget a bar it does not know, which it ignores.
        UnregisterAppBar();
        RegisterAppBar();
        Log.Info($"Taskbar on {TaskbarSettings.MonitorKey(Device)} docked again: " +
            (HasItsRoom() ? "it has its room on the desktop" : "it has no room on the desktop yet"));
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TaskbarCreatedMessage) _host.OnTaskbarCreated();
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    /// <summary>
    /// Whether the desktop leaves the bar's strip to it, so maximized windows stop beside the bar. True when it
    /// cannot be told.
    /// </summary>
    private bool HasItsRoom()
    {
        IntPtr monitor = Native.MonitorFromWindow(Handle, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (!Native.GetWindowRect(Handle, out var bar) || !Native.TryGetMonitor(monitor, out _, out var work)) return true;
        return AppBarEdge switch
        {
            AppBarEdge.Left => work.Left >= bar.Right,
            AppBarEdge.Right => work.Right <= bar.Left,
            AppBarEdge.Top => work.Top >= bar.Bottom,
            _ => work.Bottom <= bar.Top,
        };
    }

    /// <summary>
    /// Docks the bar to another edge, or gives it another thickness or Start button, without closing it. Closing
    /// and reopening it would make Windows take its docked bar away and add a new one, and Explorer shows its own
    /// taskbar again each time that happens. Only the content is made anew, since its layout follows the edge.
    /// </summary>
    public void MoveTo(AppBarEdge edge, double size)
    {
        _view.Detach();
        _sideBar = edge is AppBarEdge.Left or AppBarEdge.Right;
        AppBarEdge = edge;
        SetThickness(size);
        _view = CreateView(edge, size);
        UpdatePosition();
        ReportTrayPosition();
    }

    /// <summary>Picks up a changed transparency, backdrop or colour without reopening the bar.</summary>
    public void RefreshLook()
    {
        try
        {
            Native.ApplyBackdrop(Handle, Settings.Current.Taskbar.Appearance);
            _view.ApplyLook();
        }
        catch (Exception ex)
        {
            Log.Error("Could not restyle a taskbar", ex);
        }
    }

    protected override void OnSourceInitialized(object sender, EventArgs e)
    {
        // Timed for the first bars only, the ones waited for at sign-in (see TaskbarView).
        string? timing = _host.Mode != HostMode.SelfTest && !_host.HasRenderedBars ? TaskbarSettings.MonitorKey(Device) : null;
        long step = StartTiming.BeginStep();
        // Places the window and docks it: Windows works out every monitor's work area again.
        base.OnSourceInitialized(sender, e);
        StartTiming.Lap(timing, "docking", step);
        try
        {
            Native.MakeToolWindow(Handle);
            Native.ApplyBackdrop(Handle, Settings.Current.Taskbar.Appearance);
            ReportTrayPosition();
        }
        catch (Exception ex)
        {
            Log.Error("Could not finish setting up a taskbar window", ex);
        }
    }

    /// <summary>
    /// The base class reacts to display changes by moving the bar to the primary monitor, which is
    /// wrong when there is one bar per monitor. Let the host decide instead: it rebuilds the bars
    /// if the layout changed and repositions them if it did not.
    /// </summary>
    protected override void SetScreenProperties(ScreenSetupReason reason)
    {
        _fullScreenHelper.NotifyScreensChanged();
        _host.QueueScreenCheck();
    }

    private TaskbarView CreateView(AppBarEdge edge, double size)
    {
        var view = new TaskbarView(_host, Screen, edge, size);
        view.ResizeStarted += BeginResize;
        view.ThicknessRequested += Resize;
        view.ResizeFinished += EndResize;
        Content = view;
        return view;
    }

    /// <summary>
    /// The user took hold of the bar's edge. Until they let go, the bar only resizes its own window: docking
    /// it again on every step would make Windows work out every monitor's work area, and move the windows
    /// out of the way, many times a second, which is what made dragging slow and jumpy.
    /// </summary>
    private void BeginResize()
    {
        try
        {
            DeferWorkArea = true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not start resizing a taskbar", ex);
        }
    }

    /// <summary>Changes the bar's thickness while the user drags its edge: only this window moves.</summary>
    private void Resize(double size)
    {
        try
        {
            SetThickness(size);
            UpdatePosition();
        }
        catch (Exception ex)
        {
            Log.Error("Could not resize a taskbar", ex);
        }
    }

    /// <summary>The user let go: dock the bar once at its new size, which makes room for it on the desktop.</summary>
    private void EndResize()
    {
        try
        {
            // Docks the bar once (the base class re-docks when this goes back to false).
            DeferWorkArea = false;
            ReportTrayPosition();
        }
        catch (Exception ex)
        {
            Log.Error("Could not finish resizing a taskbar", ex);
        }
    }

    private void SetThickness(double size)
    {
        _size = size;
        if (_sideBar)
        {
            DesiredWidth = size;
        }
        else
        {
            DesiredHeight = size;
        }
    }

    /// <summary>Tells apps that ask "where is the tray?" where our main bar is.</summary>
    internal void ReportTrayPosition()
    {
        var area = _host.Shell.NotificationArea;
        if (area == null || !_host.IsMainBar(Screen)) return;

        var bounds = Screen.Bounds;
        int thickness = (int)Math.Round(_size * DpiScale);
        var data = new TrayHostSizeData();
        switch (AppBarEdge)
        {
            case AppBarEdge.Left:
                data.edge = NativeMethods.ABEdge.ABE_LEFT;
                data.rc = new NativeMethods.Rect(bounds.Left, bounds.Top, bounds.Left + thickness, bounds.Bottom);
                break;
            case AppBarEdge.Top:
                data.edge = NativeMethods.ABEdge.ABE_TOP;
                data.rc = new NativeMethods.Rect(bounds.Left, bounds.Top, bounds.Right, bounds.Top + thickness);
                break;
            case AppBarEdge.Right:
                data.edge = NativeMethods.ABEdge.ABE_RIGHT;
                data.rc = new NativeMethods.Rect(bounds.Right - thickness, bounds.Top, bounds.Right, bounds.Bottom);
                break;
            default:
                data.edge = NativeMethods.ABEdge.ABE_BOTTOM;
                data.rc = new NativeMethods.Rect(bounds.Left, bounds.Bottom - thickness, bounds.Right, bounds.Bottom);
                break;
        }
        area.SetTrayHostSizeData(data);
    }
}
