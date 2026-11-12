using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.AppBar;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;

namespace ClearStart;

/// <summary>
/// The contents of one taskbar: Start button, one button per app, tray and clock.
/// Works along any edge: laid out as a row on a top or bottom bar and as a column on a side bar.
/// </summary>
public partial class TaskbarView : UserControl
{
    private readonly ShellHost _host;
    private readonly IntPtr _monitor;
    private readonly string _device;
    private readonly AppBarEdge _edge;
    private readonly bool _vertical;
    private readonly double _scale;
    private readonly bool _centered;
    private readonly bool _showSeconds;
    // The icons apps put in the tray, and the clock with the network/volume button: each on the main bar
    // only or on every bar, as the settings say.
    private readonly bool _showTrayIcons;
    private readonly bool _showClock;
    // The main display's bar, or the first one there is when that display's bar is hidden.
    private readonly bool _main;
    // A short bar that is not the main one: no room for the tray, the clock or the weather.
    private readonly bool _cramped;
    private readonly StartButtonPlacement _startPlacement;
    private readonly int? _iconSizeOverride;
    private readonly int? _trayIconSizeOverride;
    private readonly TaskbarApps _apps;
    private readonly DispatcherTimer _clock;
    // The tray icons that are not on the bar: what the arrow beside the tray opens.
    private System.Collections.IEnumerable? HiddenIcons => _host.Shell.NotificationArea?.UnpinnedIcons;
    // A tray icon dragged onto the arrow and held there opens the panel, so it can be dropped in.
    private readonly DispatcherTimer _springOpen;
    private double _thickness;
    private double _iconSize = 24;
    // A made-up report the self-test puts on the bar, since it must not go and fetch a real one.
    private WeatherReport? _sampleWeather;

    // The window that was in front when the mouse went down on an app button (the click itself moves the focus to the bar).
    private ApplicationWindow? _pressedWindow;

    // Dragging the resize handle.
    private bool _resizing;
    private Native.POINT _dragStart;
    private double _dragStartThickness;
    private DateTime _lastResize = DateTime.MinValue;

    /// <param name="thickness">Height of a top or bottom bar, width of a side bar.</param>
    /// <param name="startButton">Where the Start button goes; null = what the settings say for this monitor.</param>
    /// <param name="iconSize">App icon size (0 = automatic); null = what the settings say.</param>
    /// <param name="trayIconSize">Tray icon size; null = what the settings say.</param>
    internal TaskbarView(ShellHost host, AppBarScreen screen, AppBarEdge edge, double thickness,
        StartButtonPlacement? startButton = null, int? iconSize = null, int? trayIconSize = null)
    {
        _host = host;
        _iconSizeOverride = iconSize;
        _trayIconSizeOverride = trayIconSize;
        _monitor = screen.HMonitor;
        _device = screen.DeviceName;
        _edge = edge;
        _vertical = edge is AppBarEdge.Left or AppBarEdge.Right;
        _scale = Native.ScaleOf(_monitor);

        var settings = Settings.Current.Taskbar;
        _centered = settings.Alignment == Alignment.Center;
        _showSeconds = settings.ShowSeconds;
        _startPlacement = startButton ?? settings.StartButtonFor(_device);

        // A short bar on a secondary monitor has no room for the tray and clock; they are on the other bars.
        double length = (_vertical ? screen.Bounds.Height : screen.Bounds.Width) / _scale;
        _main = host.IsMainBar(screen);
        _cramped = !_main && length < (_vertical ? 600 : 900);
        _showTrayIcons = (_main || settings.TrayOnAllMonitors) && !_cramped;
        _showClock = (_main || settings.ClockOnAllMonitors) && !_cramped;

        InitializeComponent();

        Resources["LabelVisibility"] = settings.ShowLabels && !_vertical ? Visibility.Visible : Visibility.Collapsed;
        ApplyLook();

        ArrangeForEdge();
        PlaceStartButton();
        ApplyThickness(thickness);
        ApplyLock();

        // With a bar on every monitor, each one lists the windows that are on its own monitor. The windows
        // on a monitor whose bar is hidden would then be on no bar at all, so the main bar lists those too.
        bool ownMonitorOnly = settings.AllMonitors && settings.TasksOnOwnMonitorOnly;
        // Which pinned apps this bar shows is asked each time, so it follows the settings without the bar reopening.
        _apps = new TaskbarApps(host, ownMonitorOnly ? _monitor : IntPtr.Zero, settings.CombineWindows, takeStrays: _main,
            showsPin: pin => Settings.Current.Taskbar.ShowsPin(_device, _main, pin.Path));
        AppList.ItemsSource = _apps.Items;

        TrayList.Visibility = _showTrayIcons ? Visibility.Visible : Visibility.Collapsed;
        OverflowButton.Visibility = TrayList.Visibility;
        ClockButton.Visibility = _showClock ? Visibility.Visible : Visibility.Collapsed;
        QuickButton.Visibility = _showClock && settings.ShowQuickSettingsButton ? Visibility.Visible : Visibility.Collapsed;
        CastButton.Visibility = _showClock && settings.ShowCastButton ? Visibility.Visible : Visibility.Collapsed;
        if (_showTrayIcons && host.Shell.NotificationArea is { } tray)
        {
            // Only the icons kept on the bar; the arrow beside them opens the rest. The tray starts after
            // the first bars are up, so its list may not be there yet: the binding picks it up when it is.
            TrayList.SetBinding(ItemsControl.ItemsSourceProperty,
                new System.Windows.Data.Binding(nameof(NotificationArea.PinnedIcons)) { Source = tray });
        }

        _springOpen = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(500) };
        _springOpen.Tick += (_, _) =>
        {
            _springOpen.Stop();
            OpenHiddenForDrag();
        };

        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateClock();
        if (_showClock)
        {
            UpdateClock();
            _clock.Start();
        }

