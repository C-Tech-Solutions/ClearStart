using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Threading;
using ManagedShell.ShellFolders;

namespace ClearStart;

/// <summary>
/// Every installed app, as Windows itself lists them (desktop programs and Store apps alike).
/// The list fills in the background. Windows does not say when its apps folder changes, so <see cref="AppWatch"/>
/// watches for apps installed or removed, and the folder is then read again and the list brought in line with it.
/// </summary>
internal sealed class AppCatalog : IDisposable
{
    /// <summary>
    /// How long after the list is first read the watch for apps installed or removed starts, with a look at the apps
    /// folder that catches what changed until then. Windows hands the list over in a few seconds; if it is still
    /// doing so, the watch waits as long again, so that a look never adds an app that the first read then adds too.
    /// </summary>
    internal static readonly TimeSpan WatchAfter = TimeSpan.FromSeconds(30);

    /// <summary>How long the list must not have grown before the watch starts.</summary>
    internal static readonly TimeSpan QuietFor = TimeSpan.FromSeconds(10);

    private readonly ObservableCollection<ShellFile> _empty = new();
    private ShellFolder? _folder;
    private DispatcherTimer? _watchSoon;
    private AppWatch? _watch;
    private bool _looking;
    private bool _lookAgain;
    // When an app was last added to the list (Environment.TickCount64).
    private long _grewAt;

    private ObservableCollection<ShellFile>? _only;

    public ObservableCollection<ShellFile> Apps => _only ?? _folder?.Files ?? _empty;

    /// <summary>False until <see cref="Load"/> has got the list from Windows: <see cref="Apps"/> is a stand-in until then.</summary>
    public bool Loaded => _only != null || _folder != null;

    /// <summary>The whole list as Windows gives it, whatever <see cref="ShowOnly"/> has narrowed it to.</summary>
    internal IReadOnlyList<ShellFile> Everything => (_folder?.Files ?? _empty).ToList();

    /// <summary>From here on the list is only these apps. For the screenshot mode's stand-in list.</summary>
    internal void ShowOnly(IEnumerable<ShellFile> apps) => _only = new ObservableCollection<ShellFile>(apps);

