using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ClearStart;

internal static partial class SelfTest
{
    // The Settings app, which every Windows 11 has, as the Apps folder names it.
    private const string SettingsAppId = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";

    /// <summary>
    /// The apps pinned to the Windows taskbar: this PC's list, read as a new install reads it; a list of a file and a
    /// Store app, made the way Windows makes it and read back in its order; broken lists, read as far as they go; and
    /// apps brought over to pins of ClearStart's own, each one only once. The pins are changed in memory and put back:
    /// nothing is saved. The report says how many pins of each kind this PC has, not which apps they are.
    /// </summary>
    private static object CheckWindowsPins(ShellHost host, string directory, List<string> errors)
    {
        var found = new Dictionary<string, object?>();

        // This PC's list, and the shortcuts in Windows' pinned folder that a new install used to read instead.
        long reading = Stopwatch.GetTimestamp();
        var listed = WindowsTaskbarPins.Read();
        found["readMs"] = Math.Round(Stopwatch.GetElapsedTime(reading).TotalMilliseconds);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        var inFolder = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.lnk") : Array.Empty<string>();
        var storeApps = listed.Where(path => path.StartsWith(AppCatalog.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        var shortcuts = listed.Where(path => path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path)).ToList();
        found["listed"] = listed.Count;
        found["storeApps"] = storeApps.Count;
        found["shortcuts"] = shortcuts.Count;
        found["shortcutsInTheFolder"] = shortcuts.Count(path => inFolder.Contains(path, StringComparer.OrdinalIgnoreCase));
        found["folderShortcuts"] = inFolder.Length;
        if (listed.Count != storeApps.Count + shortcuts.Count)
            errors.Add($"Windows pins: {listed.Count - storeApps.Count - shortcuts.Count} of this PC's pins are neither a Store app nor a shortcut");
        if (listed.Count > 0 && !PinStore.TaskbarPinPaths(null).SequenceEqual(listed))
            errors.Add("Windows pins: a new install does not start with Windows' own list");

        // A list made the way Windows makes it: a file, then a Store app.
        string file = Path.Combine(directory, "windows-pin.txt");
        File.WriteAllText(file, "A file pinned to the taskbar, for the self-test.");
        string settingsApp = AppCatalog.AppsFolderPrefix + SettingsAppId;
        var first = IdListOf(file);
        var second = IdListOf(settingsApp);
        if (first == null || second == null)
        {
            errors.Add("Windows pins: could not make the item IDs of a file and of the Settings app");
        }
        else
        {
            void Expect(string what, byte[] favorites, params string[] wanted)
            {
                var read = WindowsTaskbarPins.PathsOf(favorites);
                if (!read.SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase))
                    errors.Add($"Windows pins ({what}): read {string.Join(" | ", read)}, not {string.Join(" | ", wanted)}");
            }
            var both = Favorites(first, second);
            Expect("a file and a Store app", both, file, settingsApp);
            Expect("the other way round", Favorites(second, first), settingsApp, file);
            Expect("the second one cut short", both[..^20], file);
            var broken = (byte[])both.Clone();
            // The first item ID says it is longer than the whole list.
            broken[5] = 0xFF;
            broken[6] = 0x7F;
            Expect("a broken first one", broken);
            Expect("an empty list", new byte[] { 0x00, 0xFF });
            Expect("not a list at all", new byte[] { 1, 2, 3 });
        }

        // File Explorer pinned from the app list, and Windows' own shortcut to it: one app, so it does not come over.
        string? explorerLink = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "File Explorer.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "System Tools", "File Explorer.lnk"),
            Path.Combine(folder, "File Explorer.lnk"),
        }.FirstOrDefault(File.Exists);
        var settings = Settings.Current.Taskbar;
        var pinnedBefore = settings.Pinned?.ToList();
        try
        {
            settings.Pinned = new List<string> { AppCatalog.AppsFolderPrefix + PinnedItem.FileExplorerAppId };
            host.Pins.Reload();
            if (explorerLink == null || host.Pins.Taskbar.Count != 1)
            {
                found["fileExplorerBroughtOver"] = explorerLink == null ? "skipped: no File Explorer shortcut" : "skipped: no File Explorer app";
            }
            else
            {
                string copy = Path.Combine(directory, "windows-pins", "explorer", "File Explorer.lnk");
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(explorerLink, copy, overwrite: true);
                int explorerAdded = host.Pins.BringOver(new[] { copy });
                found["fileExplorerBroughtOver"] = explorerAdded;
                if (explorerAdded != 0)
                    errors.Add("Windows pins: File Explorer pinned from the app list came over again from Windows' shortcut to it");
            }
        }
        finally
        {
            settings.Pinned = pinnedBefore;
            host.Pins.Reload();
        }

