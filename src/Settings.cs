using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClearStart;

public enum Appearance
{
    /// <summary>Fully see-through; only the tint colour is drawn.</summary>
    Clear,
    /// <summary>Blurs what is behind the window.</summary>
    Blur,
    /// <summary>Frosted "acrylic" blur.</summary>
    Acrylic,
    /// <summary>Solid tint colour.</summary>
    Opaque,
}

public enum Alignment
{
    Center,
    Left,
}

public enum BarEdge
{
    /// <summary>Wherever the Windows taskbar is on that monitor (bottom if it cannot be told).</summary>
    Auto,
    Bottom,
    Top,
    Left,
    Right,
}

public enum StartButtonPlacement
{
    /// <summary>First in the row of app buttons, so it is centred along with them.</summary>
    WithApps,
    /// <summary>At the very start of the bar: the left end, or the top end of a side bar.</summary>
    Corner,
    /// <summary>At the very end of the bar, after the clock.</summary>
    End,
    Hidden,
}

/// <summary>What the Start button looks like: one of the built-in shapes, a word, or the user's own picture.</summary>
public enum StartButtonStyle
{
    /// <summary>Four rounded squares.</summary>
    Squares,
    /// <summary>Four round dots.</summary>
    Dots,
    /// <summary>Three rows of three dots.</summary>
    NineDots,
    /// <summary>Four squares standing on a corner.</summary>
    Diamond,
    /// <summary>A circle with a dot in the middle.</summary>
    Ring,
    Disc,
    Hexagon,
    /// <summary>Three bars.</summary>
    Lines,
    Home,
    Star,
    /// <summary>A four-pointed star.</summary>
    Sparkle,
    Bolt,
    Heart,
    /// <summary>A word: "Start", or whatever the settings say.</summary>
    Text,
    /// <summary>A picture file of the user's own.</summary>
    Picture,
}

/// <summary>A coloured shape behind the Start button's design.</summary>
public enum StartButtonBackground
{
    None,
    Circle,
    Rounded,
}

/// <summary>Which side of the start menu the column of places is on.</summary>
public enum PlacesSide
{
    Right,
    Left,
}

/// <summary>What the search button on the bars opens.</summary>
public enum SearchOpens
{
    /// <summary>ClearStart's own menu, with the cursor in its search box. It finds apps.</summary>
    Menu,
    /// <summary>Windows Search, which also finds files, settings and the web.</summary>
    Windows,
}

/// <summary>Which of the pinned apps a bar shows. An app that is running always has its button.</summary>
public enum PinsShown
{
    All,
    /// <summary>Only the ones listed for that monitor.</summary>
    Chosen,
    None,
}

/// <summary>What is different about the bar on one monitor. Anything left out follows the general setting.</summary>
public sealed class MonitorSettings
{
    /// <summary>Which pinned apps this monitor's bar shows. Left out: taskbar.pinsOnAllMonitors decides.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PinsShown? Pins { get; set; }

    /// <summary>
    /// The pinned apps this bar shows when Pins is Chosen, each written the way taskbar.pinned writes it.
    /// Kept while another choice is in force, so going back to Chosen brings the same ones back.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ChosenPins { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BarEdge? Edge { get; set; }

    /// <summary>Bar thickness on this monitor (set by dragging the bar's inner edge).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Size { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StartButtonPlacement? StartButton { get; set; }

    /// <summary>True: this monitor has no bar at all. Left out of the file otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Hidden { get; set; }
}

public sealed class TaskbarSettings
{
    /// <summary>Which edge of the screen bars sit on, unless a monitor says otherwise.</summary>
    public BarEdge Edge { get; set; } = BarEdge.Auto;

    /// <summary>Thickness of a top or bottom bar, in device-independent pixels.</summary>
    public int Height { get; set; } = 44;

    /// <summary>Thickness of a left or right bar.</summary>
    public int Width { get; set; } = 72;

    public StartButtonPlacement StartButton { get; set; } = StartButtonPlacement.WithApps;

    /// <summary>The Start button's design, the same on every bar.</summary>
    public StartButtonStyle StartButtonStyle { get; set; } = StartButtonStyle.Squares;

