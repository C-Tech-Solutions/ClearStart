using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ClearStart;

/// <summary>
/// A colour wheel: the hue goes round it, the colour gets stronger from the middle outwards, and a slider
/// underneath sets how bright it is. Click or drag on the wheel to pick.
/// </summary>
internal sealed class ColorWheel : UserControl
{
    private const double Diameter = 188;
    private const double Radius = Diameter / 2;
    private const double MarkerSize = 16;

    private static ImageSource? _disc;

    private readonly Grid _wheel;
    private readonly Ellipse _shade;
    private readonly Grid _marker;
    private readonly Ellipse _markerFill;
    private readonly Slider _brightness;
    private readonly Border _chip;
    private readonly TextBlock _hex;

    // Hue in degrees, the other two from 0 to 1. Kept apart from the colour itself: black has no hue and
    // grey no strength, and the marker should not jump when the brightness is taken all the way down.
    private double _hue;
    private double _strength;
    private double _value = 1;
    private bool _showing;

    public ColorWheel()
    {
        Focusable = false;

        _shade = new Ellipse { Fill = Brushes.Black, Opacity = 0, IsHitTestVisible = false };
        _markerFill = new Ellipse { Margin = new Thickness(2) };
        _marker = new Grid { Width = MarkerSize, Height = MarkerSize, IsHitTestVisible = false };
        // Dark outside, white inside: it shows on any colour.
        _marker.Children.Add(new Ellipse { Stroke = Ui.Brush(Color.FromArgb(0xB0, 0, 0, 0)), StrokeThickness = 1 });
        _marker.Children.Add(new Ellipse { Stroke = Brushes.White, StrokeThickness = 2, Margin = new Thickness(1) });
        _marker.Children.Add(_markerFill);

        var canvas = new Canvas { IsHitTestVisible = false };
        canvas.Children.Add(_marker);

        var picture = new Image { Source = Disc(), Width = Diameter, Height = Diameter, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);

        _wheel = new Grid
        {
            Width = Diameter,
            Height = Diameter,
            Background = Brushes.Transparent,
            Cursor = Cursors.Cross,
        };
        _wheel.Children.Add(picture);
        _wheel.Children.Add(_shade);
        _wheel.Children.Add(canvas);
        _wheel.MouseLeftButtonDown += Wheel_Down;
        _wheel.MouseMove += Wheel_Move;
        _wheel.MouseLeftButtonUp += (_, _) => _wheel.ReleaseMouseCapture();

        _brightness = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 1,
            SmallChange = 1,
            LargeChange = 10,
            Value = 100,
            Style = (Style)Application.Current.FindResource("SettingsSlider"),
            ToolTip = "Brightness",
        };
        _brightness.ValueChanged += (_, e) =>
        {
            if (_showing) return;
            _value = e.NewValue / 100;
            Refresh();
            Picked?.Invoke(Color);
        };

