using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClearStart;

/// <summary>
/// Remembers how the Windows taskbar was set up before we hid it, and puts it back.
/// Deliberately self-contained (plain Win32, no shell library) so it also works from the
/// crash handler and from a separate "ClearStart.exe --restore" process.
/// </summary>
internal static class ExplorerTaskbar
{
    private const uint ABM_GETSTATE = 0x4;
    private const uint ABM_SETSTATE = 0xA;
    private const int ABS_AUTOHIDE = 0x1;

    private static string StateFile => Path.Combine(Log.DataDirectory, "explorer-taskbar-state.txt");

    /// <summary>
    /// Records the taskbar's auto-hide state before it is hidden. If a record is already there, it is
    /// the true original: either the previous run ended without restoring, or the user signed out with
    /// "Start with Windows" on, which leaves auto-hide on until ClearStart is back (see <see cref="ShowForSignOut"/>).
    /// </summary>
    /// <param name="primary">Explorer's main taskbar window.</param>
    public static void RememberState(IntPtr primary)
    {
        try
        {
            if (File.Exists(StateFile)) return;
            Directory.CreateDirectory(Log.DataDirectory);
            int state = GetState(primary);
            File.WriteAllText(StateFile, state.ToString());
            Log.Info($"Noted the Windows taskbar's own setting, to put back later: auto-hide {((state & ABS_AUTOHIDE) != 0 ? "on" : "off")}");
        }
        catch (Exception ex)
        {
            Log.Error("Could not record the Windows taskbar state", ex);
        }
    }

    /// <summary>Switches the Windows taskbar to auto-hide, so it keeps no strip of the desktop for itself.</summary>
    public static void EnsureAutoHide(IntPtr primary)
    {
        int state = GetState(primary);
        if ((state & ABS_AUTOHIDE) != 0) return;
        var data = new Native.APPBARDATA
        {
            cbSize = Marshal.SizeOf<Native.APPBARDATA>(),
            hWnd = primary,
            lParam = new IntPtr(state | ABS_AUTOHIDE),
        };
        Native.SHAppBarMessage(ABM_SETSTATE, ref data);
    }

    /// <summary>True once Explorer has put up its taskbar (at sign-in ClearStart can be there first).</summary>
    public static bool IsUp() => FindBars().Any(bar => bar.IsPrimary);

    /// <summary>
    /// For signing out with "Start with Windows" on. Shows the Windows taskbar windows again, so a sign-out
    /// that is cancelled still leaves a taskbar, but leaves auto-hide on and keeps the record of the original
    /// setting. At the next sign-in Explorer then starts its taskbar out of sight, and ClearStart, starting
    /// moments later, hides it before it is ever in the way.
    /// </summary>
    public static void ShowForSignOut()
    {
        try
        {
            TaskbarGuard.Stop();
            var bars = FindBars();
            ShowBars(bars);
            Log.Info($"Windows taskbar shown again, auto-hidden until ClearStart starts at the next sign-in ({bars.Count} bar(s))");
        }
        catch (Exception ex)
        {
            Log.Error("Could not show the Windows taskbar", ex);
        }
    }

    /// <summary>Shows the Windows taskbar on every monitor again and restores its auto-hide setting.</summary>
    public static void Restore()
    {
        try
        {
            // Otherwise it would hide the taskbar again the moment it is shown.
            TaskbarGuard.Stop();

            int state = 0;
            bool hadRecord = false;
            try
            {
                if (File.Exists(StateFile))
                {
                    hadRecord = int.TryParse(File.ReadAllText(StateFile).Trim(), out state);
                }
            }
            catch
            {
                // No usable record: leave the auto-hide setting alone and only show the bars.
            }

            var bars = FindBars();
            foreach (var (hwnd, isPrimary) in bars)
            {
                // Only undo what we changed. Without a record we never switched auto-hide on,
                // so whatever it is set to now is the user's own choice.
                if (isPrimary && hadRecord)
                {
                    var data = new Native.APPBARDATA
                    {
                        cbSize = Marshal.SizeOf<Native.APPBARDATA>(),
                        hWnd = hwnd,
                        lParam = new IntPtr(state),
                    };
                    Native.SHAppBarMessage(ABM_SETSTATE, ref data);
                }
            }
            ShowBars(bars);

            try
            {
                File.Delete(StateFile);
            }
            catch
            {
                // Harmless: the next start overwrites nothing and the next restore reuses it.
            }

            Log.Info($"Windows taskbar restored ({bars.Count} bar(s))");
        }
        catch (Exception ex)
        {
            Log.Error("Could not restore the Windows taskbar", ex);
        }
    }

