using Windows.Devices.Enumeration;
using Windows.System.Power;
using Forms = System.Windows.Forms;
using WinBattery = Windows.Devices.Power.Battery;

namespace ClearStart;

/// <summary>
/// One battery as Windows reports it: capacities in milliwatt-hours, and the rate in milliwatts (below zero while
/// the battery runs the PC).
/// </summary>
internal sealed record BatteryReading(BatteryStatus Status, int? Remaining, int? Full, int? Rate)
{
    /// <summary>How full it is, or null when Windows does not say.</summary>
    public int? Percent => Remaining is int remaining && Full is int full && full > 0
        ? Math.Clamp((int)Math.Round(100.0 * remaining / full), 0, 100)
        : null;
}

/// <summary>
/// The power as a whole: whether the PC is plugged in and charging, the charge of all its batteries together (null
/// when it has none), how long that lasts or how long until it is full, and each battery on its own (empty when only
/// the whole was read).
/// </summary>
internal sealed record PowerReading(bool PluggedIn, bool Charging, int? Percent, TimeSpan? Left, TimeSpan? ToFull,
    IReadOnlyList<BatteryReading> Batteries);

/// <summary>
/// The batteries, for Windows' battery icon in the tray. Explorer still puts that icon there but no longer answers
/// it on Windows 11 (no tip, nothing on a click), so ClearStart says what it used to: the charge, the time left or
/// until full, and, on a click, each battery on its own (some laptops have two).
/// </summary>
internal static class Batteries
{
    // An estimate longer than this comes from a trickle of power and means nothing.
    private static readonly TimeSpan LongestEstimate = TimeSpan.FromDays(1);

    /// <summary>The power as a whole, read at once (for the tip).</summary>
    public static PowerReading Read()
    {
        var status = Forms.SystemInformation.PowerStatus;
        bool pluggedIn = status.PowerLineStatus == Forms.PowerLineStatus.Online;
        bool noBattery = status.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery);
        // Windows says 255 % when it does not know.
        int? percent = !noBattery && status.BatteryLifePercent is >= 0 and <= 1
            ? (int)Math.Round(status.BatteryLifePercent * 100)
            : null;

        var all = WinBattery.AggregateBattery.GetReport();
        int? rate = all.ChargeRateInMilliwatts;
        int? remaining = all.RemainingCapacityInMilliwattHours;
        int? full = all.FullChargeCapacityInMilliwattHours;
        bool charging = pluggedIn && (all.Status == BatteryStatus.Charging ||
                                      status.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.Charging));

        TimeSpan? left = null;
        if (!pluggedIn)
        {
            // Windows' own estimate; failing that, what is left at the rate it is going.
            if (status.BatteryLifeRemaining > 0) left = TimeSpan.FromSeconds(status.BatteryLifeRemaining);
            else if (rate < 0 && remaining > 0) left = TimeSpan.FromHours((double)remaining.Value / -rate.Value);
        }

        TimeSpan? toFull = null;
        if (charging && rate > 0 && remaining != null && full > remaining)
        {
            toFull = TimeSpan.FromHours((double)(full.Value - remaining.Value) / rate.Value);
        }

        return new PowerReading(pluggedIn, charging, percent, Sensible(left), Sensible(toFull),
            Array.Empty<BatteryReading>());
    }

    /// <summary>The power as a whole and each battery on its own (for the panel a click opens).</summary>
    public static async Task<PowerReading> ReadAsync()
    {
        var whole = Read();
        var devices = await DeviceInformation.FindAllAsync(WinBattery.GetDeviceSelector());
        var each = new List<BatteryReading>();
        // In the order Windows numbers them, so the same battery is always Battery 1.
        foreach (var device in devices.OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase))
        {
            var battery = await WinBattery.FromIdAsync(device.Id);
            // A bay with its battery taken out is still listed.
            if (battery?.GetReport() is not { } report || report.Status == BatteryStatus.NotPresent) continue;
            each.Add(new BatteryReading(report.Status, report.RemainingCapacityInMilliwattHours,
                report.FullChargeCapacityInMilliwattHours, report.ChargeRateInMilliwatts));
        }
        return whole with { Batteries = each };
    }

    /// <summary>The tip on the battery icon: the charge, plugged in or not, and the time left or until full.</summary>
    public static string Tip(PowerReading power)
    {
        if (power.Percent is not int percent) return "No battery found";
        var lines = new List<string> { $"Battery: {percent}%", Status(power) };
        if (Estimate(power) is { } estimate) lines.Add(estimate);
        return string.Join("\n", lines);
    }

    /// <summary>"Plugged in and charging", "Fully charged", "Plugged in, not charging" or "On battery".</summary>
    public static string Status(PowerReading power) =>
        !power.PluggedIn ? "On battery"
        : power.Charging ? "Plugged in and charging"
        : power.Percent >= 100 ? "Fully charged"
        : "Plugged in, not charging";

    /// <summary>"About 1 h 5 min until full", "About 3 h 20 min left", or null when there is no estimate.</summary>
    public static string? Estimate(PowerReading power)
    {
        if (power.PluggedIn) return power.Charging && power.ToFull is { } toFull ? $"About {Duration(toFull)} until full" : null;
        return power.Left is { } left ? $"About {Duration(left)} left" : null;
    }

    /// <summary>"1 h 5 min", "2 h" or "45 min"; never less than a minute.</summary>
    public static string Duration(TimeSpan time)
    {
        int minutes = Math.Max(1, (int)Math.Round(time.TotalMinutes));
        int hours = minutes / 60;
        int rest = minutes % 60;
        return hours == 0 ? $"{rest} min" : rest == 0 ? $"{hours} h" : $"{hours} h {rest} min";
    }

    /// <summary>What one battery is doing: "Charging", "In use", "Full", "Not charging" or "Not in use".</summary>
    public static string State(BatteryReading battery, bool pluggedIn) => battery.Status switch
    {
        BatteryStatus.Charging => "Charging",
        BatteryStatus.Discharging => "In use",
        _ when battery.Percent >= 100 => "Full",
        _ => pluggedIn ? "Not charging" : "Not in use",
    };

    /// <summary>One line for the log: the whole, then each battery.</summary>
    public static string Describe(PowerReading power)
    {
        if (power.Percent is not int percent) return "no battery";
        string text = $"{percent}%, {Status(power).ToLowerInvariant()}";
        if (Estimate(power) is { } estimate) text += ", " + estimate.ToLowerInvariant();
        for (int i = 0; i < power.Batteries.Count; i++)
        {
            var battery = power.Batteries[i];
            text += $"{(i == 0 ? "; " : ", ")}battery {i + 1} {battery.Percent?.ToString() ?? "?"}% " +
                    State(battery, power.PluggedIn).ToLowerInvariant();
        }
        return text;
    }

    private static TimeSpan? Sensible(TimeSpan? time) =>
        time is { } t && t > TimeSpan.Zero && t <= LongestEstimate ? t : null;
}
