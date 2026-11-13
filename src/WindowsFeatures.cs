using System.IO;

namespace ClearStart;

/// <summary>A Windows settings page, Control Panel item or tool that the start menu's search can find.</summary>
public sealed class WindowsFeature
{
    public WindowsFeature(string name, string target, int glyph, params string[] keywords)
    {
        Name = name;
        Target = target;
        Glyph = ((char)glyph).ToString();
        Keywords = keywords;
        Kind = target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) ? "Settings"
            : target.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase)
              || target.Equals("control.exe", StringComparison.OrdinalIgnoreCase) ? "Control Panel"
            : "Windows tool";
    }

    public string Name { get; }

    /// <summary>Anything the Run dialog accepts, as for a place.</summary>
    public string Target { get; }

    /// <summary>A character from the Segoe Fluent Icons font.</summary>
    public string Glyph { get; }

    /// <summary>Shown under the name: Settings, Control Panel or Windows tool.</summary>
    public string Kind { get; }

    /// <summary>Other words people search for it by, such as the command that opens it.</summary>
    public IReadOnlyList<string> Keywords { get; }

    /// <summary>A program, console or Control Panel item can run as administrator; a settings page or URI cannot.</summary>
    public bool CanElevate => !Target.Contains(':');

    /// <summary>
    /// How well it matches what was typed, as <see cref="StartMenuView.Rank"/> counts. A keyword counts only when one
    /// of its words starts with what was typed, and never above an app whose name starts with it.
    /// </summary>
    public int Rank(string query)
    {
        int best = StartMenuView.Rank(Name, query);
        if (Name.Contains('&'))
        {
            best = Better(best, StartMenuView.Rank(Name.Replace("&", "and"), query));
        }
        if (Keywords.Any(keyword => StartMenuView.Rank(keyword, query) is >= 0 and <= 2)) best = Better(best, 2);
        return best;
    }

    private static int Better(int a, int b) => a < 0 ? b : b < 0 ? a : Math.Min(a, b);
}

/// <summary>
/// The Windows settings pages, Control Panel items and tools the start menu's search finds besides the apps. Power
/// actions are left out, so Enter cannot shut the PC down by accident.
/// </summary>
internal static class WindowsFeatures
{
    private static IReadOnlyList<WindowsFeature>? _all;

    /// <summary>The ones this PC has: a console or program that is missing (Group Policy on Home) is left out.</summary>
    public static IReadOnlyList<WindowsFeature> All => _all ??= Catalog().Where(Exists).ToList();

