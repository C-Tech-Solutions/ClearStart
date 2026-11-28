using ManagedShell.AppBar;

namespace ClearStart;

// Windows' quick settings and notification center opened next to the bar whose button was clicked. The panels
// themselves are not opened: where they go is worked out for made-up screens, and the watch moves a window of the
// self-test's own, which draws nothing and lets clicks through, as it would move a panel. No other window is touched.
internal static partial class SelfTest
{
    private static async Task<object> CheckPanelsAsync(List<string> errors)
    {
        // Where it goes, on made-up screens: a 360x560 panel, and panels Windows made as tall as a 1440-high main
        // screen, as it makes quick settings (which may be made smaller) and the notification center (which may not).
        var spots = new (string Name, (int X, int Y, int W, int H) Got, (int X, int Y, int W, int H) Want)[]
        {
            ("bar at the bottom", Spot(360, 560, WorkRect(0, 0, 1920, 1032), AppBarEdge.Bottom), (1560, 472, 360, 560)),
            ("another screen, bar at the bottom", Spot(360, 560, WorkRect(1920, 0, 3840, 1040), AppBarEdge.Bottom),
                (3480, 480, 360, 560)),
            ("bar at the top", Spot(360, 560, WorkRect(1920, 48, 3840, 1080), AppBarEdge.Top), (3480, 48, 360, 560)),
            ("bar on the left", Spot(360, 560, WorkRect(1968, 0, 3840, 1080), AppBarEdge.Left), (1968, 520, 360, 560)),
            ("bar on the right", Spot(360, 560, WorkRect(1920, 0, 3792, 1080), AppBarEdge.Right),
                (3432, 520, 360, 560)),
            ("screen too small for it, made smaller", Spot(360, 560, WorkRect(0, 0, 300, 400), AppBarEdge.Bottom, true),
                (0, 0, 300, 400)),
            ("screen too small for it, kept its size", Spot(360, 560, WorkRect(0, 0, 300, 400), AppBarEdge.Bottom),
                (-60, -160, 360, 560)),
            ("quick settings on a 1080-high screen, bar on the right", Spot(384, 1392, WorkRect(2560, 0, 4410, 1080),
                AppBarEdge.Right, true), (4026, 0, 384, 1080)),
            ("quick settings on a 1080-high screen, bar at the top", Spot(384, 1392, WorkRect(2560, 48, 4480, 1080),
                AppBarEdge.Top, true), (4096, 48, 384, 1032)),
            ("notifications on a 1440-high screen, bar at the top", Spot(360, 1352, WorkRect(0, 40, 2560, 1440),
                AppBarEdge.Top), (2200, 40, 360, 1352)),
            ("notifications on a 1080-high screen, bar on the right", Spot(360, 1352, WorkRect(2560, 0, 4410, 1080),
                AppBarEdge.Right), (4050, -272, 360, 1352)),
            ("notifications on a 1080-high screen, bar on the left", Spot(360, 1352, WorkRect(-1848, 0, 0, 1080),
                AppBarEdge.Left), (-1848, -272, 360, 1352)),
            ("notifications on a 1080-high screen, bar at the top", Spot(360, 1352, WorkRect(2560, 48, 4480, 1080),
                AppBarEdge.Top), (4120, -272, 360, 1352)),
        };
        foreach (var (name, got, want) in spots)
        {
            if (got != want)
            {
                errors.Add($"Panels: {name}, the panel would go to {got.X},{got.Y} ({got.W}x{got.H}) instead of " +
                    $"{want.X},{want.Y} ({want.W}x{want.H})");
            }
        }

        // The watch, on a window of the self-test's own: it opens in the corner of the main screen, as the panel
        // does, and is moved next to a bar on another screen, or on the top of the main screen when there is no other.
        var screens = AppBarScreen.FromAllScreens();
        var main = screens.FirstOrDefault(s => s.Primary) ?? screens.First();
        var other = screens.FirstOrDefault(s => !s.Primary);
        IntPtr from = main.HMonitor;
        IntPtr to = other?.HMonitor ?? from;
        var edge = other != null ? AppBarEdge.Bottom : AppBarEdge.Top;
        uint processId = (uint)Environment.ProcessId;

        var window = new System.Windows.Forms.NativeWindow();
        window.CreateHandle(new System.Windows.Forms.CreateParams
        {
            Caption = "ClearStart self-test",
            Style = unchecked((int)0x80000000), // WS_POPUP, hidden
            ExStyle = Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT,
            X = -30000,
            Y = -30000,
            Width = 360,
            Height = 560,
        });
        try
        {
            IntPtr hwnd = window.Handle;
            if (!Native.MakeSeeThrough(hwnd)) errors.Add("Panels: the self-test's window could not be made to draw nothing");
            if (!Native.TryGetMonitor(from, out _, out var fromWork) || !Native.TryGetMonitor(to, out _, out var toWork))
            {
                errors.Add("Panels: the screens' work areas could not be read, so the watch was not tried");
                return new { spots = spots.Length };
            }

            // Opened where Windows opens the panel: in the bottom right corner of the main screen's work area.
            Native.SetWindowPos(hwnd, IntPtr.Zero, fromWork.Right - 360, fromWork.Bottom - 560, 0, 0, Keep);
            Native.GetWindowRect(hwnd, out var opened);
            var want = PanelPlacer.Spot(opened.Width, opened.Height, toWork, edge, false);
            string className = Native.ClassNameOf(hwnd);
            // Told apart by its program's name, as the notification center is, then by this process.
            var byName = new PanelPlacer.Panel("Self-test panel", className,
                ProcessName: System.Diagnostics.Process.GetCurrentProcess().ProcessName);
            var byId = new PanelPlacer.Panel("Self-test panel", className, ProcessId: processId);

            PanelPlacer.Expect(byName, to, edge);
            bool watching = PanelPlacer.Watching;
            Native.ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */);
            bool moved = await WaitForSpotAsync(hwnd, want);

            // Put back where it opened, as Windows might do: moved again.
            Native.SetWindowPos(hwnd, IntPtr.Zero, opened.Left, opened.Top, 0, 0, Keep);
            bool movedAgain = await WaitForSpotAsync(hwnd, want);

            // Closed: the watch ends soon after.
            Native.ShowWindow(hwnd, 0 /* SW_HIDE */);
            bool ended = await WaitUntilAsync(() => !PanelPlacer.Watching, 3000);
            string? movedReport = PanelPlacer.LastReport;
            if (!watching) errors.Add("Panels: the watch did not start");
            else if (!moved) errors.Add($"Panels: the panel was not moved next to the clicked bar ({Report()})");
            else if (!movedAgain) errors.Add($"Panels: the panel put back was not moved again ({Report()})");
            if (watching && !ended) errors.Add("Panels: the watch went on after the panel closed");

            // Opened again where it was left, as Windows opens the notification center: left there, at every opening.
            var top = PanelPlacer.Spot(opened.Width, opened.Height, toWork, AppBarEdge.Top, false);
            Native.SetWindowPos(hwnd, IntPtr.Zero, top.X, top.Y, 0, 0, Keep);
            PanelPlacer.Expect(byId, to, AppBarEdge.Top);
            Native.ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */);
            await Task.Delay(500);
            Native.GetWindowRect(hwnd, out var reopened);
            Native.ShowWindow(hwnd, 0 /* SW_HIDE */);
            bool keptTop = reopened.Left == top.X && reopened.Top == top.Y;
            if (!keptTop)
            {
                errors.Add($"Panels: opened again where it was left by a bar at the top, the panel was moved to " +
                    $"{reopened.Left},{reopened.Top}");
            }
            await WaitUntilAsync(() => !PanelPlacer.Watching, 3000);
            string? keptTopReport = PanelPlacer.LastReport;