    /// <summary>Its size as a percentage of the usual one, which follows the app icons. 60 to 160.</summary>
    public int StartButtonScale { get; set; } = 100;

    /// <summary>Its colour, as #RRGGBB. Empty = the colour of the text and glyphs. A picture keeps its own colours.</summary>
    public string StartButtonColor { get; set; } = "";

    /// <summary>A coloured circle or rounded square behind the design. A picture is cut to that shape.</summary>
    public StartButtonBackground StartButtonBackground { get; set; } = StartButtonBackground.None;

    /// <summary>The colour of that shape, as #RRGGBB. Empty = the Windows accent colour.</summary>
    public string StartButtonBackgroundColor { get; set; } = "";

    /// <summary>The word shown by the Text design.</summary>
    public string StartButtonText { get; set; } = "Start";

    /// <summary>The picture file shown by the Picture design: PNG, JPG, BMP, GIF or ICO.</summary>
    public string StartButtonPicture { get; set; } = "";

    public const int MaxStartButtonText = 16;

    /// <summary>When true the bars cannot be resized by dragging their edge.</summary>
    public bool Locked { get; set; }

    /// <summary>One button per app, with all of its windows behind it, instead of one button per window.</summary>
    public bool CombineWindows { get; set; } = true;

    /// <summary>Per-monitor differences, keyed by the monitor's name ("DISPLAY1", "DISPLAY2", ...).</summary>
    public Dictionary<string, MonitorSettings> Monitors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Appearance Appearance { get; set; } = Appearance.Clear;

    /// <summary>#AARRGGBB colour drawn over the bar. Alpha 00 = nothing.</summary>
    public string Tint { get; set; } = "#00000000";

    /// <summary>Colour of text and glyphs on the bar.</summary>
    public string Foreground { get; set; } = "#FFFFFFFF";

    public Alignment Alignment { get; set; } = Alignment.Center;

    /// <summary>Show a bar on every monitor, or only on the primary one.</summary>
    public bool AllMonitors { get; set; } = true;

    /// <summary>When true each bar lists only the windows on its own monitor.</summary>
    public bool TasksOnOwnMonitorOnly { get; set; } = true;

    /// <summary>Show window titles next to the icons.</summary>
    public bool ShowLabels { get; set; }

    /// <summary>Reveal pinned icons in sequence at startup. Off = reveal the initial group together.</summary>
    public bool AnimatePinnedIcons { get; set; }

    /// <summary>App icon size on every bar. 0 = automatic: icons grow and shrink with each bar's thickness.</summary>
    public int IconSize { get; set; }

    /// <summary>Size of the tray icons and the network/volume glyphs on every bar.</summary>
    public int TrayIconSize { get; set; } = 16;

    /// <summary>The tray icons (the ones apps put next to the clock) on every bar, or only on the main one.</summary>
    public bool TrayOnAllMonitors { get; set; } = true;

    /// <summary>
    /// The tray icons that sit on the bar, in order, by the names the tray gives them. All the others wait
    /// behind the arrow beside them; dragging an icon from one place to the other moves it. At first only a
    /// laptop's battery is out: the network and the volume have the quick settings button already.
    /// </summary>
    public List<string> PinnedTrayIcons { get; set; } = new() { BatteryTrayIcon };

    /// <summary>The name of the battery icon Windows puts in the tray.</summary>
    public const string BatteryTrayIcon = "7820ae75-23e3-4229-82c1-e41cb67d5b9c";

    /// <summary>The clock and the network/volume button on every bar, or only on the main one.</summary>
    public bool ClockOnAllMonitors { get; set; } = true;

    /// <summary>A search button next to Start, on every bar.</summary>
    public bool ShowSearchButton { get; set; } = true;

    /// <summary>
    /// What that button opens: Windows Search, or ClearStart's menu ready for typing a search. Windows Search
    /// is the default because the menu is what the Start button already opens.
    /// </summary>
    public SearchOpens SearchButtonOpens { get; set; } = SearchOpens.Windows;

    /// <summary>Button that opens the Windows quick settings panel (network, volume, battery).</summary>
    public bool ShowQuickSettingsButton { get; set; } = true;

