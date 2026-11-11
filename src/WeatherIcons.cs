using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShapeLine = System.Windows.Shapes.Line;
using ShapePath = System.Windows.Shapes.Path;

namespace ClearStart;

/// <summary>
/// Small weather pictures. They are drawn here rather than taken from a font so they can be in colour
/// (WPF draws emoji in one colour only).
/// </summary>
internal static class WeatherIcons
{
    private static readonly Brush SunBrush = Ui.Brush(Color.FromRgb(0xFF, 0xC9, 0x3C));
    private static readonly Brush MoonBrush = Ui.Brush(Color.FromRgb(0xEC, 0xE6, 0xC2));
    private static readonly Brush CloudBrush = Ui.Brush(Color.FromRgb(0xF1, 0xF3, 0xF6));
    private static readonly Brush DarkCloudBrush = Ui.Brush(Color.FromRgb(0xAE, 0xB6, 0xC2));
    private static readonly Brush WaterBrush = Ui.Brush(Color.FromRgb(0x62, 0xB5, 0xFF));
    private static readonly Brush SnowBrush = Ui.Brush(Colors.White);
    private static readonly Brush MistBrush = Ui.Brush(Color.FromRgb(0xC8, 0xCE, 0xD6));

    /// <summary>The picture for a weather code, 24 by 24; put it in a Viewbox to size it.</summary>
    public static Canvas Draw(int code, bool day) => Draw(WeatherCodes.KindOf(code), day);

    public static Canvas Draw(WeatherKind kind, bool day)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        switch (kind)
        {
            case WeatherKind.Clear:
                if (day) Sun(canvas, 12, 12, 4.6);
                else Moon(canvas, 11.5, 12.5, 8);
                break;

            case WeatherKind.MostlyClear:
                if (day) Sun(canvas, 10.5, 10, 4.2);
                else Moon(canvas, 10.5, 10.5, 7);
                Cloud(canvas, CloudBrush, 11, 13.5, 0.6);
                break;

            case WeatherKind.PartlyCloudy:
                if (day) Sun(canvas, 8.5, 8.5, 3.6);
                else Moon(canvas, 9, 8.5, 5.8);
                Cloud(canvas, CloudBrush, 6, 10, 0.85);
                break;

            case WeatherKind.Fog:
                Cloud(canvas, CloudBrush, 2, 2.5, 1);
                Stroke(canvas, 4, 18.2, 20, 18.2, MistBrush);
                Stroke(canvas, 7, 21.4, 17, 21.4, MistBrush);
                break;

            case WeatherKind.Drizzle:
                Cloud(canvas, CloudBrush, 2, 2.5, 1);
                Stroke(canvas, 8, 17.6, 7.4, 19.4, WaterBrush);
                Stroke(canvas, 12.5, 19.2, 11.9, 21, WaterBrush);
                Stroke(canvas, 17, 17.6, 16.4, 19.4, WaterBrush);
                break;

            case WeatherKind.Rain:
                Cloud(canvas, CloudBrush, 2, 2.5, 1);
                Stroke(canvas, 8, 17.6, 6.8, 21.2, WaterBrush);
                Stroke(canvas, 12.5, 17.6, 11.3, 21.2, WaterBrush);
                Stroke(canvas, 17, 17.6, 15.8, 21.2, WaterBrush);
                break;

            case WeatherKind.Sleet:
                Cloud(canvas, CloudBrush, 2, 2.5, 1);
                Stroke(canvas, 8, 17.6, 6.8, 21.2, WaterBrush);
                Dot(canvas, 12.2, 19.6, 1.2, SnowBrush);
                Stroke(canvas, 17, 17.6, 15.8, 21.2, WaterBrush);
                break;

            case WeatherKind.Snow:
                Cloud(canvas, CloudBrush, 2, 2.5, 1);
                Dot(canvas, 7.6, 18.6, 1.2, SnowBrush);
                Dot(canvas, 12.2, 20.8, 1.2, SnowBrush);
                Dot(canvas, 16.8, 18.6, 1.2, SnowBrush);
                break;

            case WeatherKind.Thunder:
                Cloud(canvas, DarkCloudBrush, 2, 2.5, 1);
                Fill(canvas, Geometry.Parse("M13,13.6 L9.4,18.6 H12 L10.8,22.8 L15.4,16.9 H12.7 L14.3,13.6 Z"), SunBrush);
                break;

            default:
                // Overcast: a grey cloud behind a white one.
                Cloud(canvas, DarkCloudBrush, 8, 3.5, 0.7);
                Cloud(canvas, CloudBrush, 2, 7, 0.95);
                break;
        }
        return canvas;
    }

    private static void Sun(Canvas canvas, double x, double y, double radius)
    {
        Dot(canvas, x, y, radius, SunBrush);
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4;
            double from = radius + 1.9;
            double to = radius + 3.9;
            Stroke(canvas, x + Math.Cos(angle) * from, y + Math.Sin(angle) * from,
                x + Math.Cos(angle) * to, y + Math.Sin(angle) * to, SunBrush, 1.6);
        }
    }

    /// <summary>A crescent: a disc with a bite taken out of its upper right.</summary>
    private static void Moon(Canvas canvas, double x, double y, double radius)
    {
        var disc = new EllipseGeometry(new Point(x, y), radius, radius);
        var bite = new EllipseGeometry(new Point(x + radius * 0.48, y - radius * 0.38), radius * 0.86, radius * 0.86);
        Fill(canvas, Geometry.Combine(disc, bite, GeometryCombineMode.Exclude, null), MoonBrush);
    }

    /// <summary>A cloud 20 wide and 12.5 tall at scale 1, with its top left corner at x, y.</summary>
    private static void Cloud(Canvas canvas, Brush brush, double x, double y, double scale)
    {
        Geometry shape = new RectangleGeometry(new Rect(1, 6, 18, 6.5), 3.25, 3.25);
        shape = Geometry.Combine(shape, new EllipseGeometry(new Point(6, 6.8), 3.8, 3.8), GeometryCombineMode.Union, null);
        shape = Geometry.Combine(shape, new EllipseGeometry(new Point(11, 5), 5, 5), GeometryCombineMode.Union, null);
        shape = Geometry.Combine(shape, new EllipseGeometry(new Point(15.6, 7.6), 3.2, 3.2), GeometryCombineMode.Union, null);

        var place = new TransformGroup();
        place.Children.Add(new ScaleTransform(scale, scale));
        place.Children.Add(new TranslateTransform(x, y));
        shape.Transform = place;
        Fill(canvas, shape, brush);
    }

    private static void Dot(Canvas canvas, double x, double y, double radius, Brush brush) =>
        Fill(canvas, new EllipseGeometry(new Point(x, y), radius, radius), brush);

    private static void Fill(Canvas canvas, Geometry shape, Brush brush)
    {
        if (shape.CanFreeze) shape.Freeze();
        canvas.Children.Add(new ShapePath { Data = shape, Fill = brush });
    }

    private static void Stroke(Canvas canvas, double x1, double y1, double x2, double y2, Brush brush, double thickness = 1.7) =>
        canvas.Children.Add(new ShapeLine
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });
}
