using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using ManagedShell.ShellFolders;

namespace ClearStart;

/// <summary>A tile among the start menu's pins: an app, or a folder of them.</summary>
internal interface IStartTile
{
    string Name { get; }

    /// <summary>How big its tile is. Change it through <see cref="PinStore.Resize"/>, which keeps it.</summary>
    TileSize Size { get; set; }
}

internal sealed class PinnedItem : IStartTile, INotifyPropertyChanged
{
    private TileSize _size;

    public PinnedItem(string path, ShellItem item)
    {
        Path = path;
        Item = item;
    }

    public TileSize Size
    {
        get => _size;
        set
        {
            if (_size == value) return;
            _size = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Size)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>What is stored in settings: a file path, or a shell parsing name for apps without one.</summary>
    public string Path { get; }

    public ShellItem Item { get; }

    public string Name => string.IsNullOrEmpty(Item.DisplayName)
        ? System.IO.Path.GetFileNameWithoutExtension(Path)
        : Item.DisplayName;

    // Worked out once. At startup a worker thread does it for the taskbar pins while the bars are built (see
    // PinStore.Reload), and a bar can ask for a pin before the worker gets to it. Neither waits for the other: both
    // come to the same answer, and the first one kept is the one everybody gets.
    private Resolution? _resolution;

    /// <summary>The program file this pin starts, if it starts one (Store apps do not).</summary>
    public string? TargetFile => Resolve().TargetFile;

    /// <summary>What identifies this pin's app among the open windows, so they can share one button.</summary>
    public IReadOnlyList<string> AppKeys => Resolve().AppKeys;

    private Resolution Resolve()
    {
        var resolution = Volatile.Read(ref _resolution);
        if (resolution != null) return resolution;
        resolution = WorkOut();
        return Interlocked.CompareExchange(ref _resolution, resolution, null) ?? resolution;
    }

    private Resolution WorkOut()
    {
        string? targetFile = null;
        try
        {
            var keys = new List<string>(2);
            if (Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                targetFile = ShellLink.TargetOf(Path);

                // The built-in File Explorer pin points at a shell location, not at a file.
                if (targetFile == null && System.IO.Path.GetFileNameWithoutExtension(Path)
                        .Equals("File Explorer", StringComparison.OrdinalIgnoreCase))
                {
                    targetFile = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                }
            }
            else
            {
                targetFile = AppCatalog.FileOf(Path);
                if (targetFile == null && Path.StartsWith(AppCatalog.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    keys.Add(AppKey.ForAppId(Path[AppCatalog.AppsFolderPrefix.Length..]));
                }
            }

            if (!string.IsNullOrEmpty(targetFile)) keys.Add(AppKey.ForFile(targetFile));
            return new Resolution(targetFile, keys.ToArray());
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not work out which app '{Path}' starts: {ex.Message}");
            return new Resolution(targetFile, Array.Empty<string>());
        }
    }

    private sealed record Resolution(string? TargetFile, string[] AppKeys);
}

/// <summary>A folder of pinned apps in the start menu. It always holds at least one: an empty folder goes.</summary>
internal sealed class PinFolder : IStartTile, INotifyPropertyChanged
{
    public const string DefaultName = "Folder";

    private string _name;

    public PinFolder(string name, IEnumerable<PinnedItem> items)
    {
        _name = ValidName(name);
        Items = new ObservableCollection<PinnedItem>(items);
        Items.CollectionChanged += (_, _) => Changed(nameof(Preview));
    }

    public string Name
    {
        get => _name;
        set
        {
            value = ValidName(value);
            if (_name == value) return;
            _name = value;
            Changed(nameof(Name));
        }
    }

    public ObservableCollection<PinnedItem> Items { get; }

    private TileSize _size;

    public TileSize Size
    {
        get => _size;
        set
        {
            if (_size == value) return;
            _size = value;
            Changed(nameof(Size));
        }
    }

    /// <summary>The first four, drawn small on the folder's tile.</summary>
    public IReadOnlyList<PinnedItem> Preview => Items.Take(4).ToList();

    public event PropertyChangedEventHandler? PropertyChanged;

    internal static string ValidName(string? name) => string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();

    private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>
/// The pinned launchers on the taskbar and the pinned tiles in the start menu.
/// Until the user changes a list here, it mirrors what was pinned in Windows; the first
/// change copies the list into our settings file, after which it is ours.
/// </summary>
internal sealed class PinStore
{
    public ObservableCollection<PinnedItem> Taskbar { get; } = new();

    private readonly ObservableCollection<IStartTile> _start = new();
    private bool _startLoaded;

    /// <summary>Off for a self-test, which changes the pins in memory only and must leave the settings file alone.</summary>
    public bool Saves { get; set; } = true;

    /// <summary>The start menu's tiles in order: pinned apps, and folders of them.</summary>
    // The taskbar does not need Start-menu shortcuts, shell metadata or their larger icons.
    public ObservableCollection<IStartTile> Start
    {
        get
        {
            if (!_startLoaded)
            {
                _startLoaded = true;
                FillStart();
                Log.Info($"Start-menu pins loaded: {StartPins.Count()}");
            }
            return _start;
        }
    }

    /// <summary>Every pinned app in the start menu, those in folders too.</summary>
    public IEnumerable<PinnedItem> StartPins =>
        Start.SelectMany(tile => tile is PinFolder folder ? folder.Items : Enumerable.Repeat((PinnedItem)tile, 1));

    public void Reload()
    {
        Fill(Taskbar, TaskbarPinPaths(Settings.Current.Taskbar.Pinned), taskbar: true);
        WorkOutAppsInBackground(Taskbar.ToArray());
        if (_startLoaded) FillStart();
        Log.Info($"Taskbar pins loaded: {Taskbar.Count}; Start-menu pins {(_startLoaded ? "refreshed" : "deferred")}");
    }

    /// <summary>Reads the start menu's pins again from the settings, leaving the taskbar's alone.</summary>
    internal void ReloadStart()
    {
        if (_startLoaded) FillStart();
        else _ = Start;
    }

    /// <summary>What the taskbar pins are: the settings' own list, or else what is pinned in Windows.</summary>
    internal static List<string> TaskbarPinPaths(List<string>? pinned) => pinned ?? ReadWindowsPins("TaskBar");

    /// <summary>
    /// Reads each pin's shortcut on a worker thread, to find which app it starts, so the bars being built next do not
    /// have to stop for it. A bar that gets to a pin first reads it itself.
    /// </summary>
    private static void WorkOutAppsInBackground(PinnedItem[] pins)
    {
        if (pins.Length == 0) return;
        try
        {
            Task.Run(() =>
            {
                foreach (var pin in pins) _ = pin.AppKeys;
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not start reading the pinned shortcuts in the background: " + ex.Message);
        }
    }

    public bool IsPinnedToStart(string? path) => FindStart(path) != null;

    public bool IsPinnedToTaskbar(string? path) => Contains(Taskbar, path);

    public void PinToStart(string path) => PinToStart(path, null, -1);

    /// <summary>
    /// Pins an app to Start, at a place among the tiles or in a folder (-1: at the end). Returns the pin, or null if it
    /// could not be loaded. An app pinned already stays where it is.
    /// </summary>
    public PinnedItem? PinToStart(string path, PinFolder? folder, int index)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (FindStart(path) is { } pinned) return pinned;
        var pin = Create(path, taskbar: false);
        if (pin == null) return null;
        if (folder != null && !Start.Contains(folder)) folder = null;
        Insert(ListOf(folder), pin, index);
        PersistStart();
        return pin;
    }

    public void UnpinFromStart(string path)
    {
        if (FindStart(path) is not { } pin) return;
        TakeOut(pin);
        pin.Item.Dispose();
        PersistStart();
    }

    /// <summary>The start menu's pin of this app, in a folder or not.</summary>
    public PinnedItem? FindStart(string? path) => path == null
        ? null
        : StartPins.FirstOrDefault(pin => string.Equals(pin.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>The folder a pin is in; null when it is not in one.</summary>
    public PinFolder? FolderOf(PinnedItem pin) => Start.OfType<PinFolder>().FirstOrDefault(folder => folder.Items.Contains(pin));

    /// <summary>
    /// Moves a tile to a place among the tiles (folder null) or in a folder. The place counts the tiles as they are
    /// before the move, the way a drop shows it. A folder does not go in a folder; one left empty goes.
    /// </summary>
    public void MoveStart(IStartTile tile, PinFolder? folder, int index)
    {
        if (folder != null && (tile is PinFolder || !Start.Contains(folder))) return;
        var from = ListContaining(tile);
        if (from == null) return;
        var to = ListOf(folder);
        int at = from.IndexOf(tile);

        if (ReferenceEquals(from, to))
        {
            if (index < 0 || index > to.Count) index = to.Count;
            if (index > at) index--;
            if (index == at) return;
            from.RemoveAt(at);
            from.Insert(index, tile);
        }
        else
        {
            // Placed first and the old folder emptied after, so the place counts the tiles as the drop saw them.
            from.RemoveAt(at);
            Insert(to, tile, index);
            RemoveIfEmpty(from);
        }
        PersistStart();
    }

    /// <summary>One pin dropped on another: a new folder holding both, where the one dropped on was.</summary>
    public PinFolder? Group(PinnedItem pin, PinnedItem onto)
    {
        if (ReferenceEquals(pin, onto) || !Start.Contains(onto) || ListContaining(pin) == null) return null;
        TakeOut(pin);
        // As big as the tile it takes the place of, so the tiles round it stay where they are.
        var folder = new PinFolder(PinFolder.DefaultName, Array.Empty<PinnedItem>()) { Size = onto.Size };
        int at = _start.IndexOf(onto);
        // Taken out and put in, not replaced: a replaced tile keeps its look, the pin's, for a moment.
        _start.RemoveAt(at);
        folder.Items.Add(onto);
        folder.Items.Add(pin);
        _start.Insert(at, folder);
        PersistStart();
        return folder;
    }

    /// <summary>A new folder holding just this pin, where the pin was.</summary>
    public PinFolder? NewFolder(PinnedItem pin)
    {
        if (ListContaining(pin) == null) return null;
        var old = FolderOf(pin);
        int at = old == null ? _start.IndexOf(pin) : _start.IndexOf(old) + 1;
        TakeOut(pin);
        if (old != null && !_start.Contains(old)) at--;
        var folder = new PinFolder(PinFolder.DefaultName, new[] { pin }) { Size = old == null ? pin.Size : TileSize.Medium };
        _start.Insert(Math.Clamp(at, 0, _start.Count), folder);
        PersistStart();
        return folder;
    }

    /// <summary>Out of its folder, to just after the folder.</summary>
    public void MoveOutOfFolder(PinnedItem pin)
    {
        if (FolderOf(pin) is { } folder) MoveStart(pin, null, _start.IndexOf(folder) + 1);
    }

    /// <summary>Makes a tile, in a folder or not, or a folder's own tile, another size.</summary>
    public void Resize(IStartTile tile, TileSize size)
    {
        if (tile.Size == size || ListContaining(tile) == null) return;
        tile.Size = size;
        PersistStart();
    }

    public void RenameFolder(PinFolder folder, string? name)
    {
        string old = folder.Name;
        folder.Name = name ?? "";
        if (folder.Name != old && _start.Contains(folder)) PersistStart();
    }

    /// <summary>The folder goes, and its pins take its place.</summary>
    public void Ungroup(PinFolder folder)
    {
        int at = _start.IndexOf(folder);
        if (at < 0) return;
        var pins = folder.Items.ToList();
        folder.Items.Clear();
        _start.RemoveAt(at);
        foreach (var pin in pins) _start.Insert(at++, pin);
        PersistStart();
    }

    /// <summary>Puts the tiles in a folder (null: the start menu's own) in order of name, folders among them.</summary>
    public void SortStart(PinFolder? folder)
    {
        var list = ListOf(folder);
        var sorted = list.Cast<IStartTile>().OrderBy(tile => tile.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (sorted.SequenceEqual(list.Cast<IStartTile>())) return;
        for (int i = 0; i < sorted.Count; i++)
        {
            int at = list.IndexOf(sorted[i]);
            if (at != i)
            {
                list.RemoveAt(at);
                list.Insert(i, sorted[i]);
            }
        }
        PersistStart();
    }

    public void PinToTaskbar(string path) => Add(Taskbar, path);

    public void UnpinFromTaskbar(string path) => Remove(Taskbar, path);

    private static string WindowsPinFolder(string leaf) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned", leaf);

    private static List<string> ReadWindowsPins(string leaf)
    {
        try
        {
            string folder = WindowsPinFolder(leaf);
            if (!Directory.Exists(folder)) return new List<string>();
            return Directory.GetFiles(folder, "*.lnk")
                .OrderBy(file => Path.GetFileName(file), StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the Windows pinned items: " + ex.Message);
            return new List<string>();
        }
    }

    /// <summary>
    /// The start menu's tiles from the settings. Every pin is in the plain list, a folder's pins together where the
    /// folder is, so a ClearStart without folders still shows them all; the folders only say which go together. A pin
    /// that is not in the plain list any more is not in a folder either.
    /// </summary>
    private void FillStart()
    {
        var settings = Settings.Current.StartMenu;
        var paths = settings.Pinned ?? ReadWindowsPins("StartMenu");
        var pinned = new HashSet<string>(paths.Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);

        // Which folder each pin is in. One listed in two stays in the first.
        var folderOf = new Dictionary<string, PinFolderEntry>(StringComparer.OrdinalIgnoreCase);
        if (settings.Pinned != null)
        {
            foreach (var entry in settings.PinFolders ?? new List<PinFolderEntry>())
            {
                foreach (string path in entry.Items) folderOf.TryAdd(path, entry);
            }
        }

        foreach (var tile in _start.ToList())
        {
            foreach (var pin in tile is PinFolder folder ? folder.Items.ToList() : new List<PinnedItem> { (PinnedItem)tile })
            {
                pin.Item.Dispose();
            }
        }
        _start.Clear();

        var sizes = new Dictionary<string, TileSize>(StringComparer.OrdinalIgnoreCase);
        if (settings.Pinned != null)
        {
            foreach (var (path, size) in settings.TileSizes ?? new Dictionary<string, TileSize>()) sizes.TryAdd(path, size);
        }
        PinnedItem? Pin(string path)
        {
            var pin = Create(path, taskbar: false);
            if (pin != null) pin.Size = sizes.GetValueOrDefault(path);
            return pin;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || seen.Contains(path)) continue;
            if (folderOf.TryGetValue(path, out var entry))
            {
                // The folder goes where the first of its pins is, with all of them in it.
                var items = entry.Items
                    .Where(item => pinned.Contains(item) && ReferenceEquals(folderOf[item], entry) && seen.Add(item))
                    .Select(Pin)
                    .OfType<PinnedItem>()
                    .ToList();
                if (items.Count > 0) _start.Add(new PinFolder(entry.Name, items) { Size = entry.Size });
                continue;
            }

            seen.Add(path);
            if (Pin(path) is { } pin) _start.Add(pin);
        }
    }

    private IList ListOf(PinFolder? folder) => folder == null ? Start : folder.Items;

    /// <summary>The list a tile is in: the start menu's own, or a folder's. Null when it is in neither.</summary>
    private IList? ListContaining(IStartTile tile)
    {
        if (Start.Contains(tile)) return _start;
        return tile is PinnedItem pin ? FolderOf(pin)?.Items : null;
    }

    private static void Insert(IList list, object item, int index) =>
        list.Insert(index < 0 || index > list.Count ? list.Count : index, item);

    /// <summary>Takes a pin out of the tiles or its folder; a folder left empty goes.</summary>
    private void TakeOut(PinnedItem pin)
    {
        if (ListContaining(pin) is not { } list) return;
        list.Remove(pin);
        RemoveIfEmpty(list);
    }

    private void RemoveIfEmpty(IList list)
    {
        if (list.Count == 0 && _start.OfType<PinFolder>().FirstOrDefault(folder => ReferenceEquals(folder.Items, list)) is { } empty)
        {
            _start.Remove(empty);
        }
    }

    private void PersistStart()
    {
        var paths = new List<string>();
        var folders = new List<PinFolderEntry>();
        var sizes = new Dictionary<string, TileSize>(StringComparer.OrdinalIgnoreCase);
        foreach (var tile in _start)
        {
            if (tile is PinFolder folder)
            {
                var items = folder.Items.Select(pin => pin.Path).ToList();
                paths.AddRange(items);
                folders.Add(new PinFolderEntry { Name = folder.Name, Items = items, Size = folder.Size });
                foreach (var pin in folder.Items.Where(pin => pin.Size != TileSize.Medium)) sizes.TryAdd(pin.Path, pin.Size);
            }
            else if (tile is PinnedItem pin)
            {
                paths.Add(pin.Path);
                if (pin.Size != TileSize.Medium) sizes.TryAdd(pin.Path, pin.Size);
            }
        }
        Settings.Current.StartMenu.Pinned = paths;
        Settings.Current.StartMenu.PinFolders = folders.Count > 0 ? folders : null;
        Settings.Current.StartMenu.TileSizes = sizes.Count > 0 ? sizes : null;
        if (Saves) Settings.Save();
    }

    private static void Fill(ObservableCollection<PinnedItem> target, IEnumerable<string> paths, bool taskbar = false)
    {
        var old = target.ToList();
        target.Clear();
        foreach (var item in old)
        {
            item.Item.Dispose();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !seen.Add(path)) continue;
            var item = Create(path, taskbar);
            if (item != null) target.Add(item);
        }
    }

    private static PinnedItem? Create(string path, bool taskbar)
    {
        try
        {
            var item = new ShellItem(path);
            if (item.Loaded)
            {
                // Prioritize the one size taskbar buttons use. Start tiles request their own size on demand.
                if (taskbar) _ = item.LargeIcon;
                return new PinnedItem(path, item);
            }

            item.Dispose();
            Log.Warn($"Pinned item not found, skipping: {path}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Pinned item could not be loaded ({path}): {ex.Message}");
        }
        return null;
    }

    private static bool Contains(ObservableCollection<PinnedItem> list, string? path) =>
        path != null && list.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));

    private void Add(ObservableCollection<PinnedItem> list, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Contains(list, path)) return;
        var item = Create(path, ReferenceEquals(list, Taskbar));
        if (item == null) return;
        list.Add(item);
        Persist(list);
    }

    private void Remove(ObservableCollection<PinnedItem> list, string path)
    {
        var item = list.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;
        list.Remove(item);
        item.Item.Dispose();
        Persist(list);
    }

    private void Persist(ObservableCollection<PinnedItem> list)
    {
        Settings.Current.Taskbar.Pinned = list.Select(p => p.Path).ToList();
        if (Saves) Settings.Save();
    }
}