    /// <summary>Button that opens the Windows Cast panel, the one Win+K opens. Off until switched on.</summary>
    public bool ShowCastButton { get; set; }

    public bool ShowSeconds { get; set; }

    /// <summary>Pinned launchers, in order. null = follow the Windows "pinned to taskbar" folder.</summary>
    public List<string>? Pinned { get; set; }

    /// <summary>The pinned apps on every bar, or only on the main one. A monitor can say otherwise.</summary>
    public bool PinsOnAllMonitors { get; set; } = true;

    // ---- per-monitor lookups (methods, so they stay out of the settings file) ----

    /// <summary>"\\.\DISPLAY1" as Windows names it becomes "DISPLAY1", which is easier to read and type.</summary>
    public static string MonitorKey(string? deviceName) => (deviceName ?? "").TrimStart('\\', '.');

    public MonitorSettings? ForMonitor(string? deviceName) =>
        Monitors.TryGetValue(MonitorKey(deviceName), out var monitor) ? monitor : null;

    /// <summary>The entry to write a per-monitor choice into, created on first use.</summary>
    public MonitorSettings EnsureMonitor(string? deviceName)
    {
        string key = MonitorKey(deviceName);
        if (!Monitors.TryGetValue(key, out var monitor) || monitor == null)
        {
            monitor = new MonitorSettings();
            Monitors[key] = monitor;
        }
        return monitor;
    }

    public BarEdge EdgeFor(string? deviceName) => ForMonitor(deviceName)?.Edge ?? Edge;

    public StartButtonPlacement StartButtonFor(string? deviceName) => ForMonitor(deviceName)?.StartButton ?? StartButton;

    /// <summary>True when the user has taken the bar off this monitor.</summary>
    public bool HiddenOn(string? deviceName) => ForMonitor(deviceName)?.Hidden == true;

    /// <summary>What a monitor's bar shows of the pinned apps when the monitor itself does not say.</summary>
    public PinsShown GeneralPins(bool main) => main || PinsOnAllMonitors ? PinsShown.All : PinsShown.None;

    /// <summary>Which pinned apps a monitor's bar shows. <paramref name="main"/>: it is the main bar.</summary>
    public PinsShown PinsFor(string? deviceName, bool main) => ForMonitor(deviceName)?.Pins ?? GeneralPins(main);

    /// <summary>True when a monitor's bar shows this pinned app, named the way taskbar.pinned names it.</summary>
    public bool ShowsPin(string? deviceName, bool main, string pin) => PinsFor(deviceName, main) switch
    {
        PinsShown.All => true,
        PinsShown.None => false,
        _ => ForMonitor(deviceName)?.ChosenPins?.Contains(pin, StringComparer.OrdinalIgnoreCase) == true,
    };

    /// <summary>
    /// True when the settings ask for a bar on a monitor. Asking for none at all is not honoured: see
    /// ShellHost.ScreensWithBars.
    /// </summary>
    public bool WantsBarOn(string? deviceName, bool primary) => (AllMonitors || primary) && !HiddenOn(deviceName);

    /// <summary>Brings an icon size from the settings file into range: 0 (automatic) or 16 to 48.</summary>
    public static int ValidIconSize(int size) => size <= 0 ? 0 : Math.Clamp(size, 16, 48);

    /// <summary>Brings a tray icon size from the settings file into range: 12 to 32, 16 if it is missing.</summary>
    public static int ValidTrayIconSize(int size) => size <= 0 ? 16 : Math.Clamp(size, 12, 32);

    /// <summary>Brings the Start button's size from the settings file into range: 60 to 160 percent, 100 if it is missing.</summary>
    public static int ValidStartButtonScale(int percent) => percent <= 0 ? 100 : Math.Clamp(percent, 60, 160);

    /// <summary>The Start button's word: trimmed, and no longer than fits a button.</summary>
    public static string ValidStartButtonText(string? text)
    {
        text = (text ?? "").Trim();
        return text.Length > MaxStartButtonText ? text[..MaxStartButtonText] : text;
    }

    public static (int Min, int Max) SizeLimits(bool sideBar) => sideBar ? (56, 240) : (28, 120);

