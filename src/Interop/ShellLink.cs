using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ClearStart;

/// <summary>Reads where a shortcut (.lnk file) points.</summary>
internal static class ShellLink
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject
    {
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        // The interface has many more methods; this is the first one and the only one used here.
        [PreserveSig]
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
    }

    /// <summary>The file a shortcut opens, or null when it does not open a file (or cannot be read).</summary>
    public static string? TargetOf(string shortcut)
    {
        object? link = null;
        try
        {
            link = new ShellLinkObject();
            ((IPersistFile)link).Load(shortcut, 0 /* read */);

            var path = new StringBuilder(1024);
            if (((IShellLinkW)link).GetPath(path, path.Capacity, IntPtr.Zero, 0) != 0) return null;

            string target = Environment.ExpandEnvironmentVariables(path.ToString());
            return target.Length > 0 ? target : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the shortcut '{shortcut}': {ex.Message}");
            return null;
        }
        finally
        {
            if (link != null) Marshal.FinalReleaseComObject(link);
        }
    }
}