    /// <summary>
    /// Which edge of a monitor the Windows taskbar sits on, or null if no Windows taskbar is found there.
    /// Used so that our bar starts out where the user already keeps theirs.
    /// </summary>
    public static BarEdge? EdgeOn(Native.RECT monitor)
    {
        try
        {
            BarEdge? found = null;
            long best = 0;
            foreach (var (hwnd, _) in FindBars())
            {
                if (!Native.GetWindowRect(hwnd, out var bar) || bar.Width <= 0 || bar.Height <= 0) continue;

                long overlap =
                    (long)Math.Max(0, Math.Min(bar.Right, monitor.Right) - Math.Max(bar.Left, monitor.Left)) *
                    Math.Max(0, Math.Min(bar.Bottom, monitor.Bottom) - Math.Max(bar.Top, monitor.Top));
                if (overlap <= 0) continue;

                // A bar that runs the whole length of one side belongs to this monitor even when most of
                // it is slid off-screen (auto-hide) and so overlaps a neighbouring monitor more.
                bool horizontal = bar.Width >= bar.Height;
                bool spans = horizontal
                    ? bar.Left == monitor.Left && bar.Right == monitor.Right
                    : bar.Top == monitor.Top && bar.Bottom == monitor.Bottom;
                long score = overlap + (spans ? long.MaxValue / 2 : 0);
                if (score <= best) continue;

                best = score;
                found = horizontal
                    ? ((bar.Top + bar.Bottom) / 2 < (monitor.Top + monitor.Bottom) / 2 ? BarEdge.Top : BarEdge.Bottom)
                    : ((bar.Left + bar.Right) / 2 < (monitor.Left + monitor.Right) / 2 ? BarEdge.Left : BarEdge.Right);
            }
            return found;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not tell where the Windows taskbar is: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Shows the taskbar windows above other windows, where Windows keeps them, and asks every app to put its
    /// notification icon back in the Windows tray. (Hiding put them at the bottom, which takes that away.)
    /// </summary>
    private static void ShowBars(List<(IntPtr Hwnd, bool IsPrimary)> bars)
    {
        foreach (var (hwnd, _) in bars)
        {
            Native.ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        Native.SendNotifyMessage(new IntPtr(0xFFFF), taskbarCreated, IntPtr.Zero, IntPtr.Zero);
    }

    private static int GetState(IntPtr primary)
    {
        var data = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = primary };
        return (int)Native.SHAppBarMessage(ABM_GETSTATE, ref data).ToUInt64();
    }

    /// <summary>Taskbar windows that belong to Explorer (ours use the same class name for the tray, so check the owner).</summary>
    private static List<(IntPtr Hwnd, bool IsPrimary)> FindBars()
    {
        var result = new List<(IntPtr, bool)>();
        var explorerIds = new HashSet<uint>();
        try
        {
            foreach (var process in Process.GetProcessesByName("explorer"))
            {
                explorerIds.Add((uint)process.Id);
                process.Dispose();
            }
        }
        catch
        {
            // Without the list we simply accept every window of the right class.
        }

        Native.EnumWindows((hwnd, _) =>
        {
            string name = Native.ClassNameOf(hwnd);
            bool primary = name == "Shell_TrayWnd";
            if (primary || name == "Shell_SecondaryTrayWnd")
            {
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                if (explorerIds.Count == 0 || explorerIds.Contains(pid))
                {
                    result.Add((hwnd, primary));
                }
            }
            return true;
        }, IntPtr.Zero);

        return result;
    }
}