    public int SizeFor(string? deviceName, bool sideBar)
    {
        var (min, max) = SizeLimits(sideBar);
        return Math.Clamp(ForMonitor(deviceName)?.Size ?? (sideBar ? Width : Height), min, max);
    }

    /// <summary>Drops per-monitor entries that no longer say anything, so the settings file stays readable.</summary>
    public void Tidy()
    {
        foreach (var (name, monitor) in Monitors.ToList())
        {
            if (monitor == null) Monitors.Remove(name);
            else
            {
                if (monitor.Hidden != true) monitor.Hidden = null;
                // An empty list of chosen pins says something only while it is the list in force.
                if (monitor.ChosenPins is { Count: 0 } && monitor.Pins != PinsShown.Chosen) monitor.ChosenPins = null;
                if (monitor.Edge == null && monitor.Size == null && monitor.StartButton == null && monitor.Hidden == null
                    && monitor.Pins == null && monitor.ChosenPins == null)
                {
                    Monitors.Remove(name);
                }
            }
        }
    }
}

public sealed class PlaceEntry
{
    public string Name { get; set; } = "";

    /// <summary>Anything the Run dialog accepts: a path, a shell: location, a URI, an exe. "clearstart:run" opens the Run dialog.</summary>
    public string Target { get; set; } = "";

    /// <summary>A character from the Segoe Fluent Icons font.</summary>
    public string Glyph { get; set; } = FolderGlyph;

    /// <summary>The folder icon, which an entry has until another is chosen.</summary>
    public static readonly string FolderGlyph = ((char)0xE8B7).ToString();

    /// <summary>The target of an entry that opens nothing and is drawn as a thin line between two groups.</summary>
    public const string DividerTarget = "clearstart:divider";

    [JsonIgnore]
    public bool IsDivider => string.Equals(Target?.Trim(), DividerTarget, StringComparison.OrdinalIgnoreCase);

    public static PlaceEntry Divider() => new() { Target = DividerTarget };

    /// <summary>An entry with an icon given as its code in the Segoe Fluent Icons font.</summary>
    public static PlaceEntry Of(string name, string target, int icon) =>
        new() { Name = name, Target = target, Glyph = ((char)icon).ToString() };

    public PlaceEntry Copy() => new() { Name = Name, Target = Target, Glyph = Glyph };
}

public sealed class StartMenuSettings
{
    public int Width { get; set; } = 940;

    public int Height { get; set; } = 640;

    public Appearance Appearance { get; set; } = Appearance.Acrylic;

    public string Tint { get; set; } = "#CC1C1C1C";

    /// <summary>Open this menu with the Windows key (and Ctrl+Esc) instead of the system one.</summary>
    public bool TakeOverWinKey { get; set; } = true;

    /// <summary>Pinned tiles, in order. null = follow the Windows "pinned to Start" folder.</summary>
    public List<string>? Pinned { get; set; }

    /// <summary>The column of places: folders, tools and anything else the Run dialog accepts.</summary>
    public List<PlaceEntry> Places { get; set; } = DefaultPlaces();

    /// <summary>Off: the column keeps only the account button and the power buttons.</summary>
    public bool ShowPlaces { get; set; } = true;

    public PlacesSide PlacesSide { get; set; } = PlacesSide.Right;

    /// <summary>Width of the column. 140 to 340.</summary>
    public int PlacesWidth { get; set; } = 180;

    /// <summary>Height of one entry; its icon and text grow and shrink with it. 28 to 52.</summary>
    public int PlacesRowHeight { get; set; } = 36;

    /// <summary>Off: names only.</summary>
    public bool PlacesShowIcons { get; set; } = true;

    public static List<PlaceEntry> DefaultPlaces() => new()
    {
        PlaceEntry.Of("Documents", "shell:Personal", 0xE8A5),
        PlaceEntry.Of("Downloads", "shell:Downloads", 0xE896),
        PlaceEntry.Of("Pictures", "shell:My Pictures", 0xE8B9),
        PlaceEntry.Of("This PC", "shell:MyComputerFolder", 0xE7F8),
        PlaceEntry.Of("Settings", "ms-settings:", 0xE713),
        PlaceEntry.Of("Control Panel", "control.exe", 0xE9E9),
        PlaceEntry.Of("Command Prompt", "cmd.exe", 0xE756),
        PlaceEntry.Of("Run…", "clearstart:run", 0xE8A7),
    };

