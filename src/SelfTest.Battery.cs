using System.Windows;
using Windows.System.Power;

namespace ClearStart;

// The battery icon's tip and panel: their words for made-up batteries, pictures of the panel, and what this PC's own
// batteries read. Nothing is opened on the bar.
internal static partial class SelfTest
{
    private static async Task<object> CheckBatteryAsync(string directory, List<string> errors)
    {
        var charging = new BatteryReading(BatteryStatus.Charging, 45000, 50000, 18000);
        var waiting = new BatteryReading(BatteryStatus.Idle, 18330, 23500, 0);
        var two = new PowerReading(true, true, 84, null, TimeSpan.FromMinutes(40), new[] { charging, waiting });
        // Plugged in, a time left is not shown, should Windows give one.
        var twoWithLeft = two with { Left = TimeSpan.FromHours(5) };
        var onBattery = new PowerReading(false, false, 57, TimeSpan.FromMinutes(200), null,
            new[] { new BatteryReading(BatteryStatus.Discharging, 28500, 50000, -9000) });
        // For Windows' power status with parts it does not know (255): the batteries' report beside it.
        var idle = new BatteryReading(BatteryStatus.Idle, 42000, 50000, 0);
        var none = new BatteryReading(BatteryStatus.NotPresent, null, null, null);

        var words = new (string Name, string Got, string Want)[]
        {
            ("tip, charging", Batteries.Tip(twoWithLeft), "Battery: 84%\nPlugged in and charging\nAbout 40 min until full"),
            ("tip, charging, no estimate", Batteries.Tip(two with { ToFull = null }), "Battery: 84%\nPlugged in and charging"),
            ("tip, on battery", Batteries.Tip(onBattery), "Battery: 57%\nOn battery\nAbout 3 h 20 min left"),
            ("tip, full", Batteries.Tip(two with { Charging = false, Percent = 100, ToFull = null }), "Battery: 100%\nFully charged"),
            ("tip, held below full", Batteries.Tip(two with { Charging = false, Percent = 80, ToFull = null }),
                "Battery: 80%\nPlugged in, not charging"),
            ("tip, no battery", Batteries.Tip(new PowerReading(true, false, null, null, null, Array.Empty<BatteryReading>())),
                "No battery found"),
            ("status, battery state unknown", Batteries.Tip(Batteries.Combine(1, 255, 84, -1, idle)),
                "Battery: 84%\nPlugged in, not charging"),
            ("status, charge unknown", Batteries.Tip(Batteries.Combine(1, 0, 255, -1, idle)),
                "Battery: 84%\nPlugged in, not charging"),
            ("status, on battery", Batteries.Tip(Batteries.Combine(0, 0, 57, 12000, onBattery.Batteries[0])),
                "Battery: 57%\nOn battery\nAbout 3 h 20 min left"),
            ("status, no battery", Batteries.Tip(Batteries.Combine(1, 128, 255, -1, none)), "No battery found"),
            ("status, nothing known", Batteries.Tip(Batteries.Combine(255, 255, 255, -1, none)), "No battery found"),
            ("a few seconds", Batteries.Duration(TimeSpan.FromSeconds(10)), "1 min"),
            ("an hour", Batteries.Duration(TimeSpan.FromMinutes(59.6)), "1 h"),
            ("an hour and more", Batteries.Duration(TimeSpan.FromMinutes(65)), "1 h 5 min"),
            ("charging battery", Batteries.State(charging, true), "Charging"),
            ("battery running the PC", Batteries.State(onBattery.Batteries[0], false), "In use"),
            ("full battery", Batteries.State(new BatteryReading(BatteryStatus.Idle, 50000, 50000, 0), true), "Full"),
            ("waiting battery, plugged in", Batteries.State(waiting, true), "Not charging"),
            ("waiting battery, on battery", Batteries.State(waiting, false), "Not in use"),
            ("battery with no size", (new BatteryReading(BatteryStatus.Idle, 100, 0, 0).Percent?.ToString() ?? "none"), "none"),
            ("log line", Batteries.Describe(two),
                "84%, plugged in and charging, about 40 min until full; battery 1 90% charging, battery 2 78% not charging"),
        };
        foreach (var (name, got, want) in words)
        {
            if (got != want) errors.Add($"Battery: {name} says \"{got}\" instead of \"{want}\"");
        }

        foreach (var (power, file) in new[] { (two, "battery-panel.png"), (onBattery, "battery-panel-one.png") })
        {
            var panel = BatteryFlyout.Build(power);
            panel.HorizontalAlignment = HorizontalAlignment.Center;
            panel.VerticalAlignment = VerticalAlignment.Center;
            await DrawAsync(panel, 360, 280, Path.Combine(directory, file));
        }

        // This PC's own: none is fine (a desktop), but reading them must work.
        var real = await Batteries.ReadAsync();
        return new
        {
            wordsChecked = words.Length,
            thisPc = Batteries.Describe(real),
            batteries = real.Batteries.Count,
            tip = Batteries.Tip(real),
            menu = TrayMenus.Battery().Select(e => e.Header).ToList(),
        };
    }
}
