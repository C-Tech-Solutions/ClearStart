using System.Collections.ObjectModel;
using System.Collections.Specialized;
using ManagedShell.ShellFolders;

namespace ClearStart;

/// <summary>
/// Every installed app, as Windows itself lists them (desktop programs and Store apps alike).
/// The list fills in the background and keeps itself current as apps are installed or removed.
/// </summary>
internal sealed class AppCatalog : IDisposable
{
    private readonly ObservableCollection<ShellFile> _empty = new();
    private ShellFolder? _folder;

    private ObservableCollection<ShellFile>? _only;

    public ObservableCollection<ShellFile> Apps => _only ?? _folder?.Files ?? _empty;

    /// <summary>The whole list as Windows gives it, whatever <see cref="ShowOnly"/> has narrowed it to.</summary>
    internal IReadOnlyList<ShellFile> Everything => (_folder?.Files ?? _empty).ToList();

    /// <summary>From here on the list is only these apps. For the screenshot mode's stand-in list.</summary>
    internal void ShowOnly(IEnumerable<ShellFile> apps) => _only = new ObservableCollection<ShellFile>(apps);

    public void Load()
    {
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
        }
    }

    private static void Apps_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems == null) return;
        foreach (var added in e.NewItems)
        {
            if (added is ShellFile app) _ = app.LargeIcon;
        }
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