    /// <summary>Ready-made entries the settings window offers to add, in groups.</summary>
    public static IReadOnlyList<(string Group, PlaceEntry Place)> ReadyPlaces() => new[]
    {
        ("Folders", PlaceEntry.Of("Documents", "shell:Personal", 0xE8A5)),
        ("Folders", PlaceEntry.Of("Downloads", "shell:Downloads", 0xE896)),
        ("Folders", PlaceEntry.Of("Pictures", "shell:My Pictures", 0xE8B9)),
        ("Folders", PlaceEntry.Of("Music", "shell:My Music", 0xE8D6)),
        ("Folders", PlaceEntry.Of("Videos", "shell:My Video", 0xE714)),
        ("Folders", PlaceEntry.Of("Desktop", "shell:Desktop", 0xE7F4)),
        ("Folders", PlaceEntry.Of("Home folder", "shell:Profile", 0xE80F)),
        ("Folders", PlaceEntry.Of("This PC", "shell:MyComputerFolder", 0xE7F8)),
        ("Folders", PlaceEntry.Of("Network", "shell:NetworkPlacesFolder", 0xE968)),
        ("Folders", PlaceEntry.Of("Recycle Bin", "shell:RecycleBinFolder", 0xE74D)),
        ("Folders", PlaceEntry.Of("File Explorer", "explorer.exe", 0xEC50)),
        ("Windows", PlaceEntry.Of("Settings", "ms-settings:", 0xE713)),
        ("Windows", PlaceEntry.Of("Control Panel", "control.exe", 0xE9E9)),
        ("Windows", PlaceEntry.Of("Installed apps", "ms-settings:appsfeatures", 0xE71D)),
        // The Control Panel's list, for removing or repairing a program: the one "Add or remove programs" was.
        ("Windows", PlaceEntry.Of("Programs and Features", "appwiz.cpl", 0xE7B8)),
        ("Windows", PlaceEntry.Of("Windows Update", "ms-settings:windowsupdate", 0xE895)),
        ("Windows", PlaceEntry.Of("Bluetooth and devices", "ms-settings:bluetooth", 0xE702)),
        ("Windows", PlaceEntry.Of("Device Manager", "devmgmt.msc", 0xE772)),
        ("Tools", PlaceEntry.Of("Task Manager", "taskmgr.exe", 0xE9D9)),
        ("Tools", PlaceEntry.Of("Command Prompt", "cmd.exe", 0xE756)),
        ("Tools", PlaceEntry.Of("PowerShell", "powershell.exe", 0xE943)),
        ("Tools", PlaceEntry.Of("Terminal", "wt.exe", 0xE756)),
        ("Tools", PlaceEntry.Of("Run…", "clearstart:run", 0xE8A7)),
    };

    /// <summary>Icons to choose from for an entry, as codes in the Segoe Fluent Icons font.</summary>
    public static readonly int[] PlaceIcons =
    {
        0xE8B7, 0xE838, 0xEC50, 0xE8A5, 0xE7C3, 0xE896, 0xE8B9, 0xE722, 0xE8D6, 0xEC4F, 0xE714, 0xE7FC,
        0xE80F, 0xE7F8, 0xE7F4, 0xE772, 0xE968, 0xE774, 0xE753, 0xE74D, 0xE713, 0xE9E9, 0xE771, 0xE90F,
        0xE756, 0xE943, 0xE9D9, 0xE8A7, 0xE71D, 0xE721, 0xE895, 0xE702, 0xE715, 0xE787, 0xE77B, 0xE716,
        0xE8BD, 0xE717, 0xE7BF, 0xE719, 0xE8F1, 0xE82D, 0xE7BE, 0xE70F, 0xE8EC, 0xE718, 0xE734, 0xEB51,
        0xE72E, 0xE83D, 0xE8D7, 0xE7E8, 0xE946, 0xEA80, 0xE7C1, 0xE81D, 0xE7B8,
    };
}

