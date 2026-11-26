using System.Windows.Threading;
using Windows.ApplicationModel;

namespace ClearStart;

/// <summary>
/// Says when apps may have been installed or removed, so the app list can be read again. Windows does not say so
/// for its apps folder, so this watches what changes when apps come and go: desktop programs add, rewrite and remove
/// their shortcuts in the Start menu folders, and Store apps are installed, updated and removed through Windows' list
/// of app packages.
///
/// An install changes several files in a row, so the look waits until nothing has changed for a few seconds. It is
/// taken once more half a minute later, as Windows can be slow to add a new shortcut to the apps folder.
/// </summary>
internal sealed class AppWatch : IDisposable
{
    /// <summary>How long nothing has to change before the apps are looked at.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    /// <summary>When the apps are looked at once more after a change.</summary>
    public static readonly TimeSpan LookAgainAfter = TimeSpan.FromSeconds(30);

    private readonly Action _look;
    private readonly Dispatcher _dispatcher;
    private readonly TimeSpan _settle;
    private readonly DispatcherTimer _timer;
    private readonly List<FileSystemWatcher> _watchers = new();
    private PackageCatalog? _packages;
    private bool _lookAgain;
    private bool _disposed;

    /// <param name="look">Called on the dispatcher's thread when the apps should be looked at.</param>
    /// <param name="folders">The folders whose shortcuts are watched, with every folder inside them.</param>
    /// <param name="storeApps">Whether to watch for Store apps installed, updated or removed too.</param>
    /// <param name="settle">How long nothing has to change before <paramref name="look"/> is called.</param>
    public AppWatch(Action look, Dispatcher dispatcher, IEnumerable<string> folders, bool storeApps, TimeSpan? settle = null)
    {
        _look = look;
        _dispatcher = dispatcher;
        _settle = settle ?? Settle;
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

    /// <summary>From any thread: something changed, so look once things have settled.</summary>
    private void Changed()
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_disposed) return;
            _lookAgain = true;
            _timer.Stop();
            _timer.Interval = _settle;
            _timer.Start();
        }));
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_disposed) return;
        if (_lookAgain)
        {
            _lookAgain = false;
            _timer.Interval = LookAgainAfter;
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