        _chip = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            BorderBrush = Ui.Brush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
        };
        _hex = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

        var brightnessRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var label = new TextBlock { Text = "Brightness", Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(label, Dock.Left);
        brightnessRow.Children.Add(label);
        brightnessRow.Children.Add(_brightness);

        var nowRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        nowRow.Children.Add(_chip);
        nowRow.Children.Add(_hex);

        var column = new StackPanel { Width = Diameter };
        column.Children.Add(_wheel);
        column.Children.Add(brightnessRow);
        column.Children.Add(nowRow);
        Content = column;

        Refresh();
    }

    /// <summary>Raised each time the user picks: while dragging on the wheel, and while moving the slider.</summary>
    public event Action<Color>? Picked;

    /// <summary>The colour the wheel is on.</summary>
    public Color Color => FromHsv(_hue, _strength, _value);

    /// <summary>Puts the wheel on a colour, without reporting it as picked.</summary>
    public void Show(Color color)
    {
        (_hue, _strength, _value) = ToHsv(color);
        _showing = true;
        try
        {
            _brightness.Value = Math.Round(_value * 100);
        }
        finally
        {
            _showing = false;
        }
        Refresh();
    }

    /// <summary>
    /// The wheel inside the dark panel it opens in. A popup made in code is not inside any window, so nothing
    /// is inherited: the text colour and font are said here.
    /// </summary>
    public static Border InPanel(ColorWheel wheel)
    {
        var panel = new Border
        {
            Background = (Brush)Application.Current.FindResource("MenuBackgroundBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("MenuBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = wheel,
            SnapsToDevicePixels = true,
        };
        TextElement.SetForeground(panel, Brushes.White);
        TextElement.SetFontFamily(panel, (FontFamily)Application.Current.FindResource("UiFont"));
        TextElement.SetFontSize(panel, 13);
        return panel;
    }

    /// <summary>The wheel's picture, small, for the button that opens it.</summary>
    public static Image Thumbnail(double size)
    {
        var image = new Image { Source = Disc(), Width = size, Height = size };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    // ------------------------------------------------------------------ picking

    private void Wheel_Down(object sender, MouseButtonEventArgs e)
    {
        var at = e.GetPosition(_wheel);
        // The square's corners are not part of the wheel.
        if (Distance(at) > Radius + 3) return;
        e.Handled = true;
        _wheel.CaptureMouse();
        PickAt(at);
    }

    private void Wheel_Move(object sender, MouseEventArgs e)
    {
        // Dragging past the rim keeps picking, along the rim.
        if (_wheel.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) PickAt(e.GetPosition(_wheel));
    }

    private static double Distance(Point at) => Math.Sqrt(((at.X - Radius) * (at.X - Radius)) + ((at.Y - Radius) * (at.Y - Radius)));

    private void PickAt(Point at)
    {
        double strength = Math.Min(1, Distance(at) / Radius);
        // The very middle is hard to hit: close enough counts as no colour at all (white, grey or black).
        if (strength < 0.05) strength = 0;
        if (strength > 0) _hue = HueAt(at.X - Radius, at.Y - Radius);
        _strength = strength;
        Refresh();
        Picked?.Invoke(Color);
    }

    private void Refresh()
    {
        var color = Color;
        var brush = Ui.Brush(color);
        _shade.Opacity = 1 - _value;
        _markerFill.Fill = brush;
        _chip.Background = brush;
        _hex.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        double angle = _hue * Math.PI / 180;
        Canvas.SetLeft(_marker, Radius + (_strength * Radius * Math.Cos(angle)) - (MarkerSize / 2));
        Canvas.SetTop(_marker, Radius - (_strength * Radius * Math.Sin(angle)) - (MarkerSize / 2));
    }

    // ------------------------------------------------------------------ colour arithmetic

    /// <summary>Red is on the right, and the hues go round anticlockwise from there.</summary>
    private static double HueAt(double dx, double dy)
    {
        double degrees = Math.Atan2(-dy, dx) * 180 / Math.PI;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    internal static Color FromHsv(double hue, double strength, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        double chroma = value * strength;
        double x = chroma * (1 - Math.Abs(((hue / 60) % 2) - 1));
        double m = value - chroma;
        (double r, double g, double b) = (int)(hue / 60) switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        return Color.FromRgb(Channel(r + m), Channel(g + m), Channel(b + m));
    }

    internal static (double Hue, double Strength, double Value) ToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double hue = 0;
        if (delta > 0)
        {
            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * (((b - r) / delta) + 2);
            else hue = 60 * (((r - g) / delta) + 4);
            if (hue < 0) hue += 360;
        }
        return (hue, max <= 0 ? 0 : delta / max, max);
    }

    private static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

    /// <summary>The wheel at full brightness, drawn once at twice its size so it stays sharp on a scaled screen.</summary>
    private static ImageSource Disc()
    {
        if (_disc != null) return _disc;

        const int size = (int)Diameter * 2;
        const double radius = size / 2.0;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x + 0.5 - radius;
                double dy = y + 0.5 - radius;
                double distance = Math.Sqrt((dx * dx) + (dy * dy));
                // One pixel of fading at the rim, so it is round rather than stepped.
                double cover = Math.Clamp(radius - distance, 0, 1);
                if (cover <= 0) continue;

                var color = FromHsv(HueAt(dx, dy), Math.Min(1, distance / radius), 1);
                int i = ((y * size) + x) * 4;
                pixels[i] = (byte)Math.Round(color.B * cover);
                pixels[i + 1] = (byte)Math.Round(color.G * cover);
                pixels[i + 2] = (byte)Math.Round(color.R * cover);
                pixels[i + 3] = (byte)Math.Round(255 * cover);
            }
        }

        var bitmap = BitmapSource.Create(size, size, 192, 192, PixelFormats.Pbgra32, null, pixels, size * 4);
        bitmap.Freeze();
        _disc = bitmap;
        return bitmap;
    }
}
