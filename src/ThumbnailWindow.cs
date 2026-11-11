using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.AppBar;

namespace ClearStart;

/// <summary>Where a preview belongs: the taskbar button it is for and the bar that button is on, in screen pixels.</summary>
internal readonly record struct PreviewAnchor(Native.RECT Button, Native.RECT Bar, IntPtr Monitor, AppBarEdge Edge);

/// <summary>
/// The popup with a small live picture of each window of one app, shown next to that app's taskbar button
/// while the mouse rests on it. One popup is shared by every bar. It never takes the focus, so the window
/// the user is working in stays active until they pick another one here.
/// </summary>
internal sealed class ThumbnailWindow : Window
{
    // How long the mouse rests on a button before the previews open, and how long it may be away before they close.
    private const int HoverDelayMs = 400;
    private const int LeaveDelayMs = 350;
    // Opened by a click or the keyboard, the mouse may not be anywhere near: give it time to get here.
    private const int ClickGraceMs = 2500;

    private static readonly Color Backdrop = Color.FromRgb(0x26, 0x26, 0x26);

    private readonly ThumbnailView _view;
    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _watchTimer;
    private IntPtr _handle;
    private bool _closing;

    // What is showing.
    private AppButton? _app;
    private List<WindowTile> _tiles = new();
    private PreviewAnchor _anchor;
    private DateTime? _outsideSince;
    private DateTime _graceUntil = DateTime.MinValue;

    // The button the mouse is resting on while the delay runs.
    private AppButton? _pendingApp;
    private PreviewAnchor _pendingAnchor;

    public ThumbnailWindow()
    {
        Title = "ClearStart previews";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Ui.Brush(Backdrop);

        _view = new ThumbnailView();
        _view.Chosen += OnChosen;
        _view.CloseRequested += tile => Ui.Safe(() => tile.Window.Close(), "Close window");
        Content = _view;

        _hoverTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(HoverDelayMs) };
        _hoverTimer.Tick += (_, _) => Guard(OnHoverElapsed);