public enum TemperatureUnits
{
    /// <summary>Fahrenheit or Celsius, whichever the Windows region uses.</summary>
    Auto,
    Fahrenheit,
    Celsius,
}

/// <summary>The weather on the taskbar. The place is chosen through "Set location…" on the bar.</summary>
public sealed class WeatherSettings
{
    public bool Show { get; set; } = true;

    /// <summary>The place's name, for display only; the forecast is asked for by the coordinates.</summary>
    public string Place { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Latitude { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Longitude { get; set; }

    public TemperatureUnits Units { get; set; } = TemperatureUnits.Auto;

    /// <summary>Show the conditions in words ("Partly sunny") next to the temperature.</summary>
    public bool ShowText { get; set; } = true;

    /// <summary>Show it on every taskbar, or only on the primary monitor's.</summary>
    public bool AllMonitors { get; set; }

    /// <summary>How often the forecast is fetched again.</summary>
    public int RefreshMinutes { get; set; } = 15;

    [JsonIgnore]
    public bool HasPlace => Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;
}

/// <summary>New versions from ClearStart's releases on GitHub: see docs/updates.md.</summary>
public sealed class UpdateSettings
{
    /// <summary>Ask GitHub a few minutes after the start, and then once a day, whether there is a newer version.</summary>
    public bool CheckAutomatically { get; set; } = true;

    /// <summary>Download a newer version when one is found, and install it while the PC is not in use.</summary>
    public bool InstallAutomatically { get; set; }
}

/// <summary>
/// User settings, stored as JSON in %APPDATA%\ClearStart\settings.json.
/// The file is watched: saving it in any editor applies the change immediately.
/// </summary>
public sealed class Settings
{
    public TaskbarSettings Taskbar { get; set; } = new();

    public StartMenuSettings StartMenu { get; set; } = new();

    public WeatherSettings Weather { get; set; } = new();

    public UpdateSettings Updates { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static FileSystemWatcher? _watcher;
    private static DispatcherTimer? _debounce;
    private static string? _lastText;

    public static Settings Current { get; private set; } = new();

    /// <summary>
    /// Swaps in a fresh set of defaults, in memory only. For the screenshot mode, which must not read
    /// the settings of whoever runs it and never saves.
    /// </summary>
    internal static void UseDefaults() => Current = new Settings();

    /// <summary>Raised on the UI thread after the file changed on disk and was re-read successfully.</summary>
    public static event Action? Changed;

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClearStart");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    /// <summary>Loads the settings file, creating it with defaults on first run (unless told not to).</summary>
    public static void Load(bool createIfMissing = true)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath);
                Current = Parse(text);
                _lastText = text;
            }
            else
            {
                Current = new Settings();
                if (createIfMissing) Save();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read settings; using defaults", ex);
            Current = new Settings();
        }
    }

