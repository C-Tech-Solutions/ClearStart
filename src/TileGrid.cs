using System.Windows;
using System.Windows.Controls;

namespace ClearStart;

/// <summary>
/// Lays the start menu's pinned tiles out in order, row by row, on a grid of medium tiles' places. A wide tile takes two
/// places side by side, a large one two by two, and small tiles go four to a place. A tile goes in the first place at
/// or after the one before it that it fits in, so a gap it does not fit is left as it is and the tiles keep their
/// order. With every tile medium this is the same as a wrap panel.
/// </summary>
public sealed class TileGrid : Panel
{
    /// <summary>A medium tile's place, its margin included.</summary>
    public const double CellWidth = 100, CellHeight = 92;

    public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached(
        "Size", typeof(TileSize), typeof(TileGrid),
        new FrameworkPropertyMetadata(TileSize.Medium,
            FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static TileSize GetSize(UIElement element) => (TileSize)element.GetValue(SizeProperty);

    public static void SetSize(UIElement element, TileSize value) => element.SetValue(SizeProperty, value);

    protected override Size MeasureOverride(Size available)
    {
        var places = Places(available.Width);
        var extent = new Size();
        for (int i = 0; i < places.Length; i++)
        {
            InternalChildren[i].Measure(places[i].Size);
            extent.Width = Math.Max(extent.Width, places[i].Right);
            extent.Height = Math.Max(extent.Height, places[i].Bottom);
        }
        return extent;
    }

    protected override Size ArrangeOverride(Size final)
    {
        var places = Places(final.Width);
        for (int i = 0; i < places.Length; i++) InternalChildren[i].Arrange(places[i]);
        return final;
    }

    private Rect[] Places(double width)
    {
        // As many medium tiles across as fit; unbounded, as many as a wrap panel would put in a row of four.
        int columns = double.IsInfinity(width) || double.IsNaN(width) ? 4 : Math.Max(1, (int)(width / CellWidth));
        var sizes = new TileSize[InternalChildren.Count];
        for (int i = 0; i < sizes.Length; i++) sizes[i] = GetSize(InternalChildren[i]);
        return Place(sizes, columns);
    }

    /// <summary>Where each tile goes, given their sizes in order and how many medium tiles fit across.</summary>
    internal static Rect[] Place(IReadOnlyList<TileSize> sizes, int columns)
    {
        var places = new Rect[sizes.Count];
        var taken = new List<bool[]>();
        int row = 0, column = 0;
        // A place that small tiles are filling: the next small one goes in it, after the ones there.
        int smallRow = 0, smallColumn = 0, smalls = 4;

        for (int i = 0; i < sizes.Count; i++)
        {
            var size = sizes[i];
            if (size == TileSize.Small && smalls < 4)
            {
                places[i] = SmallPlace(smallRow, smallColumn, smalls++);
                continue;
            }

            var (across, down) = size switch
            {
                TileSize.Wide => (2, 1),
                TileSize.Large => (2, 2),
                _ => (1, 1),
            };
            across = Math.Min(across, columns);
            (row, column) = FirstFree(taken, row, column, across, down, columns);
            for (int r = row; r < row + down; r++)
            {
                for (int c = column; c < column + across; c++) taken[r][c] = true;
            }

            if (size == TileSize.Small)
            {
                (smallRow, smallColumn, smalls) = (row, column, 1);
                places[i] = SmallPlace(row, column, 0);
            }
            else
            {
                smalls = 4;
                places[i] = new Rect(column * CellWidth, row * CellHeight, across * CellWidth, down * CellHeight);
            }
        }
        return places;
    }

    /// <summary>One of the four quarters of a medium tile's place, left to right and then down.</summary>
    private static Rect SmallPlace(int row, int column, int quarter) => new(
        column * CellWidth + quarter % 2 * CellWidth / 2, row * CellHeight + quarter / 2 * CellHeight / 2,
        CellWidth / 2, CellHeight / 2);

    /// <summary>The first free place at or after <paramref name="row"/> and <paramref name="column"/>, row by row.</summary>
    private static (int Row, int Column) FirstFree(List<bool[]> taken, int row, int column, int across, int down, int columns)
    {
        for (int r = row; ; r++)
        {
            for (int c = r == row ? column : 0; c + across <= columns; c++)
            {
                if (Free(taken, r, c, across, down, columns)) return (r, c);
            }
        }
    }

    private static bool Free(List<bool[]> taken, int row, int column, int across, int down, int columns)
    {
        while (taken.Count < row + down) taken.Add(new bool[columns]);
        for (int r = row; r < row + down; r++)
        {
            for (int c = column; c < column + across; c++)
            {
                if (taken[r][c]) return false;
            }
        }
        return true;
    }
}
