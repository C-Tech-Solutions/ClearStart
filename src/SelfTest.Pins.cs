using System.IO;

namespace ClearStart;

// The start menu's pinned tiles and folders. Done in memory with apps from the list; the caller reads the pins back
// from the settings as they were afterwards, and nothing is saved.
internal static partial class SelfTest
{
    /// <summary>
    /// Tiles dropped on one another to make a folder, moved, put into a folder and taken out, renamed, sorted and
    /// unpinned, the way a drag or the right-click menu does it, and read back through the settings file.
    /// </summary>
    private static async Task<object> CheckPinFoldersAsync(ShellHost host, StartMenuView menu, Stage stage, string directory,
        List<string> errors)
    {
        var settings = Settings.Current.StartMenu;
        var pins = host.Pins;
        var start = pins.Start;
        var paths = host.Apps.Apps.Select(app => AppCatalog.PinPath(app)).OfType<string>()
            .Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();

        menu.SetQuery("");
        menu.ShowFolder(null);
        settings.Pinned = paths.Take(4).ToList();
        settings.PinFolders = null;
        pins.ReloadStart();
        if (paths.Count < 5 || start.Count < 4) return new { skipped = $"only {paths.Count} app(s) to pin" };

        // The tiles as numbers, each the app's place in the list above; a folder as [name: its apps].
        string Shape() => string.Join(" ", start.Select(tile => tile is PinFolder folder
            ? $"[{folder.Name}: {string.Join(" ", folder.Items.Select(pin => paths.IndexOf(pin.Path)))}]"
            : paths.IndexOf(((PinnedItem)tile).Path).ToString()));
        PinnedItem Pin(int app) => pins.FindStart(paths[app]) ?? throw new InvalidOperationException($"app {app} is not pinned");
        var steps = new List<string>();
        void Expect(string what, string shape)
        {
            string now = Shape();
            steps.Add($"{what}: {now}");
            if (now != shape) errors.Add($"Pinned folders ({what}): the tiles are {now}, not {shape}");
        }

        Expect("pinned", "0 1 2 3");
        menu.DropOnTile(Pin(1), 0, 0.5);
        Expect("1 dropped on 0", "[Folder: 0 1] 2 3");
        menu.DropOnTile(Pin(3), 1, 0.1);
        Expect("3 dropped before 2", "[Folder: 0 1] 3 2");
        menu.DropOnTile(Pin(2), 0, 0.5);
        Expect("2 dropped on the folder", "[Folder: 0 1 2] 3");
        menu.DropOnTile(Pin(3), 0, 0.9);
        Expect("3 dropped just after the folder, where it is already", "[Folder: 0 1 2] 3");
        menu.DropOnTile(Pin(3), 1, 0.5);
        Expect("3 dropped on itself", "[Folder: 0 1 2] 3");

        var folder = (PinFolder)start[0];
        pins.RenameFolder(folder, "   ");
        if (folder.Name != PinFolder.DefaultName) errors.Add($"Pinned folders: a blank name became \"{folder.Name}\", not \"Folder\"");
        pins.RenameFolder(folder, "  Tools ");
        Expect("renamed", "[Tools: 0 1 2] 3");
        bool listed = settings.Pinned is { } pinned && pinned.SequenceEqual(paths.Take(4))
            && settings.PinFolders is [{ Name: "Tools" } entry] && entry.Items.SequenceEqual(paths.Take(3));
        if (!listed) errors.Add("Pinned folders: the settings do not list the pins and the folder as the menu shows them");

        // Through the settings file and back.
        var read = Settings.Parse(Settings.Write(Settings.Current));
        settings.Pinned = read.StartMenu.Pinned;
        settings.PinFolders = read.StartMenu.PinFolders;
        pins.ReloadStart();
        Expect("read back from the file", "[Tools: 0 1 2] 3");
        if (!pins.IsPinnedToStart(paths[2])) errors.Add("Pinned folders: an app in a folder should count as pinned to Start");

        // In the open folder: moved, and an app from the list dragged in.
        menu.ShowFolder((PinFolder)start[0]);
        menu.DropOnTile(Pin(0), 2, 0.9);
        Expect("0 moved to the end of the folder", "[Tools: 1 2 0] 3");
        menu.DropOnTile(paths[4], 0, 0.1);
        Expect("4 dragged from the list to the front of the folder", "[Tools: 4 1 2 0] 3");
        pins.MoveOutOfFolder(Pin(1));
        Expect("1 taken out of the folder", "[Tools: 4 2 0] 1 3");
        if (menu.ShownFolder != start[0]) errors.Add("Pinned folders: the folder should stay open while it has apps in it");

        folder = (PinFolder)start[0];
        pins.SortStart(folder);
        var names = folder.Items.Select(pin => pin.Name).ToList();
        steps.Add("folder sorted: " + Shape());
        if (!names.SequenceEqual(names.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)))
        {
            errors.Add("Pinned folders: sorted, the folder's apps are not in order of name: " + string.Join(", ", names));
        }

