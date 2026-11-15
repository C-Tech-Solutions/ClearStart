using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClearStart;

/// <summary>Reveals one bar's initial pinned icons together, optionally with a short staggered animation.</summary>
internal sealed class PinnedIconReveal : IDisposable
{
    // One icon's fade and slide, in milliseconds, and how far it slides.
    private const double IconTime = 320, Slide = 7;
    // The pause between one icon starting and the next, and the longest the whole row takes to start. An icon is still
    // moving while the next several start, so the row comes in as one wave rather than icon by icon.
    private const double Gap = 40, RowTime = 440;
    // WPF draws an animation 60 times a second unless told otherwise, which shows on a faster monitor. It never draws
    // more often than the monitor does, so this only lifts the limit.
    private const int FrameRate = 240;

    private readonly Dictionary<Image, int> _images = new();
    private readonly HashSet<Image> _fallbacks = new();
    // The icons whose animation has not ended yet.
    private readonly HashSet<Image> _moving = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _wait = new();
    private readonly bool _animate, _vertical;
    private bool _accepting = true;
    private bool _disposed;
    internal bool Finished { get; private set; }

    public PinnedIconReveal(bool animate, bool vertical)
    {
        _animate = animate;
        _vertical = vertical;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += Check;
    }

    /// <summary>
    /// True from the first icon being held back until the animation has played. It is drawn by the UI thread, so the
    /// host starts nothing else there meanwhile (see ShellHost.AfterPinsAppear). Never true with the animation off:
    /// icons that simply appear have nothing to disturb.
    /// </summary>
    internal bool Playing => _animate && !_disposed && (_moving.Count > 0 || (!Finished && _images.Count > 0));

    public void Add(Image image, int order)
    {
        if (_disposed || !_accepting || Finished || _images.ContainsKey(image)) return;
        _images.Add(image, order);
        image.Opacity = 0;
        if (!_wait.IsRunning) _wait.Start();
        _timer.Start();
    }

    /// <summary>Called after the first Loaded/render pass; later pins are not part of startup.</summary>
    public void Seal()
    {
        if (_disposed || !_accepting) return;
        _accepting = false;
        if (_images.Count == 0)
        {
            Finished = true;
            _timer.Stop();
        }
    }

    public void Remove(Image image)
    {
        if (!_images.Remove(image)) return;
        Reset(image);
        if (_images.Count == 0)
        {
            _timer.Stop();
            if (!_accepting) Finished = true;
        }
    }

    private void Check(object? sender, EventArgs e)
    {
        if (_images.Count == 0 || _disposed) return;
        if (_images.Keys.Any(image => image.Source == null) && _wait.Elapsed < TimeSpan.FromSeconds(2)) return;
        Finished = true;
        _timer.Stop();
        var images = _images.OrderBy(pair => pair.Value).Select(pair => pair.Key).ToArray();
        var duration = TimeSpan.FromMilliseconds(IconTime);
        // The same curve for the fade and the slide, so an icon settles into place as it becomes solid.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        for (int i = 0; i < images.Length; i++)
        {
            var image = images[i];
            if (image.Source == null)
            {
                // Keep the binding: a late icon can replace its fallback. Never wait indefinitely for a shell extension.
                image.SetBinding(Image.SourceProperty, new Binding("Pin.Item.LargeIcon") { TargetNullValue = Fallback() });
                _fallbacks.Add(image);
            }
            if (!_animate) { image.Opacity = 1; continue; }

            var delay = TimeSpan.FromMilliseconds(images.Length <= 1 ? 0 : i * Math.Min(Gap, RowTime / (images.Length - 1)));
            var offset = new TranslateTransform(_vertical ? Slide : 0, _vertical ? 0 : Slide);
            image.RenderTransform = offset;
            var fade = new DoubleAnimation(0, 1, duration) { BeginTime = delay, EasingFunction = ease };
            var move = new DoubleAnimation(Slide, 0, duration) { BeginTime = delay, EasingFunction = ease };
            Timeline.SetDesiredFrameRate(fade, FrameRate);
            Timeline.SetDesiredFrameRate(move, FrameRate);
            _moving.Add(image);
            fade.Completed += (_, _) =>
            {
                // Unless it was taken off the bar meanwhile, and put right then.
                if (!_moving.Remove(image)) return;
                image.Opacity = 1;
                image.BeginAnimation(Image.OpacityProperty, null);
                image.ClearValue(Image.RenderTransformProperty);
            };
            image.BeginAnimation(Image.OpacityProperty, fade);
            offset.BeginAnimation(_vertical ? TranslateTransform.XProperty : TranslateTransform.YProperty, move);
        }
    }

    private static ImageSource Fallback()
    {
        var image = new DrawingImage(new GeometryDrawing(Brushes.SlateGray, new Pen(Brushes.LightGray, 1.5),
            Geometry.Parse("M2,2 L22,2 22,22 2,22 Z M2,7 L22,7")));
        image.Freeze();
        return image;
    }

    private void Reset(Image image)
    {
        _moving.Remove(image);
        image.BeginAnimation(Image.OpacityProperty, null);
        image.Opacity = 1;
        image.ClearValue(Image.RenderTransformProperty);
        if (_fallbacks.Remove(image)) image.ClearValue(Image.SourceProperty);
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        foreach (var image in _images.Keys) Reset(image);
        _images.Clear();
        _moving.Clear();
    }
}
