using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ManagedShell.Common.Helpers;
using ManagedShell.ShellFolders;

namespace ClearStart;

/// <summary>Starts apps, shortcuts, folders and shell locations.</summary>
internal static class Launcher
{
    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
    private const int ERROR_CANCELLED = 1223;

    /// <summary>
    /// Opens a shell item the way double-clicking it would. Works for anything the shell knows,
    /// including Store apps that have no file path.
    /// </summary>
    public static bool Launch(ShellItem? item, bool elevated = false)
    {
        if (item == null) return false;
        try
        {
            IntPtr pidl = item.AbsolutePidl;
            if (pidl != IntPtr.Zero)
            {
                var info = new Native.SHELLEXECUTEINFO
                {
                    cbSize = Marshal.SizeOf<Native.SHELLEXECUTEINFO>(),
                    fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_FLAG_NO_UI,
                    lpVerb = elevated ? "runas" : null,
                    lpIDList = pidl,
                    nShow = 1, // SW_SHOWNORMAL
                };
                if (Native.ShellExecuteEx(ref info)) return true;

                int error = Marshal.GetLastWin32Error();
                if (error == ERROR_CANCELLED) return false; // the elevation prompt was declined
                Log.Warn($"Shell launch of '{item.DisplayName}' failed (error {error}); trying its path");
            }

            return !string.IsNullOrEmpty(item.Path) && Run(item.Path, elevated: elevated);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not launch '{item.DisplayName}'", ex);
            return false;
        }
    }

    /// <summary>Opens anything the Run dialog accepts: a path, a shell: location, a URI or a program name.</summary>
    public static bool Run(string target, string? arguments = null, bool elevated = false)
    {
        try
        {
            if (target.Equals("clearstart:run", StringComparison.OrdinalIgnoreCase))
            {
                ShellHelper.ShowRunDialog("Run",
                    "Type the name of a program, folder, document, or Internet resource, and Windows will open it for you.");
                return true;
            }

            var start = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(target))
            {
                UseShellExecute = true,
                Arguments = arguments ?? "",
            };
            if (elevated) start.Verb = "runas";
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not start '{target}'", ex);
            return false;
        }
    }

    public static void ShowInExplorer(string path)
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not show '{path}' in Explorer", ex);
        }
    }

    /// <summary>Whether <see cref="ShowProperties"/> can be offered for a target: a full path or a shell: location.</summary>
    public static bool HasProperties(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        string expanded = Environment.ExpandEnvironmentVariables(target.Trim());
        // The apps folder's items have no Properties window of their own.
        return (expanded.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                && !expanded.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase))
            || Path.IsPathFullyQualified(expanded);
    }

    /// <summary>
    /// Opens Windows' Properties window for a file, a folder or a shell: location, the one Explorer shows. A shortcut
    /// shows its own, with the Shortcut tab. The window stays open on its own.
    /// </summary>
    public static void ShowProperties(string target)
    {
        ShellItem? item = null;
        try
        {
            item = new ShellItem(Environment.ExpandEnvironmentVariables(target.Trim()));
            if (!item.Loaded || item.AbsolutePidl == IntPtr.Zero)
            {
                Log.Warn($"Could not show the properties of '{target}': not found");
                return;
            }
            var info = new Native.SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<Native.SHELLEXECUTEINFO>(),
                fMask = SEE_MASK_INVOKEIDLIST,
                lpVerb = "properties",
                lpIDList = item.AbsolutePidl,
                nShow = 1, // SW_SHOWNORMAL
            };
            if (!Native.ShellExecuteEx(ref info))
            {
                Log.Warn($"Could not show the properties of '{target}' (error {Marshal.GetLastWin32Error()})");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not show the properties of '{target}'", ex);
        }
        finally
        {
            item?.Dispose();
        }
    }

    public static void EditInNotepad(string path)
    {
        try
        {
            Process.Start("notepad.exe", $"\"{path}\"")?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open '{path}'", ex);
        }
    }
}
