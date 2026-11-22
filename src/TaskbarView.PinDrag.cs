using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClearStart;

// A pinned app dragged along the bar to a new place, the way the Windows taskbar does it: its button follows the
// mouse, the other pinned apps slide aside to show where it will land, and when it is let go it settles there. The new
// order is the same on every bar and is kept in the settings.
public partial class TaskbarView
{
    // How long the others take to slide aside, and the dropped button to settle, in milliseconds.
    private const double SlideTime = 200, SettleTime = 180;
    // As with the pinned icons at startup: WPF may draw this faster than 60 times a second on a faster monitor.
    private const int SlideFrameRate = 240;

    private static readonly CubicEase SlideEase = MakeSlideEase();

    // A left press on a pinned app's button, which turns into a drag once the mouse has gone far enough along the bar.
    private (FrameworkElement Button, AppButton App, double At)? _pinPress;
    private PinDrag? _pinDrag;

    /// <summary>One pinned app's button during a drag: where it was laid out, and how far it is shifted from there.</summary>
    private sealed class PinSlot
    {
        public PinSlot(AppButton app, FrameworkElement container, double origin, double extent)
        {
            App = app;
            Container = container;
            Origin = origin;
            Extent = extent;
        }

        public AppButton App { get; }
        public FrameworkElement Container { get; }
        public double Origin { get; }
        public double Extent { get; }
        public double Middle => Origin + Extent / 2;
        public TranslateTransform Shift { get; } = new();

        /// <summary>Where its slide is heading: nowhere, or the dragged button's length one way or the other.</summary>
        public double Heading { get; set; }
    }

    private sealed class PinDrag
    {
        public PinDrag(FrameworkElement button, List<PinSlot> row, int from, double start)
        {
            Button = button;
            Row = row;
            From = from;
            To = from;
            Start = start;
        }

        public FrameworkElement Button { get; }

        /// <summary>The pinned apps on this bar, in order.</summary>
        public List<PinSlot> Row { get; }

        public int From { get; }

        /// <summary>The place in the row it lands in if it is let go now.</summary>
        public int To { get; set; }

        /// <summary>How far along the bar the mouse was when it went down.</summary>
        public double Start { get; }
    }

    private static CubicEase MakeSlideEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    private DependencyProperty ShiftProperty => _vertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;

    /// <summary>A left press on an app's button. On a pinned app's, it may be the start of a drag.</summary>
    private void PressPin(FrameworkElement button, AppButton app, MouseButtonEventArgs e) =>
        _pinPress = app.Pin != null && _pinDrag == null ? (button, app, Along(e.GetPosition(AppList))) : null;

