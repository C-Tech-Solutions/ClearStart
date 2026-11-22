using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace ClearStart;

/// <summary>
/// Fits the Start button's picture to its shape the way a profile picture is fitted: the picture lies under a frame of
/// that shape, the part outside the frame is dimmed, and dragging the picture chooses the part that shows.
/// </summary>
internal sealed class PictureFramer : Border
{
    public const double StageSide = 176;
    public const double FrameSide = 128;

    private readonly Canvas _stage = new()
    {
        Width = StageSide,
        Height = StageSide,
        ClipToBounds = true,
        Background = Ui.Brush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
    };
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Path _shade = new() { Fill = Ui.Brush(Color.FromArgb(0xB0, 0x10, 0x10, 0x10)), IsHitTestVisible = false };
    private readonly Path _outline = new()
    {
        Stroke = Ui.Brush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)),
        StrokeThickness = 1.5,
        IsHitTestVisible = false,
    };

    private ImageSource? _picture;
    private int _zoom = 100;
    // The middle of the part that shows, across and down the picture, from 0 to 1.
    private Point _middle = new(0.5, 0.5);
    private Point? _grabbedAt;
    private Point _middleWhenGrabbed;

    /// <summary>The picture was let go of in a new place: the middle of the part that shows, across and down, 0 to 1.</summary>
    public event Action<double, double>? Moved;

    public PictureFramer()
    {
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _stage.Children.Add(_image);
        _stage.Children.Add(_shade);
        _stage.Children.Add(_outline);
        Child = _stage;
        BorderThickness = new Thickness(1);
        BorderBrush = Ui.Brush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        HorizontalAlignment = HorizontalAlignment.Left;
        Cursor = Cursors.SizeAll;
        ToolTip = "Drag the picture to choose the part that shows";

        _stage.MouseLeftButtonDown += (_, e) =>
        {
            if (!Grab(e.GetPosition(_stage))) return;
            e.Handled = true;
            if (!_stage.CaptureMouse()) LetGo();
        };
        _stage.MouseMove += (_, e) =>
        {
            if (_grabbedAt is not { } from) return;
            if (e.LeftButton == MouseButtonState.Pressed) Follow(from, e.GetPosition(_stage));
            else LetGo();
        };
        _stage.MouseLeftButtonUp += (_, e) =>
        {
            if (_grabbedAt == null) return;
            e.Handled = true;
            LetGo();
        };
        _stage.LostMouseCapture += (_, _) => LetGo();
    }

    /// <summary>Shows the picture as the settings have it. Left alone while the picture is being dragged.</summary>
    public void Show(ImageSource? picture, PictureShape shape, int zoom, double x, double y)
    {
        if (_grabbedAt != null) return;
        _picture = picture;
        _zoom = zoom;
        _image.Source = picture;

        double at = (StageSide - FrameSide) / 2;
        double corner = StartGlyph.CornerOf(shape, FrameSide);
        var frame = new RectangleGeometry(new Rect(at, at, FrameSide, FrameSide), corner, corner);
        _outline.Data = frame;
        _shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, StageSide, StageSide)), frame);

        _middle = MiddleOf(Crop(x, y));
        Place();
    }

    /// <summary>For the self-test: the picture taken hold of, dragged this far and let go of.</summary>
    internal void DragForTest(Vector by)
    {
        if (!Grab(new Point(0, 0))) return;
        Follow(new Point(0, 0), new Point(by.X, by.Y));
        LetGo();
    }

    private Rect Crop(double x, double y) =>
        _picture == null ? new Rect(0, 0, 1, 1) : StartGlyph.CropOf(_picture.Width, _picture.Height, _zoom, x, y);

    private static Point MiddleOf(Rect crop) => new(crop.X + crop.Width / 2, crop.Y + crop.Height / 2);

    // Lays the picture out under the frame so that the part that shows fills it.
    private void Place()
    {
        if (_picture is not { Width: > 0, Height: > 0 })
        {
            _image.Visibility = Visibility.Collapsed;
            return;
        }
        var crop = Crop(_middle.X, _middle.Y);
        _image.Visibility = Visibility.Visible;
        _image.Width = FrameSide / crop.Width;
        _image.Height = FrameSide / crop.Height;
        double at = (StageSide - FrameSide) / 2;
        Canvas.SetLeft(_image, at - crop.X * _image.Width);
        Canvas.SetTop(_image, at - crop.Y * _image.Height);
    }

    private bool Grab(Point at)
    {
        if (_picture is not { Width: > 0, Height: > 0 }) return false;
        _grabbedAt = at;
        _middleWhenGrabbed = _middle;
        return true;
    }

    // The picture follows the mouse, so the middle of the part that shows moves the other way across it.
    private void Follow(Point from, Point to)
    {
        _middle = MiddleOf(Crop(_middleWhenGrabbed.X - (to.X - from.X) / _image.Width,
            _middleWhenGrabbed.Y - (to.Y - from.Y) / _image.Height));
        Place();
    }

    private void LetGo()
    {
        if (_grabbedAt == null) return;
        _grabbedAt = null;
        if (_stage.IsMouseCaptured) _stage.ReleaseMouseCapture();
        if (_middle != _middleWhenGrabbed) Moved?.Invoke(_middle.X, _middle.Y);
    }
}
