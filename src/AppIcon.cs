using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClearStart;

/// <summary>
/// ClearStart's icon: a blue-to-teal tile with a see-through bar along its lower edge and three apps on it.
/// It is drawn here, as shapes, so the settings window can show it at any size. The self-test writes the
/// same drawing out as an .ico file; that file, copied into the project as ClearStart.ico, is what the
/// program file itself carries.
/// </summary>
internal static class AppIcon
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    private static ImageSource? _image;

    /// <summary>The icon as a drawing, sharp at whatever size it is shown.</summary>
    public static ImageSource Image
    {
        get
        {
            if (_image != null) return _image;
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open()) Paint(dc, detail: true);
            var image = new DrawingImage(drawing);
            image.Freeze();
            return _image = image;
        }
    }

    /// <summary>The icon as a picture of this many pixels. Small ones leave out what would only smudge.</summary>
    public static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            Paint(dc, detail: size >= 32);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Paints the icon into a 256 x 256 box.</summary>
    private static void Paint(DrawingContext dc, bool detail)
    {
        // Something invisible in two corners, so the drawing is 256 x 256 whatever is painted in it.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 256, 256));

        var tile = new LinearGradientBrush(Color.FromRgb(0x4A, 0x8B, 0xF7), Color.FromRgb(0x10, 0xC2, 0xAC),
            new Point(0.1, 0), new Point(0.9, 1));
        dc.DrawRoundedRectangle(tile, null, new Rect(10, 10, 236, 236), 56, 56);

        // Light falling on the upper half.
        var shine = new LinearGradientBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
            new Point(0, 0), new Point(0, 1));
        dc.DrawRoundedRectangle(shine, null, new Rect(10, 10, 236, 150), 56, 56);

        if (!detail)
        {
            // At a few pixels across: just the bar, solid enough to see.
            dc.DrawRoundedRectangle(Ui.Brush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)), null, new Rect(40, 150, 176, 60), 26, 26);
            return;
        }

        // The bar: glass with a bright edge, and three apps on it.
        var glass = Ui.Brush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
        var edge = new Pen(Ui.Brush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), 5);
        dc.DrawRoundedRectangle(glass, edge, new Rect(36, 148, 184, 64), 28, 28);
        var app = Ui.Brush(Color.FromArgb(0xF8, 0xFF, 0xFF, 0xFF));
        foreach (double x in new[] { 82.0, 128.0, 174.0 }) dc.DrawEllipse(app, null, new Point(x, 180), 14, 14);
    }

    /// <summary>
    /// Writes the icon as an .ico file with every size Windows asks for. The largest is stored as a PNG
    /// and the rest as plain bitmaps, which is what the oldest readers of the format expect.
    /// </summary>
    public static void WriteIco(string file)
    {
        var images = Sizes.Select(size => (Size: size, Data: size >= 256 ? Png(Render(size)) : Bitmap(Render(size)))).ToList();

        using var stream = File.Create(file);
        using var writer = new BinaryWriter(stream);
        writer.Write((short)0);
        writer.Write((short)1);                 // an icon, not a cursor
        writer.Write((short)images.Count);

        int offset = 6 + (16 * images.Count);
        foreach (var (size, data) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));   // 0 stands for 256
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);              // no palette
            writer.Write((byte)0);
            writer.Write((short)1);             // planes
            writer.Write((short)32);            // bits per pixel
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images) writer.Write(data);
    }

    private static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }

    /// <summary>A picture the way an .ico stores it: a bitmap header, the rows bottom-up, then an empty mask.</summary>
    private static byte[] Bitmap(BitmapSource image)
    {
        int size = image.PixelWidth;
        var straight = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[size * size * 4];
        straight.CopyPixels(pixels, size * 4, 0);
        int maskRow = (size + 31) / 32 * 4;

        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        writer.Write(40);                       // header length
        writer.Write(size);
        writer.Write(size * 2);                 // the picture and its mask, one above the other
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(0);                        // not compressed
        writer.Write(pixels.Length + (maskRow * size));
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        for (int y = size - 1; y >= 0; y--) writer.Write(pixels, y * size * 4, size * 4);
        writer.Write(new byte[maskRow * size]); // the alpha channel says what is see-through
        writer.Flush();
        return memory.ToArray();
    }
}
