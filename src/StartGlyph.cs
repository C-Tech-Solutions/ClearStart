using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClearStart;

/// <summary>
/// Draws the face of the Start button: one of the built-in shapes, a word, or the user's own picture,
/// with or without a coloured shape behind it. The shapes are plain geometry in a 24 x 24 box, so they
/// stay sharp at any size.
/// </summary>
internal static class StartGlyph
{
    public const string DefaultText = "Start";

    /// <summary>Every design, in the order the settings window shows them, with the name it gives each.</summary>
    public static readonly (StartButtonStyle Style, string Name)[] Designs =
    {
        (StartButtonStyle.Squares, "Squares"),
        (StartButtonStyle.Dots, "Dots"),
        (StartButtonStyle.NineDots, "Nine dots"),
        (StartButtonStyle.Diamond, "Diamond"),
        (StartButtonStyle.Ring, "Ring"),
        (StartButtonStyle.Disc, "Disc"),
        (StartButtonStyle.Hexagon, "Hexagon"),
        (StartButtonStyle.Lines, "Lines"),
        (StartButtonStyle.Home, "Home"),
        (StartButtonStyle.Star, "Star"),
        (StartButtonStyle.Sparkle, "Sparkle"),
        (StartButtonStyle.Bolt, "Bolt"),
        (StartButtonStyle.Heart, "Heart"),
        (StartButtonStyle.Text, "A word"),
        (StartButtonStyle.Picture, "Your own picture"),
    };