        // Brought over to pins of ClearStart's own: two apps from the list pinned already, and a shortcut. Windows
        // has one of those apps, the same shortcut in another folder, and the same program under a name of its own,
        // as a web app is; three apps more, and one that is not there.
        var apps = host.Apps.Apps.Select(app => AppCatalog.PinPath(app)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        string? shortcut = AShortcut();
        if (apps.Count < 4 || shortcut == null)
        {
            found["broughtOver"] = $"skipped: {apps.Count} app(s) in the list, {(shortcut == null ? "no" : "a")} shortcut to copy";
            return found;
        }
        string Copy(string into, string name)
        {
            string to = Path.Combine(directory, "windows-pins", into, name);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(shortcut, to, overwrite: true);
            return to;
        }
        string here = Copy("here", "Tool.lnk"), there = Copy("windows", "Tool.lnk"), webApp = Copy("windows", "Tool web app.lnk");

        var taskbar = Settings.Current.Taskbar;
        var before = taskbar.Pinned?.ToList();
        try
        {
            taskbar.Pinned = new List<string> { apps[0], apps[1], here };
            host.Pins.Reload();
            var windows = new[] { apps[1], there, apps[2], Path.Combine(directory, "not there.lnk"), webApp, apps[3] };
            int added = host.Pins.BringOver(windows);
            int again = host.Pins.BringOver(windows);
            found["broughtOver"] = added;
            found["broughtOverAgain"] = again;
            var wanted = new List<string> { apps[0], apps[1], here, apps[2], webApp, apps[3] };
            var now = host.Pins.Taskbar.Select(pin => pin.Path).ToList();
            if (added != 3 || again != 0 || !now.SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"Windows pins: bringing them over pinned {added}, then {again}, and the bar has " +
                    $"{string.Join(" ", now.Select(path => wanted.FindIndex(w => string.Equals(w, path, StringComparison.OrdinalIgnoreCase))))}, " +
                    "not 3, then 0, and 0 1 2 3 4 5");
            }
            if (taskbar.Pinned is not { } kept || !kept.SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase))
                errors.Add("Windows pins: the settings do not list the pins brought over as the bar has them");
        }
        finally
        {
            taskbar.Pinned = before;
            host.Pins.Reload();
        }
        return found;
    }

    /// <summary>The list as Windows keeps it: a byte, then each entry's length, the entry and a byte; 0xFF after the last.</summary>
    private static byte[] Favorites(params byte[][] lists)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(0x00);
        for (int i = 0; i < lists.Length; i++)
        {
            stream.Write(BitConverter.GetBytes(lists[i].Length));
            stream.Write(lists[i]);
            stream.WriteByte(i == lists.Length - 1 ? (byte)0xFF : (byte)0x00);
        }
        return stream.ToArray();
    }

    /// <summary>The shell's item ID list for something it can parse, as bytes.</summary>
    private static byte[]? IdListOf(string name)
    {
        Guid iid = Native.IID_IShellItem;
        if (Native.SHCreateItemFromParsingName(name, IntPtr.Zero, ref iid, out IntPtr item) != 0 || item == IntPtr.Zero) return null;
        try
        {
            if (SHGetIDListFromObject(item, out IntPtr list) != 0 || list == IntPtr.Zero) return null;
            try
            {
                var bytes = new byte[ILGetSize(list)];
                Marshal.Copy(list, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                Marshal.FreeCoTaskMem(list);
            }
        }
        finally
        {
            Marshal.Release(item);
        }
    }

    /// <summary>A shortcut to a program, from the Start menu every account shares, to copy.</summary>
    private static string? AShortcut()
    {
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        if (!Directory.Exists(programs)) return null;
        return Directory.EnumerateFiles(programs, "*.lnk", SearchOption.AllDirectories)
            .Take(50)
            .FirstOrDefault(path => ShellLink.TargetOf(path) is { } target &&
                target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target));
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetIDListFromObject(IntPtr unknown, out IntPtr idList);

    [DllImport("shell32.dll")]
    private static extern uint ILGetSize(IntPtr idList);
}
