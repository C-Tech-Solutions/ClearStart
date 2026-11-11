using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.AppBar;

namespace ClearStart;

/// <summary>
/// The start menu's window. Created once and then shown and hidden, so opening it is instant.
/// It closes itself as soon as it loses the focus, like the system one.
/// </summary>
internal sealed class StartMenuWindow : Window
{
    private readonly ShellHost _host;
    private readonly StartMenuView _view;
    private IntPtr _handle;
    private DateTime _hiddenAt = DateTime.MinValue;
    private DateTime _shownAt = DateTime.MinValue;
    private bool _retriedFocus;
    private bool _closing;

    public StartMenuWindow(ShellHost host)
    {
        _host = host;
        var settings = Settings.Current.StartMenu;

        Title = "ClearStart menu";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = settings.Width;
        Height = settings.Height;

        _view = new StartMenuView(host, HideMenu);
        Content = _view;

        Deactivated += (_, _) => OnLostFocus();
        Closed += (_, _) =>
        {
            IsClosed = true;
            _view.Detach();
        };
    }

    /// <summary>True once the window is gone for good (for example after Alt+F4) and must be replaced.</summary>
    public bool IsClosed { get; private set; }

    /// <param name="monitor">The monitor to open on.</param>
    public void Toggle(IntPtr monitor)
    {
        if (IsVisible)
        {
            HideMenu();
            return;
        }

        // Clicking the Start button while the menu is open takes the focus away from the menu,
        // which hides it; without this pause that same click would open it again.
        if ((DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 300) return;

        ShowMenu(monitor);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            _handle = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(_handle);
            Native.ApplyBackdrop(_handle, Settings.Current.StartMenu.Appearance);
            Native.RoundCorners(_handle);
        }
        catch (Exception ex)
        {
            Log.Error("Could not finish setting up the start menu window", ex);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    /// <summary>
    /// The menu closes when another window takes the focus. Taking the focus from another app in the first
    /// place can bounce it away and back for a moment, so this looks again once things have settled and only
    /// closes if another window really ended up in front.
    /// </summary>
    private void OnLostFocus()
    {
        if (_closing) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            try
            {
                if (_closing || !IsVisible || Native.GetForegroundWindow() == _handle) return;

                if (!_retriedFocus && (DateTime.UtcNow - _shownAt).TotalMilliseconds < 500)
                {
                    // Lost it while still opening: ask once more instead of vanishing under the user's hand.
                    _retriedFocus = true;
                    Log.Info("Start menu lost the focus while opening; taking it again");
                    Native.ForceForeground(_handle);
                    Activate();
                    _view.FocusSearch();
                    if (Native.GetForegroundWindow() == _handle) return;
                }

                HideMenu();
            }
            catch (Exception ex)
            {
                Log.Error("Could not check the start menu's focus", ex);
            }
        }));
    }

    private void ShowMenu(IntPtr monitor)
    {
        _handle = new WindowInteropHelper(this).EnsureHandle();
        _shownAt = DateTime.UtcNow;
        _retriedFocus = false;

        Place(monitor);
        _view.Reset();
        Show();
        // Again now that it is visible: moving to a monitor with a different scale can resize the window.
        Place(monitor);

        Native.ForceForeground(_handle);
        Activate();
        _view.FocusSearch();
    }

    private void HideMenu()
    {
        if (_closing || !IsVisible) return;
        try
        {
            _hiddenAt = DateTime.UtcNow;
            Hide();
        }
        catch (Exception ex)
        {
            Log.Error("Could not hide the start menu", ex);
        }
    }

    /// <summary>
    /// Sizes the menu for the monitor's scale and puts it against the taskbar, whichever edge that is on,
    /// and near the Start button: at the bar's corner or far end if the button is there, otherwise in the
    /// middle (or at the start of a bar whose buttons are not centred).
    /// </summary>
    private void Place(IntPtr monitor)
    {
        if (!Native.TryGetMonitor(monitor, out _, out var work)) return;

        var settings = Settings.Current;
        double scale = Native.ScaleOf(monitor);
        int margin = (int)Math.Round(12 * scale);
        int width = Math.Min((int)Math.Round(settings.StartMenu.Width * scale), work.Width - 2 * margin);
        int height = Math.Min((int)Math.Round(settings.StartMenu.Height * scale), work.Height - 2 * margin);
        if (width <= 0 || height <= 0) return;

        var edge = _host.BarEdgeOn(monitor);
        var button = settings.Taskbar.StartButtonFor(_host.DeviceOf(monitor));
        bool centred = settings.Taskbar.Alignment == Alignment.Center;

        // Where the menu can go along each axis.
        int left = work.Left + margin;
        int right = work.Right - width - margin;
        int top = work.Top + margin;
        int bottom = work.Bottom - height - margin;

        int x, y;
        if (edge is AppBarEdge.Left or AppBarEdge.Right)
        {
            x = edge == AppBarEdge.Left ? left : right;
            y = button switch
            {
                StartButtonPlacement.Corner => top,
                StartButtonPlacement.End => bottom,
                _ => centred ? (top + bottom) / 2 : top,
            };
        }
        else
        {
            y = edge == AppBarEdge.Top ? top : bottom;
            x = button switch
            {
                StartButtonPlacement.Corner => left,
                StartButtonPlacement.End => right,
                _ => centred ? (left + right) / 2 : left,
            };
        }

        Native.SetWindowPos(_handle, Native.HWND_TOPMOST, x, y, width, height, Native.SWP_NOACTIVATE);
    }
}
