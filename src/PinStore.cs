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

    private bool _resolved;
    private string? _targetFile;
    private string[] _appKeys = Array.Empty<string>();

    /// <summary>The program file this pin starts, if it starts one (Store apps do not).</summary>
    public string? TargetFile
    {
        get
        {
            Resolve();
            return _targetFile;
        }
    }

    /// <summary>What identifies this pin's app among the open windows, so they can share one button.</summary>
    public IReadOnlyList<string> AppKeys
    {
        get
        {
            Resolve();
            return _appKeys;
        }
    }

    private void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        try
        {
            var keys = new List<string>(2);
            if (Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                _targetFile = ShellLink.TargetOf(Path);

                // The built-in File Explorer pin points at a shell location, not at a file.
                if (_targetFile == null && System.IO.Path.GetFileNameWithoutExtension(Path)
                        .Equals("File Explorer", StringComparison.OrdinalIgnoreCase))
                {
                    _targetFile = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                }
            }
            else
            {
                _targetFile = AppCatalog.FileOf(Path);
                if (_targetFile == null && Path.StartsWith(AppCatalog.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    keys.Add(AppKey.ForAppId(Path[AppCatalog.AppsFolderPrefix.Length..]));
                }
            }

            if (!string.IsNullOrEmpty(_targetFile)) keys.Add(AppKey.ForFile(_targetFile));
            _appKeys = keys.ToArray();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not work out which app '{Path}' starts: {ex.Message}");
        }
    }
}

/// <summary>
/// The pinned launchers on the taskbar and the pinned tiles in the start menu.
/// Until the user changes a list here, it mirrors what was pinned in Windows; the first
/// change copies the list into our settings file, after which it is ours.
/// </summary>
internal sealed class PinStore
{
    public ObservableCollection<PinnedItem> Taskbar { get; } = new();

    public ObservableCollection<PinnedItem> Start { get; } = new();

    public void Reload()
    {
        Fill(Taskbar, Settings.Current.Taskbar.Pinned ?? ReadWindowsPins("TaskBar"));
        Fill(Start, Settings.Current.StartMenu.Pinned ?? ReadWindowsPins("StartMenu"));
        Log.Info($"Pins loaded: {Taskbar.Count} on the taskbar, {Start.Count} in the start menu");
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

    private static void Fill(ObservableCollection<PinnedItem> target, IEnumerable<string> paths)
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
            var item = Create(path);
            if (item != null) target.Add(item);
        }
    }

    private static PinnedItem? Create(string path)
    {
        try
        {
            var item = new ShellItem(path);
            if (item.Loaded)
            {
                // Asking for an icon starts loading it in the background; ask now so the taskbar
                // button and the start menu tile have theirs by the time they are first shown.
                _ = item.LargeIcon;
                _ = item.ExtraLargeIcon;
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
        var item = Create(path);
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
        if (ReferenceEquals(list, Start))
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