    private static readonly Dictionary<StartButtonStyle, Geometry> Shapes = new();
    private static readonly Dictionary<string, (DateTime Stamp, ImageSource? Image)> Pictures =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Start button's face as the settings describe it.</summary>
    /// <param name="size">How big the design is without a background.</param>
    /// <param name="limit">The most room there is across the bar, which a background may grow into.</param>
    /// <param name="textColor">The bar's text colour, used unless the button has a colour of its own.</param>
    /// <param name="maxWidth">A side bar's width: a word is shrunk to fit it.</param>
    public static FrameworkElement Build(TaskbarSettings settings, double size, double limit, Color textColor,
        double maxWidth = double.PositiveInfinity)
    {
        var brush = Ui.Brush(Settings.ParseColor(settings.StartButtonColor, textColor));
        var style = settings.StartButtonStyle;
        string text = string.IsNullOrWhiteSpace(settings.StartButtonText) ? DefaultText : settings.StartButtonText.Trim();

        // Without a picture to show, the Picture design falls back to the usual squares.
        ImageSource? picture = style == StartButtonStyle.Picture ? Picture(settings.StartButtonPicture) : null;
        if (style == StartButtonStyle.Picture && picture == null) style = StartButtonStyle.Squares;

        if (settings.StartButtonBackground == StartButtonBackground.None)
        {
            return style switch
            {
                StartButtonStyle.Text => Word(text, size, brush, maxWidth),
                StartButtonStyle.Picture => new Image
                {
                    Source = picture,
                    Height = size,
                    MaxWidth = Math.Min(size * 3, maxWidth),
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true,
                },
                _ => Shape(style, size, brush),
            };
        }

        // On a background the whole thing is a little larger than the bare design, and the design sits inside it.
        double side = Math.Max(size, Math.Min(Math.Round(size * 1.35), limit));
        var corners = new CornerRadius(settings.StartButtonBackground == StartButtonBackground.Circle
            ? side / 2
            : Math.Max(3, Math.Round(side * 0.24)));
        var plate = new Border
        {
            Height = side,
            MinWidth = side,
            CornerRadius = corners,
            Background = Ui.Brush(BackgroundColor(settings)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };

        switch (style)
        {
            case StartButtonStyle.Text:
                plate.Padding = new Thickness(Math.Round(side * 0.34), 0, Math.Round(side * 0.34), 0);
                plate.Child = Word(text, side * 0.82, brush, maxWidth - side * 0.68);
                break;
            case StartButtonStyle.Picture:
                // The picture fills the shape and is cut to it.
                plate.Width = side;
                var fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
                RenderOptions.SetBitmapScalingMode(fill, BitmapScalingMode.HighQuality);
                plate.Child = new Border { CornerRadius = corners, Background = fill };
                break;
            default:
                plate.Width = side;
                plate.Child = Shape(style, Math.Round(side * 0.56), brush);
                break;
        }
        return plate;
    }

    /// <summary>The colour of the shape behind the design: the chosen one, else the Windows accent colour.</summary>
    public static Color BackgroundColor(TaskbarSettings settings)
    {
        var accent = SystemParameters.WindowGlassColor;
        accent.A = 255;
        var color = Settings.ParseColor(settings.StartButtonBackgroundColor, accent);
        color.A = 255;
        return color;
    }

    /// <summary>One of the built-in shapes, drawn this big in this colour.</summary>
    public static FrameworkElement Shape(StartButtonStyle style, double size, Brush brush)
    {
        var path = new System.Windows.Shapes.Path { Data = GeometryOf(style), Fill = brush };
        var box = new Canvas { Width = 24, Height = 24 };
        box.Children.Add(path);
        return new Viewbox
        {
            Width = size,
            Height = size,
            Child = box,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static FrameworkElement Word(string text, double size, Brush brush, double maxWidth)
    {
        var word = new TextBlock
        {
            Text = text,
            FontSize = Math.Max(9, Math.Round(size * 0.68)),
            FontWeight = FontWeights.SemiBold,
            Foreground = brush,
        };
        // On a side bar a long word is shrunk to the bar's width rather than cut off.
        return new Viewbox
        {
            Child = word,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            MaxWidth = double.IsFinite(maxWidth) ? Math.Max(12, maxWidth) : double.PositiveInfinity,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    // ------------------------------------------------------------------ shapes

    private static Geometry GeometryOf(StartButtonStyle style)
    {
        if (Shapes.TryGetValue(style, out var known)) return known;

        Geometry shape = style switch
        {
            StartButtonStyle.Dots => Group(Dot(6.3, 6.3, 4.8), Dot(17.7, 6.3, 4.8), Dot(6.3, 17.7, 4.8), Dot(17.7, 17.7, 4.8)),
            StartButtonStyle.NineDots => Group(new[] { 4.0, 12.0, 20.0 }
                .SelectMany(y => new[] { 4.0, 12.0, 20.0 }.Select(x => Dot(x, y, 2.7))).ToArray()),
            StartButtonStyle.Diamond => Parse(
                "M12,1 L17,6 12,11 7,6 Z M6,7 L11,12 6,17 1,12 Z M18,7 L23,12 18,17 13,12 Z M12,13 L17,18 12,23 7,18 Z"),
            // Even-odd: the second circle cuts the hole, the third fills its middle again.
            StartButtonStyle.Ring => Group(FillRule.EvenOdd, Dot(12, 12, 11), Dot(12, 12, 8.1), Dot(12, 12, 3.7)),
            StartButtonStyle.Disc => Dot(12, 12, 11),
            StartButtonStyle.Hexagon => Polygon(Around(12, 12, 11.4, 6, -90)),
            StartButtonStyle.Lines => Group(Box(2, 4, 20, 3.2, 1.6), Box(2, 10.4, 20, 3.2, 1.6), Box(2, 16.8, 20, 3.2, 1.6)),
            StartButtonStyle.Home => Parse("M12,2 L23,11.6 H19.8 V21.5 H14.3 V15 H9.7 V21.5 H4.2 V11.6 H1 Z"),
            StartButtonStyle.Star => Polygon(Star(12, 12.9, 11.6, 4.9, 5)),
            StartButtonStyle.Sparkle => Parse(
                "M12,1 C12.9,7.4 16.6,11.1 23,12 C16.6,12.9 12.9,16.6 12,23 C11.1,16.6 7.4,12.9 1,12 C7.4,11.1 11.1,7.4 12,1 Z"),
            StartButtonStyle.Bolt => Parse("M14,1.5 L4.5,13.6 H11 L9.4,22.5 L19.5,9.6 H12.8 Z"),
            StartButtonStyle.Heart => Parse(
                "M12,21.6 C12,21.6 1.8,15.2 1.8,8.6 C1.8,5.3 4.4,3 7.3,3 C9.2,3 10.9,4 12,5.7 " +
                "C13.1,4 14.8,3 16.7,3 C19.6,3 22.2,5.3 22.2,8.6 C22.2,15.2 12,21.6 12,21.6 Z"),
            // Squares, and anything that has no shape of its own.
            _ => Group(Box(1.2, 1.2, 9.6, 9.6, 1.8), Box(13.2, 1.2, 9.6, 9.6, 1.8),
                Box(1.2, 13.2, 9.6, 9.6, 1.8), Box(13.2, 13.2, 9.6, 9.6, 1.8)),
        };
        shape.Freeze();
        Shapes[style] = shape;
        return shape;
    }

    private static Geometry Parse(string path) => Geometry.Parse(path).CloneCurrentValue();

    private static Geometry Dot(double x, double y, double radius) => new EllipseGeometry(new Point(x, y), radius, radius);

    private static Geometry Box(double x, double y, double width, double height, double corner) =>
        new RectangleGeometry(new Rect(x, y, width, height), corner, corner);

    private static Geometry Group(params Geometry[] parts) => Group(FillRule.Nonzero, parts);

    private static Geometry Group(FillRule rule, params Geometry[] parts)
    {
        var group = new GeometryGroup { FillRule = rule };
        foreach (var part in parts) group.Children.Add(part);
        return group;
    }

    private static Geometry Polygon(IReadOnlyList<Point> points)
    {
        var figure = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = true };
        figure.Segments.Add(new PolyLineSegment(points.Skip(1), isStroked: true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>Points evenly spaced on a circle, the first at the given angle (0 = right, -90 = top).</summary>
    private static List<Point> Around(double x, double y, double radius, int count, double startAngle)
    {
        var points = new List<Point>();
        for (int i = 0; i < count; i++)
        {
            double angle = (startAngle + 360.0 * i / count) * Math.PI / 180;
            points.Add(new Point(x + radius * Math.Cos(angle), y + radius * Math.Sin(angle)));
        }
        return points;
    }

    /// <summary>A star's outline: its points alternate between the outer and the inner circle.</summary>
    private static List<Point> Star(double x, double y, double outer, double inner, int arms)
    {
        var tips = Around(x, y, outer, arms, -90);
        var dips = Around(x, y, inner, arms, -90 + 180.0 / arms);
        var points = new List<Point>();
        for (int i = 0; i < arms; i++)
        {
            points.Add(tips[i]);
            points.Add(dips[i]);
        }
        return points;
    }

    // ------------------------------------------------------------------ the user's picture

    /// <summary>
    /// The picture in a file, or null when there is none to show. Read once and kept until the file changes;
    /// the file itself is not kept open. A large photo is kept at a size that suits a button.
    /// </summary>
    public static ImageSource? Picture(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        file = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));

        DateTime stamp = default;
        try
        {
            if (!File.Exists(file))
            {
                if (!Pictures.ContainsKey(file)) Log.Warn("The Start button's picture is not there: " + file);
                Pictures[file] = (stamp, null);
                return null;
            }

            stamp = File.GetLastWriteTimeUtc(file);
            if (Pictures.TryGetValue(file, out var known) && known.Stamp == stamp) return known.Image;

            BitmapSource picture;
            using (var stream = File.OpenRead(file))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                // An icon file holds the same picture at several sizes: take the largest.
                picture = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
            }

            const int most = 256;
            int longest = Math.Max(picture.PixelWidth, picture.PixelHeight);
            if (longest > most)
            {
                double scale = (double)most / longest;
                picture = new WriteableBitmap(new TransformedBitmap(picture, new ScaleTransform(scale, scale)));
            }
            picture.Freeze();
            Pictures[file] = (stamp, picture);
            return picture;
        }
        catch (Exception ex)
        {
            Log.Warn(string.Format(CultureInfo.InvariantCulture, "Could not read the Start button's picture {0}: {1}",
                file, ex.Message));
            Pictures[file] = (stamp, null);
            return null;
        }
    }
}
