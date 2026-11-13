using System.IO;
using System.Windows;
using ManagedShell.ShellFolders;

namespace ClearStart;

// The start menu's tiles in their sizes, drawn bigger, and the Properties line of the right-click menus. Done in
// memory; the caller puts the pins and the settings back as they were afterwards, and nothing is saved.
internal static partial class SelfTest
{
    private const TileSize S = TileSize.Small, M = TileSize.Medium, W = TileSize.Wide, L = TileSize.Large;

    /// <summary>
    /// Where the grid puts tiles of each size, the tiles resized the way the right-click menu does it and read back
    /// through the settings file, a menu that draws them bigger, and when Properties can be offered.
    /// </summary>
    private static async Task<object> CheckTileSizesAsync(ShellHost host, StartMenuView menu, Stage stage, string directory,
        List<string> errors)
    {
        var steps = new List<string>();
        string Shown(IEnumerable<Rect> places) =>
            string.Join("; ", places.Select(place => $"{place.X:0},{place.Y:0} {place.Width:0}x{place.Height:0}"));

        // The grid on its own, four tiles across unless it says otherwise.
        void ExpectPlaces(string what, string shape, int columns, params TileSize[] sizes)
        {
            string now = Shown(TileGrid.Place(sizes, columns));
            steps.Add($"{what}: {now}");
            if (now != shape) errors.Add($"Tile sizes ({what}): the tiles go at {now}, not {shape}");
        }
        ExpectPlaces("all medium, as a wrap panel", "0,0 100x92; 100,0 100x92; 200,0 100x92; 300,0 100x92; 0,92 100x92",
            4, M, M, M, M, M);
        ExpectPlaces("medium ones beside and under a large one",
            "0,0 200x184; 200,0 100x92; 300,0 100x92; 200,92 100x92; 300,92 100x92; 0,184 100x92", 4, L, M, M, M, M, M);
        ExpectPlaces("small ones four to a place", "0,0 50x46; 50,0 50x46; 0,46 50x46; 50,46 50x46; 100,0 50x46; 200,0 100x92",
            4, S, S, S, S, S, M);
        ExpectPlaces("a wide one that does not fit at the end of a row; the gap stays",
            "0,0 100x92; 100,0 100x92; 200,0 100x92; 0,92 200x92; 200,92 100x92", 4, M, M, M, W, M);
        ExpectPlaces("a small one after a medium one starts a place of its own", "0,0 50x46; 100,0 100x92; 200,0 50x46",
            4, S, M, S);
        ExpectPlaces("one across: a wide one as wide as it can be", "0,0 100x92; 0,92 100x92", 1, W, M);

        var settings = Settings.Current.StartMenu;
        var pins = host.Pins;
        var start = pins.Start;
        var paths = host.Apps.Apps.Select(app => AppCatalog.PinPath(app)).OfType<string>()
            .Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        if (paths.Count < 8) return new { steps, skipped = $"only {paths.Count} app(s) to pin" };

        menu.SetQuery("");
        menu.ShowFolder(null);
        settings.Pinned = paths.ToList();
        settings.PinFolders = null;
        settings.TileSizes = null;
        pins.ReloadStart();

        string Sizes() => string.Join(" ", start.Select(tile => tile is PinFolder folder
            ? $"[{folder.Size}: {string.Join(" ", folder.Items.Select(pin => pin.Size))}]"
            : tile.Size.ToString()));
        void Expect(string what, string sizes)
        {
            string now = Sizes();
            steps.Add($"{what}: {now}");
            if (now != sizes) errors.Add($"Tile sizes ({what}): the tiles are {now}, not {sizes}");
        }
        void ReadBack()
        {
            var read = Settings.Parse(Settings.Write(Settings.Current));
            settings.Pinned = read.StartMenu.Pinned;
            settings.PinFolders = read.StartMenu.PinFolders;
            settings.TileSizes = read.StartMenu.TileSizes;
            pins.ReloadStart();
        }

        Expect("pinned", "Medium Medium Medium Medium Medium Medium Medium Medium");
        menu.Resize(start[0], L);
        menu.Resize(start[1], W);
        menu.Resize(start[2], S);
        menu.Resize(start[3], S);
        Expect("resized", "Large Wide Small Small Medium Medium Medium Medium");
        var listed = settings.TileSizes ?? new Dictionary<string, TileSize>();
        string written = string.Join(" ", paths.Select(path => listed.TryGetValue(path, out var size) ? size.ToString() : "-"));
        if (written != "Large Wide Small Small - - - -")
        {
            errors.Add($"Tile sizes: the settings list the sizes {written}; a medium tile should not be listed");
        }

        // As drawn: each the right size against a medium one, and none on top of another.
        var places = menu.TilePlaces();
        steps.Add("drawn: " + Shown(places));
        var medium = places[4];
        void ExpectDrawn(int tile, double across, double down)
        {
            var place = places[tile];
            if (Math.Abs(place.Width - medium.Width * across) > 0.5 || Math.Abs(place.Height - medium.Height * down) > 0.5)
            {
                errors.Add($"Tile sizes: tile {tile} ({start[tile].Size}) is drawn {place.Width:0}x{place.Height:0}"
                    + $" against a medium tile's {medium.Width:0}x{medium.Height:0}");
            }
        }
        ExpectDrawn(0, 2, 2);
        ExpectDrawn(1, 2, 1);
        ExpectDrawn(2, 0.5, 0.5);
        ExpectDrawn(3, 0.5, 0.5);
        for (int i = 0; i < places.Count; i++)
        {
            for (int j = i + 1; j < places.Count; j++)
            {
                var overlap = Rect.Intersect(places[i], places[j]);
                if (!overlap.IsEmpty && overlap.Width > 0.5 && overlap.Height > 0.5) errors.Add($"Tile sizes: tiles {i} and {j} overlap");
            }
        }

        // The right-click menu ticks the size the tile is.
        var sizeMenu = menu.TileMenu((PinnedItem)start[1]).FirstOrDefault(entry => entry.Header == "Size");
        string ticked = string.Join(" ", sizeMenu?.Children?.Where(entry => entry.Checked).Select(entry => entry.Header) ?? []);
        if (sizeMenu?.Children is not { Count: 4 } || ticked != "Wide")
        {
            errors.Add($"Tile sizes: a wide tile's Size menu should list four sizes with Wide ticked, not [{ticked}]");
        }

        ReadBack();
        Expect("read back from the file", "Large Wide Small Small Medium Medium Medium Medium");

        // A folder made on a tile takes its size; a folder's own size is kept with it.
        menu.Resize(start[4], W);
        menu.DropOnTile(start[5], 4, 0.5);
        Expect("dropped on a wide tile", "Large Wide Small Small [Wide: Wide Medium] Medium Medium");
        menu.Resize(start[4], L);
        if (settings.PinFolders is not [{ Size: L }]) errors.Add("Tile sizes: the settings do not keep the folder's size");
        ReadBack();
        Expect("the folder read back", "Large Wide Small Small [Large: Wide Medium] Medium Medium");

        await Task.Delay(1500);
        stage.Save(Path.Combine(directory, "startmenu-tile-sizes.png"));

        // Drawn half as big again: a medium tile half as wide again, and fewer of them across.
        settings.Pinned = paths.ToList();
        settings.PinFolders = null;
        settings.TileSizes = null;
        pins.ReloadStart();
        int scaleBefore = settings.PinScale;
        settings.PinScale = 150;
        var bigger = new StartMenuView(host, () => { }) { Width = settings.Width, Height = settings.Height };
        var onStage = new Stage(bigger, settings.Width + 40, settings.Height + 40);
        try
        {
            await Task.Delay(1500);
            var big = bigger.TilePlaces();
            steps.Add("drawn at 150%: " + Shown(big));
            if (big.Count == 0 || Math.Abs(big[0].Width - TileGrid.CellWidth * 1.5) > 1)
            {
                errors.Add($"Tile sizes: at 150% a medium tile is drawn {(big.Count > 0 ? big[0].Width : 0):0} wide,"
                    + $" not {TileGrid.CellWidth * 1.5:0}");
            }
            onStage.Save(Path.Combine(directory, "startmenu-tile-scale.png"));
        }
        finally
        {
            settings.PinScale = scaleBefore;
            onStage.Close();
            bigger.Detach();
        }

        // Properties: offered for a file, never opened here.
        foreach (string path in paths)
        {
            var properties = menu.TileMenu(pins.FindStart(path)!).FirstOrDefault(entry => entry.Header == "Properties");
            bool offered = properties?.Action != null, file = AppCatalog.FileOf(path) != null;
            if (properties == null || offered != file)
            {
                errors.Add($"Properties: for {path} it should be {(file ? "offered" : "greyed out")}");
            }
        }
        var targets = new (string Target, bool Offered)[]
        {
            ("shell:Downloads", true), ("shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", false),
            ("C:\\", true), ("%USERPROFILE%", true), ("ms-settings:display", false), ("cmd.exe", false), ("", false),
        };
        foreach (var (target, offered) in targets)
        {
            if (Launcher.HasProperties(target) != offered)
            {
                errors.Add($"Properties: for the place \"{target}\" it should {(offered ? "" : "not ")}be offered");
            }
        }
        using (var downloads = new ShellItem("shell:Downloads"))
        {
            if (!downloads.Loaded || downloads.AbsolutePidl == IntPtr.Zero) errors.Add("Properties: shell:Downloads could not be found");
        }
        return steps;
    }
}
