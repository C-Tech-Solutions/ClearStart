using ManagedShell.AppBar;

namespace ClearStart;

// Per-monitor settings when Windows numbers the displays another way: worked out on throw-away settings and made-up
// monitors. The one thing read from Windows is how many of this PC's displays it tells apart; nothing is changed.
internal static partial class SelfTest
{
    private static List<string> CheckMonitorIds(List<string> errors)
    {
        var steps = new List<string>();

        // The monitor's name from its device path: the model and the connector, nothing else.
        foreach (var (path, wanted) in new (string?, string?)[]
                 {
                     (@"\\?\DISPLAY#ABC1234#5&1a2b3c4d&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", "ABC1234#5&1a2b3c4d&0&UID4352"),
                     (@"MONITOR\ABC1234\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001", null),
                     (@"\\?\DISPLAY##5&1a2b3c4d&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", null),
                     (@"\\?\USB#VID_1234&PID_5678#5&1a2b3c4d&0&1#{a5dcbf10-6530-11d2-901f-00c04fb951ed}", null),
                     ("", null),
                     (null, null),
                 })
        {
            string? got = MonitorIds.FromPath(path);
            if (got != wanted) errors.Add($"Monitor names: \"{path}\" gave \"{got}\", not \"{wanted}\"");
        }
        steps.Add("names from device paths");

        // Two displays naming the same monitor are both left to their numbers.
        var twins = MonitorIds.WithoutTwins(new(StringComparer.OrdinalIgnoreCase)
        {
            ["DISPLAY1"] = "AAA1111#1&1&0&UID1", ["DISPLAY2"] = "aaa1111#1&1&0&uid1", ["DISPLAY3"] = "CCC3333#1&1&0&UID3",
        });
        if (twins.Count != 1 || !twins.ContainsKey("DISPLAY3")) errors.Add("Monitor names: two displays with one name were not left out");
        steps.Add("twins left out");

        const string a = "AAA1111#1&1&0&UID1", b = "BBB2222#1&1&0&UID2", c = "CCC3333#1&1&0&UID3";
        static Dictionary<string, string> On(string one, string two, string three) =>
            new() { ["DISPLAY1"] = one, ["DISPLAY2"] = two, ["DISPLAY3"] = three };
        var devices = new[] { @"\\.\DISPLAY1", @"\\.\DISPLAY2", @"\\.\DISPLAY3" };
        try
        {
            // Settings from before, kept by display number: a bar on the top of the first, none on the second, and
            // one on the left of the third.
            var trial = new TaskbarSettings();
            trial.Monitors["DISPLAY1"] = new MonitorSettings { Edge = BarEdge.Top };
            trial.Monitors["DISPLAY2"] = new MonitorSettings { Hidden = true };
            trial.Monitors["DISPLAY3"] = new MonitorSettings { Edge = BarEdge.Left };

            // They go to the monitors on those displays now, and only once.
            MonitorIds.UseForTest(On(a, b, c));
            if (!trial.AdoptNamedEntries(devices)) errors.Add("Monitor settings: the entries kept by number did not move");
            if (trial.AdoptNamedEntries(devices)) errors.Add("Monitor settings: the entries moved a second time");
            if (trial.Monitors.Keys.Any(k => k.StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase)) || trial.Monitors.Count != 3)
            {
                errors.Add("Monitor settings: after the move, entries are kept under " + string.Join(", ", trial.Monitors.Keys));
            }
            steps.Add("entries kept by number move to their monitors");

            // Windows numbers them another way: each monitor keeps its own settings.
            void Expect(string what, TaskbarSettings settings)
            {
                if (settings.EdgeFor(@"\\.\DISPLAY2") != BarEdge.Top || !settings.HiddenOn(@"\\.\DISPLAY3") ||
                    settings.EdgeFor(@"\\.\DISPLAY1") != BarEdge.Left || settings.HiddenOn(@"\\.\DISPLAY1") || settings.HiddenOn(@"\\.\DISPLAY2"))
                {
                    errors.Add($"Monitor settings ({what}): a monitor did not keep its settings when the displays were numbered again");
                }
            }
            MonitorIds.UseForTest(On(c, a, b));
            Expect("numbered again", trial);
            if (trial.AdoptNamedEntries(devices)) errors.Add("Monitor settings: numbering the displays again moved entries");

            // A choice made now goes with the monitor, and all of it reads back from the settings file.
            trial.EnsureMonitor(@"\\.\DISPLAY2").Size = 50;
            if (trial.Monitors.TryGetValue(a, out var first) is false || first.Size != 50 || first.Edge != BarEdge.Top)
            {
                errors.Add("Monitor settings: a new choice was not kept with its monitor");
            }
            var back = Settings.Parse(Settings.Write(new Settings { Taskbar = trial })).Taskbar;
            Expect("read back from the file", back);
            if (back.SizeFor(@"\\.\DISPLAY2", sideBar: false) != 50) errors.Add("Monitor settings: a size did not read back from the file");
            steps.Add("numbered again, the settings stay with their monitors");

            // A display Windows names no monitor for keeps its number, and its entry is not moved.
            var plain = new TaskbarSettings();
            plain.Monitors["DISPLAY2"] = new MonitorSettings { Edge = BarEdge.Right };
            MonitorIds.UseForTest(new() { ["DISPLAY1"] = a });
            if (plain.AdoptNamedEntries(devices) || plain.EdgeFor(@"\\.\DISPLAY2") != BarEdge.Right)
            {
                errors.Add("Monitor settings: a display with no monitor name did not keep its entry");
            }
            plain.EnsureMonitor(@"\\.\DISPLAY2").Size = 70;
            if (plain.Monitors.Count != 1 || plain.Monitors.TryGetValue("DISPLAY2", out var kept) is false || kept.Size != 70)
            {
                errors.Add("Monitor settings: a choice on a display with no monitor name was not kept under its number");
            }
            steps.Add("a display with no monitor name keeps its number");

            // An entry under a number may be another monitor's, one not connected now: a monitor that has its own
            // entry already keeps that one, and the numbered one is left for its owner.
            var both = new TaskbarSettings();
            both.Monitors[a] = new MonitorSettings { Edge = BarEdge.Bottom };
            both.Monitors["DISPLAY1"] = new MonitorSettings { Edge = BarEdge.Right };
            if (both.AdoptNamedEntries(devices) || both.EdgeFor(@"\\.\DISPLAY1") != BarEdge.Bottom || !both.Monitors.ContainsKey("DISPLAY1"))
            {
                errors.Add("Monitor settings: a monitor's own entry was not kept over the display's");
            }
            steps.Add("a monitor's own entry wins over its display's");

            // Choosing something before the move: the entry kept by number becomes the monitor's, with what it said.
            var early = new TaskbarSettings();
            early.Monitors["DISPLAY1"] = new MonitorSettings { Edge = BarEdge.Right };
            early.EnsureMonitor(@"\\.\DISPLAY1").Size = 60;
            if (early.Monitors.ContainsKey("DISPLAY1") || early.Monitors.TryGetValue(a, out var moved) is false ||
                moved.Edge != BarEdge.Right || moved.Size != 60)
            {
                errors.Add("Monitor settings: a choice made before the move did not take the display's entry along");
            }
            steps.Add("a choice before the move takes the entry along");
        }
        finally
        {
            MonitorIds.UseForTest(null);
        }

        // This PC: how many of its displays Windows names a monitor for. Only the count is reported.
        MonitorIds.Refresh();
        var screens = AppBarScreen.FromAllScreens();
        int named = screens.Count(s => MonitorIds.For(s.DeviceName) != null);
        steps.Add($"this PC: {named} of {screens.Count} displays have a monitor name");
        return steps;
    }
}
