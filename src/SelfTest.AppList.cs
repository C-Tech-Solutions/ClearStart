using System.Windows.Threading;

namespace ClearStart;

// Apps installed or removed while ClearStart runs. The apps folder is read again, and a made-up removal and its
// return are applied to the self-test's own app list. The watch is tried on a folder of the self-test's own: the
// Start menu folders are not touched, and no app is installed or removed.
internal static partial class SelfTest
{
    private static async Task<object> CheckAppListAsync(ShellHost host, string directory, List<string> errors)
    {
        var catalog = host.Apps;
        var ids = await AppCatalog.ReadIdsAsync();
        if (ids == null || ids.Count == 0)
        {
            errors.Add("App list: the apps folder could not be read again");
            return new { readAgain = 0 };
        }

        // Read again with nothing installed or removed since: the list is left as it is.
        int before = catalog.Apps.Count;
        var (added, removed) = catalog.Apply(ids);
        if (added.Count + removed.Count != 0 || catalog.Apps.Count != before)
        {
            errors.Add($"App list: read again with nothing changed, {added.Count} apps were added " +
                $"({string.Join(", ", added)}) and {removed.Count} taken out ({string.Join(", ", removed)})");
        }

        // Every app must read back from its id as the same app, or a newly installed one could not be added.
        var unreadable = new List<string>();
        foreach (string id in ids)
        {
            var app = AppCatalog.ReadApp(null, id);
            if (app == null) unreadable.Add(id);
            app?.Dispose();
        }
        if (unreadable.Count > 0)
        {
            errors.Add($"App list: {unreadable.Count} apps do not read back from their id: " +
                string.Join(", ", unreadable.Take(5)));
        }

        // One app of each kind taken out and put back, as if uninstalled and installed again.
        var kinds = new List<object>();
        foreach (var sample in new[]
        {
            ids.FirstOrDefault(id => id.Contains('!')),                         // a Store app
            ids.FirstOrDefault(id => id.StartsWith('{') && id.Contains('\\')),   // a desktop program
        })
        {
            if (sample == null) continue;
            var app = catalog.Apps.FirstOrDefault(a => string.Equals(a.Path, sample, StringComparison.OrdinalIgnoreCase));
            string? name = app?.DisplayName;
            var gone = catalog.Apply(ids.Where(id => id != sample).ToList());
            bool outOfList = !catalog.Apps.Any(a => string.Equals(a.Path, sample, StringComparison.OrdinalIgnoreCase));
            var back = catalog.Apply(ids);
            var again = catalog.Apps.FirstOrDefault(a => string.Equals(a.Path, sample, StringComparison.OrdinalIgnoreCase));
            bool ok = app != null && gone.Removed.Count == 1 && gone.Added.Count == 0 && outOfList
                && back.Added.Count == 1 && back.Removed.Count == 0 && again != null && again.DisplayName == name
                && catalog.Apps.Count == before;
            if (!ok)
            {
                errors.Add($"App list: '{name ?? sample}' taken out and put back gave {gone.Removed.Count} out, " +
                    $"{back.Added.Count} back, named '{again?.DisplayName}', {catalog.Apps.Count} apps of {before}");
            }
            kinds.Add(new { name, ok });
        }

        // The watch, on a folder of the self-test's own standing in for a Start menu folder: an install's few
        // changes in a row bring one look, and so does an update that rewrites a shortcut in place. A folder of its
        // own each run, so the shortcuts are new even in a folder used before, and nothing that was there is deleted.
        string folder = Path.Combine(directory, "app-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string installed = Path.Combine(folder, "Installed program.lnk");
        File.WriteAllText(installed, "");
        int looks = 0;
        int rewriteLooks;
        bool storeApps;
        using (var watch = new AppWatch(() => looks++, Dispatcher.CurrentDispatcher, [folder], storeApps: true,
            settle: TimeSpan.FromMilliseconds(500)))
        {
            storeApps = watch.WatchesStoreApps;
            string programFolder = Path.Combine(folder, "Some program");
            Directory.CreateDirectory(programFolder);
            File.WriteAllText(Path.Combine(programFolder, "Some program.lnk"), "");
            File.WriteAllText(Path.Combine(programFolder, "Uninstall.lnk"), "");
            for (int wait = 0; wait < 40 && looks == 0; wait++) await Task.Delay(100);
            await Task.Delay(1000);
            if (looks != 1) errors.Add($"App list: a new program's shortcuts brought {looks} looks instead of one");

            // An update rewrites the program's shortcut in place: no file added, removed or renamed.
            int looksBefore = looks;
            File.WriteAllText(installed, "updated");
            for (int wait = 0; wait < 40 && looks == looksBefore; wait++) await Task.Delay(100);
            rewriteLooks = looks - looksBefore;
            if (rewriteLooks == 0) errors.Add("App list: a shortcut rewritten in place brought no look");
        }
        Directory.Delete(folder, recursive: true);
        if (!storeApps) errors.Add("App list: Store apps installed or removed cannot be watched for");

        return new
        {
            readAgain = ids.Count,
            listed = before,
            unreadable = unreadable.Count,
            takenOutAndBack = kinds,
            watchLooks = looks,
            rewriteLooks,
            storeApps,
        };
    }
}
