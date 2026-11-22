using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ClearStart;

/// <summary>Reads where a shortcut (.lnk file) points, and the app ID it gives its app.</summary>
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

    /// <summary>
    /// The app ID a shortcut gives its app, or null when it gives none. The app list knows an app that gives itself
    /// one by that ID, so it ties the shortcut to the same app pinned from there.
    /// </summary>
    public static string? AppIdOf(string shortcut)
    {
        object? link = null;
        try
        {
            link = new ShellLinkObject();
            ((IPersistFile)link).Load(shortcut, 0 /* read */);

            // PKEY_AppUserModel_ID.
            var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
            if (((IPropertyStore)link).GetValue(ref key, out var value) != 0) return null;
            try
            {
                string? id = value.vt == VT_LPWSTR && value.pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.pointer) : null;
                return string.IsNullOrWhiteSpace(id) ? null : id;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the app ID of the shortcut '{shortcut}': {ex.Message}");
            return null;
        }
        finally
        {
            if (link != null) Marshal.FinalReleaseComObject(link);
        }
    }

    private const ushort VT_LPWSTR = 31;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PROPERTYKEY key);

        [PreserveSig]
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);

        // SetValue and Commit are not used.
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    /// <summary>Only the string case is read; the two pointer-sized fields give the struct its real size.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr pointer;
        public IntPtr more;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT value);
}
