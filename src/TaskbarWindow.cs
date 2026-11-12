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
    private readonly ShellHost _host;
    private readonly TaskbarView _view;
    private readonly bool _sideBar;
    private double _size;

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

        _view = new TaskbarView(host, screen, edge, size);
        _view.ThicknessRequested += Resize;
        Content = _view;
        Closed += (_, _) => _view.Detach();
    }

    public string Device => Screen.DeviceName;

    public IntPtr Monitor => Screen.HMonitor;

    public AppBarEdge Edge => AppBarEdge;

    /// <summary>Shows or hides the resize handle after the "lock taskbar size" setting changed.</summary>
    public void RefreshLock() => _view.ApplyLock();

    public void RefreshIconSize() => _view.ApplyIconSize();

    /// <summary>Shows or hides the search button after its setting changed.</summary>
    public void RefreshSearchButton() => _view.ApplySearchButton();

    public void RefreshWeather() => _view.RefreshWeather();

    /// <summary>Looks again at which windows belong on this bar, after another display gained or lost its bar.</summary>
    public void RefreshApps() => _view.RefreshApps();

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
        base.OnSourceInitialized(sender, e);
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

    /// <summary>Changes the bar's thickness while it stays docked (the user is dragging its edge).</summary>
    private void Resize(double size)
    {
        try
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
            UpdatePosition();
            ReportTrayPosition();
        }
        catch (Exception ex)
        {
            Log.Error("Could not resize a taskbar", ex);
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
