using ManagedShell.AppBar;

namespace ClearStart;

// Windows' quick settings and notification center opened next to the bar whose button was clicked. The panels
// themselves are not opened: where they go is worked out for made-up screens, and the watch moves a window of the
// self-test's own, which draws nothing and lets clicks through, as it would move a panel. No other window is touched.
internal static partial class SelfTest
{
    private static async Task<object> CheckPanelsAsync(List<string> errors)
    {
        // Where it goes, on made-up screens: a 360x560 panel 12 px in from the bottom right of a 1920x1032 work area.
        var anchor = PanelPlacer.Anchor.Of(WorkRect(1548, 460, 1908, 1020), WorkRect(0, 0, 1920, 1032), 1.0);
        // And a panel Windows made as tall as a 1440-high main screen with a bar on the top, as quick settings is.
        var tallAnchor = PanelPlacer.Anchor.Of(WorkRect(2176, 0, 2560, 1392), WorkRect(0, 40, 2560, 1440), 1.0);
        var spots = new (string Name, (int X, int Y, int W, int H) Got, (int X, int Y, int W, int H) Want)[]
        {
            ("where Windows put it", Spot(WorkRect(0, 0, 1920, 1032), 1.0, AppBarEdge.Bottom), (1548, 460, 360, 560)),
            ("another screen, bar at the bottom", Spot(WorkRect(1920, 0, 3840, 1040), 1.0, AppBarEdge.Bottom),
                (3468, 468, 360, 560)),
            ("bar at the top", Spot(WorkRect(1920, 48, 3840, 1080), 1.0, AppBarEdge.Top), (3468, 60, 360, 560)),
            ("bar on the left", Spot(WorkRect(1968, 0, 3840, 1080), 1.0, AppBarEdge.Left), (1980, 508, 360, 560)),
            ("bar on the right", Spot(WorkRect(1920, 0, 3792, 1080), 1.0, AppBarEdge.Right), (3420, 508, 360, 560)),
            ("screen at 150 %", PanelPlacer.Spot(anchor, 540, 840, WorkRect(1920, 0, 4800, 1572), 1.5,
                AppBarEdge.Bottom), (4242, 714, 540, 840)),
            ("screen too small for it", Spot(WorkRect(0, 0, 300, 400), 1.0, AppBarEdge.Bottom), (0, 0, 300, 400)),
            ("taller than a 1080-high screen, bar on the right", PanelPlacer.Spot(tallAnchor, 384, 1392,
                WorkRect(2560, 0, 4410, 1080), 1.0, AppBarEdge.Right), (4026, 0, 384, 1080)),
            ("taller than a 1080-high screen, bar at the top", PanelPlacer.Spot(tallAnchor, 384, 1392,
                WorkRect(2560, 48, 4480, 1080), 1.0, AppBarEdge.Top), (4096, 48, 384, 1032)),
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

            // Opened where Windows opens the panel.
            Native.SetWindowPos(hwnd, IntPtr.Zero, fromWork.Right - 12 - 360, fromWork.Bottom - 12 - 560, 0, 0, Keep);
            Native.GetWindowRect(hwnd, out var opened);
            var want = PanelPlacer.Spot(PanelPlacer.Anchor.Of(opened, fromWork, Native.ScaleOf(from)),
                opened.Width, opened.Height, toWork, Native.ScaleOf(to), edge);
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

            // Opened by a bar on the bottom of the main screen: left where Windows put it.
            Native.SetWindowPos(hwnd, IntPtr.Zero, opened.Left, opened.Top, 0, 0, Keep);
            PanelPlacer.Expect(byId, from, AppBarEdge.Bottom);
            Native.ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */);
            await Task.Delay(500);
            Native.GetWindowRect(hwnd, out var stayed);
            Native.ShowWindow(hwnd, 0 /* SW_HIDE */);
            bool leftAlone = stayed.Left == opened.Left && stayed.Top == opened.Top;
            if (!leftAlone) errors.Add($"Panels: opened by the main screen's bar, the panel was moved to {stayed.Left},{stayed.Top}");
            await WaitUntilAsync(() => !PanelPlacer.Watching, 3000);
            string? leftReport = PanelPlacer.LastReport;

            // Taller than the screen's work area, as Windows' panels are on a screen shorter than the main one: made as
            // tall as the work area, though a panel by the bar on the bottom is otherwise left where it is.
            Native.SetWindowPos(hwnd, IntPtr.Zero, opened.Left, fromWork.Top - 100, opened.Width, fromWork.Height + 200,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Native.GetWindowRect(hwnd, out var tall);
            bool? fitted = null;
            string? fitReport = null;
            var fit = PanelPlacer.Spot(PanelPlacer.Anchor.Of(tall, fromWork, Native.ScaleOf(from)), tall.Width,
                tall.Height, fromWork, Native.ScaleOf(from), AppBarEdge.Bottom);
            // Windows may keep a window from being taller than the screens; it is only tried when it is too tall.
            if (tall.Height > fromWork.Height)
            {
                PanelPlacer.Expect(byId, from, AppBarEdge.Bottom);
                Native.ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */);
                fitted = await WaitForSpotAsync(hwnd, fit);
                Native.ShowWindow(hwnd, 0 /* SW_HIDE */);
                await WaitUntilAsync(() => !PanelPlacer.Watching, 3000);
                fitReport = PanelPlacer.LastReport;
                if (fitted != true)
                {
                    Native.GetWindowRect(hwnd, out var now);
                    errors.Add($"Panels: a panel taller than the screen was not made to fit it: it is at {Describe(now)}, " +
                        $"wanted at {fit.X},{fit.Y} {fit.Width}x{fit.Height}; {fitReport}");
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
                leftAlone,
                tall = Describe(tall),
                fitted,
                gaveUp,
                movedReport,
                leftReport,
                fitReport,
                gaveUpReport = PanelPlacer.LastReport,
            };

            string Report()
            {
                Native.GetWindowRect(hwnd, out var now);
                return $"it is at {now.Left},{now.Top}, wanted at {want.X},{want.Y}; {PanelPlacer.LastReport}";
            }
        }
        finally
        {
            window.DestroyHandle();
        }

        (int X, int Y, int W, int H) Spot(Native.RECT work, double scale, AppBarEdge edge) =>
            PanelPlacer.Spot(anchor, 360, 560, work, scale, edge);
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
