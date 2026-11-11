using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ManagedShell.AppBar;

namespace ClearStart;

internal enum SettingsPage
{
    Appearance,
    Taskbar,
    StartMenu,
    Weather,
    About,
}

/// <summary>
/// The contents of the settings window: the page names down the left, the chosen page on the right.
/// Everything is built in code, one line per setting, so that the self-test can draw each page.
/// A change is applied to the settings in memory at once and the host is told what it touches.
/// </summary>
internal sealed class SettingsView : UserControl
{
    private static readonly Color PageColor = Color.FromRgb(0x20, 0x20, 0x20);
    private static readonly Color NavColor = Color.FromRgb(0x1A, 0x1A, 0x1A);
    private static readonly Color CardColor = Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF);
    private static readonly Color LineColor = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);
    private static readonly Color StartMenuTint = Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1C);
    private const double CardRadius = 7;
    // On a wide window the lines stop here instead of stretching a switch far away from what it switches.
    private const double PageWidth = 900;
    private const double PageMargin = 32;

    /// <summary>The look of the taskbar or of the start menu: a backdrop style and a #AARRGGBB tint.</summary>
    private sealed record LookTarget(Func<Appearance> Style, Action<Appearance> SetStyle, Func<string> Tint,
        Action<string> SetTint, Color Fallback, SettingsChange What);

    /// <summary>One setting's line. Lines that follow each other are drawn as one card: see JoinRows.</summary>
    private sealed class RowCard : Border
    {
    }

    /// <summary>One entry of the start menu's list of places, as the Start menu page shows it.</summary>
    private sealed class EntryLine
    {
        public EntryLine(PlaceEntry entry)
        {
            Entry = entry;
            Divider = entry.IsDivider;
        }

        public PlaceEntry Entry { get; }
        public bool Divider { get; }
        public TextBlock? Glyph;
        public TextBox? Name;
        public TextBox? Target;
        public Button Up = null!;
        public Button Down = null!;
    }

    private readonly ShellHost _host;
    // Each control adds one of these; together they bring every control in line with the settings.
    private readonly List<Action> _loaders = new();
    private readonly Dictionary<SettingsPage, Panel> _pages = new();
    // Each page inside the frame that keeps it to a readable width.
    private readonly Dictionary<SettingsPage, FrameworkElement> _frames = new();
    private readonly Dictionary<SettingsPage, RadioButton> _nav = new();
    private readonly ScrollViewer _pageHost;

    private readonly TextBlock _placeNow = new() { TextWrapping = TextWrapping.Wrap };
    // Both stay out of the way until a search has something to say.
    private readonly TextBlock _placeStatus = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Visibility = Visibility.Collapsed };
    private readonly StackPanel _placeResults = new() { Visibility = Visibility.Collapsed };
    private TextBox? _placeBox;
    private CancellationTokenSource? _search;

    // The start menu's list of places: one grid for all its lines, so their columns line up, and what each line shows.
    private readonly Grid _entryGrid = EntryGrid();
    private readonly List<EntryLine> _entryLines = new();
    private DateTime _iconsClosed = DateTime.MinValue;

    private bool _loading;

    public SettingsView(ShellHost host, SettingsPage page = SettingsPage.Appearance)
    {
        _host = host;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        FontSize = 13.5;
        Foreground = Brushes.White;
        Background = Ui.Brush(PageColor);
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        _pages[SettingsPage.Appearance] = AppearancePage();
        _pages[SettingsPage.Taskbar] = TaskbarPage();
        _pages[SettingsPage.StartMenu] = StartMenuPage();
        _pages[SettingsPage.Weather] = WeatherPage();
        _pages[SettingsPage.About] = AboutPage();
        foreach (var (key, content) in _pages)
        {
            // One column that stops growing at the page width; what is left over stays empty on the right.
            var frame = new Grid();
            frame.ColumnDefinitions.Add(new ColumnDefinition { MaxWidth = PageWidth + (2 * PageMargin) });
            frame.Children.Add(content);
            _frames[key] = frame;
        }

        // Down the left: the icon and name, the pages, and the version at the foot.
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 18, 0, 18) };
        header.Children.Add(new Image { Source = AppIcon.Image, Width = 26, Height = 26 });
        header.Children.Add(new TextBlock
        {
            Text = "ClearStart",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(11, 0, 0, 1),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var items = new StackPanel();
        AddNav(items, SettingsPage.Appearance, "Appearance", 0xE790);
        AddNav(items, SettingsPage.Taskbar, "Taskbar", 0xE90E);
        AddNav(items, SettingsPage.StartMenu, "Start menu", 0xECA5);
        AddNav(items, SettingsPage.Weather, "Weather", 0xE706);
        AddNav(items, SettingsPage.About, "General", 0xE713);

        var foot = new TextBlock
        {
            Text = "Version " + VersionText() + (_host.Mode == HostMode.Preview ? "  ·  Preview" : ""),
            FontSize = 11.5,
            Opacity = 0.5,
            Margin = new Thickness(13, 0, 0, 4),
        };

        var nav = new DockPanel { Margin = new Thickness(8, 0, 8, 8) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        nav.Children.Add(header);
        nav.Children.Add(foot);
        nav.Children.Add(items);

        _pageHost = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(208) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(new Border { Background = Ui.Brush(NavColor), Child = nav });
        Grid.SetColumn(_pageHost, 1);
        root.Children.Add(_pageHost);
        Content = root;

        Show(page);
        LoadValues();
    }

    /// <summary>Turns to a page.</summary>
    public void Show(SettingsPage page)
    {
        if (!_frames.TryGetValue(page, out var content)) return;
        _pageHost.Content = content;
        _pageHost.ScrollToTop();
        _nav[page].IsChecked = true;
    }

    /// <summary>Brings every control in line with the settings as they are now.</summary>
    public void LoadValues()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            foreach (var load in _loaders) load();
            // Some lines only show for some choices, which changes where a card begins and ends.
            foreach (var page in _pages.Values) JoinRows(page);
        }
        catch (Exception ex)
        {
            Log.Error("Could not show the settings", ex);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The user changed something: apply it, tell the host what it touches, and show the result.</summary>
    private void Change(Action apply, SettingsChange what)
    {
        if (_loading) return;
        try
        {
            apply();
            if (what != SettingsChange.None) _host.Edited(what);
        }
        catch (Exception ex)
        {
            Log.Error("Could not apply a setting", ex);
        }
        LoadValues();
    }

    // ------------------------------------------------------------------ pages

    private Panel AppearancePage()
    {
        var look = new LookTarget(
            () => Settings.Current.Taskbar.Appearance, value => Settings.Current.Taskbar.Appearance = value,
            () => Settings.Current.Taskbar.Tint, value => Settings.Current.Taskbar.Tint = value,
            Color.FromArgb(0, 0, 0, 0), SettingsChange.Look);

        var page = NewPage("Appearance", "How see-through the taskbars are, their colors, and what the Start button looks like.");
        page.Children.Add(Group("Taskbar"));
        AddLookRows(page, "Taskbar", look);
        page.Children.Add(Row("Text and icon color", "The clock, the weather and the tray glyphs",
            ColorPicker(
                () => Settings.ParseColor(Settings.Current.Taskbar.Foreground, Colors.White),
                color => Settings.Current.Taskbar.Foreground = Settings.ColorText(Color.FromRgb(color.R, color.G, color.B)),
                SettingsChange.Look,
                ("White", Colors.White), ("Light gray", Color.FromRgb(0xC8, 0xC8, 0xC8)), ("Black", Colors.Black),
                ("Windows accent color", AccentColor()))));
        page.Children.Add(Hint("Changes show on the taskbars as you make them. " +
            "The start menu has its own transparency and color, on the Start menu page."));

        page.Children.Add(Group("Start button"));
        AddStartButtonRows(page);
        page.Children.Add(Hint("Where the Start button sits is chosen for each monitor, on the Taskbar page."));
        return page;
    }

    private Panel TaskbarPage()
    {
        var page = NewPage("Taskbar", "Which monitors have a bar and where it sits, what the bars show, and how big their icons are.");

        page.Children.Add(Group("Taskbars and app buttons"));
        page.Children.Add(Row("A taskbar on every monitor", "Off: only the main display has one",
            Toggle(() => Taskbar.AllMonitors, value => Taskbar.AllMonitors = value, SettingsChange.Bars)));
        page.Children.Add(Row("Show only the windows on the taskbar's own monitor", null,
            Toggle(() => Taskbar.TasksOnOwnMonitorOnly, value => Taskbar.TasksOnOwnMonitorOnly = value, SettingsChange.Bars)));
        // Applied by the host, bar by bar, without reopening them. Each monitor can differ: see below.
        page.Children.Add(Row("Pinned apps", "On the main taskbar only, or on every one. Each monitor can differ, below",
            Choice(() => Taskbar.PinsOnAllMonitors, _host.SetPinsOnAllBars, SettingsChange.None,
                ("Main taskbar", false), ("Every taskbar", true))));
        // Also the host's: the button appears or goes on every bar without reopening them.
        page.Children.Add(Row("Search button", "Next to Start on every taskbar",
            Toggle(() => Taskbar.ShowSearchButton, _host.SetSearchButton, SettingsChange.None)));
        page.Children.Add(Row("Search button opens", "Windows Search finds apps, files, settings and the web. ClearStart's menu is the one Start opens",
            Choice(() => Taskbar.SearchButtonOpens, _host.SetSearchOpens, SettingsChange.None,
                ("Windows Search", SearchOpens.Windows), ("ClearStart's menu", SearchOpens.Menu))));
        page.Children.Add(Row("One button per app", "All of an app's windows sit behind one button, with previews",
            Toggle(() => Taskbar.CombineWindows, value => Taskbar.CombineWindows = value, SettingsChange.Bars)));
        page.Children.Add(Row("Show window titles next to the icons", null,
            Toggle(() => Taskbar.ShowLabels, value => Taskbar.ShowLabels = value, SettingsChange.Bars)));
        page.Children.Add(Row("App buttons", "Where the row of apps sits on the bar",
            Choice(() => Taskbar.Alignment, value => Taskbar.Alignment = value, SettingsChange.Bars,
                ("Centered", Alignment.Center), ("At the start", Alignment.Left))));
        page.Children.Add(Row("Lock taskbar size", "Stops the bars being resized by dragging their edge",
            Toggle(() => Taskbar.Locked, value => Taskbar.Locked = value, SettingsChange.Lock)));

        page.Children.Add(Group("Icon sizes"));
        page.Children.Add(Row("Fit app icons to each taskbar", "Icons grow and shrink with the bar's thickness",
            Toggle(() => Taskbar.IconSize == 0, value => Taskbar.IconSize = value ? 0 : 24, SettingsChange.IconSizes)));
        page.Children.Add(Row("App icon size", "The same on every taskbar",
            SliderWith(16, 48, 2,
                () => Taskbar.IconSize == 0 ? 24 : Taskbar.IconSize,
                value => Taskbar.IconSize = TaskbarSettings.ValidIconSize((int)value),
                value => Taskbar.IconSize == 0 ? "Auto" : $"{value:0} px",
                SettingsChange.IconSizes,
                enabled: () => Taskbar.IconSize != 0)));
        page.Children.Add(Row("Tray icon size", "The tray icons and the network and volume glyphs",
            SliderWith(12, 32, 1,
                () => Taskbar.TrayIconSize,
                value => Taskbar.TrayIconSize = TaskbarSettings.ValidTrayIconSize((int)value),
                value => $"{value:0} px",
                SettingsChange.IconSizes)));

        page.Children.Add(Group("Each monitor"));
        foreach (var screen in Screens())
        {
            string device = screen.DeviceName;
            string title = TaskbarSettings.MonitorKey(device) + (screen.Primary ? "  ·  main display" : "") +
                $"  ·  {screen.Bounds.Width} × {screen.Bounds.Height}";
            // Hiding a bar, moving it and moving its Start button touch that one bar; the host saves those itself.
            // (It refuses to hide the last bar there is, and the choice then springs back to "Shown".)
            page.Children.Add(WideRow(title, null,
                Labeled("Taskbar", Choice(
                    () => !Taskbar.HiddenOn(device), shown => _host.SetBarHidden(device, !shown), SettingsChange.None,
                    ("Shown", true), ("Hidden", false))),
                Labeled("Position", Choice(
                    () => Taskbar.EdgeFor(device), edge => _host.MoveBar(device, edge), SettingsChange.None,
                    ("Same as Windows", BarEdge.Auto), ("Top", BarEdge.Top), ("Bottom", BarEdge.Bottom),
                    ("Left", BarEdge.Left), ("Right", BarEdge.Right))),
                Labeled("Start button", Choice(
                    () => Taskbar.StartButtonFor(device), placement => _host.SetStartButton(device, placement),
                    SettingsChange.None,
                    ("With the apps", StartButtonPlacement.WithApps), ("At the start", StartButtonPlacement.Corner),
                    ("At the end", StartButtonPlacement.End), ("Hidden", StartButtonPlacement.Hidden))),
                Labeled("Pinned apps", Choice(
                    () => Taskbar.PinsFor(device, IsMain()), shown => _host.SetPinsShown(device, IsMain(), shown),
                    SettingsChange.None,
                    ("All", PinsShown.All), ("Chosen", PinsShown.Chosen), ("None", PinsShown.None))),
                PinChips(device, IsMain)));

            // Asked each time: hiding a bar can make another one the main bar.
            bool IsMain() => _host.IsMainBar(screen);
        }
        page.Children.Add(Hint("A hidden taskbar's display has no bar at all, and its windows are listed on the " +
            "main taskbar instead. One display always keeps its taskbar, so there is a way back to these settings."));
        page.Children.Add(Hint("Pinned apps set to Chosen: that taskbar shows only the pinned apps switched on " +
            "for it. An app that is open has its button on its display's taskbar whether its pin shows there or not."));

        page.Children.Add(Group("Tray and clock"));
        page.Children.Add(Row("Tray icons", "The icons apps put next to the clock",
            Choice(() => Taskbar.TrayOnAllMonitors, value => Taskbar.TrayOnAllMonitors = value, SettingsChange.Bars,
                ("Main taskbar", false), ("Every taskbar", true))));
        page.Children.Add(Row("Clock, network and volume", null,
            Choice(() => Taskbar.ClockOnAllMonitors, value => Taskbar.ClockOnAllMonitors = value, SettingsChange.Bars,
                ("Main taskbar", false), ("Every taskbar", true))));
        page.Children.Add(Row("Network and volume button", "Click for quick settings; right-click for sound or network options",
            Toggle(() => Taskbar.ShowQuickSettingsButton, value => Taskbar.ShowQuickSettingsButton = value, SettingsChange.Bars)));
        page.Children.Add(Row("Show seconds on the clock", null,
            Toggle(() => Taskbar.ShowSeconds, value => Taskbar.ShowSeconds = value, SettingsChange.Bars)));
        page.Children.Add(Hint("Changes in the first and last groups reopen the taskbars, which takes a moment. " +
            "Pinned apps and the search button change in place."));
        return page;
    }

    /// <summary>
    /// The pinned apps as chips to switch on and off for one monitor's taskbar. The line only shows while
    /// that taskbar is set to show the chosen ones.
    /// </summary>
    private FrameworkElement PinChips(string device, Func<bool> isMain)
    {
        var chips = new WrapPanel();
        var none = Small("Nothing is pinned to the taskbar yet.");
        var body = new StackPanel();
        body.Children.Add(chips);
        body.Children.Add(none);
        var line = Labeled("", body);
        var made = new List<(string Pin, CheckBox Chip)>();

        _loaders.Add(() =>
        {
            bool main = isMain();
            bool chosen = Taskbar.PinsFor(device, main) == PinsShown.Chosen;
            line.Visibility = chosen ? Visibility.Visible : Visibility.Collapsed;
            if (!chosen) return;

            // The chips are only made again when the pinned apps themselves changed; a click just refills them.
            var pins = _host.Pins.Taskbar.ToList();
            if (!pins.Select(pin => pin.Path).SequenceEqual(made.Select(chip => chip.Pin)))
            {
                chips.Children.Clear();
                made.Clear();
                foreach (var pin in pins)
                {
                    string path = pin.Path;
                    var icon = new Image { Width = 16, Height = 16, Margin = new Thickness(0, 0, 7, 0) };
                    RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                    // The icon may still be loading; the binding picks it up when it arrives.
                    icon.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding("LargeIcon") { Source = pin.Item });

                    var words = new StackPanel { Orientation = Orientation.Horizontal };
                    words.Children.Add(icon);
                    words.Children.Add(new TextBlock
                    {
                        Text = pin.Name,
                        MaxWidth = 170,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                    });

                    var chip = new CheckBox { Style = StyleOf("ChoiceChip"), Content = words, ToolTip = pin.Name };
                    chip.Click += (_, _) =>
                    {
                        bool show = chip.IsChecked == true;
                        Change(() => _host.ShowPinOn(device, isMain(), path, show), SettingsChange.None);
                    };
                    chips.Children.Add(chip);
                    made.Add((path, chip));
                }
            }
            foreach (var (pin, chip) in made) chip.IsChecked = Taskbar.ShowsPin(device, main, pin);
            none.Visibility = made.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        return line;
    }

    private Panel StartMenuPage()
    {
        var look = new LookTarget(
            () => Settings.Current.StartMenu.Appearance, value => Settings.Current.StartMenu.Appearance = value,
            () => Settings.Current.StartMenu.Tint, value => Settings.Current.StartMenu.Tint = value,
            StartMenuTint, SettingsChange.StartMenu);

        var page = NewPage("Start menu", "How the menu opens, its size and look, and the list of places beside the pinned apps.");
        page.Children.Add(Group("Opening"));
        page.Children.Add(Row("Open with the Windows key",
            "Only while ClearStart has taken the place of the Windows taskbar, not in a preview",
            Toggle(() => StartMenu.TakeOverWinKey, value => StartMenu.TakeOverWinKey = value, SettingsChange.StartMenu)));

        page.Children.Add(Group("Size"));
        page.Children.Add(Row("Width", null,
            SliderWith(640, 1800, 20, () => StartMenu.Width, value => StartMenu.Width = (int)value,
                value => $"{value:0} px", SettingsChange.StartMenu)));
        page.Children.Add(Row("Height", null,
            SliderWith(420, 1400, 20, () => StartMenu.Height, value => StartMenu.Height = (int)value,
                value => $"{value:0} px", SettingsChange.StartMenu)));

        page.Children.Add(Group("Look"));
        AddLookRows(page, "Start menu", look);

        page.Children.Add(Group("List of places"));
        page.Children.Add(Row("Show the list of places", "Off: its column keeps only your name, the settings button and the power button",
            Toggle(() => StartMenu.ShowPlaces, value => StartMenu.ShowPlaces = value, SettingsChange.StartMenu)));
        page.Children.Add(Row("Side", "Which side of the menu its column is on",
            Choice(() => StartMenu.PlacesSide, value => StartMenu.PlacesSide = value, SettingsChange.StartMenu,
                ("Left", PlacesSide.Left), ("Right", PlacesSide.Right))));
        page.Children.Add(Row("Column width", null,
            SliderWith(140, 340, 10, () => StartMenu.PlacesWidth, value => StartMenu.PlacesWidth = (int)value,
                value => $"{value:0} px", SettingsChange.StartMenu)));
        page.Children.Add(Row("Row height", "The icons and names grow and shrink with it",
            SliderWith(28, 52, 2, () => StartMenu.PlacesRowHeight, value => StartMenu.PlacesRowHeight = (int)value,
                value => $"{value:0} px", SettingsChange.StartMenu)));
        page.Children.Add(Row("Show icons", "Off: names only",
            Toggle(() => StartMenu.PlacesShowIcons, value => StartMenu.PlacesShowIcons = value, SettingsChange.StartMenu)));
        page.Children.Add(EntriesEditor());
        page.Children.Add(Hint("Everything on this page shows the next time the start menu opens. " +
            "Right-clicking an entry in the start menu also moves or removes it."));
        return page;
    }

    private Panel WeatherPage()
    {
        var page = NewPage("Weather", "The temperature and conditions on the taskbar, next to the clock.");
        page.Children.Add(Group("On the taskbar"));
        page.Children.Add(Row("Show the weather on the taskbar", null,
            Toggle(() => Weather.Show, value => Weather.Show = value, SettingsChange.Weather)));
        page.Children.Add(Row("Show on every taskbar", "Off: only on the main display's",
            Toggle(() => Weather.AllMonitors, value => Weather.AllMonitors = value, SettingsChange.Weather)));

        // Location: what is set now, a box to look another one up, and what the search found.
        page.Children.Add(Group("Location"));
        var box = new TextBox { Width = 260, Style = StyleOf("DarkTextBox") };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            SearchPlaces();
        };
        _placeBox = box;
        var search = Button("Search", SearchPlaces);
        search.Margin = new Thickness(8, 0, 0, 0);
        var forget = Button("Forget this location", () => Change(() =>
        {
            Weather.Place = "";
            Weather.Latitude = null;
            Weather.Longitude = null;
        }, SettingsChange.Weather));
        forget.Margin = new Thickness(8, 0, 0, 0);
        _loaders.Add(() =>
        {
            _placeNow.Text = Weather.HasPlace
                ? "Showing the weather for " + (Weather.Place.Length > 0 ? Weather.Place : "the chosen place") + "."
                : "No location chosen yet. Type a town, city or postal code and choose it from the results.";
            forget.Visibility = Weather.HasPlace ? Visibility.Visible : Visibility.Collapsed;
        });

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(box);
        line.Children.Add(search);
        line.Children.Add(forget);
        page.Children.Add(WideRow("Where", null, _placeNow, line, _placeStatus, _placeResults,
            Small("The name you type is sent to Open-Meteo to look the place up. After that, only the place's " +
                  "coordinates, rounded to about half a mile, are sent to fetch the forecast. Nothing is sent " +
                  "until a location is chosen.")));

        page.Children.Add(Group("What it shows"));
        page.Children.Add(Row("Temperature", null,
            Choice(() => Weather.Units, value => Weather.Units = value, SettingsChange.Weather,
                ("Match Windows", TemperatureUnits.Auto), ("°F", TemperatureUnits.Fahrenheit),
                ("°C", TemperatureUnits.Celsius))));
        page.Children.Add(Row("Show the conditions in words", "\"Partly sunny\" next to the temperature, where there is room",
            Toggle(() => Weather.ShowText, value => Weather.ShowText = value, SettingsChange.Weather)));
        page.Children.Add(Row("Update every", null,
            Choice(() => Weather.RefreshMinutes, value => Weather.RefreshMinutes = value, SettingsChange.Weather,
                ("10 min", 10), ("15 min", 15), ("30 min", 30), ("1 hour", 60))));
        page.Children.Add(Hint("Weather data by Open-Meteo.com"));
        return page;
    }

    private Panel AboutPage()
    {
        bool live = _host.Mode == HostMode.Live;
        var page = NewPage("General", "Starting with Windows, where ClearStart keeps its files, and closing it.");

        // The entry Windows reads at sign-in is written the moment the switch is thrown, not with the settings.
        page.Children.Add(Group("Startup"));
        page.Children.Add(Row("Start with Windows",
            "Starts ClearStart in place of the Windows taskbar, not as a preview, each time you sign in",
            Toggle(() => AutoStart.IsEnabled, AutoStart.Set, SettingsChange.None)));
        var startup = Hint("");
        _loaders.Add(() => startup.Text = AutoStart.Describe());
        page.Children.Add(startup);

        page.Children.Add(Group("Files"));
        page.Children.Add(Row("Settings file",
            "Everything here is kept in one file. A change saved to it by hand takes effect at once.",
            Button("Open", () => Launcher.EditInNotepad(Settings.FilePath))));
        page.Children.Add(Row("Log folder", "What ClearStart did, and anything that went wrong",
            Button("Open", () => Launcher.Run(Log.DefaultDirectory))));

        page.Children.Add(Group("About"));
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        words.Children.Add(new TextBlock { Text = "ClearStart", FontSize = 17, FontWeight = FontWeights.SemiBold });
        words.Children.Add(Small("Version " + VersionText() + "  ·  " + (live
            ? "Running in place of the Windows taskbar"
            : "Running as a preview, next to the Windows taskbar")));
        var exit = Button("Exit ClearStart", _host.RequestExit);
        exit.ToolTip = live ? "Brings back the Windows taskbar" : "Closes the preview taskbars";
        exit.VerticalAlignment = VerticalAlignment.Center;
        exit.Margin = new Thickness(20, 0, 0, 0);

        var about = new Grid();
        about.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        about.ColumnDefinitions.Add(new ColumnDefinition());
        about.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        about.Children.Add(new Image { Source = AppIcon.Image, Width = 40, Height = 40, Margin = new Thickness(0, 4, 0, 4) });
        Grid.SetColumn(words, 1);
        about.Children.Add(words);
        Grid.SetColumn(exit, 2);
        about.Children.Add(exit);
        page.Children.Add(Card(about));
        return page;
    }

    private static string VersionText()
    {
        var version = typeof(App).Assembly.GetName().Version;
        return version == null ? "unknown" : version.ToString(3);
    }

    // ------------------------------------------------------------------ transparency, backdrop and color

    private void AddLookRows(Panel page, string thing, LookTarget look)
    {
        page.Children.Add(Row(thing + " transparency", "100% is fully see-through, 0% is a solid color",
            SliderWith(0, 100, 1,
                () => TransparencyOf(look),
                value => SetTransparency(look, value),
                value => $"{value:0}%",
                look.What)));

        page.Children.Add(Row("Behind the " + thing.ToLower(CultureInfo.CurrentCulture),
            "What shows through: the desktop as it is, blurred, or frosted",
            Choice(look.Style, style =>
            {
                // "Opaque" in the settings file ignores the tint's alpha; keep the bar as solid as it was.
                if (look.Style() == Appearance.Opaque) SetAlpha(look, 255);
                look.SetStyle(style);
            }, look.What,
                ("Clear", Appearance.Clear), ("Blurred", Appearance.Blur), ("Frosted", Appearance.Acrylic))));

        page.Children.Add(Row(thing + " color", "Shows more the less see-through it is",
            ColorPicker(
                () =>
                {
                    var tint = Settings.ParseColor(look.Tint(), look.Fallback);
                    return Color.FromRgb(tint.R, tint.G, tint.B);
                },
                color =>
                {
                    byte alpha = Settings.ParseColor(look.Tint(), look.Fallback).A;
                    // A color cannot show on something fully see-through: let some of it through.
                    if (alpha == 0 && look.Style() != Appearance.Opaque) alpha = 0x66;
                    look.SetTint(Settings.ColorText(Color.FromArgb(alpha, color.R, color.G, color.B)));
                },
                look.What,
                ("Black", Colors.Black), ("Charcoal", Color.FromRgb(0x20, 0x20, 0x20)),
                ("Windows accent color", AccentColor()), ("White", Colors.White))));
    }

    /// <summary>0 to 100: how see-through the tint is. A solid ("opaque") look is 0.</summary>
    private static double TransparencyOf(LookTarget look)
    {
        if (look.Style() == Appearance.Opaque) return 0;
        var tint = Settings.ParseColor(look.Tint(), look.Fallback);
        return Math.Round((255 - tint.A) / 255.0 * 100);
    }

    private static void SetTransparency(LookTarget look, double percent)
    {
        SetAlpha(look, (byte)Math.Round(255 * (100 - Math.Clamp(percent, 0, 100)) / 100));
        // From here on the tint's alpha says how solid it is.
        if (look.Style() == Appearance.Opaque) look.SetStyle(Appearance.Clear);
    }

    private static void SetAlpha(LookTarget look, byte alpha)
    {
        var tint = Settings.ParseColor(look.Tint(), look.Fallback);
        tint.A = alpha;
        look.SetTint(Settings.ColorText(tint));
    }

    private static Color AccentColor()
    {
        var accent = SystemParameters.WindowGlassColor;
        return Color.FromRgb(accent.R, accent.G, accent.B);
    }

    // ------------------------------------------------------------------ the Start button's design

    private void AddStartButtonRows(Panel page)
    {
        // The button as everything below adds up, on the taskbar's own color.
        var preview = new Border
        {
            Width = 132,
            Height = 46,
            CornerRadius = new CornerRadius(6),
            BorderBrush = Ui.Brush(LineColor),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "How the Start button looks now",
        };
        _loaders.Add(() =>
        {
            // The bar's color over a dark gray: there is no desktop behind this one to show through.
            var tint = Settings.ParseColor(Taskbar.Tint, Color.FromArgb(0, 0, 0, 0));
            if (Taskbar.Appearance == Appearance.Opaque) tint.A = 255;
            byte Over(byte channel) => (byte)Math.Round((channel * tint.A / 255.0) + (0x30 * (255 - tint.A) / 255.0));
            preview.Background = Ui.Brush(Color.FromRgb(Over(tint.R), Over(tint.G), Over(tint.B)));

            double size = Math.Clamp(Math.Round(24 * 0.84 * Taskbar.StartButtonScale / 100.0), 10, 36);
            preview.Child = StartGlyph.Build(Taskbar, size, 36, Settings.ParseColor(Taskbar.Foreground, Colors.White), 116);
        });

        var tiles = new WrapPanel();
        var designs = new List<(RadioButton Tile, StartButtonStyle Style)>();
        foreach (var (style, name) in StartGlyph.Designs)
        {
            FrameworkElement face = style switch
            {
                StartButtonStyle.Text => new TextBlock { Text = "Aa", FontSize = 15, FontWeight = FontWeights.SemiBold },
                StartButtonStyle.Picture => Icon(0xE8B9, 17),
                _ => StartGlyph.Shape(style, 20, Brushes.White),
            };
            var tile = new RadioButton { Content = face, ToolTip = name, Style = StyleOf("ChoiceTile") };
            tile.Click += (_, _) =>
            {
                if (Taskbar.StartButtonStyle == style)
                {
                    LoadValues();
                    return;
                }
                // There is nothing to show for "your own picture" until one is chosen: ask for it first.
                if (style == StartButtonStyle.Picture && StartGlyph.Picture(Taskbar.StartButtonPicture) == null)
                {
                    ChooseStartPicture();
                    return;
                }
                Change(() => Taskbar.StartButtonStyle = style, SettingsChange.Look);
            };
            tiles.Children.Add(tile);
            designs.Add((tile, style));
        }
        _loaders.Add(() =>
        {
            foreach (var (tile, style) in designs) tile.IsChecked = style == Taskbar.StartButtonStyle;
        });

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Caption("Design", "A shape, a word of your own, or a picture of your own"));
        Grid.SetColumn(preview, 1);
        head.Children.Add(preview);
        var design = new StackPanel();
        design.Children.Add(head);
        // The tiles keep a gap under themselves for when they wrap; take the last row's back.
        design.Children.Add(new Border { Margin = new Thickness(0, 12, 0, -6), Child = tiles });
        page.Children.Add(Card(design));

        // A word of the user's own, shown as it is typed.
        var word = new TextBox { Width = 190, MaxLength = TaskbarSettings.MaxStartButtonText, Style = StyleOf("DarkTextBox") };
        word.TextChanged += (_, _) =>
        {
            string text = TaskbarSettings.ValidStartButtonText(word.Text);
            if (text != Taskbar.StartButtonText) Change(() => Taskbar.StartButtonText = text, SettingsChange.Look);
        };
        _loaders.Add(() =>
        {
            if (!word.IsKeyboardFocused) word.Text = Taskbar.StartButtonText;
        });
        var wordRow = Row("Word", $"Up to {TaskbarSettings.MaxStartButtonText} letters. Left empty, it says \"{StartGlyph.DefaultText}\".", word);
        page.Children.Add(wordRow);

        var pictureName = new TextBlock
        {
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        var pictureLine = new StackPanel { Orientation = Orientation.Horizontal };
        pictureLine.Children.Add(pictureName);
        pictureLine.Children.Add(Button("Choose…", ChooseStartPicture));
        var pictureRow = Row("Picture", "A PNG, JPG, GIF, BMP or ICO file. One with a see-through background sits best on the bar.",
            pictureLine);
        page.Children.Add(pictureRow);

        page.Children.Add(Row("Size", "Next to the app icons: 100% is a little smaller than they are",
            SliderWith(60, 160, 5,
                () => Taskbar.StartButtonScale,
                value => Taskbar.StartButtonScale = TaskbarSettings.ValidStartButtonScale((int)value),
                value => $"{value:0}%",
                SettingsChange.Look)));

        var match = Button("Match the text", () => Change(() => Taskbar.StartButtonColor = "", SettingsChange.Look));
        match.Margin = new Thickness(10, 0, 0, 0);
        match.ToolTip = "Use the taskbar's text and icon color";
        _loaders.Add(() => match.IsEnabled = Taskbar.StartButtonColor.Length > 0);
        var colorLine = new StackPanel { Orientation = Orientation.Horizontal };
        colorLine.Children.Add(ColorPicker(
            () => Settings.ParseColor(Taskbar.StartButtonColor, Settings.ParseColor(Taskbar.Foreground, Colors.White)),
            color => Taskbar.StartButtonColor = Settings.ColorText(Color.FromRgb(color.R, color.G, color.B)),
            SettingsChange.Look,
            ("White", Colors.White), ("Black", Colors.Black), ("Windows accent color", AccentColor())));
        colorLine.Children.Add(match);
        var colorRow = Row("Color", "The same as the taskbar's text until another is chosen", colorLine);
        page.Children.Add(colorRow);

        page.Children.Add(Row("Background", "A filled shape behind the design",
            Choice(() => Taskbar.StartButtonBackground, value => Taskbar.StartButtonBackground = value, SettingsChange.Look,
                ("None", StartButtonBackground.None), ("Circle", StartButtonBackground.Circle),
                ("Rounded square", StartButtonBackground.Rounded))));

        var plateRow = Row("Background color", "The Windows accent color until another is chosen",
            ColorPicker(
                () => StartGlyph.BackgroundColor(Taskbar),
                // Choosing the accent color means following it, should Windows change it.
                color => Taskbar.StartButtonBackgroundColor = Color.FromRgb(color.R, color.G, color.B) == AccentColor()
                    ? ""
                    : Settings.ColorText(Color.FromRgb(color.R, color.G, color.B)),
                SettingsChange.Look,
                ("Windows accent color", AccentColor()), ("White", Colors.White),
                ("Charcoal", Color.FromRgb(0x20, 0x20, 0x20)), ("Black", Colors.Black)));
        page.Children.Add(plateRow);

        _loaders.Add(() =>
        {
            var style = Taskbar.StartButtonStyle;
            string file = Taskbar.StartButtonPicture;
            bool pictureShown = style == StartButtonStyle.Picture && StartGlyph.Picture(file) != null;

            wordRow.Visibility = style == StartButtonStyle.Text ? Visibility.Visible : Visibility.Collapsed;
            pictureRow.Visibility = style == StartButtonStyle.Picture ? Visibility.Visible : Visibility.Collapsed;
            // A picture has its own colors.
            colorRow.Visibility = pictureShown ? Visibility.Collapsed : Visibility.Visible;
            plateRow.Visibility = Taskbar.StartButtonBackground == StartButtonBackground.None
                ? Visibility.Collapsed
                : Visibility.Visible;

            if (style == StartButtonStyle.Picture)
            {
                string name = file.Length == 0 ? "" : System.IO.Path.GetFileName(file.Trim().Trim('"'));
                pictureName.Text = file.Length == 0 ? "None chosen" : pictureShown ? name : "Cannot show " + name;
                pictureName.ToolTip = file.Length == 0 ? null : file;
            }
        });
    }

    /// <summary>Asks for a picture file and puts it on the Start button.</summary>
    private void ChooseStartPicture()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a picture for the Start button",
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.ico;*.tif;*.tiff|All files|*.*",
            CheckFileExists = true,
        };
        if (Ask(dialog))
        {
            Change(() =>
            {
                Taskbar.StartButtonPicture = dialog.FileName;
                Taskbar.StartButtonStyle = StartButtonStyle.Picture;
            }, SettingsChange.Look);
        }
        else
        {
            // Nothing chosen: put the tick back on the design in use.
            LoadValues();
        }
    }

    /// <summary>Shows a file or folder dialog over the settings window. True when something was chosen.</summary>
    private bool Ask(Microsoft.Win32.CommonDialog dialog)
    {
        var owner = Window.GetWindow(this);
        return (owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true;
    }

    // ------------------------------------------------------------------ the start menu's list of places

    /// <summary>The list itself: a line for each entry, to rename, re-aim, reorder or remove, and a way to add more.</summary>
    private RowCard EntriesEditor()
    {
        Button add = null!;
        add = Button("Add…", () => ShowAddMenu(add));
        var reset = Button("Reset to the default list",
            () => Change(() => StartMenu.Places = StartMenuSettings.DefaultPlaces(), SettingsChange.StartMenu));
        reset.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(add);
        buttons.Children.Add(reset);

        _loaders.Add(LoadEntries);
        return WideRow("What is in the list",
            "From top to bottom. An entry can open a folder, a program, a file, a web address, " +
            "or anything else the Run box accepts.",
            _entryGrid, buttons);
    }

    /// <summary>Starts a new line in the list and says which one it is.</summary>
    private int NewEntryRow()
    {
        _entryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return _entryGrid.RowDefinitions.Count - 1;
    }

    private void PutInEntryRow(FrameworkElement element, int row, int column, int span = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, span);
        _entryGrid.Children.Add(element);
    }

    /// <summary>The columns every line of the list shares: icon, name, what it opens, and its buttons.</summary>
    private static Grid EntryGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        return grid;
    }

    private void LoadEntries()
    {
        var places = StartMenu.Places;

        // The lines are only made again when the list itself changed, so a box being typed in keeps the keyboard.
        // An empty grid has never been filled: even an empty list puts one line in it, to say so.
        bool same = _entryGrid.Children.Count > 0 && places.Count == _entryLines.Count;
        for (int i = 0; same && i < places.Count; i++)
        {
            same = ReferenceEquals(places[i], _entryLines[i].Entry) && places[i].IsDivider == _entryLines[i].Divider;
        }
        if (!same)
        {
            _entryGrid.Children.Clear();
            _entryGrid.RowDefinitions.Clear();
            _entryLines.Clear();

            if (places.Count == 0)
            {
                var empty = Small("Nothing in the list yet.");
                empty.Margin = new Thickness(2, 0, 0, 2);
                PutInEntryRow(empty, NewEntryRow(), 0, 4);
            }
            else
            {
                int headings = NewEntryRow();
                var name = Small("Name");
                name.Margin = new Thickness(1, 0, 0, 5);
                PutInEntryRow(name, headings, 1);
                var opens = Small("Opens");
                opens.Margin = new Thickness(1, 0, 0, 5);
                PutInEntryRow(opens, headings, 2);
            }
            foreach (var place in places) _entryLines.Add(NewEntryLine(place));
        }

        for (int i = 0; i < _entryLines.Count; i++)
        {
            var line = _entryLines[i];
            SetEnabled(line.Up, i > 0);
            SetEnabled(line.Down, i < _entryLines.Count - 1);
            if (line.Divider) continue;

            line.Glyph!.Text = line.Entry.Glyph;
            if (!line.Name!.IsKeyboardFocused) line.Name.Text = line.Entry.Name;
            if (!line.Target!.IsKeyboardFocused) line.Target.Text = line.Entry.Target;
        }

        static void SetEnabled(Button button, bool enabled)
        {
            button.IsEnabled = enabled;
            button.Opacity = enabled ? 1 : 0.3;
        }
    }

    private EntryLine NewEntryLine(PlaceEntry place)
    {
        var line = new EntryLine(place);
        int row = NewEntryRow();

        if (line.Divider)
        {
            // A line between two groups: nothing to name or aim, only to move or remove.
            var label = Small("Divider line");
            label.Margin = new Thickness(0, 0, 10, 0);
            label.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(label, Dock.Left);
            var rule = new DockPanel { Height = 30, Margin = new Thickness(2, 0, 10, 6) };
            rule.Children.Add(label);
            rule.Children.Add(new Border
            {
                Height = 1,
                Background = Ui.Brush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            PutInEntryRow(rule, row, 0, 3);
        }
        else
        {
            line.Glyph = new TextBlock { FontFamily = IconFont, FontSize = 16 };
            Button icon = null!;
            icon = new Button
            {
                Style = StyleOf("FlatButton"),
                Width = 34,
                Height = 30,
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Ui.Brush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                Content = line.Glyph,
                ToolTip = "Choose an icon",
            };
            icon.Click += (_, _) => Ui.Safe(() => ShowIconPicker(icon, place), "Icon picker");
            PutInEntryRow(icon, row, 0);

            line.Name = EntryBox(text => place.Name = text, "The name shown in the list");
            PutInEntryRow(line.Name, row, 1);

            line.Target = EntryBox(text => place.Target = text,
                "What it opens: a folder or file, a program, a web address, a shell: or ms-settings: location, " +
                "or clearstart:run for the Run box");
            PutInEntryRow(line.Target, row, 2);
        }

        line.Up = IconButton(0xE70E, "Move up", () => MoveEntry(place, -1));
        line.Down = IconButton(0xE70D, "Move down", () => MoveEntry(place, 1));
        var remove = IconButton(0xE74D, "Remove from the list",
            () => Change(() => StartMenu.Places.Remove(place), SettingsChange.StartMenu));
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 6),
        };
        tools.Children.Add(line.Up);
        tools.Children.Add(line.Down);
        tools.Children.Add(remove);
        PutInEntryRow(tools, row, 3);
        return line;
    }

    /// <summary>
    /// A box for an entry's name or target. What is typed is kept as it is typed (the menu reads the list
    /// each time it opens), without redrawing anything here, so the box keeps the keyboard.
    /// </summary>
    private TextBox EntryBox(Action<string> set, string tip)
    {
        var box = new TextBox { Style = StyleOf("DarkTextBox"), Height = 30, Margin = new Thickness(0, 0, 6, 6), ToolTip = tip };
        box.TextChanged += (_, _) =>
        {
            if (_loading) return;
            set(box.Text.Trim());
            _host.Edited(SettingsChange.None);
        };
        // Once the typing is over, show the entry as it now stands.
        box.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(new Action(LoadValues));
        return box;
    }

    private void MoveEntry(PlaceEntry place, int by) => Change(() =>
    {
        var places = StartMenu.Places;
        int from = places.IndexOf(place), to = from + by;
        if (from < 0 || to < 0 || to >= places.Count) return;
        places.RemoveAt(from);
        places.Insert(to, place);
    }, SettingsChange.StartMenu);

    private void AddEntry(PlaceEntry entry) => Change(() => StartMenu.Places.Add(entry), SettingsChange.StartMenu);

    /// <summary>The icons an entry can have, in a panel under the entry's icon button.</summary>
    private void ShowIconPicker(Button target, PlaceEntry place)
    {
        // The click that closes the panel lands on the button it hangs from; without this pause it would open again.
        if ((DateTime.UtcNow - _iconsClosed).TotalMilliseconds < 300) return;

        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 4,
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = PopupAnimation.Fade,
        };
        var icons = new WrapPanel { Width = 8 * 36 };
        foreach (int code in StartMenuSettings.PlaceIcons)
        {
            string glyph = ((char)code).ToString();
            var choice = new Button
            {
                Style = StyleOf("FlatButton"),
                Width = 36,
                Height = 36,
                Content = new TextBlock { FontFamily = IconFont, FontSize = 17, Text = glyph },
            };
            if (glyph == place.Glyph) choice.Background = Ui.Brush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
            choice.Click += (_, _) =>
            {
                popup.IsOpen = false;
                Change(() => place.Glyph = glyph, SettingsChange.StartMenu);
            };
            icons.Children.Add(choice);
        }
        popup.Child = new Border
        {
            Background = (Brush)Application.Current.FindResource("MenuBackgroundBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("MenuBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
            Child = icons,
        };
        popup.Closed += (_, _) => _iconsClosed = DateTime.UtcNow;
        popup.IsOpen = true;
    }

    /// <summary>What "Add…" offers: ready-made entries in groups, then the kinds that need choosing or typing.</summary>
    private void ShowAddMenu(UIElement target) => Ui.ShowMenu(target, PlacementMode.Bottom, AddMenu());

    internal List<MenuEntry> AddMenu()
    {
        var have = new HashSet<string>(StartMenu.Places.Select(place => (place.Target ?? "").Trim()),
            StringComparer.OrdinalIgnoreCase);
        var entries = new List<MenuEntry>();
        foreach (var group in StartMenuSettings.ReadyPlaces().GroupBy(ready => ready.Group))
        {
            // One that is already in the list is greyed out.
            var children = group.Select(ready => new MenuEntry(ready.Place.Name,
                have.Contains(ready.Place.Target) ? null : () => AddEntry(ready.Place.Copy()))).ToList();
            entries.Add(new MenuEntry(group.Key, null, Children: children));
        }
        entries.Add(new MenuEntry(null, null));
        entries.Add(new MenuEntry("A folder…", AddFolder));
        entries.Add(new MenuEntry("A program or file…", AddFile));
        entries.Add(new MenuEntry("A web address or command", () => AddEntry(PlaceEntry.Of("New entry", "", 0xE774))));
        entries.Add(new MenuEntry("A divider line", () => AddEntry(PlaceEntry.Divider())));
        return entries;
    }

    private void AddFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder for the start menu" };
        if (!Ask(dialog)) return;

        string folder = dialog.FolderName;
        string name = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
        // A drive has no name of its own: show it as it is written.
        AddEntry(new PlaceEntry { Name = name.Length > 0 ? name : folder, Target = folder });
    }

    private void AddFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a program or file for the start menu",
            Filter = "Programs and shortcuts|*.exe;*.lnk;*.bat;*.cmd;*.msc|All files|*.*",
            CheckFileExists = true,
            // A shortcut is added as the shortcut, so what it passes to its program still applies.
            DereferenceLinks = false,
        };
        if (!Ask(dialog)) return;

        string file = dialog.FileName;
        string kind = System.IO.Path.GetExtension(file).ToLowerInvariant();
        bool program = kind is ".exe" or ".lnk" or ".bat" or ".cmd" or ".msc";
        AddEntry(PlaceEntry.Of(System.IO.Path.GetFileNameWithoutExtension(file), file, program ? 0xECAA : 0xE8A5));
    }

    // ------------------------------------------------------------------ looking up a place

    private async void SearchPlaces()
    {
        if (_placeBox == null) return;
        string text = _placeBox.Text.Trim();
        _placeResults.Children.Clear();
        if (text.Length < 2)
        {
            SetPlaceStatus("Type a town, city or postal code first.");
            return;
        }

        _search?.Cancel();
        var search = _search = new CancellationTokenSource();
        SetPlaceStatus("Searching…");
        try
        {
            var places = await WeatherService.FindPlacesAsync(text, search.Token);
            if (search.IsCancellationRequested) return;

            foreach (var place in places)
            {
                var choice = Button(place.FullName, () => ChoosePlace(place));
                choice.HorizontalAlignment = HorizontalAlignment.Left;
                choice.Margin = new Thickness(0, 0, 0, 6);
                _placeResults.Children.Add(choice);
            }
            SetPlaceStatus(places.Count == 0 ? "No place found by that name." : "Choose the right one:");
        }
        catch (OperationCanceledException)
        {
            // A newer search took over.
        }
        catch (Exception ex)
        {
            if (search.IsCancellationRequested) return;
            Log.Warn("Place search failed: " + ex.Message);
            SetPlaceStatus("Could not reach the place search. Check the connection and try again.");
        }
    }

    /// <summary>Says how the search went, and shows what it found if it found anything.</summary>
    private void SetPlaceStatus(string text)
    {
        _placeStatus.Text = text;
        _placeStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _placeResults.Visibility = _placeResults.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChoosePlace(PlaceMatch place)
    {
        _placeResults.Children.Clear();
        SetPlaceStatus("");
        if (_placeBox != null) _placeBox.Text = "";
        Change(() =>
        {
            Weather.Place = place.FullName;
            Weather.Latitude = place.Latitude;
            Weather.Longitude = place.Longitude;
            Weather.Show = true;
        }, SettingsChange.Weather);
    }

    // ------------------------------------------------------------------ controls

    private static TaskbarSettings Taskbar => Settings.Current.Taskbar;

    private static StartMenuSettings StartMenu => Settings.Current.StartMenu;

    private static WeatherSettings Weather => Settings.Current.Weather;

    private static FontFamily IconFont => (FontFamily)Application.Current.FindResource("IconFont");

    private static Style StyleOf(string key) => (Style)Application.Current.FindResource(key);

    private static IEnumerable<AppBarScreen> Screens()
    {
        try
        {
            return AppBarScreen.FromAllScreens();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not list the monitors: " + ex.Message);
            return Array.Empty<AppBarScreen>();
        }
    }

    private void AddNav(Panel nav, SettingsPage page, string name, int icon)
    {
        var glyph = Icon(icon, 16);
        glyph.Width = 22;
        var words = new StackPanel { Orientation = Orientation.Horizontal };
        words.Children.Add(glyph);
        words.Children.Add(new TextBlock { Text = name, Margin = new Thickness(10, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });

        var item = new RadioButton { Content = words, Style = StyleOf("NavItem") };
        item.Click += (_, _) => Show(page);
        _nav[page] = item;
        nav.Children.Add(item);
    }

    /// <summary>A character from the icon font.</summary>
    private static TextBlock Icon(int code, double size) => new()
    {
        Text = ((char)code).ToString(),
        FontFamily = IconFont,
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A small button that is only an icon.</summary>
    private static Button IconButton(int code, string tip, Action action)
    {
        var button = new Button
        {
            Style = StyleOf("FlatButton"),
            Width = 28,
            Height = 28,
            Content = Icon(code, 12),
            ToolTip = tip,
        };
        button.Click += (_, _) => Ui.Safe(action, tip);
        return button;
    }

    private CheckBox Toggle(Func<bool> get, Action<bool> set, SettingsChange what)
    {
        var box = new CheckBox { Style = StyleOf("SettingsToggle") };
        _loaders.Add(() => box.IsChecked = get());
        box.Click += (_, _) =>
        {
            bool value = box.IsChecked == true;
            Change(() => set(value), what);
        };
        return box;
    }

    /// <summary>A few choices side by side in one track, the chosen one filled.</summary>
    private FrameworkElement Choice<T>(Func<T> get, Action<T> set, SettingsChange what,
        params (string Text, T Value)[] options)
    {
        var same = EqualityComparer<T>.Default;
        var panel = new WrapPanel();
        var buttons = new List<(RadioButton Button, T Value)>();
        foreach (var (text, value) in options)
        {
            var button = new RadioButton { Content = text, Style = StyleOf("ChoicePill") };
            button.Click += (_, _) =>
            {
                if (!same.Equals(get(), value)) Change(() => set(value), what);
            };
            panel.Children.Add(button);
            buttons.Add((button, value));
        }
        _loaders.Add(() =>
        {
            var current = get();
            foreach (var (button, value) in buttons) button.IsChecked = same.Equals(value, current);
        });
        return new Border
        {
            Background = Ui.Brush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
            BorderBrush = Ui.Brush(LineColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = panel,
        };
    }

    private FrameworkElement SliderWith(double min, double max, double step, Func<double> get, Action<double> set,
        Func<double, string> label, SettingsChange what, Func<bool>? enabled = null)
    {
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = step,
            SmallChange = step,
            LargeChange = step * 5,
            Width = 210,
            Style = StyleOf("SettingsSlider"),
        };
        var text = new TextBlock
        {
            Width = 58,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, e) =>
        {
            double value = e.NewValue;
            Change(() => set(value), what);
        };
        _loaders.Add(() =>
        {
            slider.Value = Math.Clamp(get(), min, max);
            slider.IsEnabled = enabled?.Invoke() ?? true;
            text.Text = label(slider.Value);
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(slider);
        row.Children.Add(text);
        return row;
    }

    /// <summary>
    /// A few ready-made colors to click, a color wheel for any other, and a box that takes one as #RRGGBB.
    /// </summary>
    private FrameworkElement ColorPicker(Func<Color> get, Action<Color> set, SettingsChange what,
        params (string Name, Color Color)[] swatches)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var rings = new List<(Ellipse Ring, Color Color)>();
        foreach (var (name, color) in swatches)
        {
            var ring = new Ellipse { Width = 20, Height = 20, Fill = Ui.Brush(color) };
            var swatch = new Button
            {
                Style = StyleOf("FlatButton"),
                Padding = new Thickness(3),
                Margin = new Thickness(0, 0, 3, 0),
                Content = ring,
                ToolTip = name,
            };
            swatch.Click += (_, _) => Change(() => set(color), what);
            row.Children.Add(swatch);
            rings.Add((ring, color));
        }

        // The wheel opens in a panel under its button and changes the color as the mouse moves over it.
        var wheelButton = new Button
        {
            Style = StyleOf("FlatButton"),
            Padding = new Thickness(3),
            Margin = new Thickness(3, 0, 0, 0),
            Content = ColorWheel.Thumbnail(20),
            ToolTip = "Pick any color from the color wheel",
        };
        DateTime wheelClosed = DateTime.MinValue;
        wheelButton.Click += (_, _) =>
        {
            // Clicking the button while the panel is open closes it (the click lands outside the panel);
            // without this pause the same click would open it again.
            if ((DateTime.UtcNow - wheelClosed).TotalMilliseconds < 300) return;

            var wheel = new ColorWheel();
            wheel.Show(get());
            wheel.Picked += color => Change(() => set(color), what);
            var popup = new Popup
            {
                Child = ColorWheel.InPanel(wheel),
                PlacementTarget = wheelButton,
                Placement = PlacementMode.Bottom,
                VerticalOffset = 6,
                AllowsTransparency = true,
                StaysOpen = false,
                PopupAnimation = PopupAnimation.Fade,
            };
            popup.Closed += (_, _) => wheelClosed = DateTime.UtcNow;
            popup.IsOpen = true;
        };
        row.Children.Add(wheelButton);

        var box = new TextBox
        {
            Width = 84,
            MaxLength = 7,
            Margin = new Thickness(6, 0, 0, 0),
            Style = StyleOf("DarkTextBox"),
            ToolTip = "Any color, as #RRGGBB",
        };
        void Commit()
        {
            if (_loading) return;
            if (TryParseColor(box.Text, out var typed))
            {
                if (typed != get()) Change(() => set(typed), what);
            }
            else
            {
                // Not a color: put back the one in use.
                LoadValues();
            }
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Commit();
        };
        row.Children.Add(box);

        _loaders.Add(() =>
        {
            var current = get();
            current.A = 255;
            foreach (var (ring, color) in rings)
            {
                bool chosen = color == current;
                ring.Stroke = chosen ? Brushes.White : Ui.Brush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
                ring.StrokeThickness = chosen ? 2.5 : 1;
            }
            if (!box.IsKeyboardFocused) box.Text = $"#{current.R:X2}{current.G:X2}{current.B:X2}";
        });
        return row;
    }

    private static bool TryParseColor(string text, out Color color)
    {
        color = default;
        text = text.Trim().TrimStart('#');
        if (text.Length == 3) text = string.Concat(text.Select(digit => $"{digit}{digit}"));
        if (text.Length != 6 ||
            !int.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb))
        {
            return false;
        }
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Style = StyleOf("DialogButton") };
        button.Click += (_, _) => Ui.Safe(action, text);
        return button;
    }

    // ------------------------------------------------------------------ layout

    /// <summary>A page: its name, a line on what it is for, and room for groups of settings below.</summary>
    private static StackPanel NewPage(string title, string about)
    {
        var page = new StackPanel { Margin = new Thickness(PageMargin, 22, PageMargin, PageMargin) };
        page.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
        });
        page.Children.Add(new TextBlock
        {
            Text = about,
            Opacity = 0.62,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(1, 4, 0, 0),
        });
        return page;
    }

    private static TextBlock Group(string title) => new()
    {
        Text = title,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(2, 24, 0, 9),
    };

    private static TextBlock Small(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Opacity = 0.65,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 0),
    };

    private static TextBlock Hint(string text)
    {
        var hint = Small(text);
        hint.Margin = new Thickness(2, 8, 2, 0);
        return hint;
    }

    private static RowCard Card(UIElement content) => new()
    {
        Background = Ui.Brush(CardColor),
        BorderBrush = Ui.Brush(LineColor),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(CardRadius),
        Padding = new Thickness(16, 12, 16, 12),
        MinHeight = 54,
        Child = content,
    };

    /// <summary>
    /// Draws the lines that follow one another on a page as one card: rounded at its two ends, with a
    /// hairline between one line and the next. A line that is not showing just now is left out.
    /// </summary>
    private static void JoinRows(Panel page)
    {
        var run = new List<RowCard>();
        foreach (UIElement child in page.Children)
        {
            if (child.Visibility != Visibility.Visible) continue;
            if (child is RowCard row) run.Add(row);
            else Close();
        }
        Close();

        void Close()
        {
            for (int i = 0; i < run.Count; i++)
            {
                double top = i == 0 ? CardRadius : 0, bottom = i == run.Count - 1 ? CardRadius : 0;
                run[i].CornerRadius = new CornerRadius(top, top, bottom, bottom);
                // Each line draws the hairline above itself; only the last one closes the card.
                run[i].BorderThickness = new Thickness(1, 1, 1, i == run.Count - 1 ? 1 : 0);
            }
            run.Clear();
        }
    }

    private static StackPanel Caption(string title, string? note)
    {
        var caption = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(note)) caption.Children.Add(Small(note));
        return caption;
    }

    /// <summary>One setting: what it is on the left, its control on the right.</summary>
    private static RowCard Row(string title, string? note, FrameworkElement control)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Caption(title, note));
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(20, 0, 0, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return Card(grid);
    }

    /// <summary>One setting whose controls need the whole width: what it is on top, the controls below.</summary>
    private static RowCard WideRow(string title, string? note, params FrameworkElement[] below)
    {
        var column = new StackPanel();
        column.Children.Add(Caption(title, note));
        foreach (var element in below)
        {
            // The gap belongs to the part itself, so a part that is hidden takes its gap with it.
            element.Margin = new Thickness(0, 10, 0, 0);
            column.Children.Add(element);
        }
        return Card(column);
    }

    private static Grid Labeled(string label, FrameworkElement control)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new TextBlock { Text = label, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }
}