        _watchTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(100) };
        _watchTimer.Tick += (_, _) => Guard(Watch);

        Closed += (_, _) =>
        {
            IsClosed = true;
            _hoverTimer.Stop();
            _watchTimer.Stop();
            ReleasePictures();
        };
    }

    /// <summary>True once the window is gone for good and must be replaced.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>The app whose windows are showing, if any (for the self-check).</summary>
    public AppButton? ShownApp => IsVisible ? _app : null;

    // ------------------------------------------------------------------ what the bars ask for

    /// <summary>The mouse came onto a running app's button: open after a moment, or at once if already open.</summary>
    public void Hover(AppButton app, PreviewAnchor anchor)
    {
        if (_closing) return;
        if (IsVisible)
        {
            // Moving along the bar with the previews open switches them straight away.
            CancelHover();
            if (ReferenceEquals(app, _app))
            {
                _anchor = anchor;
            }
            else
            {
                ShowFor(app, anchor, fromClick: false);
            }
            return;
        }

        _pendingApp = app;
        _pendingAnchor = anchor;
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    /// <summary>The mouse left the button, or pressed it, before the previews opened.</summary>
    public void CancelHover()
    {
        _pendingApp = null;
        _hoverTimer.Stop();
    }

    /// <summary>Open now (the user clicked a button with several windows behind it).</summary>
    public void ShowNow(AppButton app, PreviewAnchor anchor)
    {
        if (_closing) return;
        CancelHover();
        ShowFor(app, anchor, fromClick: true);
    }

    public void HidePreview()
    {
        CancelHover();
        _watchTimer.Stop();
        ReleasePictures();
        _app = null;
        _tiles = new List<WindowTile>();
        _outsideSince = null;
        if (_closing) return;
        if (IsVisible) Hide();
        _view.Clear();
    }

    // ------------------------------------------------------------------ window

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            _handle = new WindowInteropHelper(this).Handle;
            Native.MakeNoActivateToolWindow(_handle);
            Native.RoundCorners(_handle);
        }
        catch (Exception ex)
        {
            Log.Error("Could not finish setting up the preview window", ex);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error("Window previews failed", ex);
            try { HidePreview(); } catch { /* nothing more to do */ }
        }
    }

    // ------------------------------------------------------------------ showing

    private void OnHoverElapsed()
    {
        _hoverTimer.Stop();
        var app = _pendingApp;
        var anchor = _pendingAnchor;
        _pendingApp = null;
        if (app == null || _closing) return;

        // Only if the mouse is still resting on that button.
        if (!Native.GetCursorPos(out var cursor) || !Within(anchor.Button, cursor, 2)) return;
        ShowFor(app, anchor, fromClick: false);
    }

    private void ShowFor(AppButton app, PreviewAnchor anchor, bool fromClick)
    {
        var windows = app.Windows.ToList();
        if (windows.Count == 0 || !Native.TryGetMonitor(anchor.Monitor, out _, out var work))
        {
            HidePreview();
            return;
        }

        ReleasePictures();
        _app = app;
        _anchor = anchor;
        _handle = new WindowInteropHelper(this).EnsureHandle();

        double scale = Native.ScaleOf(anchor.Monitor);
        bool stacked = anchor.Edge is AppBarEdge.Left or AppBarEdge.Right;
        int margin = (int)Math.Round(8 * scale);

        _tiles = windows.Select(w => new WindowTile(w)).ToList();
        foreach (var tile in _tiles)
        {
            if (Native.DwmRegisterThumbnail(_handle, tile.Window.Handle, out var picture) != 0 || picture == IntPtr.Zero) continue;
            tile.Thumbnail = picture;
            if (Native.DwmQueryThumbnailSourceSize(picture, out var size) == 0)
            {
                tile.SourceSize = new Size(size.Width / scale, size.Height / scale);
            }
        }

        double maxLength = ((stacked ? work.Height : work.Width) - 2 * margin) / scale;
        _view.SetTiles(_tiles, stacked, maxLength);
        _view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int width = (int)Math.Ceiling(_view.DesiredSize.Width * scale);
        int height = (int)Math.Ceiling(_view.DesiredSize.Height * scale);
        if (width <= 0 || height <= 0)
        {
            HidePreview();
            return;
        }

        Place(work, width, height, scale);
        if (!IsVisible) Show();
        // Again now that it is visible: showing it can make the window settle on a size of its own.
        Place(work, width, height, scale);
        UpdateLayout();
        PlacePictures();
        // And once more when the layout has settled, in case anything moved after this pass.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Guard(PlacePictures)));

        _outsideSince = null;
        _graceUntil = fromClick ? DateTime.UtcNow.AddMilliseconds(ClickGraceMs) : DateTime.MinValue;
        _watchTimer.Start();
    }

    /// <summary>Next to the bar on its desktop side, centred on the button, kept inside the monitor.</summary>
    private void Place(Native.RECT work, int width, int height, double scale)
    {
        int gap = (int)Math.Round(8 * scale);
        int margin = gap;
        var button = _anchor.Button;
        var bar = _anchor.Bar;

        int x = Clamp((button.Left + button.Right - width) / 2, work.Left + margin, work.Right - width - margin);
        int y = Clamp((button.Top + button.Bottom - height) / 2, work.Top + margin, work.Bottom - height - margin);
        switch (_anchor.Edge)
        {
            case AppBarEdge.Top:
                y = Math.Max(bar.Bottom, work.Top) + gap;
                break;
            case AppBarEdge.Left:
                x = Math.Max(bar.Right, work.Left) + gap;
                break;
            case AppBarEdge.Right:
                x = Math.Min(bar.Left, work.Right) - gap - width;
                break;
            default:
                y = Math.Min(bar.Top, work.Bottom) - gap - height;
                break;
        }

        Native.SetWindowPos(_handle, Native.HWND_TOPMOST, x, y, width, height, Native.SWP_NOACTIVATE);
    }

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(value, max));

    /// <summary>Tells the compositor where in this window to draw each live picture.</summary>
    private void PlacePictures()
    {
        if (_closing || !IsVisible) return;
        double scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

        foreach (var (tile, area) in _view.PictureAreas(this))
        {
            if (tile.Thumbnail == IntPtr.Zero) continue;
            var properties = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY
                    | Native.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new Native.RECT
                {
                    Left = (int)Math.Round(area.Left * scale),
                    Top = (int)Math.Round(area.Top * scale),
                    Right = (int)Math.Round(area.Right * scale),
                    Bottom = (int)Math.Round(area.Bottom * scale),
                },
                opacity = 255,
                fVisible = true,
                fSourceClientAreaOnly = false,
            };
            Native.DwmUpdateThumbnailProperties(tile.Thumbnail, ref properties);
        }
    }

    private void ReleasePictures()
    {
        foreach (var tile in _tiles)
        {
            if (tile.Thumbnail == IntPtr.Zero) continue;
            try { Native.DwmUnregisterThumbnail(tile.Thumbnail); } catch { /* the window may be gone already */ }
            tile.Thumbnail = IntPtr.Zero;
        }
    }

    // ------------------------------------------------------------------ while showing

    private void OnChosen(WindowTile tile)
    {
        var window = tile.Window;
        HidePreview();
        Ui.Safe(() => window.BringToFront(), "Switch to window");
    }

    /// <summary>
    /// Runs while the previews are open: follows the app's windows as they open and close, and closes the
    /// previews once the mouse has been away from both the button and the previews for a moment.
    /// </summary>
    private void Watch()
    {
        var app = _app;
        if (_closing || !IsVisible || app == null)
        {
            _watchTimer.Stop();
            return;
        }

        var windows = app.Windows;
        if (windows.Count == 0)
        {
            HidePreview();
            return;
        }
        if (!windows.SequenceEqual(_tiles.Select(t => t.Window)))
        {
            var grace = _graceUntil;
            ShowFor(app, _anchor, fromClick: false);
            _graceUntil = grace;
            return;
        }

        if (!Native.GetCursorPos(out var cursor) || !Native.GetWindowRect(_handle, out var popup)) return;

        var now = DateTime.UtcNow;
        if (IsOver(cursor, popup))
        {
            _outsideSince = null;
            _graceUntil = DateTime.MinValue;
        }
        else if (now >= _graceUntil)
        {
            _outsideSince ??= now;
            if ((now - _outsideSince.Value).TotalMilliseconds >= LeaveDelayMs) HidePreview();
        }
    }

    /// <summary>On the button, on the previews, or on the way from one to the other.</summary>
    private bool IsOver(Native.POINT cursor, Native.RECT popup)
    {
        var button = _anchor.Button;
        if (Within(button, cursor, 4) || Within(popup, cursor, 6)) return true;

        var between = _anchor.Edge is AppBarEdge.Left or AppBarEdge.Right
            ? new Native.RECT
            {
                Left = Math.Min(button.Left, popup.Left),
                Right = Math.Max(button.Right, popup.Right),
                Top = button.Top,
                Bottom = button.Bottom,
            }
            : new Native.RECT
            {
                Left = button.Left,
                Right = button.Right,
                Top = Math.Min(button.Top, popup.Top),
                Bottom = Math.Max(button.Bottom, popup.Bottom),
            };
        return Within(between, cursor, 4);
    }

    private static bool Within(Native.RECT rect, Native.POINT point, int slack) =>
        point.X >= rect.Left - slack && point.X < rect.Right + slack
        && point.Y >= rect.Top - slack && point.Y < rect.Bottom + slack;
}
