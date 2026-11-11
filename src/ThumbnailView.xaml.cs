using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ManagedShell.WindowsTasks;

namespace ClearStart;

/// <summary>One window in the preview popup.</summary>
internal sealed class WindowTile
{
    public WindowTile(ApplicationWindow window) => Window = window;

    public ApplicationWindow Window { get; }

    /// <summary>The compositor's live picture of the window; zero if it could not be had.</summary>
    public IntPtr Thumbnail { get; set; }

    /// <summary>The size of the window itself, which gives the picture its shape.</summary>
    public Size SourceSize { get; set; }
}

/// <summary>
/// The contents of the preview popup: one tile per window of an app. The tiles are all the same size;
/// the live pictures are not drawn here but by the compositor, in the areas <see cref="PictureAreas"/> reports.
/// </summary>
public partial class ThumbnailView : UserControl
{
    // The picture box is this wide when there is room and shrinks, down to the minimum, to fit more windows in a row.
    private const double BoxWidthMax = 208;
    private const double BoxWidthMin = 132;
    private const double BoxShape = 9.0 / 16.0;

    // What a tile takes up around its picture box: padding and margin across, plus the title row down.
    private const double TileExtraAcross = 20;
    private const double TileExtraDown = 46;

    public ThumbnailView()
    {
        InitializeComponent();
        SetBox(BoxWidthMax);
    }

    /// <summary>The user clicked a window's tile.</summary>
    internal event Action<WindowTile>? Chosen;

    /// <summary>The user asked for a window to be closed (the tile's close button, or a middle-click).</summary>
    internal event Action<WindowTile>? CloseRequested;

    /// <param name="stacked">One under the other (for a side bar) instead of side by side.</param>
    /// <param name="maxLength">How long the popup may get in that direction before the tiles wrap.</param>
    internal void SetTiles(IReadOnlyList<WindowTile> tiles, bool stacked, double maxLength)
    {
        int count = Math.Max(1, tiles.Count);
        double room = maxLength - 8;
        double width = stacked
            ? (room / count - TileExtraDown) / BoxShape
            : room / count - TileExtraAcross;
        SetBox(Math.Clamp(Math.Floor(width), BoxWidthMin, BoxWidthMax));

        Tiles.ItemsPanel = (ItemsPanelTemplate)Resources[stacked ? "Down" : "Across"];
        Frame.MaxWidth = stacked ? double.PositiveInfinity : maxLength;
        Frame.MaxHeight = stacked ? maxLength : double.PositiveInfinity;
        Tiles.ItemsSource = tiles;
    }

    /// <summary>Lets go of the windows once the popup is hidden.</summary>
    internal void Clear() => Tiles.ItemsSource = null;

    private void SetBox(double width)
    {
        Resources["BoxWidth"] = width;
        Resources["BoxHeight"] = Math.Round(width * BoxShape);
    }

    /// <summary>
    /// Where each window's picture goes, in the coordinates of <paramref name="relativeTo"/>: centred in the
    /// tile's box, in the window's own shape, never enlarged. Call after layout.
    /// </summary>
    internal List<(WindowTile Tile, Rect Area)> PictureAreas(Visual relativeTo)
    {
        var result = new List<(WindowTile, Rect)>();
        foreach (var item in Tiles.Items)
        {
            if (item is not WindowTile tile) continue;
            if (Tiles.ItemContainerGenerator.ContainerFromItem(item) is not ContentPresenter presenter) continue;
            if (presenter.ContentTemplate?.FindName("Picture", presenter) is not FrameworkElement box) continue;
            if (box.ActualWidth <= 0 || box.ActualHeight <= 0) continue;

            var source = tile.SourceSize;
            if (source.Width <= 0 || source.Height <= 0) continue;

            double scale = Math.Min(1, Math.Min(box.ActualWidth / source.Width, box.ActualHeight / source.Height));
            double width = Math.Round(source.Width * scale);
            double height = Math.Round(source.Height * scale);
            var inBox = new Point(Math.Round((box.ActualWidth - width) / 2), Math.Round((box.ActualHeight - height) / 2));
            var origin = box.TransformToAncestor(relativeTo).Transform(inBox);
            result.Add((tile, new Rect(origin, new Size(width, height))));
        }
        return result;
    }

    private void Tile_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WindowTile tile) return;
        e.Handled = true;
        Chosen?.Invoke(tile);
    }

    private void Tile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if ((sender as FrameworkElement)?.DataContext is not WindowTile tile) return;
        e.Handled = true;
        CloseRequested?.Invoke(tile);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WindowTile tile) CloseRequested?.Invoke(tile);
    }
}