            // Opened by a bar on the bottom of the main screen, a little above it, as Windows puts a panel by a bar
            // thinner than its own taskbar: left where Windows put it.
            Native.SetWindowPos(hwnd, IntPtr.Zero, opened.Left, opened.Top - 4, 0, 0, Keep);
            PanelPlacer.Expect(byId, from, AppBarEdge.Bottom);
            Native.ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */);
            await Task.Delay(500);
            Native.GetWindowRect(hwnd, out var stayed);
            Native.ShowWindow(hwnd, 0 /* SW_HIDE */);
            bool leftAlone = stayed.Left == opened.Left && stayed.Top == opened.Top - 4;
            if (!leftAlone) errors.Add($"Panels: opened by the main screen's bar, the panel was moved to {stayed.Left},{stayed.Top}");
            await WaitUntilAsync(() => !PanelPlacer.Watching, 3000);
            string? leftReport = PanelPlacer.LastReport;

            // Opened again at the top of the screen, where it was left for a bar there, by a bar now on the bottom:
            // moved down to it, not left in the wrong corner.
            var high = PanelPlacer.Spot(opened.Width, opened.Height, fromWork, AppBarEdge.Top, false);
            Native.SetWindowPos(hwnd, IntPtr.Zero, high.X, high.Y, 0, 0, Keep);
            PanelPlacer.Expect(byId, from, AppBarEdge.Bottom);
            Native.ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */);
            bool movedDown = await WaitForSpotAsync(hwnd, (opened.Left, opened.Top, opened.Width, opened.Height));
            Native.ShowWindow(hwnd, 0 /* SW_HIDE */);
            if (!movedDown) errors.Add($"Panels: left at the top, the panel was not moved down to the bar: it is at {Now()}");
            await WaitUntilAsync(() => !PanelPlacer.Watching, 3000);

            // Taller than the screen's work area, as Windows' panels are on a screen shorter than the main one. One that
            // may be made smaller is made as tall as the work area; one that may not keeps its size, lined up with the
            // bottom. Windows may keep a window from being taller than the screens: then neither is tried.
            Native.RECT tall = default;
            bool? fitted = null, kept = null;
            string? fitReport = null, keptReport = null;
            if (MakeTall())
            {
                var fit = PanelPlacer.Spot(tall.Width, tall.Height, fromWork, AppBarEdge.Bottom, true);
                (fitted, fitReport) = await OpenAsync(byId with { Resizable = true }, fit);
                if (fitted != true)
                {
                    errors.Add($"Panels: a panel taller than the screen was not made to fit it: it is at {Now()}, " +
                        $"wanted at {fit.X},{fit.Y} {fit.Width}x{fit.Height}; {fitReport}");
                }
            }
            if (MakeTall())
            {
                var keep = PanelPlacer.Spot(tall.Width, tall.Height, fromWork, AppBarEdge.Bottom, false);
                (kept, keptReport) = await OpenAsync(byId, keep);
                if (kept != true)
                {
                    errors.Add($"Panels: a panel taller than the screen was not lined up with its bottom: it is at " +
                        $"{Now()}, wanted at {keep.X},{keep.Y} {keep.Width}x{keep.Height}; {keptReport}");
                }
            }

            // Asked for, and nothing opens (as when the button closes an open panel): the watch ends by itself.
            PanelPlacer.Expect(byId with { ClassName = "ClearStart self-test: no such window" }, to, edge);
            bool gaveUp = await WaitUntilAsync(() => !PanelPlacer.Watching,
                (int)PanelPlacer.WaitToOpen.TotalMilliseconds + 2000);
            if (!gaveUp) errors.Add("Panels: with no panel opening, the watch did not end");

            return new
            {
                spots = spots.Length,
                fromMain = Describe(fromWork),
                to = Describe(toWork),
                edge = edge.ToString(),
                want = $"{want.X},{want.Y}",
                moved,
                movedAgain,
                ended,
                keptTop,
                leftAlone,
                movedDown,
                tall = Describe(tall),
                fitted,
                kept,
                gaveUp,
                movedReport,
                keptTopReport,
                leftReport,
                fitReport,
                keptReport,
                gaveUpReport = PanelPlacer.LastReport,
            };

            string Report() => $"it is at {Now()}, wanted at {want.X},{want.Y}; {PanelPlacer.LastReport}";

            string Now() => Native.GetWindowRect(hwnd, out var now) ? Describe(now) : "nowhere";

            // Made taller than the main screen's work area, by the bar on its bottom, and read back.
            bool MakeTall()
            {
                Native.SetWindowPos(hwnd, IntPtr.Zero, opened.Left, fromWork.Top - 100, opened.Width,
                    fromWork.Height + 200, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                Native.GetWindowRect(hwnd, out tall);
                return tall.Height > fromWork.Height;
            }

            // Opened by the bar on the bottom of the main screen, as this panel: whether it went where it should.
            async Task<(bool, string?)> OpenAsync(PanelPlacer.Panel panel, (int X, int Y, int Width, int Height) spot)
            {
                PanelPlacer.Expect(panel, from, AppBarEdge.Bottom);
                Native.ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */);
                bool there = await WaitForSpotAsync(hwnd, spot);
                Native.ShowWindow(hwnd, 0 /* SW_HIDE */);
                await WaitUntilAsync(() => !PanelPlacer.Watching, 3000);
                return (there, PanelPlacer.LastReport);
            }
        }
        finally
        {
            window.DestroyHandle();
        }

        static (int X, int Y, int W, int H) Spot(int width, int height, Native.RECT work, AppBarEdge edge,
            bool resizable = false) => PanelPlacer.Spot(width, height, work, edge, resizable);
    }

    private const uint Keep = Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE;

    private static Native.RECT WorkRect(int left, int top, int right, int bottom) =>
        new() { Left = left, Top = top, Right = right, Bottom = bottom };

    private static string Describe(Native.RECT rect) => $"{rect.Left},{rect.Top} {rect.Width}x{rect.Height}";

    private static async Task<bool> WaitForSpotAsync(IntPtr hwnd, (int X, int Y, int Width, int Height) want) =>
        await WaitUntilAsync(() => Native.GetWindowRect(hwnd, out var now) && now.Left == want.X && now.Top == want.Y &&
            now.Width == want.Width && now.Height == want.Height, 2000);

    private static async Task<bool> WaitUntilAsync(Func<bool> done, int milliseconds)
    {
        for (int waited = 0; !done(); waited += 50)
        {
            if (waited >= milliseconds) return false;
            await Task.Delay(50);
        }
        return true;
    }
}
