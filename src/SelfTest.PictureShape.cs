using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClearStart;

internal static partial class SelfTest
{
    /// <summary>
    /// The Start button's picture cut to each shape, zoomed and moved, as the bars draw it; the part of a picture that
    /// shows, worked out for pictures of several shapes; the frame on the settings page that fits it, dragged; and the
    /// new settings read back from a file. The picture is twice as wide as it is high, red on the left and blue on the
    /// right, so the colours show which part was cut out. Throw-away settings: nothing is saved.
    /// </summary>
    private static async Task CheckPictureShapesAsync(string directory, Dictionary<string, object?> report, List<string> errors)
    {
        var found = new Dictionary<string, object?>();

        // The part of the picture that shows, in fractions of it.
        void Crop(string what, double width, double height, int zoom, double x, double y, Rect expected)
        {
            var crop = StartGlyph.CropOf(width, height, zoom, x, y);
            bool same = Math.Abs(crop.X - expected.X) < 1e-9 && Math.Abs(crop.Y - expected.Y) < 1e-9
                && Math.Abs(crop.Width - expected.Width) < 1e-9 && Math.Abs(crop.Height - expected.Height) < 1e-9;
            if (!same) errors.Add($"Picture shapes ({what}): the part that shows is {crop}, not {expected}");
        }
        Crop("a square picture", 100, 100, 100, 0.5, 0.5, new Rect(0, 0, 1, 1));
        Crop("a wide picture", 200, 100, 100, 0.5, 0.5, new Rect(0.25, 0, 0.5, 1));
        Crop("a tall picture", 100, 200, 100, 0.5, 0.5, new Rect(0, 0.25, 1, 0.5));
        Crop("zoomed into the top left", 200, 100, 200, 0, 0, new Rect(0, 0, 0.25, 0.5));
        Crop("zoomed into the bottom right", 200, 100, 200, 1, 1, new Rect(0.75, 0.5, 0.25, 0.5));
        Crop("a point off the picture", 200, 100, 100, -5, 9, new Rect(0, 0, 0.5, 1));
        Crop("zoomed too far", 100, 100, 9000, 0.5, 0.5, new Rect(0.375, 0.375, 0.25, 0.25));

        // The new settings in a file.
        var written = new Settings();
        written.Taskbar.StartButtonPictureShape = PictureShape.Square;
        written.Taskbar.StartButtonPictureZoom = 250;
        written.Taskbar.StartButtonPictureX = 0.3;
        written.Taskbar.StartButtonPictureY = 0.7;
        var read = Settings.Parse(Settings.Write(written)).Taskbar;
        if (read.StartButtonPictureShape != PictureShape.Square || read.StartButtonPictureZoom != 250 ||
            read.StartButtonPictureX != 0.3 || read.StartButtonPictureY != 0.7)
        {
            errors.Add("Picture shapes: the shape, zoom and place did not read back as written");
        }
        // A file from before there was a choice: the picture takes the background's shape, as it did then.
        var old = Settings.Parse("""{"taskbar":{"startButtonStyle":"picture","startButtonBackground":"circle"}}""");
        if (old.Taskbar.PictureShapeInUse != PictureShape.Circle || old.Taskbar.StartButtonPictureZoom != 100 ||
            old.Taskbar.StartButtonPictureX != 0.5 || old.Taskbar.StartButtonPictureY != 0.5)
        {
            errors.Add("Picture shapes: a picture on a circle from an older file is not cut to a circle, whole");
        }
        if (Settings.Write(old).Contains("startButtonPictureShape", StringComparison.Ordinal))
            errors.Add("Picture shapes: a shape that was never chosen was written to the file");
        var rough = Settings.Parse("""{"taskbar":{"startButtonPictureZoom":9000,"startButtonPictureX":-3,"startButtonPictureY":7}}""");
        var none = Settings.Parse("""{"taskbar":{"startButtonPictureZoom":0}}""");
        if (rough.Taskbar.StartButtonPictureZoom != 400 || rough.Taskbar.StartButtonPictureX != 0 ||
            rough.Taskbar.StartButtonPictureY != 1 || none.Taskbar.StartButtonPictureZoom != 100)
        {
            errors.Add("Picture shapes: a zoom or place out of range in the file was not put right");
        }

        // The picture: red on the left half, blue on the right.
        string file = Path.Combine(directory, "start-picture-wide.png");
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 100, 100));
            draw.DrawRectangle(Brushes.Blue, null, new Rect(100, 0, 100, 100));
        }
        var drawn = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32);
        drawn.Render(visual);
        SavePng(drawn, file);
        if (StartGlyph.Picture(file) == null)
        {
            errors.Add("Picture shapes: could not read the picture it was given");
            return;
        }

        TaskbarSettings With(PictureShape? shape, int zoom = 100, double x = 0.5, double y = 0.5,
            StartButtonBackground background = StartButtonBackground.None) => new()
        {
            StartButtonStyle = StartButtonStyle.Picture,
            StartButtonPicture = file,
            StartButtonPictureShape = shape,
            StartButtonPictureZoom = zoom,
            StartButtonPictureX = x,
            StartButtonPictureY = y,
            StartButtonBackground = background,
        };

        // The button as a bar draws it, 40 high and with room for a shape 54 across, and what colour a pixel of it is.
        (int Width, int Height, Func<int, int, string> At) Face(TaskbarSettings settings)
        {
            var face = StartGlyph.Build(settings, 40, 54, Colors.White);
            face.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            face.Arrange(new Rect(face.DesiredSize));
            int width = (int)Math.Ceiling(face.DesiredSize.Width), height = (int)Math.Ceiling(face.DesiredSize.Height);
            var shot = new RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Pbgra32);
            shot.Render(face);
            var pixels = new byte[shot.PixelWidth * shot.PixelHeight * 4];
            shot.CopyPixels(pixels, shot.PixelWidth * 4, 0);
            string At(int x, int y)
            {
                int i = (y * shot.PixelWidth + x) * 4;
                byte blue = pixels[i], red = pixels[i + 2], alpha = pixels[i + 3];
                if (alpha < 40) return "clear";
                if (red > 180 && blue < 80) return "red";
                if (blue > 180 && red < 80) return "blue";
                return "mixed";
            }
            return (width, height, At);
        }

        void Expect(string what, TaskbarSettings settings, int width, params (int X, int Y, string Color)[] points)
        {
            var face = Face(settings);
            if (face.Width != width || face.Height != (width == 54 ? 54 : 40))
            {
                errors.Add($"Picture shapes ({what}): the button is {face.Width} x {face.Height}");
                return;
            }
            foreach (var (x, y, color) in points)
            {
                string seen = face.At(x, y);
                if (seen != color) errors.Add($"Picture shapes ({what}): at {x},{y} it is {seen}, not {color}");
            }
        }

        // Whole: as high as the design, and as wide as that makes it.
        Expect("whole", With(PictureShape.Whole), 80, (10, 20, "red"), (70, 20, "blue"));
        // Cut to the middle of the picture: half red, half blue, and the corners go as the shape says.
        Expect("square", With(PictureShape.Square), 54, (1, 1, "red"), (6, 6, "red"), (13, 27, "red"), (40, 27, "blue"));
        Expect("rounded square", With(PictureShape.Rounded), 54, (1, 1, "clear"), (6, 6, "red"), (13, 27, "red"), (40, 27, "blue"));
        Expect("circle", With(PictureShape.Circle), 54, (1, 1, "clear"), (6, 6, "clear"), (13, 27, "red"), (40, 27, "blue"));
        // Zoomed in to the left or right end: only one colour shows.
        Expect("zoomed in on the left", With(PictureShape.Circle, 200, 0, 0.5), 54, (13, 27, "red"), (45, 27, "red"));
        Expect("zoomed in on the right", With(PictureShape.Circle, 200, 1, 0.5), 54, (8, 27, "blue"), (40, 27, "blue"));
        // From an older file: cut to the background's shape, with nothing behind it.
        Expect("older file", With(null, background: StartButtonBackground.Circle), 54, (6, 6, "clear"), (13, 27, "red"));

        // The frame on the settings page: the picture dragged right shows more of its left part, and stops at its edge.
        var framer = new PictureFramer();
        var moves = new List<(double X, double Y)>();
        framer.Moved += (x, y) => moves.Add((x, y));
        var picture = StartGlyph.Picture(file);
        framer.Show(picture, PictureShape.Circle, 100, 0.5, 0.5);
        // At 100% the picture is drawn twice as wide as the frame, so 32 pixels move its middle an eighth of the way.
        framer.DragForTest(new Vector(32, 0));
        framer.Show(picture, PictureShape.Circle, 100, moves.LastOrDefault().X, 0.5);
        framer.DragForTest(new Vector(5000, 0));
        // Up and down there is nowhere to go: the picture is no higher than the frame. Nothing moves, so nothing is said.
        framer.DragForTest(new Vector(0, 40));
        found["framerMoves"] = moves.Select(move => $"{move.X:0.###},{move.Y:0.###}").ToList();
        if (moves.Count != 2 || Math.Abs(moves[0].X - 0.375) > 1e-6 || Math.Abs(moves[1].X - 0.25) > 1e-6 ||
            moves.Any(move => Math.Abs(move.Y - 0.5) > 1e-6))
        {
            errors.Add("Picture shapes: dragging the picture in its frame did not move it as far as it should");
        }

        // Each shape side by side, to look at.
        var sheet = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12) };
        foreach (var settings in new[]
                 {
                     With(PictureShape.Whole), With(PictureShape.Square), With(PictureShape.Rounded), With(PictureShape.Circle),
                     With(PictureShape.Circle, 200, 0, 0.5), With(PictureShape.Rounded, 300, 1, 0.5),
                 })
        {
            var face = StartGlyph.Build(settings, 40, 54, Colors.White);
            face.Margin = new Thickness(0, 0, 16, 0);
            sheet.Children.Add(face);
        }
        var framed = new PictureFramer();
        framed.Show(picture, PictureShape.Circle, 150, 0.3, 0.5);
        sheet.Children.Add(framed);
        await DrawAsync(sheet, 720, 210, Path.Combine(directory, "start-picture-shapes.png"));

        report["pictureShapes"] = found;
    }
}
