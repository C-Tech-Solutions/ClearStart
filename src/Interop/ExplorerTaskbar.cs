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
    public static bool RememberState(IntPtr primary)
    {
        try
        {
            if (File.Exists(StateFile))
            {
                if (!int.TryParse(File.ReadAllText(StateFile).Trim(), out int saved)) return false;
                EarlySession.RecordTaskbarState(saved);
                return true;
            }
            Directory.CreateDirectory(Log.DataDirectory);
            int state = GetState(primary);
            File.WriteAllText(StateFile, state.ToString());
            EarlySession.RecordTaskbarState(state);
            Log.Info($"Noted the Windows taskbar's own setting, to put back later: auto-hide {((state & ABS_AUTOHIDE) != 0 ? "on" : "off")}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not record the Windows taskbar state", ex);
            return false;
        }
    }

    /// <summary>Switches the Windows taskbar to auto-hide, so it keeps no strip of the desktop for itself.</summary>
    /// <returns>
    /// Whether auto-hide is on now. Windows 11 turns it down for a taskbar on the top or a side (see <see cref="TaskbarEdge"/>).
    /// </returns>
    public static bool EnsureAutoHide(IntPtr primary)
    {
        int state = GetState(primary);
        if ((state & ABS_AUTOHIDE) != 0) return true;
        var data = new Native.APPBARDATA
        {
            cbSize = Marshal.SizeOf<Native.APPBARDATA>(),
            hWnd = primary,
            lParam = new IntPtr(state | ABS_AUTOHIDE),
        };
        Native.SHAppBarMessage(ABM_SETSTATE, ref data);
        return (GetState(primary) & ABS_AUTOHIDE) != 0;
    }

    /// <summary>
    /// False if any of Explorer's taskbars is on a side of a monitor, or on the top of one and not on the bottom of
    /// another. An auto-hidden taskbar slides mostly off its monitor, so where one monitor sits above another, the
    /// bottom of the upper one and the top of the lower one can look alike; that counts as the bottom.
    /// </summary>
    public static bool AllOnBottom()
    {
        var monitors = System.Windows.Forms.Screen.AllScreens.Select(screen => screen.Bounds).ToList();
        foreach (var (hwnd, _) in FindBars())
        {
            if (!Native.GetWindowRect(hwnd, out var bar) || bar.Width <= 0 || bar.Height <= 0) continue;
            if (bar.Width < bar.Height) return false;
            var under = monitors.Where(monitor => bar.Left < monitor.Right && bar.Right > monitor.Left).ToList();
            bool onTop = under.Any(monitor => bar.Top <= monitor.Top && bar.Bottom > monitor.Top);
            bool onBottom = under.Any(monitor => bar.Top < monitor.Bottom && bar.Bottom >= monitor.Bottom);
            if (onTop && !onBottom) return false;
        }
        return true;
    }

    /// <summary>
    /// Asks every app to hand its tray icon over again, once ClearStart's tray is the one Windows hands them to: the
    /// first taskbar window in line. Explorer's can get in front of it when Explorer starts, or brings its own up.
    /// </summary>
    /// <returns>False if Explorer's tray stays in front, so the icons would go to Explorer.</returns>
    public static bool AskForTrayIcons()
    {
        IntPtr first = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        if (first != IntPtr.Zero && !IsOurs(first))
        {
            // Hidden already: this only puts it behind ClearStart's (putting the taskbar back raises it again).
            Native.SetWindowPos(first, Native.HWND_BOTTOM, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            first = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        }
        if (first == IntPtr.Zero || !IsOurs(first)) return false;
        AnnounceTaskbar();
        return true;
    }

    private static bool IsOurs(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>True once Explorer has put up its taskbar (at sign-in ClearStart can be there first).</summary>
    public static bool IsUp() => FindPrimary() != IntPtr.Zero;

    /// <summary>Explorer's main taskbar window, or zero while it has none. A new one means Explorer started again.</summary>
    public static IntPtr FindPrimary() => FindBars().FirstOrDefault(bar => bar.IsPrimary).Hwnd;

    /// <summary>
    /// How long Explorer takes to settle after it starts: it is still starting its own parts then, and after sign-in
    /// the user's apps. It is not closed, and ClearStart's tray does not go in front of it, before it has run this long.
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);

    /// <summary>
    /// True once the Explorer that owns <paramref name="taskbar"/> has run for <see cref="Settle"/>, or when that
    /// cannot be told. False while there is no taskbar.
    /// </summary>
    public static bool HasSettled(IntPtr taskbar)
    {
        if (taskbar == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(taskbar, out uint pid);
        if (pid == 0) return false;
        return (Age(taskbar) ?? Settle) >= Settle;
    }

    /// <summary>How long the Explorer that owns <paramref name="taskbar"/> has run, or null when that cannot be told.</summary>
    public static TimeSpan? Age(IntPtr taskbar)
    {
        Native.GetWindowThreadProcessId(taskbar, out uint pid);
        if (pid == 0) return null;
        try
        {
            using var shell = Process.GetProcessById((int)pid);
            return DateTime.Now - shell.StartTime;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// For signing out with "Start with Windows" on. Shows the Windows taskbar windows again, so a sign-out
    /// that is cancelled still leaves a taskbar, but leaves auto-hide on and keeps the record of the original
    /// setting. This reduces visibility at the next sign-in, but does not guarantee that Explorer never draws
    /// its taskbar before ClearStart can hide it.
    /// </summary>
    public static void ShowForSignOut()
    {
        EarlySession.Release();
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

    /// <summary>
    /// Shows the Windows taskbar on every monitor again, and restores its auto-hide setting and its edges.
    /// </summary>
    /// <param name="sessionEnding">The user is signing out or shutting down (see <see cref="TaskbarEdge.PutBack"/>).</param>
    /// <param name="keepEdge">
    /// ClearStart starts again at the next sign-in: the taskbar stays on the bottom edge, and the record of its own
    /// edges is kept for when ClearStart closes for good.
    /// </param>
    public static void Restore(bool sessionEnding = false, bool keepEdge = false)
    {
        EarlySession.Release();
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
            // Explorer keeps that only in memory until it closes; written down now, it also holds if it never closes
            // normally (ended, crashed, power lost).
            if (hadRecord) TaskbarEdge.WriteAutoHide((state & ABS_AUTOHIDE) != 0);
            // After the auto-hide setting: Explorer writes that down as it closes, which putting the edges back needs.
            if (!keepEdge && TaskbarEdge.PutBack(sessionEnding)) bars = FindBars();
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
    /// Used so that our bar starts out where the user already keeps theirs. While ClearStart keeps the taskbar on the
    /// bottom (see <see cref="TaskbarEdge"/>), that is the edge it was moved from.
    /// </summary>
    public static BarEdge? EdgeOn(Native.RECT monitor)
    {
        try
        {
            // A monitor that had no taskbar before the move goes by the one it has now.
            return EdgeOn(monitor, TaskbarEdge.OriginalBars()) ?? EdgeOn(monitor, BarRects());
        }
        catch (Exception ex)
        {
            Log.Warn("Could not tell where the Windows taskbar is: " + ex.Message);
            return null;
        }
    }

    /// <summary>Where each of Explorer's taskbar windows is.</summary>
    public static List<Native.RECT> BarRects()
    {
        var rects = new List<Native.RECT>();
        foreach (var (hwnd, _) in FindBars())
        {
            if (Native.GetWindowRect(hwnd, out var bar) && bar.Width > 0 && bar.Height > 0) rects.Add(bar);
        }
        return rects;
    }

    /// <summary>The edge of <paramref name="monitor"/> that the one of <paramref name="bars"/> on it sits on.</summary>
    private static BarEdge? EdgeOn(Native.RECT monitor, List<Native.RECT> bars)
    {
        BarEdge? found = null;
        long best = 0;
        foreach (var bar in bars)
        {
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
        AnnounceTaskbar();
    }

    /// <summary>Tells every app a taskbar has started: each one hands its tray icon to the first taskbar in line.</summary>
    private static void AnnounceTaskbar()
    {
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