    public static void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var text = Write(Current);
            _lastText = text;
            File.WriteAllText(FilePath, text);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
        }
    }

    /// <summary>Starts watching the settings file. Call once, on the UI thread.</summary>
    public static void Watch()
    {
        if (_watcher != null) return;
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(300),
            };
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                ReloadIfChanged();
            };

            System.IO.Directory.CreateDirectory(Directory);
            _watcher = new FileSystemWatcher(Directory, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler onChange = (_, _) => dispatcher.BeginInvoke(() =>
            {
                _debounce.Stop();
                _debounce.Start();
            });
            _watcher.Changed += onChange;
            _watcher.Created += onChange;
            _watcher.Renamed += (s, e) => onChange(s, e);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not watch the settings file", ex);
        }
    }

    private static void ReloadIfChanged()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var text = File.ReadAllText(FilePath);
            if (text == _lastText) return;

            Current = Parse(text);
            _lastText = text;
            Log.Info("Settings reloaded");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // A half-saved or invalid file: keep what we have and wait for the next save.
            Log.Warn("Settings file is not valid yet; keeping the current settings (" + ex.Message + ")");
        }
    }

    /// <summary>
    /// Reads the settings file on a thread of its own while a live run waits for Explorer, so that the JSON reader
    /// has done its first-time work by the time <see cref="Load"/> reads the file for real. The result is only used to
    /// see which pins to look up early; it never becomes the settings. Null when there is no file or it cannot be read.
    /// </summary>
    internal static Settings? ParseForWarmUp()
    {
        try
        {
            // Lets a save go ahead while this reads.
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The settings as the file holds them. The self-test uses this to check what is written reads back.</summary>
    internal static string Write(Settings settings) => JsonSerializer.Serialize(settings, JsonOptions);

    internal static Settings Parse(string text)
    {
        var settings = JsonSerializer.Deserialize<Settings>(text, JsonOptions) ?? new Settings();
        settings.Taskbar ??= new TaskbarSettings();
        settings.StartMenu ??= new StartMenuSettings();
        settings.StartMenu.Places ??= StartMenuSettings.DefaultPlaces();
        // A hand-edited list may hold a null entry or leave parts of one out.
        settings.StartMenu.Places.RemoveAll(place => place == null);
        foreach (var place in settings.StartMenu.Places)
        {
            place.Name ??= "";
            place.Target ??= "";
            place.Glyph ??= PlaceEntry.FolderGlyph;
        }
        settings.StartMenu.PlacesWidth = Math.Clamp(settings.StartMenu.PlacesWidth, 140, 340);
        settings.StartMenu.PlacesRowHeight = Math.Clamp(settings.StartMenu.PlacesRowHeight, 28, 52);
        settings.Weather ??= new WeatherSettings();
        settings.Weather.Place ??= "";
        settings.Weather.RefreshMinutes = Math.Clamp(settings.Weather.RefreshMinutes, 5, 240);
        settings.Updates ??= new UpdateSettings();

        var (minHeight, maxHeight) = TaskbarSettings.SizeLimits(sideBar: false);
        var (minWidth, maxWidth) = TaskbarSettings.SizeLimits(sideBar: true);
        settings.Taskbar.Height = Math.Clamp(settings.Taskbar.Height, minHeight, maxHeight);
        settings.Taskbar.Width = Math.Clamp(settings.Taskbar.Width, minWidth, maxWidth);
        settings.Taskbar.IconSize = TaskbarSettings.ValidIconSize(settings.Taskbar.IconSize);
        settings.Taskbar.TrayIconSize = TaskbarSettings.ValidTrayIconSize(settings.Taskbar.TrayIconSize);
        settings.Taskbar.StartButtonScale = TaskbarSettings.ValidStartButtonScale(settings.Taskbar.StartButtonScale);
        settings.Taskbar.StartButtonColor ??= "";
        settings.Taskbar.StartButtonBackgroundColor ??= "";
        settings.Taskbar.StartButtonPicture ??= "";
        settings.Taskbar.StartButtonText = TaskbarSettings.ValidStartButtonText(settings.Taskbar.StartButtonText);
        settings.Taskbar.PinnedTrayIcons ??= new List<string>();
        settings.Taskbar.PinnedTrayIcons.RemoveAll(string.IsNullOrWhiteSpace);

        // Monitor names are matched without regard to case, and a hand-edited "DISPLAY1": null is tolerated.
        var monitors = new Dictionary<string, MonitorSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, monitor) in settings.Taskbar.Monitors ?? new Dictionary<string, MonitorSettings>())
        {
            if (monitor == null) continue;
            monitor.ChosenPins?.RemoveAll(string.IsNullOrWhiteSpace);
            monitors[TaskbarSettings.MonitorKey(name)] = monitor;
        }
        settings.Taskbar.Monitors = monitors;
        settings.StartMenu.Width = Math.Clamp(settings.StartMenu.Width, 640, 1800);
        settings.StartMenu.Height = Math.Clamp(settings.StartMenu.Height, 420, 1400);
        return settings;
    }

    /// <summary>A colour the way the settings file writes it: "#AARRGGBB".</summary>
    public static string ColorText(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Parses "#AARRGGBB" / "#RRGGBB" / a colour name; returns the fallback for anything invalid.</summary>
    public static Color ParseColor(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        try
        {
            return (Color)ColorConverter.ConvertFromString(text.Trim());
        }
        catch
        {
            return fallback;
        }
    }
}
