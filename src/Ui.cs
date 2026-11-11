using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClearStart;

/// <summary>
/// One line of a context menu. A null header is a separator; a null action greys the line out, unless the
/// line has children, in which case it opens them as a further menu.
/// </summary>
internal sealed record MenuEntry(string? Header, Action? Action, ImageSource? Icon = null, bool Checked = false,
    IReadOnlyList<MenuEntry>? Children = null);

/// <summary>Small UI helpers shared by the taskbar and the start menu.</summary>
internal static class Ui
{
    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static (Color Tint, Brush Brush)? _frosted;

    /// <summary>
    /// What a bar or the start menu paints over whatever Windows shows behind it: the chosen colour, as
    /// solid as its alpha says. "Frosted" adds a faint milky veil with a fine grain to it, which is what
    /// tells it apart from "Blurred": the blur itself is the same one, and Windows fixes how strong it is.
    /// </summary>
    public static Brush Backdrop(Appearance appearance, Color tint)
    {
        if (appearance == Appearance.Opaque) tint.A = 255;
        if (appearance != Appearance.Acrylic || tint.A == 255) return Brush(tint);

        // Every bar asks for the same one, many times a second while a slider is dragged.
        if (_frosted is { } made && made.Tint == tint) return made.Brush;

        const int size = 128;
        const double veil = 0.07, grain = 0.035;
        double alpha = tint.A / 255.0;
        var pixels = new byte[size * size * 4];
        // Always the same grain, so the bars match each other and a tile meets its neighbour the same way.
        var random = new Random(20261111);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            // White at a few percent, a little more or less from one pixel to the next, with the colour
            // laid over it. Premultiplied, as the bitmap format wants.
            double frost = (veil + ((random.NextDouble() * 2) - 1) * grain) * (1 - alpha);
            pixels[i] = Channel((tint.B / 255.0 * alpha) + frost);
            pixels[i + 1] = Channel((tint.G / 255.0 * alpha) + frost);
            pixels[i + 2] = Channel((tint.R / 255.0 * alpha) + frost);
            pixels[i + 3] = Channel(alpha + frost);
        }

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32,
            null, pixels, size * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            Viewport = new Rect(0, 0, size, size),
            ViewportUnits = BrushMappingMode.Absolute,
        };
        // Grain that is smoothed when the screen is scaled turns into blotches.
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();
        _frosted = (tint, brush);
        return brush;

        static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
    }

    /// <summary>Runs a UI action; a failure is logged instead of surfacing as a crash.</summary>
    public static void Safe(Action? action, string what = "Action")
    {
        if (action == null) return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error(what + " failed", ex);
        }
    }

    /// <summary>Opens a context menu at the mouse position. An entry with a null header is a separator.</summary>
    public static void ShowMenu(UIElement target, params (string? Header, Action? Action)[] entries) =>
        ShowMenu(target, PlacementMode.MousePoint, entries.Select(e => new MenuEntry(e.Header, e.Action)));

    /// <summary>
    /// Opens a context menu. With <see cref="PlacementMode.MousePoint"/> it opens at the mouse; any other
    /// placement is relative to <paramref name="target"/> (for example above a taskbar button).
    /// </summary>
    public static void ShowMenu(UIElement target, PlacementMode placement, IEnumerable<MenuEntry> entries)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = placement,
        };
        Fill(menu.Items, entries.ToList(), target.Dispatcher);
        menu.IsOpen = true;
    }

    private static void Fill(ItemCollection items, List<MenuEntry> list, Dispatcher dispatcher)
    {
        bool icons = list.Any(e => e.Icon != null);
        bool checks = !icons && list.Any(e => e.Checked);

        foreach (var entry in list)
        {
            if (entry.Header == null)
            {
                items.Add(new Separator());
                continue;
            }

            bool opensMore = entry.Children is { Count: > 0 };
            var item = new MenuItem
            {
                Header = HeaderFor(entry, icons, checks),
                IsEnabled = opensMore || entry.Action != null,
            };
            if (opensMore)
            {
                Fill(item.Items, entry.Children!.ToList(), dispatcher);
            }
            else
            {
                var action = entry.Action;
                string name = entry.Header;
                // Run once the menu has finished closing: the action may open another menu, or close the very
                // window this menu belongs to (moving a taskbar does).
                item.Click += (_, _) => dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => Safe(action, name)));
            }
            items.Add(item);
        }
    }

    private static object HeaderFor(MenuEntry entry, bool icons, bool checks)
    {
        if (!icons && !checks) return entry.Header!;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (icons)
        {
            row.Children.Add(new Image
            {
                Source = entry.Icon,
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else
        {
            row.Children.Add(new TextBlock
            {
                Text = entry.Checked ? "" : "",
                FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
                FontSize = 12,
                Width = 22,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        row.Children.Add(new TextBlock
        {
            Text = entry.Header,
            MaxWidth = 340,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            // In a list with icons (an app's windows) the ticked line is the one in front.
            FontWeight = icons && entry.Checked ? FontWeights.SemiBold : FontWeights.Normal,
        });
        return row;
    }
}