        _host.Network.Changed += RefreshNetwork;
        _host.Weather.Changed += RefreshWeather;
        RefreshNetwork();
        RefreshWeather();
    }

    /// <summary>Raised while the user drags the resize handle: the bar should become this thick.</summary>
    internal event Action<double>? ThicknessRequested;

    /// <summary>The app buttons currently on this bar, in order.</summary>
    internal IReadOnlyList<AppButton> AppButtons => _apps.Items;

    /// <summary>Looks again at which windows belong on this bar, after another display gained or lost its bar.</summary>
    internal void RefreshApps() => _apps.Refresh();

    /// <summary>Fills the tray with stand-in icons (anything with Icon and Title) so the self-test can draw it.</summary>
    internal void ShowSampleTray(System.Collections.IEnumerable icons) => TrayList.ItemsSource = icons;

    /// <summary>
    /// Fills the bar with stand-in app buttons (anything with the properties the button template reads)
    /// in place of the pinned apps and open windows, so the screenshot mode shows nobody's own desktop.
    /// </summary>
    internal void ShowSampleApps(System.Collections.IEnumerable apps) => AppList.ItemsSource = apps;

    /// <summary>Whether this bar has the tray icons, and whether it has the clock: for the self-test's report.</summary>
    internal (bool TrayIcons, bool Clock) TrayParts => (_showTrayIcons, _showClock);

    /// <summary>Puts a made-up weather report on the bar so the self-test can draw it.</summary>
    internal void ShowSampleWeather(WeatherReport report)
    {
        _sampleWeather = report;
        RefreshWeather();
    }

    /// <summary>Called when the bar closes, so nothing long-lived keeps this view alive.</summary>
    internal void Detach()
    {
        _host.Network.Changed -= RefreshNetwork;
        _host.Weather.Changed -= RefreshWeather;
        _host.HidePreview();
        WeatherFlyout.Close();
        TrayOverflowWindow.CloseFor(OverflowButton);
        _springOpen.Stop();
        _clock.Stop();
        _apps.Dispose();
    }

    /// <summary>The bar's colours: how see-through it is, its tint, and the colour of its text and glyphs.</summary>
    internal void ApplyLook()
    {
        var settings = Settings.Current.Taskbar;
        Resources["ForegroundBrush"] = Ui.Brush(Settings.ParseColor(settings.Foreground, Colors.White));

        TintLayer.Background = Ui.Backdrop(settings.Appearance, Settings.ParseColor(settings.Tint, Colors.Transparent));
        ApplyStartButton();
    }

    /// <summary>Draws the Start button's face the way the settings say: its design, size, colours and background.</summary>
    private void ApplyStartButton()
    {
        // Not until the bar knows how thick it is; ApplyThickness comes back here.
        if (_thickness <= 0) return;

        var settings = Settings.Current.Taskbar;
        // The usual size follows the app icons; the most it may be leaves a little of the bar around it.
        double limit = Math.Max(10, _vertical ? _thickness - 16 : _thickness - 6);
        double size = Math.Clamp(Math.Round(_iconSize * 0.84 * settings.StartButtonScale / 100.0), 10, limit);
        StartButton.Content = StartGlyph.Build(settings, size, limit,
            Settings.ParseColor(settings.Foreground, Colors.White),
            _vertical ? _thickness - 12 : double.PositiveInfinity);
    }

    // ------------------------------------------------------------------ layout

    /// <summary>The XAML describes a horizontal bar; this turns the same parts into a column for a side bar.</summary>
    private void ArrangeForEdge()
    {
        if (_vertical)
        {
            CornerHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            CornerHost.VerticalAlignment = VerticalAlignment.Top;

            MainGroup.HorizontalAlignment = HorizontalAlignment.Stretch;
            MainGroup.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(AppsStartHost, Dock.Top);
            AppScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            AppScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            AppList.ItemsPanel = (ItemsPanelTemplate)Resources["Column"];

            EndGroup.Orientation = Orientation.Vertical;
            EndGroup.HorizontalAlignment = HorizontalAlignment.Stretch;
            EndGroup.VerticalAlignment = VerticalAlignment.Bottom;
            TrayList.ItemsPanel = (ItemsPanelTemplate)Resources["Wrap"];
            TrayList.HorizontalAlignment = HorizontalAlignment.Center;
            TrayList.Margin = new Thickness(0, 0, 0, 2);
            OverflowButton.Padding = new Thickness(0, 4, 0, 4);
            CastButton.Padding = new Thickness(2, 7, 2, 7);
            QuickButton.Padding = new Thickness(2, 7, 2, 7);
            ClockButton.Padding = new Thickness(3, 6, 3, 6);
            // The weather: its picture above the temperature.
            WeatherButton.Padding = new Thickness(2, 6, 2, 6);
            WeatherStack.Orientation = Orientation.Vertical;
            WeatherIcon.HorizontalAlignment = HorizontalAlignment.Center;
            WeatherTemp.HorizontalAlignment = HorizontalAlignment.Center;
            WeatherTemp.Margin = new Thickness(0, 2, 0, 0);
            TimeText.HorizontalAlignment = HorizontalAlignment.Center;
            DayText.HorizontalAlignment = HorizontalAlignment.Center;
            DateText.HorizontalAlignment = HorizontalAlignment.Center;
        }

        // The arrow points the way the panel of hidden icons opens: away from the screen edge.
        OverflowGlyph.Text = _edge switch
        {
            AppBarEdge.Top => "",
            AppBarEdge.Left => "",
            AppBarEdge.Right => "",
            _ => "",
        };

        // The resize handle runs along the edge that faces the desktop.
        const double grip = 5;
        switch (_edge)
        {
            case AppBarEdge.Top:
                ResizeGrip.Height = grip;
                ResizeGrip.VerticalAlignment = VerticalAlignment.Bottom;
                ResizeGrip.Cursor = Cursors.SizeNS;
                break;
            case AppBarEdge.Left:
                ResizeGrip.Width = grip;
                ResizeGrip.HorizontalAlignment = HorizontalAlignment.Right;
                ResizeGrip.Cursor = Cursors.SizeWE;
                break;
            case AppBarEdge.Right:
                ResizeGrip.Width = grip;
                ResizeGrip.HorizontalAlignment = HorizontalAlignment.Left;
                ResizeGrip.Cursor = Cursors.SizeWE;
                break;
            default:
                ResizeGrip.Height = grip;
                ResizeGrip.VerticalAlignment = VerticalAlignment.Top;
                ResizeGrip.Cursor = Cursors.SizeNS;
                break;
        }
    }

    /// <summary>Puts Start, and the search button beside it, where Start is set to go on this bar.</summary>
    private void PlaceStartButton()
    {
        AppsStartHost.Child = null;
        StartGroup.Orientation = _vertical ? Orientation.Vertical : Orientation.Horizontal;
        switch (_startPlacement)
        {
            case StartButtonPlacement.WithApps:
                AppsStartHost.Child = StartGroup;
                break;
            case StartButtonPlacement.Corner:
                CornerHost.Child = StartGroup;
                break;
            case StartButtonPlacement.End:
                // At the far end Start stays outermost, with the search button on its inner side.
                StartGroup.Children.Remove(SearchButton);
                StartGroup.Children.Insert(0, SearchButton);
                EndStartHost.Child = StartGroup;
                break;
            default:
                // Hidden: Start stays out of the bar (the Windows key still opens the menu). The search
                // button, if it is on, sits where Start would have been: with the app buttons.
                StartButton.Visibility = Visibility.Collapsed;
                AppsStartHost.Child = StartGroup;
                break;
        }
        ApplySearchButton();
    }

    /// <summary>Shows or hides the search button as the settings say, without reopening the bar.</summary>
    internal void ApplySearchButton() =>
        SearchButton.Visibility = Settings.Current.Taskbar.ShowSearchButton ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>What sits in the Start button's place, in order, for the self-test's report.</summary>
    internal IReadOnlyList<string> StartGroupParts => StartGroup.Children.OfType<Button>()
        .Where(button => button.Visibility == Visibility.Visible)
        .Select(button => ReferenceEquals(button, StartButton) ? "start" : "search")
        .ToList();

    /// <summary>The resize handle is there unless the user locked the size.</summary>
    internal void ApplyLock() =>
        ResizeGrip.Visibility = Settings.Current.Taskbar.Locked ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Picks up a changed app or tray icon size without reopening the bar.</summary>
    internal void ApplyIconSize() => ApplyThickness(_thickness);

    /// <summary>Sizes the buttons and icons for a bar of this thickness.</summary>
    private void ApplyThickness(double thickness)
    {
        _thickness = thickness;

        // A chosen size is kept unless the bar is too thin for it; automatic follows the thickness.
        int wanted = _iconSizeOverride ?? Settings.Current.Taskbar.IconSize;
        double icon = wanted > 0
            ? Math.Min(wanted, _vertical ? thickness - 16 : thickness - 8)
            // Automatic: 24 on a bar of the default thickness, larger and smaller along with it.
            : _vertical ? Math.Round(thickness * 0.4 / 2) * 2 : thickness - 20;
        icon = Math.Clamp(icon, 16, 48);

        // Tray icons likewise: the chosen size, unless the bar is too thin for it.
        double tray = Math.Min(_trayIconSizeOverride ?? Settings.Current.Taskbar.TrayIconSize,
            _vertical ? thickness - 16 : thickness - 12);
        tray = Math.Clamp(tray, 12, 32);
        Resources["TrayIconSize"] = tray;
        Resources["TrayGlyphSize"] = Math.Round(tray * 15 / 16);
        Resources["ChevronSize"] = Math.Max(10, Math.Round(tray * 0.75));
        // The weather picture is a little larger than a tray icon: it has more to show.
        Resources["WeatherIconSize"] = Math.Min(tray + 4, _vertical ? thickness - 16 : thickness - 8);

        double across = thickness < 36 ? 2 : 4;
        Resources["IconSize"] = icon;
        // The magnifier: a little smaller than an app icon, so it sits level with the Start button's shape.
        Resources["SearchGlyphSize"] = Math.Max(11, Math.Round(icon * 0.85));
        _iconSize = icon;
        ApplyStartButton();
        Resources["ButtonMargin"] = _vertical ? new Thickness(across, 2, across, 2) : new Thickness(2, across, 2, across);
        Resources["ButtonMinWidth"] = _vertical ? 0.0 : icon + 20;
        Resources["ButtonMinHeight"] = _vertical ? icon + 18 : 0.0;

        // Two lines of clock do not fit a thin horizontal bar.
        DateText.Visibility = !_vertical && thickness < 40 ? Visibility.Collapsed : Visibility.Visible;
        DayText.Visibility = DayFits(thickness, across) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Whether the clock has room for the day of the week on a line of its own, between the time and the
    /// date. A flat bar needs the height for three lines. A side bar needs the width for the longest day's
    /// name, so the clock keeps one shape all week; a squeeze of up to a tenth still counts as fitting.
    /// </summary>
    private bool DayFits(double thickness, double across)
    {
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        FormattedText Measure(string text) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, FontSize, Brushes.White, _scale);

        if (!_vertical) return thickness - 2 * across >= 3 * Measure("0").Height;

        double widest = CultureInfo.CurrentCulture.DateTimeFormat.DayNames.Max(day => Measure(day).Width);
        double room = thickness - 2 * across - ClockButton.Padding.Left - ClockButton.Padding.Right;
        return widest * 0.9 <= room;
    }

    /// <summary>The lines the clock shows, top to bottom: for the self-test's report.</summary>
    internal string ClockLines => string.Join(" + ", new[] { (TimeText, "time"), (DayText, "day"), (DateText, "date") }
        .Where(line => line.Item1.Visibility == Visibility.Visible).Select(line => line.Item2));

    private double Along(double horizontal, double vertical) => _vertical ? vertical : horizontal;

    private void Layout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep the app group clear of whatever sits at the two ends of the bar. Centring needs the larger
        // end's room free on both sides, so a crowded bar starts from the near end instead and gets that
        // room back. What still does not fit scrolls.
        double length = Along(BarRoot.ActualWidth, BarRoot.ActualHeight);
        if (length <= 0) return;

        double corner = Along(CornerHost.ActualWidth, CornerHost.ActualHeight);
        double end = Along(EndGroup.ActualWidth, EndGroup.ActualHeight);
        if (end > 0) end += 4;
        double wanted = Along(
            AppsStartHost.DesiredSize.Width + AppList.DesiredSize.Width,
            AppsStartHost.DesiredSize.Height + AppList.DesiredSize.Height);

        double roomCentred = length - 2 * Math.Max(corner, end) - 12;
        double roomFromStart = length - corner - end - 12;
        bool centre = _centered && wanted <= roomCentred;
        double room = Math.Max(40, centre ? roomCentred : roomFromStart);

        if (_vertical)
        {
            MainGroup.VerticalAlignment = centre ? VerticalAlignment.Center : VerticalAlignment.Top;
            MainGroup.Margin = new Thickness(0, centre ? 0 : corner, 0, 0);
            MainGroup.MaxHeight = room;
            EndGroup.Margin = new Thickness(0, 0, 0, 4);
        }
        else
        {
            MainGroup.HorizontalAlignment = centre ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            MainGroup.Margin = new Thickness(centre ? 0 : corner, 0, 0, 0);
            MainGroup.MaxWidth = room;
            EndGroup.Margin = new Thickness(0, 0, 4, 0);
        }
    }

    private void Apps_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // A column scrolls with the wheel by itself; a row needs to be told which way.
        if (_vertical) return;
        AppScroller.ScrollToHorizontalOffset(AppScroller.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        string time = now.ToString(_showSeconds ? "T" : "t", CultureInfo.CurrentCulture);
        if (TimeText.Text != time) TimeText.Text = time;

        string date = now.ToString("d", CultureInfo.CurrentCulture);
        if (DateText.Text != date)
        {
            DateText.Text = date;
            DayText.Text = now.ToString("dddd", CultureInfo.CurrentCulture);
            ClockButton.ToolTip = now.ToString("D", CultureInfo.CurrentCulture);
        }
    }

    // ------------------------------------------------------------------ resizing

    private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Native.GetCursorPos(out _dragStart)) return;
        _dragStartThickness = _thickness;
        _resizing = true;
        ResizeGrip.CaptureMouse();
        e.Handled = true;
    }

    private void Grip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_resizing || !Native.GetCursorPos(out var now)) return;

        // Every step re-docks the bar and makes Windows move the other windows out of the way; do not flood it.
        if ((DateTime.UtcNow - _lastResize).TotalMilliseconds < 50) return;
        SetThickness(ThicknessAt(now));
    }

    private void Grip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_resizing) return;
        if (Native.GetCursorPos(out var now)) SetThickness(ThicknessAt(now));
        FinishResize();
        ResizeGrip.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Grip_LostCapture(object sender, MouseEventArgs e)
    {
        if (_resizing) FinishResize();
    }

    private void FinishResize()
    {
        _resizing = false;
        if (_thickness != _dragStartThickness) _host.SaveBarSize(_device, (int)_thickness);
    }

    /// <summary>How thick the bar should be with the mouse here: it follows the mouse away from the screen edge.</summary>
    private double ThicknessAt(Native.POINT now)
    {
        int pixels = _edge switch
        {
            AppBarEdge.Top => now.Y - _dragStart.Y,
            AppBarEdge.Left => now.X - _dragStart.X,
            AppBarEdge.Right => _dragStart.X - now.X,
            _ => _dragStart.Y - now.Y,
        };
        var (min, max) = TaskbarSettings.SizeLimits(_vertical);
        double thickness = Math.Round((_dragStartThickness + pixels / _scale) / 2) * 2;
        return Math.Clamp(thickness, min, max);
    }

    private void SetThickness(double thickness)
    {
        if (thickness == _thickness) return;
        _lastResize = DateTime.UtcNow;
        ApplyThickness(thickness);
        ThicknessRequested?.Invoke(thickness);
    }

    // ------------------------------------------------------------------ start button

    private void Start_Click(object sender, RoutedEventArgs e) => _host.ToggleStartMenu(_monitor);

    private void Start_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Ui.Safe(ShellHelper.ShowStartContextMenu, "Power user menu");
    }

    // ------------------------------------------------------------------ search button

    /// <summary>ClearStart's menu ready for typing a search, or Windows Search: whichever the settings say.</summary>
    private void Search_Click(object sender, RoutedEventArgs e) => _host.OpenSearch(_monitor);

    private void Search_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _host.HidePreview();
        Ui.ShowMenu(SearchButton, PlacementMode.MousePoint, SearchButtonMenu());
    }

    /// <summary>The search button's right-click menu: what it opens, and a way to take it off the bars.</summary>
    internal List<MenuEntry> SearchButtonMenu()
    {
        var opens = Settings.Current.Taskbar.SearchButtonOpens;
        return new List<MenuEntry>
        {
            new("Opens Windows Search", () => _host.SetSearchOpens(SearchOpens.Windows), Checked: opens == SearchOpens.Windows),
            new("Opens ClearStart's search", () => _host.SetSearchOpens(SearchOpens.Menu), Checked: opens == SearchOpens.Menu),
            new(null, null),
            new("Hide the search button", () => _host.SetSearchButton(false)),
            new(null, null),
            new("Taskbar settings", () => _host.ShowSettings(SettingsPage.Taskbar)),
        };
    }

    // ------------------------------------------------------------------ app buttons

    /// <summary>Resting the mouse on a running app's button opens small live previews of its windows.</summary>
    private void App_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not AppButton app) return;
        if (!app.IsRunning || _resizing) return;
        if (AnchorFor(element) is { } anchor) _host.HoverPreview(app, anchor);
    }

    private void App_MouseLeave(object sender, MouseEventArgs e) => _host.CancelPreviewHover();

    /// <summary>Where a button and this bar are on the screen, so the previews can open next to them.</summary>
    private PreviewAnchor? AnchorFor(FrameworkElement button)
    {
        try
        {
            if (PresentationSource.FromVisual(button) is not HwndSource source) return null;
            if (!Native.GetWindowRect(source.Handle, out var bar)) return null;

            var topLeft = button.PointToScreen(new Point(0, 0));
            var bottomRight = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
            var rect = new Native.RECT
            {
                Left = (int)Math.Floor(topLeft.X),
                Top = (int)Math.Floor(topLeft.Y),
                Right = (int)Math.Ceiling(bottomRight.X),
                Bottom = (int)Math.Ceiling(bottomRight.Y),
            };
            return new PreviewAnchor(rect, bar, _monitor, _edge);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not locate a taskbar button: " + ex.Message);
            return null;
        }
    }

    private void App_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppButton app) return;

        // A press is not a rest: no previews popping up a moment after the click.
        _host.CancelPreviewHover();

        if (e.ChangedButton == MouseButton.Left)
        {
            // Remember which window was in front before this click took the focus away from it.
            _pressedWindow = app.Windows.FirstOrDefault(w => w.State == ApplicationWindow.WindowState.Active);
        }
        else if (e.ChangedButton == MouseButton.Middle)
        {
            e.Handled = true;
            Ui.Safe(() => OpenNew(app), "New window");
        }
    }

    private void App_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not AppButton app) return;

        var inFront = _pressedWindow;
        _pressedWindow = null;
        Ui.Safe(() =>
        {
            var modifiers = Keyboard.Modifiers;
            if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                _host.HidePreview();
                OpenNew(app, elevated: true);
            }
            else if (modifiers == ModifierKeys.Shift || !app.IsRunning)
            {
                _host.HidePreview();
                OpenNew(app);
            }
            else if (app.Count == 1)
            {
                // One window: bring it up, or put it away if it was already in front.
                _host.HidePreview();
                var window = app.Windows[0];
                if (ReferenceEquals(window, inFront) && window.CanMinimize)
                {
                    window.Minimize();
                }
                else
                {
                    window.BringToFront();
                }
            }
            else if (AnchorFor(element) is { } anchor)
            {
                // Several windows behind one button: show a live preview of each, to pick from.
                _host.ShowPreview(app, anchor);
            }
        }, "Taskbar button");
    }

    private void App_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not AppButton app) return;
        e.Handled = true;
        _host.HidePreview();

        var windows = app.Windows.ToList();
        var first = windows.Count > 0 ? windows[0] : null;
        var pin = app.Pin;

        string? file = pin != null ? pin.TargetFile : first is { IsUWP: false } ? first.WinFileName : null;
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) file = null;

        // What to store if this app gets pinned: its program file, or its app id for a Store app.
        string? pinPath = file;
        if (pinPath == null && first is { IsUWP: true } && !string.IsNullOrEmpty(first.AppUserModelID))
        {
            pinPath = AppCatalog.AppsFolderPrefix + first.AppUserModelID;
        }
        bool canElevate = pin != null || file != null;

        // A button without a pin can still be a pinned app's: one whose pin this bar does not show, or
        // (with a button per window) one whose pin sits beside it.
        var pinned = pin ?? PinOf(first);
        bool shownHere = pinned != null && Settings.Current.Taskbar.ShowsPin(_device, _main, pinned.Path);

        var entries = new List<MenuEntry>
        {
            new MenuEntry(app.IsRunning ? "New window" : "Open", () => OpenNew(app)),
            new MenuEntry("Run as administrator", canElevate ? () => OpenNew(app, elevated: true) : null),
            new MenuEntry(null, null),
            pinned != null
                ? new MenuEntry("Unpin from taskbar", () => _host.Pins.UnpinFromTaskbar(pinned.Path))
                : new MenuEntry("Pin to taskbar", pinPath != null ? () => _host.PinToTaskbar(_device, _main, pinPath) : null),
        };
        if (pinned != null && !shownHere)
        {
            entries.Add(new MenuEntry("Show its pin on this taskbar", () => _host.ShowPinOn(_device, _main, pinned.Path, true)));
        }
        else if (pinned != null && _host.BarCount > 1)
        {
            // With one bar this would be unpinning by another name.
            entries.Add(new MenuEntry("Hide its pin from this taskbar", () => _host.ShowPinOn(_device, _main, pinned.Path, false)));
        }
        entries.Add(new MenuEntry("Open file location", file != null ? () => Launcher.ShowInExplorer(file) : null));

        if (windows.Count == 1)
        {
            var window = windows[0];
            entries.Add(new MenuEntry(null, null));
            entries.Add(window.IsMinimized
                ? new MenuEntry("Restore", () => window.Restore())
                : new MenuEntry("Minimize", window.CanMinimize ? () => window.Minimize() : null));
            entries.Add(new MenuEntry("Maximize", () => window.Maximize()));
            entries.Add(new MenuEntry("Close window", () => window.Close()));
        }
        else if (windows.Count > 1)
        {
            entries.Add(new MenuEntry(null, null));
            entries.Add(new MenuEntry($"Close all {windows.Count} windows", () => CloseAll(windows)));
        }

        Ui.ShowMenu(element, PlacementMode.MousePoint, entries);
    }

    /// <summary>The taskbar pin of the app a window belongs to, if that app is pinned.</summary>
    private PinnedItem? PinOf(ApplicationWindow? window)
    {
        if (window == null) return null;
        var keys = TaskbarApps.KeysOf(window);
        return _host.Pins.Taskbar.FirstOrDefault(pin =>
            pin.AppKeys.Any(key => keys.Contains(key, StringComparer.OrdinalIgnoreCase)));
    }

    private static void CloseAll(List<ApplicationWindow> windows)
    {
        foreach (var window in windows)
        {
            Ui.Safe(() => window.Close(), "Close window");
        }
    }

    /// <summary>Starts the app (again): a pinned app through its pin, a running one through its program.</summary>
    private static void OpenNew(AppButton app, bool elevated = false)
    {
        if (app.Pin != null)
        {
            Launcher.Launch(app.Pin.Item, elevated);
            return;
        }

        var window = app.First;
        if (window == null) return;
        if (window.IsUWP && !string.IsNullOrEmpty(window.AppUserModelID))
        {
            Launcher.Run(AppCatalog.AppsFolderPrefix + window.AppUserModelID);
        }
        else if (!string.IsNullOrEmpty(window.WinFileName))
        {
            Launcher.Run(window.WinFileName, elevated: elevated);
        }
    }

    // ------------------------------------------------------------------ tray

    // The mouse on a tray icon goes to its app, unless the icon is being dragged: see TrayIconInput.
    private void Tray_MouseEnter(object sender, MouseEventArgs e) => TrayIconInput.Enter(sender);

    private void Tray_MouseLeave(object sender, MouseEventArgs e) => TrayIconInput.Leave(sender);

    private void Tray_MouseMove(object sender, MouseEventArgs e) => TrayIconInput.Move(sender, e);

    private void Tray_MouseDown(object sender, MouseButtonEventArgs e) => TrayIconInput.Down(sender, e);

    private void Tray_MouseUp(object sender, MouseButtonEventArgs e) => TrayIconInput.Up(sender, e);

    private void Tray_LostCapture(object sender, MouseEventArgs e) => TrayIconInput.LostCapture(sender);

    private double TrayIconSize => Resources["TrayIconSize"] is double size ? size : 16;

    /// <summary>The arrow opens (or closes) the panel of the tray icons that are not on the bar.</summary>
    private void Overflow_Click(object sender, RoutedEventArgs e)
    {
        _host.HidePreview();
        Ui.Safe(() => TrayOverflowWindow.Toggle(OverflowButton, _monitor, _edge, HiddenIcons, TrayIconSize,
            _host.UnpinTrayIcon), "Hidden icons");
    }

    private void OpenHiddenForDrag()
    {
        if (TrayIconInput.Dragging == null) return;
        Ui.Safe(() => TrayOverflowWindow.OpenForDrag(OverflowButton, _monitor, _edge, HiddenIcons, TrayIconSize,
            _host.UnpinTrayIcon), "Hidden icons");
    }

    private void Overflow_DragEnter(object sender, DragEventArgs e)
    {
        Overflow_DragOver(sender, e);
        if (TrayIconInput.DraggedIcon(e) != null) _springOpen.Start();
    }

    private void Overflow_DragOver(object sender, DragEventArgs e)
    {
        HideDropMark();
        // An icon from the bar dropped on the arrow goes behind it; one from the panel is there already.
        e.Effects = TrayIconInput.DraggedIcon(e) is { IsPinned: true } ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Overflow_DragLeave(object sender, DragEventArgs e) => _springOpen.Stop();

    private void Overflow_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _springOpen.Stop();
        if (TrayIconInput.DraggedIcon(e) is not { IsPinned: true } icon) return;
        // Once the drag is over: until then the icon on the bar is still what is being dragged.
        Dispatcher.InvokeAsync(() => _host.UnpinTrayIcon(icon));
    }

    /// <summary>A tray icon dragged over the bar: a line shows where among the tray icons it would land.</summary>
    private void Bar_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!_showTrayIcons || TrayIconInput.DraggedIcon(e) is not { } icon)
        {
            e.Effects = DragDropEffects.None;
            HideDropMark();
            return;
        }

        e.Effects = DragDropEffects.Move;
        var (_, mark, moves) = DropPlace(e.GetPosition(BarRoot), icon);
        if (!moves)
        {
            HideDropMark();
            return;
        }
        Canvas.SetLeft(DropMark, mark.Left);
        Canvas.SetTop(DropMark, mark.Top);
        DropMark.Width = mark.Width;
        DropMark.Height = mark.Height;
        DropMark.Visibility = Visibility.Visible;
    }

    private void Bar_DragLeave(object sender, DragEventArgs e) => HideDropMark();

    private void Bar_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        HideDropMark();
        if (!_showTrayIcons || TrayIconInput.DraggedIcon(e) is not { } icon) return;

        var (before, _, moves) = DropPlace(e.GetPosition(BarRoot), icon);
        if (!moves) return;
        // Once the drag is over: until then the icon it came from is still what is being dragged.
        Dispatcher.InvokeAsync(() => _host.PinTrayIcon(icon, before));
    }

    private void HideDropMark() => DropMark.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Where a tray icon dropped at this point of the bar lands: just before the icon this returns (null:
    /// after the last one), next to whichever icon on the bar is nearest. Also the line that shows that
    /// spot, in the bar's own coordinates, and whether dropping there changes anything.
    /// </summary>
    private (NotifyIcon? Before, Rect Mark, bool Moves) DropPlace(Point at, NotifyIcon dragged)
    {
        const double line = 2;
        var cells = new List<(NotifyIcon Icon, Rect Box)>();
        for (int i = 0; i < TrayList.Items.Count; i++)
        {
            if (TrayList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement cell &&
                cell.DataContext is NotifyIcon icon && cell.IsVisible)
            {
                cells.Add((icon, new Rect(cell.TransformToAncestor(BarRoot).Transform(new Point(0, 0)), cell.RenderSize)));
            }
        }

        if (cells.Count == 0)
        {
            // None on the bar yet: the first one goes just past the arrow.
            var arrow = new Rect(OverflowButton.TransformToAncestor(BarRoot).Transform(new Point(0, 0)), OverflowButton.RenderSize);
            var first = _vertical
                ? new Rect(arrow.Left + 4, arrow.Bottom, Math.Max(0, arrow.Width - 8), line)
                : new Rect(arrow.Right, arrow.Top + 4, line, Math.Max(0, arrow.Height - 8));
            return (null, first, !dragged.IsPinned);
        }

        int nearest = 0;
        double best = double.MaxValue;
        for (int i = 0; i < cells.Count; i++)
        {
            var box = cells[i].Box;
            double distance = (at - new Point(box.Left + box.Width / 2, box.Top + box.Height / 2)).LengthSquared;
            if (distance < best)
            {
                best = distance;
                nearest = i;
            }
        }

        // Rows run across in both layouts (a side bar wraps its icons into rows), so the side is left or right.
        var near = cells[nearest].Box;
        int spot = at.X > near.Left + near.Width / 2 ? nearest + 1 : nearest;
        var before = spot < cells.Count ? cells[spot].Icon : null;
        var after = spot > 0 ? cells[spot - 1].Icon : null;
        // Dropped right beside itself, an icon on the bar stays where it is.
        bool moves = !dragged.IsPinned || (!ReferenceEquals(before, dragged) && !ReferenceEquals(after, dragged));

        double x = (spot > nearest ? near.Right : near.Left) - line / 2;
        return (before, new Rect(x, near.Top + 4, line, Math.Max(0, near.Height - 8)), moves);
    }

    // ------------------------------------------------------------------ network, sound, clock

    /// <summary>The network glyph follows the real connection: a cable, Wi-Fi with its strength, or none.</summary>
    private void RefreshNetwork()
    {
        var state = _host.Network.State;
        NetworkGlyph.Text = state.Glyph;
        QuickButton.ToolTip = state.Description + "\nClick for quick settings, right-click for sound or network options";
    }

    private void Quick_Click(object sender, RoutedEventArgs e) => Ui.Safe(ShellHelper.ShowActionCenter, "Quick settings");

    private void Quick_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _host.HidePreview();

        // Two glyphs share the button: the menu is for the one under the mouse.
        bool network = e.GetPosition(VolumeGlyph).X < -4;
        Ui.Safe(() => Ui.ShowMenu(QuickButton, PlacementMode.MousePoint, network ? TrayMenus.Network() : TrayMenus.Sound()),
            "Tray menu");
    }

    private void Cast_Click(object sender, RoutedEventArgs e) => _host.OpenCast();

    private void Clock_Click(object sender, RoutedEventArgs e) =>
        Ui.Safe(ShellHelper.ShowNotificationCenter, "Notification center");

    // ------------------------------------------------------------------ weather

    /// <summary>Puts the latest weather report on its button, or says what is missing for one.</summary>
    internal void RefreshWeather()
    {
        var settings = Settings.Current.Weather;
        bool show = _sampleWeather != null || (settings.Show && !_cramped && (_main || settings.AllMonitors));
        if (!show)
        {
            WeatherButton.Visibility = Visibility.Collapsed;
            return;
        }

        var report = _sampleWeather ?? _host.Weather.Report;
        string? problem = _sampleWeather == null ? _host.Weather.Problem : null;
        string tip;
        if (report != null)
        {
            WeatherIcon.Child = WeatherIcons.Draw(report.Code, report.IsDay);
            WeatherTemp.Text = report.TemperatureText;
            WeatherWords.Text = report.Conditions;
            tip = $"{(report.Place.Length > 0 ? report.Place : "Weather")}: {report.TemperatureText}, {report.Conditions}";
            if (problem != null) tip += "\n" + problem;
        }
        else if (!settings.HasPlace)
        {
            WeatherIcon.Child = WeatherIcons.Draw(WeatherKind.PartlyCloudy, day: true);
            WeatherTemp.Text = _vertical ? "" : "Set location";
            WeatherWords.Text = "";
            tip = "Weather: click to choose your location";
        }
        else
        {
            WeatherIcon.Child = WeatherIcons.Draw(WeatherKind.Cloudy, day: true);
            WeatherTemp.Text = "…";
            WeatherWords.Text = "";
            tip = problem ?? "Getting the weather…";
        }

        WeatherTemp.Visibility = WeatherTemp.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // A side bar has room for the picture and the temperature only.
        WeatherWords.Visibility = WeatherWords.Text.Length > 0 && settings.ShowText && !_vertical
            ? Visibility.Visible
            : Visibility.Collapsed;
        WeatherButton.ToolTip = tip;
        WeatherButton.Visibility = Visibility.Visible;
    }

    private void Weather_Click(object sender, RoutedEventArgs e)
    {
        _host.HidePreview();
        var report = _sampleWeather ?? _host.Weather.Report;
        if (report != null)
        {
            Ui.Safe(() => WeatherFlyout.Toggle(WeatherButton, _edge, report, _host.Weather.Problem), "Weather forecast");
        }
        else if (Settings.Current.Weather.HasPlace)
        {
            _host.Weather.Refresh();
        }
        else
        {
            _host.ShowSettings(SettingsPage.Weather);
        }
    }

    private void Weather_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _host.HidePreview();
        WeatherFlyout.Close();

        void Change(Action<WeatherSettings> change)
        {
            change(Settings.Current.Weather);
            _host.Edited(SettingsChange.Weather);
        }

        var weather = Settings.Current.Weather;
        MenuEntry Units(string text, TemperatureUnits units) =>
            new(text, () => Change(w => w.Units = units), Checked: weather.Units == units);

        Ui.ShowMenu(WeatherButton, PlacementMode.MousePoint, new[]
        {
            new MenuEntry("Refresh now", weather.HasPlace ? _host.Weather.Refresh : null),
            new MenuEntry("Set location…", () => _host.ShowSettings(SettingsPage.Weather)),
            new MenuEntry("Units", null, Children: new[]
            {
                Units("Match the Windows region", TemperatureUnits.Auto),
                Units("Fahrenheit", TemperatureUnits.Fahrenheit),
                Units("Celsius", TemperatureUnits.Celsius),
            }),
            new MenuEntry(null, null),
            new MenuEntry("Show conditions in words", () => Change(w => w.ShowText = !w.ShowText), Checked: weather.ShowText),
            new MenuEntry("Show on every taskbar", () => Change(w => w.AllMonitors = !w.AllMonitors),
                Checked: weather.AllMonitors),
            new MenuEntry(null, null),
            new MenuEntry("Hide the weather", () => Change(w => w.Show = false)),
        });
    }

    // ------------------------------------------------------------------ bar menu

    /// <summary>
    /// Which pinned apps this bar shows: all, none or only the ticked ones, and then the pinned apps
    /// themselves, each ticked when it shows here. Ticking or unticking one makes it "only the ticked ones".
    /// </summary>
    internal List<MenuEntry> PinnedAppsMenu()
    {
        var taskbar = Settings.Current.Taskbar;
        var shown = taskbar.PinsFor(_device, _main);

        MenuEntry Mode(string text, PinsShown value) =>
            new(text, () => _host.SetPinsShown(_device, _main, value), Checked: shown == value);

        var entries = new List<MenuEntry>
        {
            Mode("All of them", PinsShown.All),
            Mode("Only the ticked ones", PinsShown.Chosen),
            Mode("None", PinsShown.None),
        };
        if (_host.Pins.Taskbar.Count > 0) entries.Add(new MenuEntry(null, null));
        foreach (var pin in _host.Pins.Taskbar)
        {
            string path = pin.Path;
            bool here = taskbar.ShowsPin(_device, _main, path);
            entries.Add(new MenuEntry(pin.Name, () => _host.ShowPinOn(_device, _main, path, !here), Checked: here));
        }
        return entries;
    }

    private void Bar_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        _host.HidePreview();

        var taskbar = Settings.Current.Taskbar;
        bool locked = taskbar.Locked;
        var edge = taskbar.EdgeFor(_device);

        MenuEntry Edge(string text, BarEdge value) =>
            new(text, () => _host.MoveBar(_device, value), Checked: edge == value);
        MenuEntry Start(string text, StartButtonPlacement value) =>
            new(text, () => _host.SetStartButton(_device, value), Checked: _startPlacement == value);

        // Hiding greys out on the last bar left: with none, there would be no way back to this menu.
        Action? hide = _host.CanHideBar(_device) ? () => _host.SetBarHidden(_device, true) : null;
        var showOrHide = new List<MenuEntry> { new("Hide this taskbar", hide) };
        var hidden = _host.HiddenBars();
        if (hidden.Count > 0)
        {
            showOrHide.Add(new MenuEntry("Show the taskbar on", null, Children: hidden
                .Select(device => new MenuEntry(TaskbarSettings.MonitorKey(device), () => _host.SetBarHidden(device, false)))
                .ToList()));
        }

        var entries = new List<MenuEntry>
        {
            new MenuEntry("Show desktop", _host.ShowDesktop),
            new MenuEntry("Task Manager", ShellHelper.StartTaskManager),
            new MenuEntry(null, null),
            new MenuEntry("Move this taskbar", null, Children: new[]
            {
                Edge("Top", BarEdge.Top),
                Edge("Bottom", BarEdge.Bottom),
                Edge("Left side", BarEdge.Left),
                Edge("Right side", BarEdge.Right),
                new MenuEntry(null, null),
                Edge("Same place as the Windows taskbar", BarEdge.Auto),
            }),
            new MenuEntry("Start button", null, Children: new[]
            {
                Start("With the app buttons", StartButtonPlacement.WithApps),
                Start(_vertical ? "At the top" : "In the left corner", StartButtonPlacement.Corner),
                Start(_vertical ? "At the bottom" : "In the right corner", StartButtonPlacement.End),
                Start("Hidden", StartButtonPlacement.Hidden),
                new MenuEntry(null, null),
                new MenuEntry("Use this on every taskbar", () => _host.SetStartButtonEverywhere(_startPlacement)),
                new MenuEntry(null, null),
                new MenuEntry("Change its design…", () => _host.ShowSettings(SettingsPage.Appearance)),
            }),
            new MenuEntry("Pinned apps on this taskbar", null, Children: PinnedAppsMenu()),
            new MenuEntry(taskbar.ShowSearchButton ? "Hide the search button" : "Show the search button",
                () => _host.SetSearchButton(!taskbar.ShowSearchButton)),
        };
        entries.AddRange(showOrHide);
        entries.AddRange(new[]
        {
            new MenuEntry(locked ? "Unlock taskbar size" : "Lock taskbar size", () => _host.SetLocked(!locked)),
            new MenuEntry(null, null),
            new MenuEntry("Settings", () => _host.ShowSettings()),
            new MenuEntry(null, null),
            new MenuEntry(_host.Mode == HostMode.Live ? "Exit ClearStart (bring back the Windows taskbar)" : "Exit ClearStart",
                _host.RequestExit),
        });
        Ui.ShowMenu(BarRoot, PlacementMode.MousePoint, entries);
    }
}
