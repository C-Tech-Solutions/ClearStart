using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using ManagedShell.ShellFolders;

namespace ClearStart;

// The start menu's motion. A dragged tile's icon floats under the pointer and flies to where it lands, the other tiles
// slide to their new places, and a folder opens out of its tile and closes back into it. All of it is left out when
// Windows' animation effects are off.
public partial class StartMenuView
{
    private static readonly Brush FolderCardBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));

    private readonly ScaleTransform _ghostScale = new();
    /// <summary>How far the floating icon is from the pointer: from where it was picked up, closing in as it rises.</summary>
    private readonly TranslateTransform _ghostGrab = new();
    private readonly TranslateTransform _ghostMove = new();
    private readonly DropShadowEffect _ghostShadow = new()
    {
        BlurRadius = 18, ShadowDepth = 6, Direction = 270, Color = Colors.Black, Opacity = 0,
    };
    /// <summary>Counts the floating icon's flights, so one that was overtaken does not end the next.</summary>
    private int _ghostRun;
    /// <summary>A tile kept hidden or faint until the floating icon reaches it.</summary>
    private UIElement? _ghostHidden;
    /// <summary>Set when a drag ends in a drop, which then decides where the floating icon goes.</summary>
    private bool _dropped;
    /// <summary>The tile a drop would go into, grown a little.</summary>
    private FrameworkElement? _hoverTile;

    private readonly ScaleTransform _stageScale = new();
    private readonly ScaleTransform _beforeScale = new();
    /// <summary>Counts a folder's openings and closings, so one that was overtaken does not end the next.</summary>
    private int _folderRun;
    /// <summary>Where the pins were scrolled to when a folder opened, to come back to.</summary>
    private double _pinsOffset;

    private void SetUpMotion()
    {
        Ghost.RenderTransform = new TransformGroup { Children = { _ghostScale, _ghostGrab, _ghostMove } };
        Ghost.Effect = _ghostShadow;
        PinStage.RenderTransform = _stageScale;
        PinsBefore.RenderTransform = _beforeScale;
    }

    /// <summary>Puts everything that moves back at rest at once: the menu closed, or opens again.</summary>
    private void EndMotion()
    {
        StopFolderMotion();
        HideGhost();
        HoverOnto(null);
    }

    // ------------------------------------------------------------------ the floating icon

    /// <summary>
    /// Lifts a copy of the dragged thing's icon out of its place, a little larger, to follow the pointer: where it is,
    /// or <paramref name="pointer"/> in the menu's terms.
    /// </summary>
    private void LiftGhost(FrameworkElement source, object dragged, Point? pointer = null)
    {
        HideGhost();
        if (FindIcon(source) is not { } icon || (pointer ?? CursorIn(MotionLayer)) is not { } cursor
            || Bounds(icon, MotionLayer) is not { } from)
        {
            return;
        }

        double size;
        if (dragged is PinFolder folder)
        {
            Ghost.Child = new ContentPresenter { Content = folder, ContentTemplate = (DataTemplate)Resources["FolderPlate"] };
            size = 38;
        }
        else
        {
            // The large icon, as a tile shows it; a row of the list shows a smaller one, until the large one is ready.
            var image = new Image { Width = 36, Height = 36 };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            string? large = source.DataContext switch
            {
                PinnedItem => "Item.ExtraLargeIcon",
                ShellItem => nameof(ShellItem.ExtraLargeIcon),
                _ => null,
            };
            if (large != null)
            {
                image.SetBinding(Image.SourceProperty,
                    new Binding(large) { Source = source.DataContext, TargetNullValue = (icon as Image)?.Source });
            }
            else
            {
                image.Source = (icon as Image)?.Source;
            }
            Ghost.Child = image;
            size = 36;
        }
        Ghost.Width = size;
        Ghost.Height = size;

        // It starts just where the icon is, and rises towards the pointer.
        Vector grab = Center(from) - cursor;
        Motion.Set(_ghostMove, TranslateTransform.XProperty, cursor.X - size / 2);
        Motion.Set(_ghostMove, TranslateTransform.YProperty, cursor.Y - size / 2);
        Motion.To(_ghostGrab, TranslateTransform.XProperty, Math.Clamp(grab.X, -14, 14), 200, Motion.Out, from: grab.X);
        Motion.To(_ghostGrab, TranslateTransform.YProperty, Math.Clamp(grab.Y, -14, 14), 200, Motion.Out, from: grab.Y);
        ScaleBoth(_ghostScale, from.Width / size, 1.15, 220, Motion.Pop);
        Motion.To(_ghostShadow, DropShadowEffect.OpacityProperty, 0.45, 220, Motion.Out, from: 0);
        Motion.Set(Ghost, OpacityProperty, 1);
        Ghost.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// While dragging, the floating icon stands for the pointer's own picture. It goes a little faint where nothing
    /// can be dropped.
    /// </summary>
    private void View_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (Ghost.Visibility != Visibility.Visible) return;
        if (CursorIn(MotionLayer) is { } cursor)
        {
            _ghostMove.X = cursor.X - Ghost.Width / 2;
            _ghostMove.Y = cursor.Y - Ghost.Height / 2;
        }
        Ghost.Opacity = e.Effects == DragDropEffects.None ? 0.6 : 1;
        e.UseDefaultCursors = false;
        Mouse.SetCursor(Cursors.Arrow);
        e.Handled = true;
    }

    /// <summary>Let go where nothing was dropped: the icon flies back to where it came from.</summary>
    private void ReturnGhost(FrameworkElement source)
    {
        if (Ghost.Visibility == Visibility.Visible && FindIcon(source) is { } home)
        {
            FlyGhost(home, 1, fade: false, hide: source);
            return;
        }
        HideGhost();
        Restore(source);
    }

    /// <summary>After a drop: the icon flies into the tile it became, or into the folder it went into.</summary>
    private void Land(IStartTile? landed)
    {
        if (Ghost.Visibility != Visibility.Visible) return;
        if (landed != null && TileOf(landed) is { } own && FindIcon(own) is { } icon)
        {
            Motion.Set(own, OpacityProperty, 0);
            FlyGhost(icon, 1, fade: false, hide: own);
        }
        else if (landed is PinnedItem pin && _host.Pins.FolderOf(pin) is { } folder && TileOf(folder) is { } tile)
        {
            FlyGhost(FindIcon(tile), 0.4, fade: true, done: () => Bounce(tile));
        }
        else
        {
            // Out of the open folder by its arrow: it goes that way. Otherwise it fades where it is.
            FlyGhost(_openFolder != null ? FolderBack : null, 0.5, fade: true);
        }
    }

    /// <summary>
    /// Sends the floating icon to <paramref name="target"/>, at its size times <paramref name="scale"/>, or lets it
    /// fade where it is. <paramref name="hide"/> stays as it is until the icon arrives, and shows again then.
    /// </summary>
    private void FlyGhost(FrameworkElement? target, double scale, bool fade, UIElement? hide = null, Action? done = null)
    {
        if (Ghost.Visibility != Visibility.Visible)
        {
            if (hide != null) Restore(hide);
            done?.Invoke();
            return;
        }
        int run = ++_ghostRun;
        _ghostHidden = hide;

        double size = Ghost.Width;
        Point to;
        double toScale;
        if (target is { IsVisible: true } && Bounds(target, MotionLayer) is { } bounds)
        {
            to = Center(bounds);
            toScale = bounds.Width / size * scale;
        }
        else
        {
            to = new Point(_ghostMove.X + _ghostGrab.X + size / 2, _ghostMove.Y + _ghostGrab.Y + size / 2);
            toScale = 0.6;
        }

        const double time = 240;
        Motion.To(_ghostMove, TranslateTransform.XProperty, to.X - size / 2, time, Motion.Out);
        Motion.To(_ghostMove, TranslateTransform.YProperty, to.Y - size / 2, time, Motion.Out);
        Motion.To(_ghostGrab, TranslateTransform.XProperty, 0, time, Motion.Out);
        Motion.To(_ghostGrab, TranslateTransform.YProperty, 0, time, Motion.Out);
        ScaleBoth(_ghostScale, null, toScale, time, Motion.Out);
        Motion.To(_ghostShadow, DropShadowEffect.OpacityProperty, 0, time, Motion.Out);
        Motion.To(Ghost, OpacityProperty, fade ? 0 : 1, time, Motion.In, done: () =>
        {
            if (run != _ghostRun) return;
            HideGhost();
            done?.Invoke();
        });
    }

    private void HideGhost()
    {
        _ghostRun++;
        Ghost.Visibility = Visibility.Collapsed;
        Ghost.Child = null;
        Motion.Set(_ghostMove, TranslateTransform.XProperty, 0);
        Motion.Set(_ghostMove, TranslateTransform.YProperty, 0);
        Motion.Set(_ghostGrab, TranslateTransform.XProperty, 0);
        Motion.Set(_ghostGrab, TranslateTransform.YProperty, 0);
        ScaleBoth(_ghostScale, null, 1, 0, null);
        Motion.Set(_ghostShadow, DropShadowEffect.OpacityProperty, 0);
        Motion.Set(Ghost, OpacityProperty, 1);
        if (_ghostHidden is { } hidden)
        {
            _ghostHidden = null;
            Restore(hidden);
        }
    }

    // ------------------------------------------------------------------ tiles moving

    /// <summary>Settles a drop once the drag is over and the dragged tile is itself again.</summary>
    private void AfterDrop(object dragged, Action change)
    {
        _dropped = true;
        Dispatcher.InvokeAsync(() =>
        {
            int run = _ghostRun;
            Ui.Safe(() => Settle(dragged, change), "Pin");
            // Nothing took the floating icon on: it goes.
            if (run == _ghostRun) HideGhost();
        });
    }

    /// <summary>Makes the change a drop asked for: the other tiles slide, and the floating icon flies to its place.</summary>
    private void Settle(object dragged, Action change)
    {
        var spots = TileSpots();
        change();
        UpdateLayout();
        var landed = dragged as IStartTile ?? (dragged is string path ? _host.Pins.FindStart(path) : null);
        Slide(spots, landed);
        Land(landed);
    }

    /// <summary>Makes a change to the pins from a menu, the tiles sliding to their new places.</summary>
    private void Rearrange(Action change)
    {
        var spots = TileSpots();
        change();
        Slide(spots, null);
    }

    private readonly record struct Spots(IEnumerable? Source, Dictionary<object, Point> Places);

    /// <summary>Where each tile is now, to slide them from there once they have moved.</summary>
    private Spots TileSpots()
    {
        var places = new Dictionary<object, Point>(ReferenceEqualityComparer.Instance);
        foreach (var item in PinList.Items)
        {
            if (PinList.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement cell && cell.IsVisible)
            {
                places[item] = cell.TranslatePoint(new Point(), PinList);
            }
        }
        return new Spots(PinList.ItemsSource, places);
    }

    /// <summary>
    /// Slides the tiles that moved from where they were, and grows the new ones in. Not <paramref name="landed"/>,
    /// which the floating icon brings, and nothing when the pins shown changed for a folder's or back.
    /// </summary>
    private void Slide(Spots spots, object? landed)
    {
        if (!Motion.On || !ReferenceEquals(PinList.ItemsSource, spots.Source)) return;
        UpdateLayout();
        foreach (var item in PinList.Items)
        {
            if (ReferenceEquals(item, landed) || PinList.ItemContainerGenerator.ContainerFromItem(item) is not UIElement cell
                || TileOf(item) is not { } tile)
            {
                continue;
            }
            var (scale, move) = Parts(tile);
            if (spots.Places.TryGetValue(item, out var was))
            {
                var shift = was - cell.TranslatePoint(new Point(), PinList);
                if (shift.Length < 0.5) continue;
                Motion.To(move, TranslateTransform.XProperty, 0, 260, Motion.Out, from: shift.X);
                Motion.To(move, TranslateTransform.YProperty, 0, 260, Motion.Out, from: shift.Y);
            }
            else
            {
                ScaleBoth(scale, 0.6, 1, 260, Motion.Pop);
                Motion.To(tile, OpacityProperty, 1, 160, Motion.Out, from: 0);
            }
        }
    }

    /// <summary>The tile a drop would go into grows a little while the pointer is over it.</summary>
    private void HoverOnto(IStartTile? onto)
    {
        var tile = onto != null ? TileOf(onto) : null;
        if (ReferenceEquals(tile, _hoverTile)) return;
        if (_hoverTile != null) ScaleBoth(Parts(_hoverTile).Scale, null, 1, 160, Motion.Out);
        _hoverTile = tile;
        if (tile != null) ScaleBoth(Parts(tile).Scale, null, 1.06, 200, Motion.Pop);
    }

    /// <summary>A tile swells and settles, as something goes into it.</summary>
    private static void Bounce(UIElement tile) => ScaleBoth(Parts(tile).Scale, 1.12, 1, 320, Motion.Pop);

    // ------------------------------------------------------------------ a folder opening and closing

    /// <summary>
    /// A folder grows out of its tile as it opens, the pins going past and fading; closing, it shrinks back into its
    /// tile as the pins come forward again. <paramref name="before"/> is a picture of what was shown before,
    /// <paramref name="tile"/> where the folder's tile is among the pins, and <paramref name="closedInto"/> that tile,
    /// once the pins are back.
    /// </summary>
    private void PlayFolderMotion(BitmapSource before, bool opening, Rect? tile, FrameworkElement? closedInto)
    {
        int run = ++_folderRun;
        double width = PinStage.ActualWidth, height = PinStage.ActualHeight;
        if (width < 1 || height < 1) return;
        var center = tile is { } at
            ? new Point(Math.Clamp(at.X + at.Width / 2, 0, width), Math.Clamp(at.Y + at.Height / 2, 0, height))
            : new Point(width / 2, height / 3);
        double small = tile is { } size ? Math.Clamp(Math.Min(size.Width / width, size.Height / height), 0.15, 0.6) : 0.6;

        PinsBefore.Source = before;
        PinsBefore.Visibility = Visibility.Visible;
        // Opening, the folder comes over the pins; closing, it goes into its tile over them.
        Panel.SetZIndex(PinsBefore, opening ? -1 : 1);
        _beforeScale.CenterX = _stageScale.CenterX = center.X;
        _beforeScale.CenterY = _stageScale.CenterY = center.Y;

        void Done()
        {
            if (run != _folderRun) return;
            PinsBefore.Visibility = Visibility.Collapsed;
            PinsBefore.Source = null;
            if (!opening && closedInto != null) Bounce(closedInto);
        }

        if (opening)
        {
            ScaleBoth(_beforeScale, 1, 1.08, 260, Motion.Out);
            Motion.To(PinsBefore, OpacityProperty, 0, 220, Motion.Out, from: 1, done: Done);
            ScaleBoth(_stageScale, small, 1, 340, Motion.Pop);
            Motion.To(PinStage, OpacityProperty, 1, 160, Motion.Out, from: 0);
        }
        else
        {
            ScaleBoth(_beforeScale, 1, small, 260, Motion.Out);
            Motion.To(PinsBefore, OpacityProperty, 0, 240, Motion.In, from: 1, done: Done);
            ScaleBoth(_stageScale, 1.06, 1, 280, Motion.Out);
            Motion.To(PinStage, OpacityProperty, 1, 220, Motion.Out, from: 0);
        }
    }

    private void StopFolderMotion()
    {
        _folderRun++;
        PinsBefore.Visibility = Visibility.Collapsed;
        PinsBefore.Source = null;
        ScaleBoth(_beforeScale, null, 1, 0, null);
        Motion.Set(PinsBefore, OpacityProperty, 1);
        ScaleBoth(_stageScale, null, 1, 0, null);
        Motion.Set(PinStage, OpacityProperty, 1);
        Motion.Set(PinsHeader, OpacityProperty, 1);
        Motion.Set(FolderHeader, OpacityProperty, 1);
    }

    /// <summary>A picture of an element as it is now, the size it is on screen.</summary>
    private static BitmapSource? Snapshot(FrameworkElement element)
    {
        double width = element.ActualWidth, height = element.ActualHeight;
        if (width < 1 || height < 1) return null;
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // Drawn through a brush, so where the element sits in the menu does not shift the picture.
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var area = new Rect(0, 0, width, height);
            context.DrawRectangle(new VisualBrush(element)
            {
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = area,
            }, null, area);
        }
        bitmap.Render(drawing);
        bitmap.Freeze();
        return bitmap;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A pinned tile's button, as drawn: the one with the icon, which is what moves.</summary>
    private FrameworkElement? TileOf(object item) =>
        PinList.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement cell && VisualTreeHelper.GetChildrenCount(cell) > 0
            ? VisualTreeHelper.GetChild(cell, 0) as FrameworkElement
            : null;

    /// <summary>Where a tile is among the pins, in the stage's own terms.</summary>
    private Rect? TileBounds(IStartTile tile) =>
        PinList.ItemContainerGenerator.ContainerFromItem(tile) is FrameworkElement { IsVisible: true } cell
            ? Bounds(cell, PinStage)
            : null;

    /// <summary>The element named Icon in a tile or a row of the list, as the templates name it.</summary>
    private static FrameworkElement? FindIcon(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (next is FrameworkElement { Name: "Icon" } icon) return icon.ActualWidth > 0 ? icon : null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(next); i++) queue.Enqueue(VisualTreeHelper.GetChild(next, i));
        }
        return null;
    }

    private static Rect? Bounds(FrameworkElement element, Visual to)
    {
        if (element.ActualWidth <= 0 || !element.IsDescendantOf(to.FindCommonVisualAncestor(element))) return null;
        try
        {
            return element.TransformToVisual(to).TransformBounds(new Rect(element.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static Point Center(Rect bounds) => new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

    private static Point? CursorIn(Visual visual)
    {
        if (PresentationSource.FromVisual(visual) == null || !Native.GetCursorPos(out var cursor)) return null;
        return visual.PointFromScreen(new Point(cursor.X, cursor.Y));
    }

    /// <summary>A tile's own scale and offset, which the layout does not see; made the first time they are needed.</summary>
    private static (ScaleTransform Scale, TranslateTransform Move) Parts(UIElement tile)
    {
        if (tile.RenderTransform is TransformGroup { Children.Count: 2 } group
            && group.Children[0] is ScaleTransform scale && group.Children[1] is TranslateTransform move)
        {
            return (scale, move);
        }
        (scale, move) = (new ScaleTransform(), new TranslateTransform());
        tile.RenderTransform = new TransformGroup { Children = { scale, move } };
        tile.RenderTransformOrigin = new Point(0.5, 0.5);
        return (scale, move);
    }

    private static void ScaleBoth(ScaleTransform scale, double? from, double to, double time, IEasingFunction? ease)
    {
        Motion.To(scale, ScaleTransform.ScaleXProperty, to, time, ease, from);
        Motion.To(scale, ScaleTransform.ScaleYProperty, to, time, ease, from);
    }

    /// <summary>Shows an element again as its template draws it.</summary>
    private static void Restore(UIElement element)
    {
        element.BeginAnimation(OpacityProperty, null);
        element.ClearValue(OpacityProperty);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// <summary>Short animations, left out when Windows' animation effects are off.</summary>
internal static class Motion
{
    internal static bool On => SystemParameters.ClientAreaAnimation;

    internal static readonly IEasingFunction Out = Ease(new CubicEase { EasingMode = EasingMode.EaseOut });
    internal static readonly IEasingFunction In = Ease(new CubicEase { EasingMode = EasingMode.EaseIn });
    /// <summary>Goes a little past and comes back: a pop.</summary>
    internal static readonly IEasingFunction Pop = Ease(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });

    /// <summary>
    /// Moves a value to <paramref name="to"/> over <paramref name="time"/> milliseconds, from where it is now or from
    /// <paramref name="from"/>. The value itself is set at once, and the animation only shows the way there, so no
    /// finished animation holds on to it. <paramref name="done"/> runs at the end, even when the value stays as it is;
    /// at once when animations are off.
    /// </summary>
    internal static void To(IAnimatable target, DependencyProperty property, double to, double time,
        IEasingFunction? ease = null, double? from = null, Action? done = null)
    {
        var item = (DependencyObject)target;
        double start = from ?? (double)item.GetValue(property);
        target.BeginAnimation(property, null);
        item.SetValue(property, to);
        if (!On || time <= 0 || (start == to && done == null))
        {
            done?.Invoke();
            return;
        }
        var animation = new DoubleAnimation(start, to, TimeSpan.FromMilliseconds(time))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        };
        if (done != null) animation.Completed += (_, _) => done();
        target.BeginAnimation(property, animation);
    }

    /// <summary>Sets a value at once, stopping any animation of it.</summary>
    internal static void Set(IAnimatable target, DependencyProperty property, double value)
    {
        target.BeginAnimation(property, null);
        ((DependencyObject)target).SetValue(property, value);
    }

    private static IEasingFunction Ease(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }
}
