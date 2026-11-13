using System.Collections.ObjectModel;
using ManagedShell.ShellFolders;

namespace ClearStart;

internal sealed class PinnedItem
{
    public PinnedItem(string path, ShellItem item)
    {
        Path = path;
        Item = item;
    }

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

/// <summary>
/// The pinned launchers on the taskbar and the pinned tiles in the start menu.
/// Until the user changes a list here, it mirrors what was pinned in Windows; the first
/// change copies the list into our settings file, after which it is ours.
/// </summary>
internal sealed class PinStore
{
    public ObservableCollection<PinnedItem> Taskbar { get; } = new();

    private readonly ObservableCollection<PinnedItem> _start = new();
    private bool _startLoaded;

    // The taskbar does not need Start-menu shortcuts, shell metadata or their larger icons.
    public ObservableCollection<PinnedItem> Start
    {
        get
        {
            if (!_startLoaded)
            {
                _startLoaded = true;
                Fill(_start, Settings.Current.StartMenu.Pinned ?? ReadWindowsPins("StartMenu"));
                Log.Info($"Start-menu pins loaded: {_start.Count}");
            }
            return _start;
        }
    }

    public void Reload()
    {
        Fill(Taskbar, TaskbarPinPaths(Settings.Current.Taskbar.Pinned), taskbar: true);
        WorkOutAppsInBackground(Taskbar.ToArray());
        if (_startLoaded) Fill(_start, Settings.Current.StartMenu.Pinned ?? ReadWindowsPins("StartMenu"));
        Log.Info($"Taskbar pins loaded: {Taskbar.Count}; Start-menu pins {(_startLoaded ? "refreshed" : "deferred")}");
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

    public bool IsPinnedToStart(string? path) => Contains(Start, path);

    public bool IsPinnedToTaskbar(string? path) => Contains(Taskbar, path);

    public void PinToStart(string path) => Add(Start, path);

    public void UnpinFromStart(string path) => Remove(Start, path);

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
        var paths = list.Select(p => p.Path).ToList();
        if (ReferenceEquals(list, _start))
        {
            Settings.Current.StartMenu.Pinned = paths;
        }
        else
        {
            Settings.Current.Taskbar.Pinned = paths;
        }
        Settings.Save();
    }
}
