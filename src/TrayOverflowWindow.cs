using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.AppBar;
using ManagedShell.WindowsTray;

namespace ClearStart;

/// <summary>
/// The window the panel of hidden tray icons opens in, beside the arrow that opened it. Like the Windows one
/// it closes as soon as something else is clicked. It is a window rather than a popup so that a drag can
/// start in it and end on the bar: a popup closes the moment a drag takes hold of the mouse.
/// </summary>
internal sealed class TrayOverflowWindow : Window
{
    private static TrayOverflowWindow? _open;
    private static DateTime _closedAt = DateTime.MinValue;
    // The arrow of the panel that closed last.
    private static FrameworkElement? _closedArrow;

    private readonly TrayOverflowView _view;
    private readonly FrameworkElement _arrow;
    private readonly IntPtr _monitor;
    private readonly AppBarEdge _edge;
    private IntPtr _handle;
    private bool _closing;
    // Opened by a drag resting on the arrow. It was not given the focus then, so it cannot close on losing it.
    private bool _forDrag;
    private bool _droppedHere;

    static TrayOverflowWindow()
    {
        TrayIconInput.DragEnded += OnDragEnded;
    }

    private TrayOverflowWindow(FrameworkElement arrow, IntPtr monitor, AppBarEdge edge, IEnumerable? icons,
        double iconSize, Action<NotifyIcon>? hide, bool forDrag)
    {
        _arrow = arrow;
        _monitor = monitor;
        _edge = edge;
        _forDrag = forDrag;

        Title = "Hidden icons";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = !forDrag;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _view = new TrayOverflowView(icons, iconSize, hide);
        _view.Dropped += () => _droppedHere = true;
        Content = _view;

        Deactivated += (_, _) => OnLostFocus();
        // Icons come and go while it is open: keep it against the bar at its new size.
        SizeChanged += (_, _) => Place();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) CloseNow();
        };
        Closed += (_, _) =>
        {
            _view.Detach();
            if (!ReferenceEquals(_open, this)) return;
            _open = null;
            _closedAt = DateTime.UtcNow;
            _closedArrow = _arrow;
        };
    }

    /// <summary>
    /// Opens the panel by the arrow, or closes it when it is open from this arrow. Open from another bar's
    /// arrow, it moves to this one.
    /// </summary>
    /// <param name="arrow">The arrow on the bar; the panel opens against the bar, next to it.</param>
    /// <param name="hide">Takes an icon dropped in the panel off the bar.</param>
    public static void Toggle(FrameworkElement arrow, IntPtr monitor, AppBarEdge edge, IEnumerable? icons,
        double iconSize, Action<NotifyIcon> hide)
    {
        if (_open != null)
        {
            bool sameArrow = ReferenceEquals(_open._arrow, arrow);
            _open.CloseNow();
            if (sameArrow) return;
        }

        // Clicking the arrow while the panel is open takes the focus from the panel, which closes it; without
        // this pause that same click would open it again. Another bar's arrow opens its panel straight away.
        if (ReferenceEquals(_closedArrow, arrow) && (DateTime.UtcNow - _closedAt).TotalMilliseconds < 300) return;

        Open(new TrayOverflowWindow(arrow, monitor, edge, icons, iconSize, hide, forDrag: false));
    }

    /// <summary>Opens the panel without taking the focus, for an icon being dragged onto the arrow.</summary>
    public static void OpenForDrag(FrameworkElement arrow, IntPtr monitor, AppBarEdge edge, IEnumerable? icons,
        double iconSize, Action<NotifyIcon> hide)
    {
        if (_open != null) return;
        Open(new TrayOverflowWindow(arrow, monitor, edge, icons, iconSize, hide, forDrag: true));
    }

    /// <summary>Closes the panel if it was opened from this arrow (its bar is closing).</summary>
    public static void CloseFor(FrameworkElement arrow)
    {
        if (_open != null && ReferenceEquals(_open._arrow, arrow)) _open.CloseNow();
    }

    private static void Open(TrayOverflowWindow window)
    {
        try
        {
            _open = window;
            window._handle = new WindowInteropHelper(window).EnsureHandle();
            window.Place();
            window.Show();
            // Again at its real size, which is known once it is showing (and on the monitor it is for).
            window.UpdateLayout();
            window.Place();
            if (window._forDrag) return;

            Native.ForceForeground(window._handle);
            window.Activate();
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the hidden icons", ex);
            window.CloseNow();
        }
    }

    /// <summary>
    /// A drag that opened the panel is over. Dropped in it, the panel stays open as if it had been clicked
    /// open; dropped anywhere else or given up, it closes again.
    /// </summary>
    private static void OnDragEnded()
    {
        var window = _open;
        if (window == null) return;

        bool dropped = window._droppedHere;
        window._droppedHere = false;
        if (!window._forDrag) return;

        if (!dropped)
        {
            window.CloseNow();
            return;
        }
        window._forDrag = false;
        Native.ForceForeground(window._handle);
        window.Activate();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            Native.MakeToolWindow(new WindowInteropHelper(this).Handle);
        }
        catch (Exception ex)
        {
            Log.Error("Could not finish setting up the hidden icons window", ex);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    /// <summary>Closes once another window has really taken the focus (see the start menu for why it looks again).</summary>
    private void OnLostFocus()
    {
        if (_closing) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_closing || !IsVisible || Native.GetForegroundWindow() == _handle) return;
            CloseNow();
        }));
    }

    private void CloseNow()
    {
        if (_closing) return;
        _closing = true;
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Could not close the hidden icons", ex);
        }
    }

    /// <summary>
    /// Puts the panel against the bar, centred on the arrow where there is room, and always on the screen.
    /// In the screen's own pixels, which also hold across monitors of different scales.
    /// </summary>
    private void Place()
    {
        if (_handle == IntPtr.Zero || _closing) return;
        try
        {
            if (PresentationSource.FromVisual(_arrow) == null || !Native.TryGetMonitor(_monitor, out _, out var work)) return;

            var topLeft = _arrow.PointToScreen(new Point(0, 0));
            var bottomRight = _arrow.PointToScreen(new Point(_arrow.ActualWidth, _arrow.ActualHeight));
            var dpi = VisualTreeHelper.GetDpi(this);
            int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
            int height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
            int gap = (int)Math.Round(8 * Native.ScaleOf(_monitor));
            int alongX = (int)Math.Round((topLeft.X + bottomRight.X - width) / 2);
            int alongY = (int)Math.Round((topLeft.Y + bottomRight.Y - height) / 2);

            var (x, y) = _edge switch
            {
                AppBarEdge.Top => (alongX, work.Top + gap),
                AppBarEdge.Left => (work.Left + gap, alongY),
                AppBarEdge.Right => (work.Right - width - gap, alongY),
                _ => (alongX, work.Bottom - height - gap),
            };
            x = Math.Max(work.Left + gap, Math.Min(x, work.Right - width - gap));
            y = Math.Max(work.Top + gap, Math.Min(y, work.Bottom - height - gap));
            Native.SetWindowPos(_handle, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not place the hidden icons: " + ex.Message);
        }
    }
}