    /// <summary>Starts reading the list. Does nothing if it has been started already.</summary>
    /// <param name="watch">Whether to keep the list current as apps are installed or removed. The self-test
    /// does that itself.</param>
    public void Load(bool watch = true)
    {
        if (_folder != null) return;
        try
        {
            _folder = new ShellFolder("shell:AppsFolder", IntPtr.Zero, true, true);

            // The folder only starts reading its contents when the list is first asked for, and an
            // icon only starts loading when it is first asked for. Ask for both now, so the start
            // menu is complete the first time it opens instead of filling in over a few seconds.
            var files = _folder.Files;
            files.CollectionChanged += Apps_Changed;
            foreach (var app in files.ToList())
            {
                _ = app.LargeIcon;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the list of installed apps", ex);
            return;
        }

        if (!watch) return;
        _watchSoon = new DispatcherTimer(DispatcherPriority.Background) { Interval = WatchAfter };
        _watchSoon.Tick += (_, _) =>
        {
            // Still filling: try again next time.
            if (Environment.TickCount64 - _grewAt < QuietFor.TotalMilliseconds) return;
            _watchSoon?.Stop();
            _watchSoon = null;
            Ui.Safe(StartWatching, "Watching for apps installed or removed");
        };
        _watchSoon.Start();
    }

    private void StartWatching()
    {
        if (_folder == null || _watch != null) return;
        _watch = new AppWatch(() => _ = LookAsync(), Dispatcher.CurrentDispatcher, AppWatch.StartMenuFolders(), storeApps: true);
        Log.Info($"Watching {_watch.FolderCount} Start menu folders{(_watch.WatchesStoreApps ? " and Store apps" : "")} for apps installed or removed");
        _ = LookAsync();
    }

    private void Apps_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems == null) return;
        _grewAt = Environment.TickCount64;
        foreach (var added in e.NewItems)
        {
            if (added is ShellFile app) _ = app.LargeIcon;
        }
    }

    /// <summary>
    /// Reads the apps folder again, away from the UI, and adds and removes the apps that have come and gone.
    /// A look asked for while one is under way is taken once that one is done.
    /// </summary>
    internal async Task LookAsync()
    {
        if (_looking)
        {
            _lookAgain = true;
            return;
        }
        _looking = true;
        try
        {
            do
            {
                _lookAgain = false;
                var ids = await ReadIdsAsync();
                if (_folder == null) return;
                if (ids != null) Apply(ids);
            }
            while (_lookAgain);
        }
        catch (Exception ex)
        {
            Log.Error("Could not look for apps installed or removed", ex);
        }
        finally
        {
            _looking = false;
        }
    }

    /// <summary>
    /// The ids of the apps in Windows' apps folder, as <see cref="ShellItem.Path"/> gives them for the list's apps.
    /// Read on a thread of their own, so the UI does not wait; the items read there are let go of there too.
    /// Null if the folder could not be read.
    /// </summary>
    internal static Task<List<string>?> ReadIdsAsync()
    {
        var done = new TaskCompletionSource<List<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ShellFolder? folder = null;
            try
            {
                // Read straight away on this thread: nothing is handed to the UI's thread but the ids.
                folder = new ShellFolder("shell:AppsFolder", IntPtr.Zero, false, false);
                var ids = new List<string>();
                foreach (var app in folder.Files)
                {
                    string? id = app.Path;
                    if (!string.IsNullOrEmpty(id)) ids.Add(id);
                }
                done.TrySetResult(ids);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read the apps folder again: {ex.Message}");
                done.TrySetResult(null);
            }
            finally
            {
                try
                {
                    folder?.Dispose();
                    // The list made a dispatcher for this thread; let it go with the thread.
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not let go of the apps folder read again: {ex.Message}");
                }
            }
        })
        {
            IsBackground = true,
            Name = "ClearStart app list",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    /// <summary>
    /// Brings the list in line with the apps folder's <paramref name="ids"/>: the apps no longer there are taken out,
    /// and those new to it are added. If the folder gave nothing, the list is left as it is.
    /// </summary>
    /// <returns>The names of the apps added and of those taken out.</returns>
    internal (List<string> Added, List<string> Removed) Apply(IReadOnlyCollection<string> ids)
    {
        var added = new List<string>();
        var removed = new List<string>();
        if (_folder == null) return (added, removed);
        if (ids.Count == 0)
        {
            Log.Warn("The apps folder read again was empty; the app list is left as it is");
            return (added, removed);
        }

        var files = _folder.Files;
        var current = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gone = new List<ShellFile>();
        foreach (var app in files.ToList())
        {
            string? id = app.Path;
            if (string.IsNullOrEmpty(id)) continue;
            // No longer in the folder, or listed twice.
            if (!current.Contains(id) || !listed.Add(id)) gone.Add(app);
        }

        var arrived = new List<ShellFile>();
        foreach (string id in current)
        {
            if (listed.Contains(id)) continue;
            var app = ReadApp(_folder, id);
            if (app != null) arrived.Add(app);
        }

        foreach (var app in gone)
        {
            removed.Add(app.DisplayName ?? app.Path ?? "?");
            files.Remove(app);
            app.Dispose();
        }
        foreach (var app in arrived)
        {
            added.Add(app.DisplayName ?? app.Path ?? "?");
            files.Add(app);
        }

        if (added.Count > 0) Log.Info($"Apps added to the list: {string.Join(", ", added)}");
        if (removed.Count > 0) Log.Info($"Apps taken out of the list: {string.Join(", ", removed)}");
        return (added, removed);
    }

    /// <summary>
    /// One app of the apps folder, by its id. Null if it cannot be read, or if Windows names it another way once
    /// read: the next look would not find it in the list then, and would add it again and again.
    /// </summary>
    internal static ShellFile? ReadApp(ShellFolder? folder, string id)
    {
        ShellFile? app = null;
        try
        {
            app = new ShellFile(folder, AppsFolderPrefix + id);
            if (app.Loaded && string.Equals(app.Path, id, StringComparison.OrdinalIgnoreCase)) return app;
            Log.Warn($"Could not add the app '{id}' to the list: Windows reads it back as '{(app.Loaded ? app.Path : "nothing")}'");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not add the app '{id}' to the list: {ex.Message}");
        }
        app?.Dispose();
        return null;
    }

    internal const string AppsFolderPrefix = @"shell:AppsFolder\";

    /// <summary>
    /// What to store when an app from the list is pinned. Windows identifies each app in the list by a
    /// short id that only means something inside the apps folder, so the folder goes in front.
    /// </summary>
    public static string? PinPath(ShellItem app)
    {
        string? id = app.Path;
        return string.IsNullOrEmpty(id) ? null : AppsFolderPrefix + id;
    }

    /// <summary>The program file behind an app or pin, if it has one (Store apps do not).</summary>
    public static string? FileOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            if (path.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[AppsFolderPrefix.Length..];
            }
            if (File.Exists(path)) return path;

            // Desktop programs are listed as "{id of a known folder}\rest\of\the\path.exe".
            int end = path.IndexOf('}');
            if (path.StartsWith('{') && end > 0 && end + 2 < path.Length && path[end + 1] == '\\'
                && Guid.TryParse(path.AsSpan(0, end + 1), out Guid folderId))
            {
                string? folder = Native.KnownFolderPath(folderId);
                if (folder != null)
                {
                    string full = Path.Combine(folder, path[(end + 2)..]);
                    if (File.Exists(full)) return full;
                }
            }
        }
        catch
        {
            // Not a file we can point at.
        }
        return null;
    }

    public void Dispose()
    {
        _watchSoon?.Stop();
        _watchSoon = null;
        _watch?.Dispose();
        _watch = null;
        try
        {
            if (_folder != null) _folder.Files.CollectionChanged -= Apps_Changed;
            _folder?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Could not release the app list", ex);
        }
        _folder = null;
    }
}
