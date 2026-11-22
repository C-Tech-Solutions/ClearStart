using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClearStart;

/// <summary>
/// The apps pinned to the Windows taskbar, in the order it shows them. Windows keeps that list in the registry, as the
/// shell's item IDs: a shortcut in its pinned folder for a desktop app, or an entry in the Apps folder for an app from
/// the Store, which has no shortcut there.
/// </summary>
internal static class WindowsTaskbarPins
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband";

    // How the shell names an entry in the Apps folder: the folder's class ID, then the app's ID.
    private const string AppsFolderName = @"::{4234D49B-0245-4DF3-B780-3893943456E1}\";

    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

    /// <summary>
    /// The pins as ClearStart keeps them: a shortcut by its path, a Store app as shell:AppsFolder\ and its ID. Empty when
    /// Windows has no list, or none of it could be read.
    /// </summary>
    public static List<string> Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue("Favorites") is byte[] favorites ? PathsOf(favorites) : new List<string>();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the order of the Windows taskbar pins: " + ex.Message);
            return new List<string>();
        }
    }

    /// <summary>The pins in a list as Windows keeps it, in its order. One the shell cannot name is left out.</summary>
    internal static List<string> PathsOf(byte[] favorites)
    {
        var paths = new List<string>();
        foreach (var ids in ItemIdLists(favorites))
        {
            if (NameOf(ids) is { } name && PinPath(name) is { } path &&
                !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(path);
            }
        }
        return paths;
    }

    /// <summary>
    /// The item ID lists in the list as Windows keeps it. A byte comes first; then each entry is its length (four bytes),
    /// the item ID list itself, and one byte more, which is 0xFF after the last one. An entry that does not add up ends
    /// the reading there.
    /// </summary>
    internal static List<byte[]> ItemIdLists(byte[] favorites)
    {
        var lists = new List<byte[]>();
        int at = 1;
        while (at + 4 <= favorites.Length)
        {
            int length = BitConverter.ToInt32(favorites, at);
            if (length < 2 || length > favorites.Length - at - 4) break;
            var ids = favorites.AsSpan(at + 4, length);
            if (!WellFormed(ids)) break;
            lists.Add(ids.ToArray());
            at += 4 + length;
            if (at >= favorites.Length || favorites[at] == 0xFF) break;
            at++;
        }
        return lists;
    }

    // Each item ID starts with its own length in two bytes, and a length of zero ends the list: it has to end inside it.
    private static bool WellFormed(ReadOnlySpan<byte> ids)
    {
        int at = 0;
        while (at + 2 <= ids.Length)
        {
            int size = ids[at] | ids[at + 1] << 8;
            if (size == 0) return at > 0;
            if (size <= 2) return false;
            at += size;
        }
        return false;
    }

    /// <summary>What ClearStart keeps for a pin the shell names so: a Store app as shell:AppsFolder\ and its ID.</summary>
    internal static string? PinPath(string name)
    {
        if (name.StartsWith(AppsFolderName, StringComparison.OrdinalIgnoreCase))
        {
            string id = name[AppsFolderName.Length..];
            return id.Length > 0 ? AppCatalog.AppsFolderPrefix + id : null;
        }
        return name.Length > 0 ? name : null;
    }

    // A shortcut by its path on disk; anything else by the name the shell parses.
    private static string? NameOf(byte[] ids)
    {
        IntPtr list = Marshal.AllocCoTaskMem(ids.Length);
        try
        {
            Marshal.Copy(ids, 0, list, ids.Length);
            return NameOf(list, SIGDN_FILESYSPATH) ?? NameOf(list, SIGDN_DESKTOPABSOLUTEPARSING);
        }
        finally
        {
            Marshal.FreeCoTaskMem(list);
        }
    }

    private static string? NameOf(IntPtr list, uint form)
    {
        if (SHGetNameFromIDList(list, form, out IntPtr name) != 0 || name == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(name);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetNameFromIDList(IntPtr idList, uint form, out IntPtr name);
}