        foreach (int app in new[] { 4, 2, 0 }) pins.UnpinFromStart(paths[app]);
        Expect("the folder's apps unpinned", "1 3");
        if (menu.ShownFolder != null) errors.Add("Pinned folders: the folder view should close when its folder goes");
        if (settings.PinFolders != null) errors.Add("Pinned folders: with no folders left, the settings should list none");

        // An app from the list dropped on a pin: a new folder with both.
        menu.DropOnTile(paths[0], 0, 0.5);
        Expect("0 dragged from the list onto 1", "[Folder: 1 0] 3");
        pins.Ungroup((PinFolder)start[0]);
        Expect("ungrouped", "1 0 3");

        // The same with the motion: the icon lifted and flown to its place, a folder opened out of its tile and
        // closed again before it was done. Pictures part of the way, and once it is all over, nothing is left
        // moved, faint or shown. The tiles were made again just now; their icons come first.
        await Task.Delay(1000);
        menu.DropOnTile(Pin(3), 0, 0.1, lift: true);
        Expect("3 lifted and dropped first", "3 1 0");
        if (Motion.On && !menu.Floating) errors.Add("Pinned folders: a dropped tile's icon should fly to its place");
        await Task.Delay(80);
        stage.Save(Path.Combine(directory, "startmenu-pin-landing.png"));
        await Task.Delay(500);
        menu.DropOnTile(Pin(0), 1, 0.5, lift: true);
        Expect("0 lifted and dropped on 1", "3 [Folder: 1 0]");
        await Task.Delay(500);
        menu.ShowFolder((PinFolder)start[1], animate: true);
        await Task.Delay(90);
        stage.Save(Path.Combine(directory, "startmenu-folder-opening.png"));
        menu.ShowFolder(null, animate: true);
        await Task.Delay(90);
        stage.Save(Path.Combine(directory, "startmenu-folder-closing.png"));
        await Task.Delay(1000);
        if (menu.Unsettled() is { } left) errors.Add("Pinned folders: after the animations, " + left);

        // Edited by hand: a folder listing an app that is not pinned, and an app listed in two folders (the first keeps it).
        settings.Pinned = new List<string> { paths[0], paths[1], paths[3] };
        settings.PinFolders = new List<PinFolderEntry>
        {
            new() { Name = "A", Items = new List<string> { paths[1], paths[2], paths[0] } },
            new() { Name = "B", Items = new List<string> { paths[0], paths[3] } },
        };
        pins.ReloadStart();
        Expect("hand-edited", "[A: 1 0] [B: 3]");

        // Pictures: a folder among the pins, then open.
        settings.Pinned = paths.ToList();
        settings.PinFolders = new List<PinFolderEntry> { new() { Name = "Tools", Items = paths.Skip(1).Take(4).ToList() } };
        pins.ReloadStart();
        await Task.Delay(1500);
        stage.Save(Path.Combine(directory, "startmenu-folder.png"));
        menu.ShowFolder(start.OfType<PinFolder>().First());
        await Task.Delay(1000);
        stage.Save(Path.Combine(directory, "startmenu-folder-open.png"));
        menu.ShowFolder(null);
        return steps;
    }
}