    /// <summary>Every entry, whether this PC has it or not.</summary>
    internal static IReadOnlyList<WindowsFeature> Catalog() => new WindowsFeature[]
    {
        // Control Panel and its classic items
        new("Control Panel", "control.exe", 0xE9E9, "control"),
        new("Power Options", "powercfg.cpl", 0xE7E8, "power plan", "powercfg", "lid", "power button"),
        new("Programs and Features", "appwiz.cpl", 0xE7B8, "add or remove programs", "uninstall a program", "appwiz"),
        new("Network Connections", "ncpa.cpl", 0xE968, "network adapters", "adapter settings", "ncpa", "ethernet"),
        new("Sound control panel", "mmsys.cpl", 0xE767, "playback devices", "recording devices", "audio devices", "mmsys"),
        new("Mouse properties", "main.cpl", 0xE962, "pointer", "cursor", "double-click speed"),
        new("System Properties", "sysdm.cpl", 0xE7F8, "computer name", "workgroup", "domain", "sysdm", "remote settings"),
        new("Advanced system settings", "SystemPropertiesAdvanced.exe", 0xE7F8,
            "environment variables", "performance options", "virtual memory", "page file", "visual effects"),
        new("Create a restore point", "SystemPropertiesProtection.exe", 0xE90F, "system protection", "restore point"),
        new("System Restore", "rstrui.exe", 0xE90F, "restore point", "rstrui"),
        new("Internet Options", "inetcpl.cpl", 0xE774, "inetcpl", "proxy"),
        new("Date and Time", "timedate.cpl", 0xE787, "timedate", "clock", "time zone"),
        new("Region", "intl.cpl", 0xE774, "intl", "number format", "date format"),
        new("Windows Defender Firewall", "firewall.cpl", 0xEA18, "firewall"),
        new("User Accounts", "netplwiz.exe", 0xE77B, "netplwiz", "userpasswords2", "automatic sign-in"),
        new("Turn Windows features on or off", "optionalfeatures.exe", 0xE71D,
            "windows features", "optional features", "hyper-v", "wsl", "subsystem for linux", "net framework"),
        new("Fonts", "shell:Fonts", 0xE8D2),

        // Management consoles and tools
        new("Device Manager", "devmgmt.msc", 0xE772, "drivers", "devmgmt", "hardware"),
        new("Disk Management", "diskmgmt.msc", 0xEDA2, "partitions", "format a drive", "drive letters", "diskmgmt"),
        new("Computer Management", "compmgmt.msc", 0xE7F8, "compmgmt"),
        new("Services", "services.msc", 0xE912, "services.msc"),
        new("Event Viewer", "eventvwr.msc", 0xE7C3, "event log", "logs", "eventvwr"),
        new("Task Scheduler", "taskschd.msc", 0xE823, "scheduled tasks", "taskschd"),
        new("Local Group Policy Editor", "gpedit.msc", 0xE912, "group policy", "gpedit", "policies"),
        new("Hyper-V Manager", "virtmgmt.msc", 0xE7F8, "virtual machines", "virtmgmt"),
        new("Registry Editor", "regedit.exe", 0xE70F, "regedit", "registry"),
        new("System Configuration", "msconfig.exe", 0xE912, "msconfig", "boot options", "safe mode"),
        new("System Information", "msinfo32.exe", 0xE946, "msinfo32", "hardware specs", "bios version"),
        new("Task Manager", "taskmgr.exe", 0xE9D9, "taskmgr", "processes", "end task"),
        new("Resource Monitor", "resmon.exe", 0xE9D9, "resmon"),
        new("Performance Monitor", "perfmon.exe", 0xE9D9, "perfmon"),
        new("Disk Cleanup", "cleanmgr.exe", 0xE74D, "cleanmgr", "free up space", "temporary files"),
        new("Defragment and Optimize Drives", "dfrgui.exe", 0xEDA2, "defrag", "optimize drives", "trim"),
        new("Character Map", "charmap.exe", 0xE8D2, "charmap", "special characters", "symbols"),
        new("Remote Desktop Connection", "mstsc.exe", 0xE7F4, "mstsc", "rdp", "remote desktop"),
        new("Windows Tools", "shell:Common Administrative Tools", 0xE912, "administrative tools", "admin tools"),
        new("Windows Security", "windowsdefender:", 0xEA18, "defender", "antivirus", "virus protection"),

        // Settings pages
        new("Settings", "ms-settings:", 0xE713, "windows settings"),
        new("Display settings", "ms-settings:display", 0xE7F4,
            "screen resolution", "scale", "monitors", "brightness", "refresh rate", "hdr"),
        new("Night light", "ms-settings:nightlight", 0xE708, "blue light"),
        new("Sound settings", "ms-settings:sound", 0xE767, "volume", "output device", "microphone", "speakers"),
        new("Volume mixer", "ms-settings:apps-volume", 0xE767, "app volume"),
        new("Notifications", "ms-settings:notifications", 0xEA8F, "do not disturb", "focus"),
        new("Power & battery", "ms-settings:powersleep", 0xE7E8, "sleep", "battery", "screen timeout", "power mode"),
        new("Storage", "ms-settings:storagesense", 0xEDA2, "storage sense", "disk space", "free up space"),
        new("Multitasking", "ms-settings:multitasking", 0xE7C4, "snap windows", "alt+tab"),
        new("Clipboard", "ms-settings:clipboard", 0xE77F, "clipboard history"),
        new("About your PC", "ms-settings:about", 0xE946,
            "rename this pc", "pc name", "device specifications", "windows version", "product key"),
        new("Activation", "ms-settings:activation", 0xE946, "product key", "windows license"),
        new("Troubleshoot", "ms-settings:troubleshoot", 0xE90F, "troubleshooters", "fix problems"),
        new("Recovery", "ms-settings:recovery", 0xE90F, "reset this pc", "advanced startup"),
        new("Bluetooth & devices", "ms-settings:bluetooth", 0xE702, "bluetooth", "pair a device", "devices"),
        new("Printers & scanners", "ms-settings:printers", 0xE749, "printers", "scanners", "add a printer"),
        new("Mouse settings", "ms-settings:mousetouchpad", 0xE962, "mouse", "scrolling", "pointer speed"),
        new("Touchpad", "ms-settings:devices-touchpad", 0xE962, "trackpad", "gestures"),
        new("Network & internet", "ms-settings:network", 0xE968, "internet", "network status"),
        new("Wi-Fi", "ms-settings:network-wifi", 0xE701, "wifi", "wireless", "known networks"),
        new("VPN", "ms-settings:network-vpn", 0xE968, "vpn"),
        new("Personalization", "ms-settings:personalization", 0xE771),
        new("Background", "ms-settings:personalization-background", 0xE771, "wallpaper", "desktop background"),
        new("Colors", "ms-settings:colors", 0xE790, "dark mode", "light mode", "accent color", "transparency"),
        new("Themes", "ms-settings:themes", 0xE771, "desktop icons"),
        new("Installed apps", "ms-settings:appsfeatures", 0xE71D, "uninstall", "apps and features"),
        new("Default apps", "ms-settings:defaultapps", 0xE7AC, "default browser", "open with", "file associations"),
        new("Startup apps", "ms-settings:startupapps", 0xE71D, "startup programs", "run at startup"),
        new("Optional features", "ms-settings:optionalfeatures", 0xE71D, "add a feature"),
        new("Your info", "ms-settings:yourinfo", 0xE77B, "account", "microsoft account", "profile picture"),
        new("Sign-in options", "ms-settings:signinoptions", 0xE8D7, "password", "pin", "windows hello", "fingerprint"),
        new("Other users", "ms-settings:otherusers", 0xE77B, "add a user", "family"),
        new("Date & time settings", "ms-settings:dateandtime", 0xE787, "time zone", "set time"),
        new("Language & region", "ms-settings:regionlanguage", 0xE774, "language", "keyboard layout", "input"),
        new("Accessibility", "ms-settings:easeofaccess", 0xE776, "ease of access", "narrator", "magnifier", "text size"),
        new("Privacy & security", "ms-settings:privacy", 0xEA18, "permissions", "location", "camera access"),
        new("Windows Update", "ms-settings:windowsupdate", 0xE895, "updates", "check for updates"),
        new("For developers", "ms-settings:developers", 0xEC7A, "developer mode", "sudo"),
    };

    /// <summary>Whether this PC has it: a program, console or Control Panel item must be in Windows' folders.</summary>
    private static bool Exists(WindowsFeature feature)
    {
        string target = feature.Target;
        if (target.Contains(':') || target.Equals("control.exe", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return File.Exists(Path.Combine(system, target)) || File.Exists(Path.Combine(windows, target));
        }
        catch
        {
            return true;
        }
    }
}
