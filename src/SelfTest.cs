using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManagedShell.AppBar;
using ManagedShell.ShellFolders;

namespace ClearStart;

/// <summary>
/// "ClearStart.exe --selftest DIR": builds the real taskbar and start menu contents from the real
/// data on this PC, draws them into PNG files and writes what it found to selftest.json.
/// Nothing is shown on the desktop, nothing is hidden and no settings are written.
/// </summary>
internal static partial class SelfTest
{
    public static async Task<int> RunAsync(string directory)
    {
        var errors = new List<string>();
        var report = new Dictionary<string, object?>();

        var bindingErrors = new BindingErrorListener();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        ShellHost? host = null;
        try
        {
            Settings.Load(createIfMissing: false);
            host = new ShellHost(HostMode.SelfTest, directory);

            var screens = AppBarScreen.FromAllScreens();
            report["screens"] = screens.Select(s => new
            {
                device = s.DeviceName,
                bounds = $"{s.Bounds.X},{s.Bounds.Y} {s.Bounds.Width}x{s.Bounds.Height}",
                primary = s.Primary,
                scale = Native.ScaleOf(s.HMonitor),
            }).ToList();

            await WaitForAppListAsync(host);
            report["appCount"] = host.Apps.Apps.Count;
            report["appPathSamples"] = SampleApps(host);
            report["taskbarPins"] = host.Pins.Taskbar.Select(p => p.Name).ToList();
            report["startPins"] = host.Pins.Start.Select(p => p.Name).ToList();
            report["openWindowCount"] = host.TaskView.Cast<object>().Count();
            // The taskbar must not wait for the app list's icons: these should all be there already.
            report["windowIconsReadyBeforeBars"] = host.TaskView.Cast<ManagedShell.WindowsTasks.ApplicationWindow>()
                .Count(w => w.Icon != null);
            report["appIconsReadyBeforeBars"] = host.Apps.Apps.Count(a => a.LargeIcon != null);

            // What each monitor's bar would hold: which edge, and its app buttons with their window counts.
            try
            {
                var taskbar = Settings.Current.Taskbar;
                bool ownMonitorOnly = taskbar.AllMonitors && taskbar.TasksOnOwnMonitorOnly;
                var bars = new List<object>();
                var withBars = ShellHost.ScreensWithBars(screens, taskbar);
                foreach (var screen in screens)
                {
                    using var apps = new TaskbarApps(host, ownMonitorOnly ? screen.HMonitor : IntPtr.Zero, taskbar.CombineWindows);
                    var edge = host.PlannedEdge(screen.DeviceName);
                    bars.Add(new
                    {
                        device = screen.DeviceName,
                        hasBar = withBars.Contains(screen),
                        edge = edge.ToString(),
                        size = taskbar.SizeFor(screen.DeviceName, edge is AppBarEdge.Left or AppBarEdge.Right),
                        startButton = taskbar.StartButtonFor(screen.DeviceName).ToString(),
                        buttons = apps.Items.Select(b => new
                        {
                            app = b.AppName,
                            pinned = b.IsPinned,
                            windows = b.Count,
                            state = b.State.ToString(),
                        }).ToList(),
                    });
                }
                report["bars"] = bars;
            }
            catch (Exception ex)
            {
                errors.Add("Bar contents: " + ex);
            }

            // Hiding bars, worked out on throw-away settings: which displays keep one. One always must.
            try
            {
                List<string> Kept(params string[] hidden)
                {
                    var trial = new TaskbarSettings();
                    foreach (string device in hidden) trial.EnsureMonitor(device).Hidden = true;
                    return ShellHost.ScreensWithBars(screens, trial)
                        .Select(s => TaskbarSettings.MonitorKey(s.DeviceName)).ToList();
                }

                var everything = screens.Select(s => s.DeviceName).ToArray();
                var main = screens.FirstOrDefault(s => s.Primary) ?? screens.First();
                var others = everything.Where(d => d != main.DeviceName).ToArray();

                var noneHidden = Kept();
                var mainHidden = Kept(main.DeviceName);
                var othersHidden = Kept(others);
                var allHidden = Kept(everything);
                report["hidingBars"] = new { noneHidden, mainHidden, othersHidden, allHidden };

                string mainKey = TaskbarSettings.MonitorKey(main.DeviceName);
                if (noneHidden.Count != screens.Count) errors.Add("Hiding: with nothing hidden, every display should have a bar");
                if (others.Length > 0 && (mainHidden.Count != others.Length || mainHidden.Contains(mainKey)))
                {
                    errors.Add("Hiding: hiding the main display's bar should leave exactly the others");
                }
                if (othersHidden.Count != 1 || othersHidden[0] != mainKey) errors.Add("Hiding: hiding the others should leave the main display's bar");
                if (allHidden.Count != 1 || allHidden[0] != mainKey) errors.Add("Hiding: with all hidden, the main display must keep its bar");
            }
            catch (Exception ex)
            {
                errors.Add("Hiding bars: " + ex);
            }

            // Start with Windows: read only. The switch is never thrown here.
            try
            {
                report["startWithWindows"] = new
                {
                    on = AutoStart.IsEnabled,
                    windowsStarts = AutoStart.Target,
                    thisProgram = AutoStart.ThisProgram,
                    note = AutoStart.Describe(),
                };
                var commands = new (string Command, string? Program)[]
                {
                    ("\"C:\\Program Files\\ClearStart\\ClearStart.exe\"", "C:\\Program Files\\ClearStart\\ClearStart.exe"),
                    ("\"C:\\Program Files\\ClearStart\\ClearStart.exe\" --preview", "C:\\Program Files\\ClearStart\\ClearStart.exe"),
                    ("C:\\Tools\\ClearStart.exe --preview", "C:\\Tools\\ClearStart.exe"),
                    ("  ", null),
                };
                foreach (var (command, program) in commands)
                {
                    string? found = AutoStart.ProgramIn(command);
                    if (found != program) errors.Add($"Start with Windows: read \"{found}\" out of [{command}]");
                }
            }
            catch (Exception ex)
            {
                errors.Add("Start with Windows: " + ex);
            }

            // The colour wheel: a colour put on it must come back as the same colour, and its picture.
            try
            {
                var samples = new[]
                {
                    Colors.Black, Colors.White, Color.FromRgb(0x80, 0x80, 0x80), Colors.Red, Colors.Lime, Colors.Blue,
                    Color.FromRgb(0x3A, 0x86, 0xFF), Color.FromRgb(0xFF, 0xB7, 0x03), Color.FromRgb(0x1C, 0x1C, 0x1C),
                    Color.FromRgb(0x9B, 0x5D, 0xE5), Color.FromRgb(0x01, 0x02, 0x03), Color.FromRgb(0xFE, 0x00, 0xFF),
                };
                foreach (var sample in samples)
                {
                    var (hue, strength, value) = ColorWheel.ToHsv(sample);
                    var back = ColorWheel.FromHsv(hue, strength, value);
                    if (back != sample) errors.Add($"Colour wheel: {sample} came back as {back}");
                }

                var wheel = new ColorWheel();
                wheel.Show(Color.FromRgb(0x3A, 0x86, 0xFF));
                if (wheel.Color != Color.FromRgb(0x3A, 0x86, 0xFF)) errors.Add("Colour wheel: shows a different colour than it was given");
                var panel = ColorWheel.InPanel(wheel);
                panel.HorizontalAlignment = HorizontalAlignment.Center;
                panel.VerticalAlignment = VerticalAlignment.Center;
                await DrawAsync(panel, 260, 320, Path.Combine(directory, "color-wheel.png"));

                var dim = new ColorWheel();
                dim.Show(Color.FromRgb(0x5A, 0x2D, 0x0C));
                var dimPanel = ColorWheel.InPanel(dim);
                dimPanel.HorizontalAlignment = HorizontalAlignment.Center;
                dimPanel.VerticalAlignment = VerticalAlignment.Center;
                await DrawAsync(dimPanel, 260, 320, Path.Combine(directory, "color-wheel-dim.png"));
            }
            catch (Exception ex)
            {
                errors.Add("Colour wheel: " + ex);
            }

            // Which network glyph the bars would show, and what the sound menu would list. Reading only:
            // nothing is switched.
            try
            {
                host.Network.ReadNow();
                var network = host.Network.State;
                report["network"] = new
                {
                    kind = network.Kind.ToString(),
                    internet = network.Internet,
                    name = network.Name,
                    bars = network.Bars,
                    glyph = string.Join(" ", network.Glyph.Select(c => $"U+{(int)c:X4}")),
                    description = network.Description,
                    menu = TrayMenus.Network().Select(e => e.Header ?? "-").ToList(),
                };
            }
            catch (Exception ex)
            {
                errors.Add("Network: " + ex);
            }

            try
            {
                var outputs = Audio.Outputs();
                var spatial = Audio.Spatial();
                report["sound"] = new
                {
                    outputs = outputs.Select(o => new { name = o.Name, isDefault = o.IsDefault }).ToList(),
                    defaultOutputs = outputs.Count(o => o.IsDefault),
                    spatialSupported = spatial?.Supported,
                    spatialNow = spatial?.ActiveName,
                    spatialFormats = spatial?.Formats.Select(f => new { name = f.Name, active = f.IsActive }).ToList(),
                    menu = MenuLines(TrayMenus.Sound(), ""),
                };
            }
            catch (Exception ex)
            {
                errors.Add("Sound: " + ex);
            }

            // Weather: a saved reply from the forecast service is read and drawn, so nothing is fetched here.
            WeatherReport? sampleWeather = null;
            try
            {
                sampleWeather = WeatherService.Parse(SampleForecast, "Springfield, Illinois",
                    fahrenheit: true, new DateTime(2026, 11, 11, 12, 45, 0));
                var places = WeatherService.ParsePlaces(SamplePlaces);
                report["weather"] = new
                {
                    onTheBar = sampleWeather.TemperatureText + " " + sampleWeather.Conditions,
                    feelsLike = WeatherReport.Degrees(sampleWeather.FeelsLike),
                    humidity = sampleWeather.Humidity,
                    wind = sampleWeather.WindText,
                    days = sampleWeather.Days.Select(d =>
                        $"{d.Date:ddd}: {WeatherCodes.Describe(d.Code, true)}, {d.High:0}/{d.Low:0}, rain {d.RainChance}%").ToList(),
                    placesFound = places.Select(p => $"{p.FullName} ({p.Latitude}, {p.Longitude})").ToList(),
                    placeSetInSettings = Settings.Current.Weather.HasPlace,
                };

                string File(string name) => Path.Combine(directory, name);

                var flyout = WeatherFlyout.Build(sampleWeather, null);
                flyout.HorizontalAlignment = HorizontalAlignment.Center;
                flyout.VerticalAlignment = VerticalAlignment.Center;
                await DrawAsync(flyout, 380, 400, File("weather-flyout.png"));

                var stale = WeatherFlyout.Build(sampleWeather, "Could not reach the weather service");
                stale.HorizontalAlignment = HorizontalAlignment.Center;
                stale.VerticalAlignment = VerticalAlignment.Center;
                await DrawAsync(stale, 380, 440, File("weather-flyout-stale.png"));

                await DrawAsync(WeatherIconSheet(), 640, 420, File("weather-icons.png"));
            }
            catch (Exception ex)
            {
                errors.Add("Weather: " + ex);
            }

            // The app icon: the file the program carries, read back the way Windows reads it, and a picture
            // of every size in it.
            try
            {
                string ico = Path.Combine(directory, "app-icon.ico");
                AppIcon.WriteIco(ico);
                using (var stream = File.OpenRead(ico))
                {
                    var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    var sizes = decoder.Frames.Select(frame => frame.PixelWidth).OrderBy(size => size).ToList();
                    report["appIcon"] = new { bytes = stream.Length, sizes };
                    if (sizes.Count != 9 || sizes[0] != 16 || sizes[^1] != 256)
                    {
                        errors.Add("App icon: the .ico file does not hold the sizes it should: " + string.Join(", ", sizes));
                    }
                }

                // A rounded tile: see-through at the corners, solid in the middle.
                var small = AppIcon.Render(32);
                var pixels = new byte[32 * 32 * 4];
                small.CopyPixels(pixels, 32 * 4, 0);
                if (pixels[3] != 0) errors.Add("App icon: its corner should be see-through");
                if (pixels[(((16 * 32) + 16) * 4) + 3] != 255) errors.Add("App icon: its middle should be solid");

                await DrawAsync(AppIconSheet(), 800, 420, Path.Combine(directory, "app-icon.png"));
            }
            catch (Exception ex)
            {
                errors.Add("App icon: " + ex);
            }

            // Every icon the app names by its code must be in the icon font, or it shows as an empty box.
            try
            {
                var named = StartMenuSettings.PlaceIcons
                    .Concat(new[] { 0xE790, 0xE90E, 0xECA5, 0xE706, 0xE713, 0xE70E, 0xE70D, 0xE74D, 0xECAA, 0xE8B9, 0xE774 })
                    .Concat(StartMenuSettings.DefaultPlaces().Concat(StartMenuSettings.ReadyPlaces().Select(ready => ready.Place))
                        .Where(place => place.Glyph.Length == 1).Select(place => (int)place.Glyph[0]))
                    .Distinct().ToList();
                var missing = MissingGlyphs(named).Select(code => $"U+{code:X4}").ToList();
                report["iconsChecked"] = named.Count;
                report["iconsMissing"] = missing;
                if (missing.Count > 0) errors.Add("Icons the icon font does not have: " + string.Join(", ", missing));
                await DrawAsync(IconSheet(StartMenuSettings.PlaceIcons), 560, 480, Path.Combine(directory, "place-icons.png"));
            }
            catch (Exception ex)
            {
                errors.Add("Icons: " + ex);
            }

            // The Start button's designs, each drawn four ways. The picture design is given the app icon.
            string startPicture = Path.Combine(directory, "start-picture.png");
            try
            {
                SavePng(AppIcon.Render(64), startPicture);
                if (StartGlyph.Picture(startPicture) == null) errors.Add("Start button: could not read the picture it was given");
                if (StartGlyph.Picture("") != null) errors.Add("Start button: no picture chosen should show no picture");
                report["startButtonDesigns"] = StartGlyph.Designs.Select(design => design.Name).ToList();
                if (StartGlyph.Designs.Length != Enum.GetValues<StartButtonStyle>().Length)
                {
                    errors.Add("Start button: not every design is offered in the settings");
                }
                await DrawAsync(StartButtonSheet(startPicture), 1340, 380, Path.Combine(directory, "start-buttons.png"));
            }
            catch (Exception ex)
            {
                errors.Add("Start button designs: " + ex);
            }

            // What is written to the settings file must read back the same, and a file edited by hand with
            // parts missing or out of range must still be usable. Throw-away settings: nothing is saved.
            try
            {
                var written = new Settings();
                written.Taskbar.StartButtonStyle = StartButtonStyle.NineDots;
                written.Taskbar.StartButtonBackground = StartButtonBackground.Rounded;
                written.Taskbar.StartButtonScale = 135;
                written.Taskbar.StartButtonText = "Go";
                written.Taskbar.StartButtonColor = "#FFFFB703";
                written.StartMenu.PlacesSide = PlacesSide.Left;
                written.StartMenu.PlacesRowHeight = 44;
                written.StartMenu.PlacesWidth = 220;
                written.StartMenu.PlacesShowIcons = false;
                written.StartMenu.Places = new List<PlaceEntry>
                {
                    PlaceEntry.Of("Notes", "C:\\Notes", 0xE70F),
                    PlaceEntry.Divider(),
                    PlaceEntry.Of("Web", "https://example.com", 0xE774),
                };
                string text = Settings.Write(written);
                var read = Settings.Parse(text);
                bool same = read.Taskbar.StartButtonStyle == StartButtonStyle.NineDots
                    && read.Taskbar.StartButtonBackground == StartButtonBackground.Rounded
                    && read.Taskbar.StartButtonScale == 135 && read.Taskbar.StartButtonText == "Go"
                    && read.Taskbar.StartButtonColor == "#FFFFB703"
                    && read.StartMenu.PlacesSide == PlacesSide.Left && read.StartMenu.PlacesRowHeight == 44
                    && read.StartMenu.PlacesWidth == 220 && !read.StartMenu.PlacesShowIcons
                    && read.StartMenu.Places.Count == 3 && read.StartMenu.Places[1].IsDivider
                    && read.StartMenu.Places[0].Name == "Notes" && read.StartMenu.Places[0].Target == "C:\\Notes"
                    && read.StartMenu.Places[0].Glyph == ((char)0xE70F).ToString()
                    && read.StartMenu.Places[2].Target == "https://example.com";
                if (!same) errors.Add("Settings: the Start button and places options did not read back as written");
                report["settingsFileLines"] = text.Split('\n').Select(line => line.Trim())
                    .Where(line => line.StartsWith("\"startButton", StringComparison.Ordinal) ||
                                   line.StartsWith("\"places", StringComparison.Ordinal) ||
                                   line.StartsWith("\"showPlaces", StringComparison.Ordinal)).ToList();

                var rough = Settings.Parse("""
                    {"taskbar":{"startButtonStyle":"hexagon","startButtonScale":900,"startButtonText":null,"startButtonColor":null},
                     "startMenu":{"places":[null,{"name":"Only a name"}],"placesWidth":9,"placesRowHeight":400}}
                    """);
                if (rough.Taskbar.StartButtonStyle != StartButtonStyle.Hexagon || rough.Taskbar.StartButtonScale != 160 ||
                    rough.Taskbar.StartButtonText != "" || rough.Taskbar.StartButtonColor != "" ||
                    rough.StartMenu.Places.Count != 1 || rough.StartMenu.Places[0].Target != "" ||
                    rough.StartMenu.PlacesWidth != 140 || rough.StartMenu.PlacesRowHeight != 52)
                {
                    errors.Add("Settings: a rough hand-edited file was not put right");
                }
                if (new Settings().StartMenu.Places.Count != StartMenuSettings.DefaultPlaces().Count)
                {
                    errors.Add("Settings: new settings should start with the default places");
                }
                if (TaskbarSettings.ValidStartButtonText("   a word that is far too long to fit   ").Length > TaskbarSettings.MaxStartButtonText)
                {
                    errors.Add("Settings: the Start button's word was not cut to length");
                }
            }
            catch (Exception ex)
            {
                errors.Add("Settings file: " + ex);
            }

            // The settings window's pages.
            try
            {
                var heights = new Dictionary<SettingsPage, double>
                {
                    [SettingsPage.Appearance] = 1060,
                    [SettingsPage.Taskbar] = 1640 + 210 * screens.Count,
                    [SettingsPage.StartMenu] = 1620,
                    [SettingsPage.Weather] = 860,
                    [SettingsPage.About] = 960,
                };
                foreach (var (page, height) in heights)
                {
                    var view = new SettingsView(host, page) { Width = 880, Height = height };
                    await DrawAsync(view, 880, height,
                        Path.Combine(directory, $"settings-{page.ToString().ToLowerInvariant()}.png"));
                    if (page == SettingsPage.StartMenu) report["addPlaceMenu"] = MenuLines(view.AddMenu(), "");
                }
                // Wider than the pages are meant to stretch: the lines should stop, not run on.
                var wide = new SettingsView(host, SettingsPage.About) { Width = 1500, Height = 680 };
                await DrawAsync(wide, 1500, 680, Path.Combine(directory, "settings-wide.png"));
                // The size the window opens at, to see the page scroll rather than spill.
                var window = new SettingsView(host, SettingsPage.Taskbar) { Width = 880, Height = 620 };
                await DrawAsync(window, 880, 620, Path.Combine(directory, "settings-window-size.png"));
            }
            catch (Exception ex)
            {
                errors.Add("Settings window: " + ex);
            }

            // Taskbar, drawn along each edge and at several sizes
            try
            {
                var primary = screens.FirstOrDefault(s => s.Primary) ?? screens.First();
                double flat = Settings.Current.Taskbar.Height;
                double side = Settings.Current.Taskbar.Width;
                string File(string name) => Path.Combine(directory, name);

                // Icons arrive in the background, so the first picture waits longest.
                await DrawBarAsync(host, primary, File("taskbar.png"), AppBarEdge.Bottom, flat, 1600, null, 2500);
                // Too little room: the buttons should start from the left and stay clear of the clock.
                await DrawBarAsync(host, primary, File("taskbar-crowded.png"), AppBarEdge.Bottom, flat, 700, null, 1200);
                await DrawBarAsync(host, primary, File("taskbar-top-thin.png"), AppBarEdge.Top, 28, 1600, StartButtonPlacement.Corner, 1200);
                await DrawBarAsync(host, primary, File("taskbar-thick.png"), AppBarEdge.Bottom, 72, 1600, StartButtonPlacement.End, 1200);
                await DrawBarAsync(host, primary, File("sidebar-left.png"), AppBarEdge.Left, side, 900, null, 1200);
                await DrawBarAsync(host, primary, File("sidebar-right-wide.png"), AppBarEdge.Right, 120, 900, StartButtonPlacement.Corner, 1200);
                await DrawBarAsync(host, primary, File("sidebar-left-narrow.png"), AppBarEdge.Left, 56, 500, StartButtonPlacement.End, 1200);
                // Chosen icon sizes: small icons on bars of the usual thickness, large ones on a thick bar.
                await DrawBarAsync(host, primary, File("taskbar-icons-16.png"), AppBarEdge.Bottom, flat, 1600, null, 1200, 16);
                await DrawBarAsync(host, primary, File("sidebar-icons-16.png"), AppBarEdge.Left, side, 900, null, 1200, 16);
                await DrawBarAsync(host, primary, File("taskbar-icons-40.png"), AppBarEdge.Bottom, 56, 1600, null, 1200, 40);

                // Tray icon sizes. The real tray icons need live mode, so a few window icons stand in for them.
                var sampleTray = host.TaskView.Cast<ManagedShell.WindowsTasks.ApplicationWindow>()
                    .Where(w => w.Icon != null).Take(6)
                    .Select(w => new { w.Icon, w.Title }).ToList();
                report["sampleTrayIcons"] = sampleTray.Count;
                await DrawBarAsync(host, primary, File("taskbar-tray-12.png"), AppBarEdge.Bottom, flat, 1600, null, 1200, null, 12, sampleTray);
                await DrawBarAsync(host, primary, File("taskbar-tray-16.png"), AppBarEdge.Bottom, flat, 1600, null, 1200, null, 16, sampleTray);
                await DrawBarAsync(host, primary, File("taskbar-tray-24.png"), AppBarEdge.Bottom, flat, 1600, null, 1200, null, 24, sampleTray);
                await DrawBarAsync(host, primary, File("sidebar-tray-24.png"), AppBarEdge.Left, side, 900, null, 1200, null, 24, sampleTray);
                // Too big for a thin bar: it should be shrunk to fit rather than cut off.
                await DrawBarAsync(host, primary, File("taskbar-thin-tray-32.png"), AppBarEdge.Top, 28, 1600, null, 1200, null, 32, sampleTray);

                // Tray icons, and the clock, on the main bar only or on every bar: a second display's bar drawn
                // each way. The two settings are changed in memory only and put back; nothing is saved.
                var second = screens.FirstOrDefault(s => !s.Primary && s.Bounds.Width / Native.ScaleOf(s.HMonitor) >= 900);
                if (second != null)
                {
                    var trial = Settings.Current.Taskbar;
                    bool trayBefore = trial.TrayOnAllMonitors;
                    bool clockBefore = trial.ClockOnAllMonitors;
                    try
                    {
                        var found = new List<object>();
                        var cases = new[] { (true, true, "all"), (false, true, "tray-main-only"), (true, false, "clock-main-only"), (false, false, "both-main-only") };
                        foreach (var (tray, clock, name) in cases)
                        {
                            trial.TrayOnAllMonitors = tray;
                            trial.ClockOnAllMonitors = clock;

                            var onSecond = new TaskbarView(host, second, AppBarEdge.Bottom, flat);
                            var parts = onSecond.TrayParts;
                            onSecond.Detach();
                            var onMain = new TaskbarView(host, primary, AppBarEdge.Bottom, flat);
                            var mainParts = onMain.TrayParts;
                            onMain.Detach();

                            if (parts != (tray, clock)) errors.Add($"Tray ({name}): the second bar has tray icons: {parts.TrayIcons}, clock: {parts.Clock}");
                            if (mainParts != (true, true)) errors.Add($"Tray ({name}): the main bar must keep its tray icons and clock");
                            found.Add(new
                            {
                                trayIconsOnEveryBar = tray,
                                clockOnEveryBar = clock,
                                secondBarTrayIcons = parts.TrayIcons,
                                secondBarClock = parts.Clock,
                            });
                            await DrawBarAsync(host, second, File($"second-bar-{name}.png"), AppBarEdge.Bottom, flat, 1600, null, 800, null, null, sampleTray);
                        }
                        report["trayOnSecondBar"] = found;
                    }
                    finally
                    {
                        trial.TrayOnAllMonitors = trayBefore;
                        trial.ClockOnAllMonitors = clockBefore;
                    }
                }

                // Which bars show the pinned apps: every bar or the main one only, and all, none or a chosen few
                // on one bar. Changed in memory only, through the calls the menus and the settings page make,
                // and put back; nothing is saved.
                if (second != null && host.Pins.Taskbar.Count >= 3)
                {
                    var trial = Settings.Current.Taskbar;
                    bool everyBarBefore = trial.PinsOnAllMonitors;
                    var monitorsBefore = trial.Monitors;
                    try
                    {
                        // The trial starts with no monitor saying anything about pins, whatever the settings say.
                        trial.Monitors = new Dictionary<string, MonitorSettings>(StringComparer.OrdinalIgnoreCase);
                        foreach (var (name, monitor) in monitorsBefore)
                        {
                            trial.Monitors[name] = new MonitorSettings
                            {
                                Edge = monitor.Edge,
                                Size = monitor.Size,
                                StartButton = monitor.StartButton,
                                Hidden = monitor.Hidden,
                            };
                        }

                        var pins = host.Pins.Taskbar.Select(p => p.Path).ToList();
                        string here = second.DeviceName, mainDevice = primary.DeviceName;
                        bool hereIsMain = host.IsMainBar(second), mainIsMain = host.IsMainBar(primary);
                        if (hereIsMain || !mainIsMain) errors.Add("Pinned apps: the main display's bar should be the main bar");

                        void Expect(string what, AppBarScreen screen, IEnumerable<string> wanted)
                        {
                            var view = new TaskbarView(host, screen, AppBarEdge.Bottom, flat);
                            var shown = view.AppButtons.Where(b => b.Pin != null).Select(b => b.Pin!.Path).ToList();
                            view.Detach();
                            if (!shown.SequenceEqual(wanted))
                            {
                                errors.Add($"Pinned apps ({what}): the bar on {TaskbarSettings.MonitorKey(screen.DeviceName)} " +
                                    $"shows {shown.Count} of them, not the {wanted.Count()} expected");
                            }
                        }

                        host.SetPinsOnAllBars(true);
                        Expect("every bar", second, pins);
                        Expect("every bar", primary, pins);

                        host.SetPinsOnAllBars(false);
                        Expect("main bar only", second, Array.Empty<string>());
                        Expect("main bar only", primary, pins);
                        await DrawBarAsync(host, second, File("second-bar-pins-none.png"), AppBarEdge.Bottom, flat, 1600, null, 800, null, null, sampleTray);

                        // A chosen few: they start as what the bar shows (nothing here), then two are switched on.
                        host.SetPinsShown(here, hereIsMain, PinsShown.Chosen);
                        Expect("chosen, none yet", second, Array.Empty<string>());
                        host.ShowPinOn(here, hereIsMain, pins[2], true);
                        host.ShowPinOn(here, hereIsMain, pins[0], true);
                        var two = new[] { pins[0], pins[2] };
                        Expect("two chosen", second, two);
                        Expect("two chosen on another bar", primary, pins);
                        await DrawBarAsync(host, second, File("second-bar-pins-chosen.png"), AppBarEdge.Bottom, flat, 1600, null, 800, null, null, sampleTray);

                        var bar = new TaskbarView(host, second, AppBarEdge.Bottom, flat);
                        report["pinnedAppsMenu"] = MenuLines(bar.PinnedAppsMenu(), "");
                        bar.Detach();
                        double pageHeight = 1800 + 210 * screens.Count;
                        var page = new SettingsView(host, SettingsPage.Taskbar) { Width = 880, Height = pageHeight };
                        await DrawAsync(page, 880, pageHeight, File("settings-taskbar-pins.png"));

                        // All of them and back again: the chosen ones are remembered.
                        host.SetPinsShown(here, hereIsMain, PinsShown.All);
                        Expect("all on one bar", second, pins);
                        host.SetPinsShown(here, hereIsMain, PinsShown.Chosen);
                        Expect("chosen again", second, two);

                        // One pin hidden from a bar that showed them all: the rest stay.
                        host.ShowPinOn(mainDevice, mainIsMain, pins[1], false);
                        var allButOne = pins.Where(p => p != pins[1]).ToList();
                        Expect("one hidden from the main bar", primary, allButOne);

                        // What the settings file holds reads back the same, and tidying keeps what still says something.
                        trial.Tidy();
                        var back = Settings.Parse(Settings.Write(Settings.Current)).Taskbar;
                        if (back.PinsOnAllMonitors || back.PinsFor(here, hereIsMain) != PinsShown.Chosen ||
                            !back.ShowsPin(here, hereIsMain, pins[2]) || back.ShowsPin(here, hereIsMain, pins[1]) ||
                            back.PinsFor(mainDevice, mainIsMain) != PinsShown.Chosen || back.ShowsPin(mainDevice, mainIsMain, pins[1]))
                        {
                            errors.Add("Pinned apps: the choices did not read back from the settings file");
                        }
                        report["pinnedAppsFileLines"] = Settings.Write(Settings.Current).Split('\n').Select(line => line.Trim())
                            .Where(line => line.StartsWith("\"pins", StringComparison.Ordinal)).ToList();

                        // Every bar again: a bar told to show none follows; a bar with a chosen few keeps them.
                        host.SetPinsShown(here, hereIsMain, PinsShown.None);
                        if (trial.ForMonitor(here)?.Pins != null) errors.Add("Pinned apps: a choice that matches the general one should not be stored");
                        host.SetPinsOnAllBars(true);
                        Expect("every bar again", second, pins);
                        Expect("every bar again, on a bar with a chosen few", primary, allButOne);
                        trial.Tidy();
                        if (trial.ForMonitor(here)?.ChosenPins is not { Count: 2 }) errors.Add("Pinned apps: the chosen ones were not kept for later");

                        var rough = Settings.Parse("""
                            {"taskbar":{"pinsOnAllMonitors":false,"monitors":{"display9":{"pins":"chosen","chosenPins":[null,"  ","C:\\a.lnk"]}}}}
                            """).Taskbar;
                        if (rough.ForMonitor(@"\\.\DISPLAY9")?.ChosenPins is not { Count: 1 } || !rough.ShowsPin("DISPLAY9", false, @"c:\A.LNK") ||
                            rough.PinsFor("DISPLAY8", false) != PinsShown.None || rough.PinsFor("DISPLAY8", true) != PinsShown.All)
                        {
                            errors.Add("Pinned apps: a hand-edited file was not read as meant");
                        }
                    }
                    finally
                    {
                        trial.PinsOnAllMonitors = everyBarBefore;
                        trial.Monitors = monitorsBefore;
                    }
                }
                else
                {
                    report["pinnedAppsMenu"] = "not tried: it needs a second display and three pinned apps";
                }

                // The search button: beside Start wherever Start is, on its own when Start is hidden, and gone
                // when switched off. Switched in memory only, the way the menus and the settings page do it,
                // and put back; nothing is saved.
                {
                    var searchTrial = Settings.Current.Taskbar;
                    bool searchBefore = searchTrial.ShowSearchButton;
                    var opensBefore = searchTrial.SearchButtonOpens;
                    try
                    {
                        var searchFound = new Dictionary<string, string>();
                        foreach (bool shown in new[] { true, false })
                        {
                            host.SetSearchButton(shown);
                            var searchCases = new (StartButtonPlacement Placement, string Parts)[]
                            {
                                (StartButtonPlacement.WithApps, shown ? "start + search" : "start"),
                                (StartButtonPlacement.Corner, shown ? "start + search" : "start"),
                                (StartButtonPlacement.End, shown ? "search + start" : "start"),
                                (StartButtonPlacement.Hidden, shown ? "search" : ""),
                            };
                            foreach (var (placement, parts) in searchCases)
                            {
                                var searchBar = new TaskbarView(host, primary, AppBarEdge.Bottom, flat, placement);
                                string searchParts = string.Join(" + ", searchBar.StartGroupParts);
                                searchBar.Detach();
                                string which = $"search {(shown ? "on" : "off")}, Start {placement}";
                                if (searchParts != parts) errors.Add($"Search button ({which}): the bar has [{searchParts}], not [{parts}]");
                                searchFound[which] = searchParts;
                            }
                        }
                        report["searchButton"] = searchFound;

                        // On a bar that is already up: it goes and comes back without the bar being made again.
                        host.SetSearchButton(true);
                        var inPlace = new TaskbarView(host, primary, AppBarEdge.Bottom, flat, StartButtonPlacement.WithApps);
                        host.SetSearchButton(false);
                        inPlace.ApplySearchButton();
                        string whenOff = string.Join(" + ", inPlace.StartGroupParts);
                        host.SetSearchButton(true);
                        inPlace.ApplySearchButton();
                        string whenOn = string.Join(" + ", inPlace.StartGroupParts);
                        inPlace.Detach();
                        if (whenOff != "start" || whenOn != "start + search")
                        {
                            errors.Add($"Search button: switched in place, the bar had [{whenOff}] when off and [{whenOn}] when on");
                        }

                        await DrawBarAsync(host, primary, File("search-with-apps.png"), AppBarEdge.Bottom, flat, 1600, StartButtonPlacement.WithApps, 800);
                        await DrawBarAsync(host, primary, File("search-corner-thin.png"), AppBarEdge.Top, 28, 1600, StartButtonPlacement.Corner, 800);
                        await DrawBarAsync(host, primary, File("search-end.png"), AppBarEdge.Bottom, flat, 1600, StartButtonPlacement.End, 800);
                        await DrawBarAsync(host, primary, File("search-start-hidden.png"), AppBarEdge.Bottom, flat, 1600, StartButtonPlacement.Hidden, 800);
                        await DrawBarAsync(host, primary, File("search-thick.png"), AppBarEdge.Bottom, 72, 1600, StartButtonPlacement.WithApps, 800);
                        await DrawBarAsync(host, primary, File("search-side.png"), AppBarEdge.Left, side, 900, StartButtonPlacement.Corner, 800);
                        await DrawBarAsync(host, primary, File("search-side-narrow-end.png"), AppBarEdge.Left, 56, 500, StartButtonPlacement.End, 800);

                        host.SetSearchButton(false);
                        await DrawBarAsync(host, primary, File("search-off.png"), AppBarEdge.Bottom, flat, 1600, StartButtonPlacement.WithApps, 800);

                        // The settings file: "off" is written and read back, and a file that says nothing means "on".
                        string searchLine = Settings.Write(Settings.Current).Split('\n').Select(line => line.Trim())
                            .FirstOrDefault(line => line.StartsWith("\"showSearchButton\"", StringComparison.Ordinal)) ?? "";
                        report["searchButtonFileLine"] = searchLine;
                        if (!searchLine.Contains("false", StringComparison.Ordinal) ||
                            Settings.Parse(Settings.Write(Settings.Current)).Taskbar.ShowSearchButton ||
                            !Settings.Parse("""{"taskbar":{}}""").Taskbar.ShowSearchButton)
                        {
                            errors.Add("Search button: its setting did not write to and read back from the settings file");
                        }

                        // What it opens: the choice is written and read back, a file that says nothing means
                        // Windows Search, and the button's menu ticks the one in force. Nothing here clicks the
                        // button: Windows Search must not open on the desktop during the test.
                        host.SetSearchOpens(SearchOpens.Menu);
                        var opensBar = new TaskbarView(host, primary, AppBarEdge.Bottom, flat);
                        report["searchButtonMenu"] = MenuLines(opensBar.SearchButtonMenu(), "");
                        opensBar.Detach();
                        string opensLine = Settings.Write(Settings.Current).Split('\n').Select(line => line.Trim())
                            .FirstOrDefault(line => line.StartsWith("\"searchButtonOpens\"", StringComparison.Ordinal)) ?? "";
                        report["searchButtonOpensFileLine"] = opensLine;
                        if (!opensLine.Contains("\"menu\"", StringComparison.Ordinal) ||
                            Settings.Parse(Settings.Write(Settings.Current)).Taskbar.SearchButtonOpens != SearchOpens.Menu ||
                            Settings.Parse("""{"taskbar":{}}""").Taskbar.SearchButtonOpens != SearchOpens.Windows)
                        {
                            errors.Add("Search button: what it opens did not write to and read back from the settings file");
                        }
                    }
                    finally
                    {
                        searchTrial.ShowSearchButton = searchBefore;
                        searchTrial.SearchButtonOpens = opensBefore;
                    }
                }

                // The clock: the day of the week has a line of its own wherever there is room for three.
                {
                    var clockFound = new Dictionary<string, string>();
                    var clockCases = new (AppBarEdge Edge, double Thickness, string? Lines)[]
                    {
                        (AppBarEdge.Top, 28, "time"),
                        (AppBarEdge.Bottom, 48, "time + date"),
                        (AppBarEdge.Bottom, 72, "time + day + date"),
                        (AppBarEdge.Left, 56, "time + date"),
                        // Close to the width "Wednesday" needs, so this one is only reported.
                        (AppBarEdge.Left, 76, null),
                        (AppBarEdge.Left, 120, "time + day + date"),
                    };
                    foreach (var (clockEdge, clockThickness, clockLines) in clockCases)
                    {
                        var clockBar = new TaskbarView(host, primary, clockEdge, clockThickness);
                        string clockHas = clockBar.ClockLines;
                        clockBar.Detach();
                        if (clockLines != null && clockHas != clockLines)
                        {
                            errors.Add($"Clock ({clockEdge}, {clockThickness} thick): it shows [{clockHas}], not [{clockLines}]");
                        }
                        clockFound[$"{clockEdge}, {clockThickness} thick"] = clockHas;
                    }
                    report["clockLines"] = clockFound;
                    await DrawBarAsync(host, primary, File("clock-side-76.png"), AppBarEdge.Left, 76, 500, null, 800);
                }

                // The weather, on bars of each kind.
                if (sampleWeather != null)
                {
                    await DrawBarAsync(host, primary, File("taskbar-weather.png"), AppBarEdge.Bottom, flat, 1600, null, 1200, null, null, sampleTray, sampleWeather);
                    await DrawBarAsync(host, primary, File("taskbar-thin-weather.png"), AppBarEdge.Top, 28, 1600, null, 1200, null, null, sampleTray, sampleWeather);
                    await DrawBarAsync(host, primary, File("taskbar-thick-weather.png"), AppBarEdge.Bottom, 72, 1600, null, 1200, null, 24, sampleTray, sampleWeather);
                    await DrawBarAsync(host, primary, File("sidebar-weather.png"), AppBarEdge.Left, side, 900, null, 1200, null, null, sampleTray, sampleWeather);
                    await DrawBarAsync(host, primary, File("sidebar-narrow-weather.png"), AppBarEdge.Right, 56, 900, null, 1200, null, null, sampleTray, sampleWeather);
                }

                // The Start button's designs on bars of each kind, and the settings page as it stands for
                // them. The design is changed in memory only and put back; nothing is saved.
                {
                    var trial = Settings.Current.Taskbar;
                    var before = (trial.StartButtonStyle, trial.StartButtonScale, trial.StartButtonColor,
                        trial.StartButtonBackground, trial.StartButtonBackgroundColor, trial.StartButtonText,
                        trial.StartButtonPicture);
                    void Design(StartButtonStyle style, int scale = 100, string color = "",
                        StartButtonBackground background = StartButtonBackground.None, string backgroundColor = "",
                        string word = "Start", string picture = "")
                    {
                        trial.StartButtonStyle = style;
                        trial.StartButtonScale = scale;
                        trial.StartButtonColor = color;
                        trial.StartButtonBackground = background;
                        trial.StartButtonBackgroundColor = backgroundColor;
                        trial.StartButtonText = word;
                        trial.StartButtonPicture = picture;
                    }
                    async Task Page(string name)
                    {
                        var view = new SettingsView(host, SettingsPage.Appearance) { Width = 880, Height = 1120 };
                        await DrawAsync(view, 880, 1120, File(name));
                    }

                    try
                    {
                        Design(StartButtonStyle.Dots, background: StartButtonBackground.Circle);
                        await DrawBarAsync(host, primary, File("start-dots-circle.png"), AppBarEdge.Bottom, flat, 700, StartButtonPlacement.Corner, 700);
                        await DrawBarAsync(host, primary, File("start-dots-circle-thin.png"), AppBarEdge.Top, 28, 700, StartButtonPlacement.Corner, 700);
                        await Page("settings-appearance-circle.png");

                        Design(StartButtonStyle.Text, word: "Menu");
                        await DrawBarAsync(host, primary, File("start-word.png"), AppBarEdge.Bottom, flat, 700, StartButtonPlacement.Corner, 700);
                        // A word as long as it may be, on the narrowest side bar: it has to shrink to fit.
                        Design(StartButtonStyle.Text, word: "Everything I own", background: StartButtonBackground.Rounded);
                        await DrawBarAsync(host, primary, File("start-word-narrow-side.png"), AppBarEdge.Left, 56, 500, StartButtonPlacement.Corner, 700);
                        await DrawBarAsync(host, primary, File("start-word-long.png"), AppBarEdge.Bottom, flat, 700, StartButtonPlacement.Corner, 700);
                        await Page("settings-appearance-word.png");

                        Design(StartButtonStyle.Picture, picture: startPicture);
                        await DrawBarAsync(host, primary, File("start-picture-bar.png"), AppBarEdge.Bottom, 56, 700, StartButtonPlacement.Corner, 700);
                        await DrawBarAsync(host, primary, File("start-picture-side.png"), AppBarEdge.Left, side, 500, StartButtonPlacement.Corner, 700);
                        await Page("settings-appearance-picture.png");
                        // A picture that is not there: the button must still be something to click.
                        Design(StartButtonStyle.Picture, picture: File("no-such-picture.png"));
                        await DrawBarAsync(host, primary, File("start-picture-missing.png"), AppBarEdge.Bottom, flat, 700, StartButtonPlacement.Corner, 700);
                        await Page("settings-appearance-picture-missing.png");

                        // As large as it goes, on the thinnest bar and on a thick one.
                        Design(StartButtonStyle.Hexagon, scale: 160, color: "#FFFFB703");
                        await DrawBarAsync(host, primary, File("start-large-thin.png"), AppBarEdge.Top, 28, 700, StartButtonPlacement.Corner, 700);
                        await DrawBarAsync(host, primary, File("start-large-thick.png"), AppBarEdge.Bottom, 72, 700, StartButtonPlacement.Corner, 700);
                        Design(StartButtonStyle.Home, scale: 60, color: "#FF101010",
                            background: StartButtonBackground.Rounded, backgroundColor: "#FFFFFFFF");
                        await DrawBarAsync(host, primary, File("start-small-side.png"), AppBarEdge.Left, side, 500, null, 700);
                    }
                    finally
                    {
                        (trial.StartButtonStyle, trial.StartButtonScale, trial.StartButtonColor,
                            trial.StartButtonBackground, trial.StartButtonBackgroundColor, trial.StartButtonText,
                            trial.StartButtonPicture) = before;
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add("Taskbar: " + ex);
            }

            // Window previews: the popup's layout for the app with the most windows. The live pictures are
            // drawn by the compositor straight onto the screen, so only the boxes they go in show up here.
            try
            {
                using var apps = new TaskbarApps(host, IntPtr.Zero, combine: true);
                var busiest = apps.Items.OrderByDescending(b => b.Count).FirstOrDefault();
                if (busiest != null && busiest.Count > 0)
                {
                    var windows = busiest.Windows.ToList();
                    report["previewApp"] = busiest.AppName;
                    report["previewWindows"] = windows.Count;
                    report["previewPictureBoxes"] = new[]
                    {
                        await DrawPreviewAsync(windows, Path.Combine(directory, "previews.png"), stacked: false, 1600),
                        await DrawPreviewAsync(windows, Path.Combine(directory, "previews-narrow.png"), stacked: false, 500),
                        await DrawPreviewAsync(windows, Path.Combine(directory, "previews-side.png"), stacked: true, 900),
                    };
                }
            }
            catch (Exception ex)
            {
                errors.Add("Window previews: " + ex);
            }

            // Every open window should have handed over an icon by now.
            try
            {
                var open = host.TaskView.Cast<ManagedShell.WindowsTasks.ApplicationWindow>().ToList();
                var arrivedAfter = new Dictionary<ManagedShell.WindowsTasks.ApplicationWindow, long>();
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 8000 && arrivedAfter.Count < open.Count)
                {
                    foreach (var window in open)
                    {
                        // Asking for the icon is what starts it loading.
                        if (!arrivedAfter.ContainsKey(window) && window.Icon != null)
                        {
                            arrivedAfter[window] = clock.ElapsedMilliseconds;
                        }
                    }
                    await Task.Delay(200);
                }

                report["windowIcons"] = open.Select(w => new
                {
                    title = w.Title,
                    program = Path.GetFileName(w.WinFileName ?? ""),
                    storeApp = w.IsUWP,
                    appId = w.AppUserModelID,
                    iconAfterMs = arrivedAfter.TryGetValue(w, out long ms) ? ms : -1,
                }).ToList();
            }
            catch (Exception ex)
            {
                errors.Add("Window icons: " + ex);
            }

            await CheckUpdatesAsync(host, directory, report, errors);

            // Start menu
            try
            {
                var menuSettings = Settings.Current.StartMenu;
                var menu = new StartMenuView(host, () => { })
                {
                    Width = menuSettings.Width,
                    Height = menuSettings.Height,
                };
                var stage = new Stage(menu, menuSettings.Width + 40, menuSettings.Height + 40);
                await Task.Delay(3000);
                stage.Save(Path.Combine(directory, "startmenu.png"));
                report["startMenuAppCount"] = menu.VisibleAppCount;

                var searches = new Dictionary<string, List<string>>();
                foreach (string query in new[] { "calc", "note", "term", "paint", "vsc", "zzzzqq" })
                {
                    menu.SetQuery(query);
                    searches[query] = menu.TopResults(3);
                }
                report["searchResults"] = searches;

                menu.SetQuery("note");
                await Task.Delay(1500);
                stage.Save(Path.Combine(directory, "startmenu-search.png"));

                stage.Close();
                menu.Detach();

                // The list of places, arranged other ways. Changed in memory only and put back; nothing is saved.
                var before = (menuSettings.Places, menuSettings.ShowPlaces, menuSettings.PlacesSide,
                    menuSettings.PlacesWidth, menuSettings.PlacesRowHeight, menuSettings.PlacesShowIcons);
                async Task DrawMenuAsync(string name)
                {
                    var view = new StartMenuView(host, () => { }) { Width = menuSettings.Width, Height = menuSettings.Height };
                    var onStage = new Stage(view, menuSettings.Width + 40, menuSettings.Height + 40);
                    try
                    {
                        await Task.Delay(1500);
                        onStage.Save(Path.Combine(directory, name));
                    }
                    finally
                    {
                        onStage.Close();
                        view.Detach();
                    }
                }
                try
                {
                    // On the left, wide and tall, with a divider and an entry whose name is too long to fit.
                    var places = StartMenuSettings.DefaultPlaces();
                    places.Insert(3, PlaceEntry.Divider());
                    places.Add(PlaceEntry.Divider());
                    places.Add(PlaceEntry.Of("A place with a name far too long to fit in its column", "C:\\", 0xE774));
                    menuSettings.Places = places;
                    menuSettings.PlacesSide = PlacesSide.Left;
                    menuSettings.PlacesWidth = 240;
                    menuSettings.PlacesRowHeight = 48;
                    await DrawMenuAsync("startmenu-places-left.png");
                    var page = new SettingsView(host, SettingsPage.StartMenu) { Width = 880, Height = 1760 };
                    await DrawAsync(page, 880, 1760, Path.Combine(directory, "settings-startmenu-divider.png"));

                    // Narrow, short rows, names only, and more of them than fit: the list should scroll.
                    menuSettings.Places = Enumerable.Range(1, 3).SelectMany(_ => StartMenuSettings.DefaultPlaces()).ToList();
                    menuSettings.PlacesSide = PlacesSide.Right;
                    menuSettings.PlacesWidth = 140;
                    menuSettings.PlacesRowHeight = 28;
                    menuSettings.PlacesShowIcons = false;
                    await DrawMenuAsync("startmenu-places-names.png");

                    menuSettings.ShowPlaces = false;
                    await DrawMenuAsync("startmenu-no-places.png");

                    // An empty list, as the settings page shows it.
                    menuSettings.ShowPlaces = true;
                    menuSettings.Places = new List<PlaceEntry>();
                    var empty = new SettingsView(host, SettingsPage.StartMenu) { Width = 880, Height = 1300 };
                    await DrawAsync(empty, 880, 1300, Path.Combine(directory, "settings-startmenu-empty.png"));
                }
                finally
                {
                    (menuSettings.Places, menuSettings.ShowPlaces, menuSettings.PlacesSide,
                        menuSettings.PlacesWidth, menuSettings.PlacesRowHeight, menuSettings.PlacesShowIcons) = before;
                }
            }
            catch (Exception ex)
            {
                errors.Add("Start menu: " + ex);
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex.ToString());
        }
        finally
        {
            try
            {
                host?.Dispose();
            }
            catch (Exception ex)
            {
                errors.Add("Shutdown: " + ex);
            }
        }

        report["bindingErrors"] = bindingErrors.Messages;
        report["errors"] = errors;

        try
        {
            File.WriteAllText(Path.Combine(directory, "selftest.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Error("Could not write the self-test report", ex);
            return 1;
        }

        Log.Info($"Self-test finished with {errors.Count} error(s) and {bindingErrors.Messages.Count} binding warning(s)");
        return errors.Count == 0 ? 0 : 1;
    }

    /// <summary>Draws one taskbar into a picture: docked to the given edge of a stage of the given length.</summary>
    private static async Task DrawBarAsync(ShellHost host, AppBarScreen screen, string file, AppBarEdge edge,
        double thickness, double length, StartButtonPlacement? startButton, int waitMs, int? iconSize = null,
        int? trayIconSize = null, System.Collections.IEnumerable? sampleTray = null, WeatherReport? sampleWeather = null)
    {
        var view = new TaskbarView(host, screen, edge, thickness, startButton, iconSize, trayIconSize);
        if (sampleTray != null) view.ShowSampleTray(sampleTray);
        if (sampleWeather != null) view.ShowSampleWeather(sampleWeather);
        Stage stage;
        if (edge is AppBarEdge.Left or AppBarEdge.Right)
        {
            view.Width = thickness;
            view.HorizontalAlignment = edge == AppBarEdge.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            stage = new Stage(view, thickness + 80, length);
        }
        else
        {
            view.Height = thickness;
            view.VerticalAlignment = edge == AppBarEdge.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            stage = new Stage(view, length, thickness + 36);
        }

        try
        {
            await Task.Delay(waitMs);
            stage.Save(file);
        }
        finally
        {
            stage.Close();
            view.Detach();
        }
    }

    // A forecast reply and a place-search reply as the weather service sends them, saved for the test.
    private const string SampleForecast = """
        {"current":{"time":"2026-11-11T12:45","interval":900,"temperature_2m":62.7,"relative_humidity_2m":40,
        "apparent_temperature":57.1,"is_day":1,"weather_code":2,"wind_speed_10m":6.8},
        "daily":{"time":["2026-11-11","2026-11-12","2026-11-13","2026-11-14","2026-11-15"],
        "weather_code":[3,61,71,3,95],"temperature_2m_max":[63.7,59.1,46.9,46.9,49.5],
        "temperature_2m_min":[47.8,46.9,40.0,38.1,38.7],"precipitation_probability_max":[2,85,85,15,60]}}
        """;

    private const string SamplePlaces = """
        {"results":[
        {"id":1,"name":"Springfield","latitude":39.80172,"longitude":-89.64371,"country":"United States","admin1":"Illinois"},
        {"id":2,"name":"Springfield","latitude":37.21533,"longitude":-93.29824,"country":"United States","admin1":"Missouri"},
        {"id":3,"name":"Nowhere"}]}
        """;

    /// <summary>Draws something into a picture, on a stage of the given size.</summary>
    private static async Task DrawAsync(FrameworkElement content, double width, double height, string file)
    {
        var stage = new Stage(content, width, height);
        try
        {
            await Task.Delay(400);
            stage.Save(file);
        }
        finally
        {
            stage.Close();
        }
    }

    private static void SavePng(BitmapSource image, string file)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }

    /// <summary>The app icon at every size its file holds, pixel for pixel, on dark, light and colored ground.</summary>
    private static FrameworkElement AppIconSheet()
    {
        StackPanel Strip(Color ground, params int[] sizes)
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Background = Ui.Brush(ground) };
            foreach (int size in sizes)
            {
                var image = new Image
                {
                    Source = AppIcon.Render(size),
                    Width = size,
                    Height = size,
                    Stretch = Stretch.None,
                    Margin = new Thickness(10),
                    VerticalAlignment = VerticalAlignment.Bottom,
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
                strip.Children.Add(image);
            }
            return strip;
        }

        var sheet = new StackPanel { Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Left };
        sheet.Children.Add(Strip(Color.FromRgb(0x20, 0x20, 0x20), 256, 128, 64, 48, 40, 32, 24, 20, 16));
        var lower = new StackPanel { Orientation = Orientation.Horizontal };
        lower.Children.Add(Strip(Color.FromRgb(0xF3, 0xF3, 0xF3), 64, 48, 32, 24, 20, 16));
        lower.Children.Add(Strip(Color.FromRgb(0x2A, 0x74, 0x8F), 64, 48, 32, 24, 20, 16));
        sheet.Children.Add(lower);
        return sheet;
    }

    /// <summary>Icons from the icon font with their codes underneath, to see that none comes out as an empty box.</summary>
    private static FrameworkElement IconSheet(IEnumerable<int> codes)
    {
        var sheet = new WrapPanel
        {
            Width = 8 * 66,
            Margin = new Thickness(12),
            Background = Ui.Brush(Color.FromRgb(0x20, 0x20, 0x20)),
            VerticalAlignment = VerticalAlignment.Top,
        };
        foreach (int code in codes)
        {
            var cell = new StackPanel { Width = 66, Height = 62 };
            cell.Children.Add(new TextBlock
            {
                Text = ((char)code).ToString(),
                FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
                FontSize = 22,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0),
            });
            cell.Children.Add(new TextBlock
            {
                Text = code.ToString("X4"),
                Foreground = Brushes.White,
                Opacity = 0.6,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 0),
            });
            sheet.Children.Add(cell);
        }
        return sheet;
    }

    /// <summary>Which of these characters neither icon font has a shape for.</summary>
    private static List<int> MissingGlyphs(IEnumerable<int> codes)
    {
        var fonts = new List<GlyphTypeface>();
        foreach (string name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            var face = new Typeface(new FontFamily(name), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (face.TryGetGlyphTypeface(out var glyphs)) fonts.Add(glyphs);
        }
        return codes.Where(code => !fonts.Any(font => font.CharacterToGlyphMap.ContainsKey(code))).ToList();
    }

    /// <summary>Every Start button design, drawn four ways, over a dark bar's color.</summary>
    private static FrameworkElement StartButtonSheet(string picture)
    {
        var ways = new (Action<TaskbarSettings> Set, double Size)[]
        {
            (_ => { }, 20),
            (settings => settings.StartButtonBackground = StartButtonBackground.Circle, 20),
            (settings =>
            {
                settings.StartButtonBackground = StartButtonBackground.Rounded;
                settings.StartButtonBackgroundColor = "#FFFFFFFF";
                settings.StartButtonColor = "#FF1C1C1C";
            }, 28),
            (settings => settings.StartButtonColor = "#FFFFB703", 36),
        };

        var sheet = new StackPanel
        {
            Margin = new Thickness(12),
            Background = Ui.Brush(Color.FromArgb(0xB0, 0x10, 0x10, 0x10)),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var (set, size) in ways)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (style, name) in StartGlyph.Designs)
            {
                var settings = new TaskbarSettings { StartButtonStyle = style, StartButtonPicture = picture };
                set(settings);
                var glyph = StartGlyph.Build(settings, size, 44, Colors.White, 80);
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                glyph.VerticalAlignment = VerticalAlignment.Center;
                glyph.Margin = new Thickness(0, 0, 0, 16);

                var cell = new Grid { Width = 87, Height = 86 };
                cell.Children.Add(glyph);
                cell.Children.Add(new TextBlock
                {
                    Text = name,
                    Foreground = Brushes.White,
                    Opacity = 0.7,
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 4),
                });
                line.Children.Add(cell);
            }
            sheet.Children.Add(line);
        }
        return sheet;
    }

    /// <summary>Every weather picture, by day and by night, as large as the forecast panel draws them.</summary>
    private static FrameworkElement WeatherIconSheet()
    {
        var sheet = new UniformGrid { Columns = 5, Margin = new Thickness(12) };
        foreach (bool day in new[] { true, false })
        {
            foreach (var kind in Enum.GetValues<WeatherKind>())
            {
                var cell = new StackPanel { Margin = new Thickness(6) };
                cell.Children.Add(new Viewbox { Width = 54, Height = 54, Child = WeatherIcons.Draw(kind, day) });
                cell.Children.Add(new TextBlock
                {
                    Text = kind + (day ? "" : ", night"),
                    Foreground = Brushes.White,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0),
                });
                sheet.Children.Add(cell);
            }
        }
        return sheet;
    }

    /// <summary>A menu written out as lines of text, one level of indent per submenu.</summary>
    private static List<string> MenuLines(IEnumerable<MenuEntry> entries, string indent)
    {
        var lines = new List<string>();
        foreach (var entry in entries)
        {
            lines.Add(indent + (entry.Header ?? "-") + (entry.Checked ? "  [ticked]" : ""));
            if (entry.Children != null) lines.AddRange(MenuLines(entry.Children, indent + "    "));
        }
        return lines;
    }

    /// <summary>
    /// Draws the preview popup's contents for these windows and returns how many picture boxes it laid out
    /// (one per window is right).
    /// </summary>
    private static async Task<int> DrawPreviewAsync(List<ManagedShell.WindowsTasks.ApplicationWindow> windows,
        string file, bool stacked, double maxLength)
    {
        // Stand-in window shapes, wide and tall by turns, since nothing is asked of the compositor here.
        var tiles = windows.Select((w, i) => new WindowTile(w)
        {
            SourceSize = i % 2 == 0 ? new Size(1600, 900) : new Size(900, 1200),
        }).ToList();

        var view = new ThumbnailView
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        view.SetTiles(tiles, stacked, maxLength);
        var stage = stacked ? new Stage(view, 760, maxLength + 40) : new Stage(view, maxLength + 40, 520);
        try
        {
            await Task.Delay(600);
            stage.Save(file);
            return view.PictureAreas(view).Count;
        }
        finally
        {
            stage.Close();
        }
    }

    /// <summary>The app list fills in the background; wait until it stops growing.</summary>
    private static async Task WaitForAppListAsync(ShellHost host)
    {
        int last = -1;
        int stableFor = 0;
        for (int i = 0; i < 40 && stableFor < 4; i++)
        {
            await Task.Delay(250);
            int count = host.Apps.Apps.Count;
            stableFor = count == last && count > 0 ? stableFor + 1 : 0;
            last = count;
        }
    }

    /// <summary>
    /// Pins are stored as the app's path, so check that a stored path can be turned back into the same app.
    /// </summary>
    private static List<object> SampleApps(ShellHost host)
    {
        var result = new List<object>();
        var apps = host.Apps.Apps.ToList();
        if (apps.Count == 0) return result;

        int step = Math.Max(1, apps.Count / 8);
        for (int i = 0; i < apps.Count && result.Count < 8; i += step)
        {
            var app = apps[i];
            string? path = AppCatalog.PinPath(app);
            bool reloads = false;
            string? reloadedName = null;
            try
            {
                if (!string.IsNullOrEmpty(path))
                {
                    var again = new ShellItem(path);
                    reloads = again.Loaded;
                    reloadedName = reloads ? again.DisplayName : null;
                    again.Dispose();
                }
            }
            catch (Exception ex)
            {
                reloadedName = "error: " + ex.Message;
            }

            result.Add(new
            {
                name = app.DisplayName,
                path,
                file = AppCatalog.FileOf(path),
                reloads,
                sameName = reloadedName == app.DisplayName,
            });
        }
        return result;
    }

    /// <summary>A window far off-screen that holds the content over a colourful backdrop, so see-through parts show.</summary>
    private sealed class Stage
    {
        private readonly Window _window;
        private readonly Grid _root;

        public Stage(FrameworkElement content, double width, double height)
        {
            var backdrop = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(0x1B, 0x4F, 0x9C), 0.0),
                    new GradientStop(Color.FromRgb(0x7A, 0x2F, 0x8F), 0.5),
                    new GradientStop(Color.FromRgb(0xD9, 0x6B, 0x3C), 1.0),
                },
                new Point(0, 0), new Point(1, 1));
            backdrop.Freeze();

            _root = new Grid { Width = width, Height = height, Background = backdrop };
            _root.Children.Add(content);

            _window = new Window
            {
                Title = "ClearStart self-test",
                Left = -30000,
                Top = -30000,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = _root,
            };
            _window.Show();
        }

        public void Save(string path)
        {
            _root.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)_root.Width, (int)_root.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(_root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }

        public void Close() => _window.Close();
    }

    /// <summary>Collects the warnings WPF prints when a binding in the UI points at something that is not there.</summary>
    private sealed class BindingErrorListener : TraceListener
    {
        private readonly StringBuilder _line = new();

        public List<string> Messages { get; } = new();

        public override void Write(string? message) => _line.Append(message);

        public override void WriteLine(string? message)
        {
            _line.Append(message);
            Messages.Add(_line.ToString());
            _line.Clear();
        }
    }
}