    private void App_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pinDrag is { } drag)
        {
            if (!ReferenceEquals(drag.Button, sender)) return;
            // Let go without this bar hearing of it: it lands where it is.
            if (e.LeftButton != MouseButtonState.Pressed) EndPinDrag(drop: true);
            else MovePinDrag(Along(e.GetPosition(AppList)));
            return;
        }

        if (_pinPress is not { } press || !ReferenceEquals(press.Button, sender)) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pinPress = null;
            return;
        }
        double at = Along(e.GetPosition(AppList));
        double far = _vertical ? SystemParameters.MinimumVerticalDragDistance : SystemParameters.MinimumHorizontalDragDistance;
        if (Math.Abs(at - press.At) < far) return;
        _pinPress = null;
        if (BeginPinDrag(press.Button, press.App, press.At)) MovePinDrag(at);
    }

    private void App_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _pinPress = null;
        if (_pinDrag is not { } drag || !ReferenceEquals(drag.Button, sender)) return;
        // A drag is not a click: the app is not opened or brought up.
        e.Handled = true;
        EndPinDrag(drop: true);
    }

    private void App_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_pinPress is { } press && ReferenceEquals(press.Button, sender)) _pinPress = null;
        // Something else took the mouse in the middle of a drag: everything goes back where it was.
        if (_pinDrag is { } drag && ReferenceEquals(drag.Button, sender)) EndPinDrag(drop: false);
    }

    /// <summary>Starts dragging a pinned app's button. False when it has nothing to move among, or is not laid out.</summary>
    private bool BeginPinDrag(FrameworkElement button, AppButton app, double start)
    {
        // The pinned apps come first on the bar, in their pinned order.
        var row = new List<PinSlot>();
        foreach (var item in _apps.Items)
        {
            if (item.Pin == null) continue;
            if (AppList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container
                || VisualTreeHelper.GetParent(container) == null)
            {
                return false;
            }
            row.Add(new PinSlot(item, container, Along(VisualTreeHelper.GetOffset(container)),
                Along(container.ActualWidth, container.ActualHeight)));
        }
        int from = row.FindIndex(slot => ReferenceEquals(slot.App, app));
        if (from < 0 || row.Count < 2) return false;

        _host.CancelPreviewHover();
        _host.HidePreview();
        foreach (var slot in row) slot.Container.RenderTransform = slot.Shift;
        // Drawn over the buttons it passes, and with no tooltip trailing after it.
        Panel.SetZIndex(row[from].Container, 1);
        ToolTipService.SetIsEnabled(button, false);
        _pinDrag = new PinDrag(button, row, from, start);
        _apps.Items.CollectionChanged += Apps_ChangedMidDrag;
        return true;
    }

    /// <summary>The mouse is this far along the bar: the dragged button follows it, and the others make room.</summary>
    private void MovePinDrag(double along)
    {
        if (_pinDrag is not { } drag) return;
        var row = drag.Row;
        var dragged = row[drag.From];

        // It goes as far as either end of the pinned apps, and no further.
        double least = row[0].Origin - dragged.Origin;
        double most = row[^1].Origin + row[^1].Extent - (dragged.Origin + dragged.Extent);
        double wanted = along - drag.Start;
        SetShift(dragged.Shift, Math.Max(least, Math.Min(most, wanted)));

        // A button steps aside once the middle of the dragged one has passed its own middle. Counted from the mouse,
        // so the button at either end can be passed though the dragged one stops level with it.
        double middle = dragged.Middle + wanted;
        int to = drag.From;
        for (int i = 0; i < row.Count; i++)
        {
            if (i == drag.From) continue;
            var slot = row[i];
            double heading = 0;
            if (i < drag.From && middle < slot.Middle)
            {
                heading = dragged.Extent;
                to--;
            }
            else if (i > drag.From && middle > slot.Middle)
            {
                heading = -dragged.Extent;
                to++;
            }
            if (heading != slot.Heading)
            {
                slot.Heading = heading;
                Slide(slot.Shift, null, heading, SlideTime);
            }
        }
        drag.To = to;
    }

    /// <summary>
    /// Ends a drag. Let go in a new place, the pin moves there on every bar, and the buttons here settle into their new
    /// places from where they are drawn. Otherwise they all slide back.
    /// </summary>
    private void EndPinDrag(bool drop)
    {
        if (_pinDrag is not { } drag) return;
        _pinDrag = null;
        _apps.Items.CollectionChanged -= Apps_ChangedMidDrag;
        drag.Button.ClearValue(ToolTipService.IsEnabledProperty);
        // Without the mouse, the button no longer counts as pressed, so letting go is not taken for a click.
        if (drag.Button.IsMouseCaptured) drag.Button.ReleaseMouseCapture();

        if (drop && drag.To != drag.From)
        {
            try
            {
                if (SettleMovedPin(drag)) return;
            }
            catch (Exception ex)
            {
                Log.Error("Could not move a pinned app", ex);
            }
        }

        var dragged = drag.Row[drag.From];
        foreach (var slot in drag.Row)
        {
            slot.Heading = 0;
            Slide(slot.Shift, null, 0, SettleTime,
                ReferenceEquals(slot, dragged) ? () => dragged.Container.ClearValue(Panel.ZIndexProperty) : null);
        }
    }

    /// <summary>Moves the dropped pin to its new place, then lets each button settle there from where it is drawn now.</summary>
    private bool SettleMovedPin(PinDrag drag)
    {
        var dragged = drag.Row[drag.From];
        var others = drag.Row.Where(slot => !ReferenceEquals(slot, dragged)).Select(slot => slot.App.Pin!).ToList();
        // Where each button is drawn now, before the move lays them out afresh.
        var drawn = drag.Row.Select(slot => (slot.App, At: slot.Origin + (double)slot.Shift.GetValue(ShiftProperty))).ToList();

        // Next to the pinned app it now comes before, or after the last one. Pins this bar does not show stay put.
        bool moved = drag.To < others.Count
            ? _host.Pins.MoveOnTaskbar(dragged.App.Pin!, others[drag.To], after: false)
            : _host.Pins.MoveOnTaskbar(dragged.App.Pin!, others[^1], after: true);
        if (!moved) return false;
        Log.Info($"Pinned app moved on the taskbar from place {drag.From + 1} to {drag.To + 1} of {drag.Row.Count}");

        // Laid out in the new order before the bar is drawn again, so nothing jumps.
        _apps.UpdateNow();
        AppList.UpdateLayout();
        foreach (var (app, at) in drawn)
        {
            if (AppList.ItemContainerGenerator.ContainerFromItem(app) is not FrameworkElement container) continue;
            var shift = new TranslateTransform();
            container.RenderTransform = shift;
            bool isDragged = ReferenceEquals(app, dragged.App);
            Slide(shift, at - Along(VisualTreeHelper.GetOffset(container)), 0, isDragged ? SettleTime : SlideTime,
                isDragged ? () => container.ClearValue(Panel.ZIndexProperty) : null);
        }
        return true;
    }

    // A button came or went in the middle of a drag, so the places worked out when it began are out of date.
    private void Apps_ChangedMidDrag(object? sender, NotifyCollectionChangedEventArgs e) => EndPinDrag(drop: false);

    private void SetShift(TranslateTransform shift, double value)
    {
        shift.BeginAnimation(ShiftProperty, null);
        shift.SetValue(ShiftProperty, value);
    }

    /// <summary>
    /// Slides a button to a shift, from where it is or from the shift given. With animations turned off in Windows it is
    /// put there at once.
    /// </summary>
    private void Slide(TranslateTransform shift, double? from, double to, double milliseconds, Action? done = null)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            SetShift(shift, to);
            done?.Invoke();
            return;
        }

        var slide = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = SlideEase };
        if (from is { } start) slide.From = start;
        Timeline.SetDesiredFrameRate(slide, SlideFrameRate);
        if (done != null) slide.Completed += (_, _) => done();
        shift.BeginAnimation(ShiftProperty, slide, HandoffBehavior.SnapshotAndReplace);
    }

    // ------------------------------------------------------------------ for the self-test

    /// <summary>The middle of each pinned app's button along the bar, in order: where it is laid out, or where it is drawn now.</summary>
    internal List<double> PinMiddles(bool drawn = false) => _apps.Items
        .Where(app => app.Pin != null)
        .Select(app => AppList.ItemContainerGenerator.ContainerFromItem(app) is FrameworkElement container
            ? Along(VisualTreeHelper.GetOffset(container)) + Along(container.ActualWidth, container.ActualHeight) / 2
                + (drawn && container.RenderTransform is TranslateTransform shift ? (double)shift.GetValue(ShiftProperty) : 0)
            : double.NaN)
        .ToList();

    /// <summary>Takes hold of the pinned app at this place in the row by its middle, as a press and a move would.</summary>
    internal bool StartPinDragForTest(int index)
    {
        var pinned = _apps.Items.Where(app => app.Pin != null).ToList();
        if (index < 0 || index >= pinned.Count) return false;
        if (AppList.ItemContainerGenerator.ContainerFromItem(pinned[index]) is not FrameworkElement container) return false;
        return BeginPinDrag(container, pinned[index], PinMiddles()[index]);
    }

    internal void MovePinDragForTest(double along) => MovePinDrag(along);

    /// <summary>Where the dragged pin would land now, as a place in the row; -1 with no drag on.</summary>
    internal int PinDropPlace => _pinDrag?.To ?? -1;

    internal void EndPinDragForTest(bool drop) => EndPinDrag(drop);
}
