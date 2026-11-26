using System.Windows.Threading;
using Windows.ApplicationModel;

namespace ClearStart;

/// <summary>
/// Says when apps may have been installed or removed, so the app list can be read again. Windows does not say so
/// for its apps folder, so this watches what changes when apps come and go: desktop programs add, rewrite and remove
/// their shortcuts in the Start menu folders, and Store apps are installed, updated and removed through Windows' list
/// of app packages.
///
/// Windows lists a new shortcut in its apps folder a few seconds after it is written (about 4.5 s on a laptop it was
/// timed on), and takes a removed one out as late. So after a change the apps are looked at every couple of seconds
/// until half a minute has passed since the last change, and a last time a minute after it, in case Windows is slow.
/// </summary>
internal sealed class AppWatch : IDisposable
{
    /// <summary>How often the apps are looked at after a change.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(2);

    /// <summary>How long after the last change the apps are looked at every <see cref="Every"/>.</summary>
    public static readonly TimeSpan LookFor = TimeSpan.FromSeconds(30);

    /// <summary>When, after the last change, the apps are looked at a last time.</summary>
    public static readonly TimeSpan LastLookAfter = TimeSpan.FromMinutes(1);

    private readonly Action _look;
    private readonly Dispatcher _dispatcher;
    private readonly TimeSpan _every;
    private readonly TimeSpan _lookFor;
    private readonly TimeSpan _lastLookAfter;
    private readonly DispatcherTimer _timer;
    private readonly List<FileSystemWatcher> _watchers = new();
    private PackageCatalog? _packages;
    private long _lastChange;
    private bool _waitingForLastLook;
    private bool _disposed;

    /// <param name="look">Called on the dispatcher's thread when the apps should be looked at.</param>
    /// <param name="folders">The folders whose shortcuts are watched, with every folder inside them.</param>
    /// <param name="storeApps">Whether to watch for Store apps installed, updated or removed too.</param>
    /// <param name="every">How often <paramref name="look"/> is called after a change.</param>
    /// <param name="lookFor">How long after the last change <paramref name="look"/> is called every
    /// <paramref name="every"/>.</param>
    /// <param name="lastLookAfter">When, after the last change, <paramref name="look"/> is called a last time.</param>
    public AppWatch(Action look, Dispatcher dispatcher, IEnumerable<string> folders, bool storeApps,
        TimeSpan? every = null, TimeSpan? lookFor = null, TimeSpan? lastLookAfter = null)
    {
        _look = look;
        _dispatcher = dispatcher;
        _every = every ?? Every;
        _lookFor = lookFor ?? LookFor;
        _lastLookAfter = lastLookAfter ?? LastLookAfter;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += Timer_Tick;
        foreach (string folder in folders) WatchFolder(folder);
        if (storeApps) WatchStoreApps();
    }

    /// <summary>The Start menu folders that desktop programs put their shortcuts in: the user's own, and everyone's.</summary>
    public static IEnumerable<string> StartMenuFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
    ];

    /// <summary>How many folders are watched.</summary>
    public int FolderCount => _watchers.Count;

    /// <summary>Whether Store apps installed, updated or removed are watched for.</summary>
    public bool WatchesStoreApps => _packages != null;

    private void WatchFolder(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                // Written too: an update can rewrite a shortcut in place to point at its new version.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            watcher.Created += File_Changed;
            watcher.Deleted += File_Changed;
            watcher.Renamed += File_Changed;
            watcher.Changed += File_Written;
            // Too many changes at once for Windows to list them: something changed all the same.
            watcher.Error += (_, _) => Changed();
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            Log.Warn($"Cannot watch '{folder}' for apps installed or removed: {ex.Message}");
        }
    }

    private void WatchStoreApps()
    {
        try
        {
            var packages = PackageCatalog.OpenForCurrentUser();
            packages.PackageInstalling += Package_Installing;
            packages.PackageUninstalling += Package_Uninstalling;
            packages.PackageUpdating += Package_Updating;
            _packages = packages;
        }
        catch (Exception ex)
        {
            Log.Warn($"Cannot watch for Store apps installed or removed: {ex.Message}");
        }
    }

    private void File_Changed(object sender, FileSystemEventArgs e) => Changed();

    private void File_Written(object sender, FileSystemEventArgs e)
    {
        // A folder is "written" when something in it is added or removed, which is seen already, and Windows marks
        // that a few seconds late: only a file written counts.
        if (Directory.Exists(e.FullPath)) return;
        Changed();
    }

    private void Package_Installing(PackageCatalog sender, PackageInstallingEventArgs e)
    {
        if (e.IsComplete) Changed();
    }

    private void Package_Uninstalling(PackageCatalog sender, PackageUninstallingEventArgs e)
    {
        if (e.IsComplete) Changed();
    }

    private void Package_Updating(PackageCatalog sender, PackageUpdatingEventArgs e)
    {
        if (e.IsComplete) Changed();
    }

    /// <summary>From any thread: something changed, so look every few seconds for a while.</summary>
    private void Changed()
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_disposed) return;
            _lastChange = Environment.TickCount64;
            // Looking every few seconds already: that goes on, now counted from this change.
            if (_timer.IsEnabled && !_waitingForLastLook) return;
            _waitingForLastLook = false;
            _timer.Stop();
            _timer.Interval = _every;
            _timer.Start();
        }));
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_disposed) return;
        if (_waitingForLastLook)
        {
            // The last look: nothing comes after it until something changes again.
            _waitingForLastLook = false;
        }
        else
        {
            var since = TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastChange);
            if (since < _lookFor)
            {
                _timer.Interval = _every;
            }
            else
            {
                _waitingForLastLook = true;
                _timer.Interval = _lastLookAfter > since ? _lastLookAfter - since : TimeSpan.FromMilliseconds(1);
            }
            _timer.Start();
        }
        Ui.Safe(_look, "Looking for apps installed or removed");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not stop watching a Start menu folder: {ex.Message}");
            }
        }
        _watchers.Clear();
        if (_packages != null)
        {
            try
            {
                _packages.PackageInstalling -= Package_Installing;
                _packages.PackageUninstalling -= Package_Uninstalling;
                _packages.PackageUpdating -= Package_Updating;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not stop watching for Store apps: {ex.Message}");
            }
            _packages = null;
        }
    }
}
