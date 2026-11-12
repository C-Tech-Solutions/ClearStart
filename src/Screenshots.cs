using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ManagedShell.AppBar;
using ManagedShell.ShellFolders;

namespace ClearStart;

/// <summary>
/// "ClearStart.exe --screenshots DIR": draws the pictures the documentation uses.
///
/// Unlike the self-test, which draws the desktop of whoever runs it, this shows nobody's own PC: the
/// settings are the defaults (the settings file is not read), the apps are the ones every copy of
/// Windows comes with, the name in the start menu is made up, the weather is a saved sample, and the
/// open windows are left out. Nothing is shown on the desktop and nothing is saved but the pictures.
/// </summary>
internal static partial class SelfTest
{
    private const double DesktopWidth = 1440;
    private const double DesktopHeight = 810;

    /// <summary>Apps that come with Windows, by the names its app list gives them. The stand-in app list is whichever of these are installed.</summary>
    private static readonly HashSet<string> StockApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Calculator", "Calendar", "Camera", "Character Map", "Clock", "Command Prompt", "Control Panel", "Disk Cleanup",
        "Feedback Hub", "File Explorer", "Get Help", "Magnifier", "Mail", "Maps", "Media Player", "Microsoft Edge",
        "Microsoft Store", "Microsoft To Do", "Narrator", "Notepad", "On-Screen Keyboard", "Outlook", "Paint",
        "Phone Link", "Photos", "Quick Assist", "Registry Editor", "Remote Desktop Connection", "Run", "Settings",
        "Snipping Tool", "Sound Recorder", "Steps Recorder", "Sticky Notes", "System Information", "Task Manager",
        "Terminal", "Voice access", "Weather", "Windows Backup", "Windows PowerShell", "Windows Security", "Windows Tools",
    };

    private static readonly string[] StandInTaskbarPins =
    {
        "File Explorer", "Microsoft Edge", "Terminal", "Notepad", "Paint", "Calculator", "Photos", "Media Player",
        "Snipping Tool", "Microsoft Store", "Settings",
    };

    private static readonly string[] StandInStartPins =
    {
        "Microsoft Edge", "File Explorer", "Photos", "Settings", "Calculator", "Notepad", "Paint", "Terminal",
        "Microsoft Store", "Media Player", "Clock", "Camera", "Snipping Tool", "Sticky Notes", "Weather", "Maps",
        "Outlook", "Microsoft To Do", "Sound Recorder", "Task Manager",
    };

    public static async Task<int> ScreenshotsAsync(string directory)
    {
        ShellHost? host = null;
        var failed = new List<string>();
        string File(string name) => Path.Combine(directory, name);

        try
        {
            Settings.UseDefaults();
            host = new ShellHost(HostMode.SelfTest, directory) { UserName = "Alex" };
            host.Network.ReadNow();
            await WaitForAppListAsync(host);

            // The stand-in app list, and pins chosen from it.
            var stock = host.Apps.Everything.Where(app => StockApps.Contains(app.DisplayName ?? "")).ToList();
            host.Apps.ShowOnly(stock);
            var pathOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in stock)
            {
                string? path = AppCatalog.PinPath(app);
                if (!string.IsNullOrEmpty(path)) pathOf.TryAdd(app.DisplayName, path);
            }
            List<string> PathsOf(IEnumerable<string> names) =>
                names.Where(pathOf.ContainsKey).Select(name => pathOf[name]).ToList();

            Settings.Current.Taskbar.Pinned = PathsOf(StandInTaskbarPins);
            Settings.Current.StartMenu.Pinned = PathsOf(StandInStartPins);
            host.Pins.Reload();

            var trayItems = PathsOf(new[] { "Windows Security", "Phone Link", "Quick Assist" })
                .Select(path => new ShellItem(path)).Where(item => item.Loaded).ToList();
            // Stand-ins for the tray icons behind the arrow beside the tray.
            var hiddenItems = PathsOf(new[] { "Sticky Notes", "Clock", "Snipping Tool", "Task Manager", "Magnifier", "Voice access" })
                .Select(path => new ShellItem(path)).Where(item => item.Loaded).ToList();
            foreach (var item in trayItems.Concat(hiddenItems)) _ = item.LargeIcon;

            // Icons load in the background; give them time before anything is drawn.
            await Task.Delay(4000);

            var tray = trayItems.Select(item => new SampleTrayIcon(item)).ToList();
            var weather = WeatherService.Parse(SampleForecast, "Springfield, Illinois",
                fahrenheit: true, new DateTime(2026, 11, 11, 12, 45, 0));
            var screen = AppBarScreen.FromAllScreens().FirstOrDefault(s => s.Primary) ?? AppBarScreen.FromAllScreens().First();

            // A few of the pinned apps are shown as open: two windows, one in front, one behind.
            List<SampleApp> Apps(int count) => host.Pins.Taskbar.Take(count).Select((pin, index) => index switch
            {
                0 => new SampleApp(pin, 2, AppState.Inactive),
                1 => new SampleApp(pin, 1, AppState.Active),
                2 => new SampleApp(pin, 1, AppState.Inactive),
                _ => new SampleApp(pin, 0, AppState.Idle),
            }).ToList();

            // "apps" is how many of the pinned apps the bar gets: a thick bar's bigger buttons need fewer
            // of them to stay centred on a desktop this narrow.
            TaskbarView Bar(AppBarEdge edge, double thickness, StartButtonPlacement? start = null, int apps = 99)
            {
                var view = new TaskbarView(host, screen, edge, thickness, start, null, null);
                view.ShowSampleApps(Apps(apps));
                view.ShowSampleTray(tray);
                view.ShowSampleWeather(weather);
                if (edge is AppBarEdge.Left or AppBarEdge.Right)
                {
                    view.Width = thickness;
                    view.HorizontalAlignment = edge == AppBarEdge.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                }
                else
                {
                    view.Height = thickness;
                    view.VerticalAlignment = edge == AppBarEdge.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                }
                return view;
            }

            async Task Try(string name, Func<Task> draw)
            {
                try
                {
                    await draw();
                }
                catch (Exception ex)
                {
                    failed.Add(name);
                    Log.Error("Could not draw " + name, ex);
                }
            }

            // A whole desktop: a bar on one edge and, if asked, the start menu open where it really opens
            // (against the bar, 12 from it, centred along it: see StartMenuWindow.Place).
            async Task Desktop(string name, AppBarEdge edge, double thickness, bool menuOpen, string query = "")
            {
                var bar = Bar(edge, thickness);
                var desktop = new Grid();
                desktop.Children.Add(bar);

                StartMenuView? menu = null;
                if (menuOpen)
                {
                    var size = Settings.Current.StartMenu;
                    menu = new StartMenuView(host, () => { }) { Width = size.Width, Height = size.Height };
                    double gap = thickness + 12;
                    (menu.HorizontalAlignment, menu.VerticalAlignment, menu.Margin) = edge switch
                    {
                        AppBarEdge.Left => (HorizontalAlignment.Left, VerticalAlignment.Center, new Thickness(gap, 0, 0, 0)),
                        AppBarEdge.Right => (HorizontalAlignment.Right, VerticalAlignment.Center, new Thickness(0, 0, gap, 0)),
                        AppBarEdge.Top => (HorizontalAlignment.Center, VerticalAlignment.Top, new Thickness(0, gap, 0, 0)),
                        _ => (HorizontalAlignment.Center, VerticalAlignment.Bottom, new Thickness(0, 0, 0, gap)),
                    };
                    desktop.Children.Add(menu);
                }

                var stage = new Stage(desktop, DesktopWidth, DesktopHeight);
                try
                {
                    await Task.Delay(2500);
                    if (menu != null && query.Length > 0)
                    {
                        menu.SetQuery(query);
                        await Task.Delay(1200);
                    }
                    stage.Save(File(name));
                }
                finally
                {
                    stage.Close();
                    bar.Detach();
                    menu?.Detach();
                }
            }

            // One bar on a strip of desktop.
            async Task Strip(string name, AppBarEdge edge, double thickness, StartButtonPlacement? start = null, int apps = 99)
            {
                var bar = Bar(edge, thickness, start, apps);
                var stage = new Stage(bar, DesktopWidth, thickness + 40);
                try
                {
                    await Task.Delay(1200);
                    stage.Save(File(name));
                }
                finally
                {
                    stage.Close();
                    bar.Detach();
                }
            }

            async Task Page(string name, SettingsPage page, double height)
            {
                var view = new SettingsView(host, page) { Width = 880, Height = height };
                await DrawAsync(view, 880, height, File(name));
            }

            var taskbar = Settings.Current.Taskbar;

            await Try("hero.png", () => Desktop("hero.png", AppBarEdge.Bottom, taskbar.Height, menuOpen: true));
            await Try("desktop-search.png", () => Desktop("desktop-search.png", AppBarEdge.Bottom, taskbar.Height, menuOpen: true, "pa"));
            await Try("desktop-side.png", () => Desktop("desktop-side.png", AppBarEdge.Left, 76, menuOpen: true));
            await Try("desktop-top.png", () => Desktop("desktop-top.png", AppBarEdge.Top, 28, menuOpen: false));

            await Try("bar-default.png", () => Strip("bar-default.png", AppBarEdge.Bottom, taskbar.Height));
            await Try("bar-thin.png", () => Strip("bar-thin.png", AppBarEdge.Top, 28));
            await Try("bar-thick.png", () => Strip("bar-thick.png", AppBarEdge.Bottom, 72, apps: 7));
            await Try("bar-start-corner.png", () => Strip("bar-start-corner.png", AppBarEdge.Bottom, taskbar.Height, StartButtonPlacement.Corner));
            await Try("bar-start-end.png", () => Strip("bar-start-end.png", AppBarEdge.Bottom, taskbar.Height, StartButtonPlacement.End));

            // Window titles next to the icons, at the start of the bar.
            await Try("bar-titles.png", async () =>
            {
                taskbar.ShowLabels = true;
                taskbar.Alignment = Alignment.Left;
                try
                {
                    await Strip("bar-titles.png", AppBarEdge.Bottom, taskbar.Height);
                }
                finally
                {
                    taskbar.ShowLabels = false;
                    taskbar.Alignment = Alignment.Center;
                }
            });

            // Dressed up: a frosted, tinted bar and a Start button of another design on a filled circle.
            await Try("bar-styled.png", async () =>
            {
                var before = (taskbar.Appearance, taskbar.Tint, taskbar.StartButtonStyle, taskbar.StartButtonBackground,
                    taskbar.StartButtonBackgroundColor, taskbar.StartButtonScale);
                taskbar.Appearance = Appearance.Acrylic;
                taskbar.Tint = "#8C12202E";
                taskbar.StartButtonStyle = StartButtonStyle.Sparkle;
                taskbar.StartButtonBackground = StartButtonBackground.Circle;
                taskbar.StartButtonBackgroundColor = "#3A86FF";
                taskbar.StartButtonScale = 120;
                try
                {
                    await Strip("bar-styled.png", AppBarEdge.Bottom, 52, apps: 9);
                }
                finally
                {
                    (taskbar.Appearance, taskbar.Tint, taskbar.StartButtonStyle, taskbar.StartButtonBackground,
                        taskbar.StartButtonBackgroundColor, taskbar.StartButtonScale) = before;
                }
            });

            await Try("start-menu.png", async () =>
            {
                var size = Settings.Current.StartMenu;
                var menu = new StartMenuView(host, () => { }) { Width = size.Width, Height = size.Height };
                var stage = new Stage(menu, size.Width + 40, size.Height + 40);
                try
                {
                    await Task.Delay(2500);
                    stage.Save(File("start-menu.png"));
                }
                finally
                {
                    stage.Close();
                    menu.Detach();
                }
            });

            await Try("settings-appearance.png", () => Page("settings-appearance.png", SettingsPage.Appearance, 900));
            await Try("settings-taskbar.png", () => Page("settings-taskbar.png", SettingsPage.Taskbar, 950));
            await Try("settings-start-menu.png", () => Page("settings-start-menu.png", SettingsPage.StartMenu, 1120));
            await Try("settings-weather.png", async () =>
            {
                var place = Settings.Current.Weather;
                place.Place = "Springfield, Illinois";
                place.Latitude = 39.8;
                place.Longitude = -89.64;
                try
                {
                    await Page("settings-weather.png", SettingsPage.Weather, 620);
                }
                finally
                {
                    place.Place = "";
                    place.Latitude = null;
                    place.Longitude = null;
                }
            });

            await Try("weather.png", async () =>
            {
                var flyout = WeatherFlyout.Build(weather, null);
                flyout.HorizontalAlignment = HorizontalAlignment.Center;
                flyout.VerticalAlignment = VerticalAlignment.Center;
                await DrawAsync(flyout, 380, 400, File("weather.png"));
            });

            await Try("tray-overflow.png", async () =>
            {
                var panel = new TrayOverflowView(hiddenItems.Select(item => new SampleTrayIcon(item)).ToList(), 16, null)
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                await DrawAsync(panel, 260, 150, File("tray-overflow.png"));
            });

            await Try("color-wheel.png", async () =>
            {
                var wheel = new ColorWheel();
                wheel.Show(Color.FromRgb(0x3A, 0x86, 0xFF));
                var panel = ColorWheel.InPanel(wheel);
                panel.HorizontalAlignment = HorizontalAlignment.Center;
                panel.VerticalAlignment = VerticalAlignment.Center;
                await DrawAsync(panel, 260, 320, File("color-wheel.png"));
            });

            await Try("start-buttons.png", async () =>
            {
                // The "your own picture" design is given the app icon, from a file that is removed again.
                string picture = File("start-picture.tmp.png");
                SavePng(AppIcon.Render(64), picture);
                try
                {
                    await DrawAsync(StartButtonSheet(picture), 1340, 380, File("start-buttons.png"));
                }
                finally
                {
                    System.IO.File.Delete(picture);
                }
            });

            await Try("icon.png", () =>
            {
                SavePng(AppIcon.Render(256), File("icon.png"));
                return Task.CompletedTask;
            });

            foreach (var item in trayItems.Concat(hiddenItems)) item.Dispose();
        }
        catch (Exception ex)
        {
            failed.Add("everything");
            Log.Error("The screenshots could not be drawn", ex);
        }
        finally
        {
            try
            {
                host?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("Could not close down after the screenshots", ex);
            }
        }

        Log.Info(failed.Count == 0 ? "Screenshots drawn" : "Screenshots not drawn: " + string.Join(", ", failed));
        return failed.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// A stand-in for <see cref="AppButton"/>: a pinned app shown with a made-up number of open windows.
    /// It has the properties the taskbar's button template reads, and nothing behind them.
    /// </summary>
    private sealed class SampleApp
    {
        private readonly int _windows;

        public SampleApp(PinnedItem pin, int windows, AppState state)
        {
            Pin = pin;
            _windows = windows;
            State = state;
        }

        public PinnedItem Pin { get; }

        public object? First => null;

        public bool IsPinned => true;

        public bool IsRunning => _windows > 0;

        public bool IsMultiple => _windows > 1;

        public AppState State { get; }

        public string Title => Pin.Name;

        public string Label => _windows > 0 ? Pin.Name : "";
    }

    /// <summary>A stand-in tray icon: the icon of an app that comes with Windows.</summary>
    private sealed class SampleTrayIcon
    {
        private readonly ShellItem _item;

        public SampleTrayIcon(ShellItem item) => _item = item;

        public string Title => _item.DisplayName;

        public ImageSource? Icon => _item.LargeIcon;
    }
}
