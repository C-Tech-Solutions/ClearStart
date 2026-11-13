using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ManagedShell.Common.Helpers;
using ManagedShell.ShellFolders;

namespace ClearStart;

/// <summary>
/// The contents of the start menu: a search box, every installed app, the pinned tiles, and a
/// column of places and power actions. Typing filters the app list; Enter opens the best match.
/// </summary>
public partial class StartMenuView : UserControl
{
    private readonly ShellHost _host;
    private readonly Action _close;
    private readonly ListCollectionView _apps;
    private string _query = "";
    private object? _pressedApp;

    internal StartMenuView(ShellHost host, Action close)
    {
        _host = host;
        _close = close;
        InitializeComponent();

        var settings = Settings.Current.StartMenu;
        Chrome.Background = Ui.Backdrop(settings.Appearance,
            Settings.ParseColor(settings.Tint, Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1C)));

        _apps = new ListCollectionView(host.Apps.Apps)
        {
            CustomSort = new AppOrder(this),
            Filter = item => item is ShellItem app && Rank(app.DisplayName, _query) >= 0,
        };
        AppList.ItemsSource = _apps;
        PinList.ItemsSource = host.Pins.Start;
        UserName.Text = host.UserName;
        ArrangePlaces(settings);

        CollectionChangedEventManager.AddHandler(host.Pins.Start, Pins_Changed);
        UpdatePinHint();
    }

    /// <summary>Back to the unfiltered list, ready for the next time the menu opens.</summary>
    internal void Reset()
    {
        SearchBox.Clear();
        AppList.SelectedIndex = -1;
        ScrollToTop();
    }

    internal void FocusSearch()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    /// <summary>Called when the menu window closes, so the shared app list does not keep this view alive.</summary>
    internal void Detach()
    {
        CollectionChangedEventManager.RemoveHandler(_host.Pins.Start, Pins_Changed);
        AppList.ItemsSource = null;
        PinList.ItemsSource = null;
        _apps.DetachFromSourceCollection();
    }

    // ------------------------------------------------------------------ search

    /// <summary>
    /// How well a name matches what was typed; lower is better, -1 is no match.
    /// 0 exact, 1 starts with it, 2 a later word starts with it, 3 contains it, 4 initials ("vsc" = Visual Studio Code).
    /// </summary>
    internal static int Rank(string? name, string query)
    {
        if (query.Length == 0) return 0;
        if (string.IsNullOrEmpty(name)) return -1;

        const StringComparison ignoreCase = StringComparison.OrdinalIgnoreCase;
        if (name.Equals(query, ignoreCase)) return 0;
        if (name.StartsWith(query, ignoreCase)) return 1;

        bool contains = false;
        for (int at = name.IndexOf(query, ignoreCase); at >= 0; at = name.IndexOf(query, at + 1, ignoreCase))
        {
            contains = true;
            if (at > 0 && !char.IsLetterOrDigit(name[at - 1])) return 2;
        }
        if (contains) return 3;

        if (query.Length >= 2 && Initials(name).StartsWith(query, ignoreCase)) return 4;
        return -1;
    }

    private static string Initials(string name)
    {
        var initials = new System.Text.StringBuilder();
        bool atWordStart = true;
        foreach (char c in name)
        {
            if (!char.IsLetterOrDigit(c))
            {
                atWordStart = true;
            }
            else if (atWordStart)
            {
                initials.Append(c);
                atWordStart = false;
            }
        }
        return initials.ToString();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => Ui.Safe(() =>
    {
        _query = SearchBox.Text.Trim();
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppHeader.Text = _query.Length == 0 ? "All apps" : "Results";

        _apps.Refresh();

        bool searching = _query.Length > 0;
        NoResults.Visibility = searching && _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppList.SelectedIndex = searching && _apps.Count > 0 ? 0 : -1;
        ScrollToTop();
    }, "Search");

    private void ScrollToTop()
    {
        if (_apps.Count > 0) AppList.ScrollIntoView(_apps.GetItemAt(0));
    }

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                break;
            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;
            case Key.PageDown:
                e.Handled = true;
                MoveSelection(10);
                break;
            case Key.PageUp:
                e.Handled = true;
                MoveSelection(-10);
                break;
            case Key.Enter:
                e.Handled = true;
                Ui.Safe(OpenSelection, "Open");
                break;
            case Key.Escape:
                e.Handled = true;
                if (SearchBox.Text.Length > 0) SearchBox.Clear();
                else _close();
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        int count = _apps.Count;
        if (count == 0) return;

        int index = AppList.SelectedIndex < 0
            ? (delta > 0 ? 0 : count - 1)
            : Math.Clamp(AppList.SelectedIndex + delta, 0, count - 1);
        AppList.SelectedIndex = index;
        if (AppList.SelectedItem != null) AppList.ScrollIntoView(AppList.SelectedItem);
    }

    /// <summary>Enter: the highlighted app, else the best match, else run the text as a command.</summary>
    private void OpenSelection()
    {
        bool elevated = Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift);

        var app = AppList.SelectedItem as ShellItem;
        if (app == null && _query.Length > 0 && _apps.Count > 0) app = _apps.GetItemAt(0) as ShellItem;

        if (app != null)
        {
            Open(app, elevated);
        }
        else if (_query.Length > 0)
        {
            string command = _query;
            _close();
            Launcher.Run(command, elevated: elevated);
        }
    }

    // ------------------------------------------------------------------ apps and pins

    private void Open(ShellItem item, bool elevated = false)
    {
        Launcher.Launch(item, elevated);
        _close();
    }

    private void App_Press(object sender, MouseButtonEventArgs e) =>
        _pressedApp = (sender as FrameworkElement)?.DataContext;

    private void App_Click(object sender, MouseButtonEventArgs e)
    {
        // Only a press and release on the same row counts, so ending a scrollbar drag over a row opens nothing.
        var pressed = _pressedApp;
        _pressedApp = null;
        if ((sender as FrameworkElement)?.DataContext is ShellItem app && ReferenceEquals(app, pressed))
        {
            Ui.Safe(() => Open(app), "Open");
        }
    }

    private void App_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ShellItem app) return;
        e.Handled = true;
        Ui.Safe(() => ShowItemMenu(element, app, AppCatalog.PinPath(app)), "App menu");
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PinnedItem pin) return;
        bool elevated = Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
        Ui.Safe(() => Open(pin.Item, elevated), "Open");
    }

    private void Pin_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not PinnedItem pin) return;
        e.Handled = true;
        Ui.Safe(() => ShowItemMenu(element, pin.Item, pin.Path), "Pin menu");
    }

    /// <param name="path">What a pin of this item stores: a file path, or the app's id in the apps folder.</param>
    private void ShowItemMenu(UIElement target, ShellItem item, string? path)
    {
        bool hasPath = !string.IsNullOrEmpty(path);
        string? file = AppCatalog.FileOf(path);
        var pins = _host.Pins;

        var entries = new List<(string? Header, Action? Action)>
        {
            ("Open", () => Open(item)),
            ("Run as administrator", () => Open(item, elevated: true)),
            (null, null),
        };

        if (pins.IsPinnedToStart(path)) entries.Add(("Unpin from Start", () => pins.UnpinFromStart(path!)));
        else entries.Add(("Pin to Start", hasPath ? () => pins.PinToStart(path!) : null));

        if (pins.IsPinnedToTaskbar(path)) entries.Add(("Unpin from taskbar", () => pins.UnpinFromTaskbar(path!)));
        else entries.Add(("Pin to taskbar", hasPath ? () => pins.PinToTaskbar(path!) : null));

        entries.Add((null, null));
        entries.Add(("Open file location", file != null ? () => { Launcher.ShowInExplorer(file); _close(); } : null));

        Ui.ShowMenu(target, entries.ToArray());
    }

    private void Pins_Changed(object? sender, NotifyCollectionChangedEventArgs e) => UpdatePinHint();

    private void UpdatePinHint() =>
        PinHint.Visibility = _host.Pins.Start.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ------------------------------------------------------------------ right column

    private void User_Click(object sender, RoutedEventArgs e)
    {
        Launcher.Run("ms-settings:yourinfo");
        _close();
    }

    /// <summary>
    /// The column of places as the settings describe it: which side it is on, how wide, how tall its
    /// entries are, and whether they have icons.
    /// </summary>
    private void ArrangePlaces(StartMenuSettings settings)
    {
        // However wide it is asked to be, the column leaves room for the app list and some pinned tiles.
        double width = Math.Clamp(settings.PlacesWidth, 140, Math.Max(140, settings.Width - 36 - 270 - 170));
        bool left = settings.PlacesSide == PlacesSide.Left;

        var columns = MenuGrid.ColumnDefinitions;
        columns[left ? 0 : 2].Width = new GridLength(width);
        columns[left ? 1 : 0].Width = new GridLength(270);
        columns[left ? 2 : 1].Width = new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(PlacesPanel, left ? 0 : 2);
        Grid.SetColumn(AppsPanel, left ? 1 : 0);
        Grid.SetColumn(PinsPanel, left ? 2 : 1);
        PlacesPanel.Margin = left ? new Thickness(0, 0, 12, 0) : default;
        PinsPanel.Margin = left ? new Thickness(14, 0, 0, 0) : new Thickness(14, 0, 10, 0);
        UserName.MaxWidth = width - 62;

        // An entry's icon and text grow and shrink with its height; 36 is the usual one.
        double row = Math.Clamp(settings.PlacesRowHeight, 28, 52);
        double glyph = Math.Round(row * 0.44);
        Resources["PlaceRowHeight"] = row;
        Resources["PlaceGlyphSize"] = glyph;
        Resources["PlaceGlyphWidth"] = glyph + 4;
        Resources["PlaceFontSize"] = Math.Clamp(Math.Round(row * 0.36), 12.0, 17.0);
        Resources["PlaceGlyphVisibility"] = settings.PlacesShowIcons ? Visibility.Visible : Visibility.Collapsed;
        Resources["PlaceTextMargin"] = settings.PlacesShowIcons ? new Thickness(12, 0, 0, 0) : new Thickness(2, 0, 0, 0);

        ShowPlaces();
    }

    /// <summary>Lists the places again, after the list changed.</summary>
    private void ShowPlaces()
    {
        var settings = Settings.Current.StartMenu;
        bool any = settings.ShowPlaces && settings.Places.Count > 0;
        PlaceList.ItemsSource = any ? settings.Places.ToList() : null;
        PlaceScroller.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        PlaceRule.Visibility = PlaceScroller.Visibility;
    }

    private void Place_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlaceEntry place) return;
        if (string.IsNullOrWhiteSpace(place.Target) || place.IsDivider) return;
        _close();
        Launcher.Run(place.Target);
    }

    private void Place_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not PlaceEntry place) return;
        e.Handled = true;
        Ui.Safe(() => ShowPlaceMenu(element, place), "Place menu");
    }

    /// <summary>Right-click on a place: move it, remove it, or go and change the whole list.</summary>
    private void ShowPlaceMenu(UIElement target, PlaceEntry place)
    {
        var places = Settings.Current.StartMenu.Places;
        int at = places.IndexOf(place);
        if (at < 0) return;

        void Edit(Action change)
        {
            change();
            ShowPlaces();
            // Saved shortly; nothing else needs to hear of it, and the menu stays open.
            _host.Edited(SettingsChange.None);
        }

        var entries = new List<(string? Header, Action? Action)>();
        if (!place.IsDivider && !string.IsNullOrWhiteSpace(place.Target))
        {
            entries.Add(("Open", () => { _close(); Launcher.Run(place.Target); }));
            entries.Add((null, null));
        }
        entries.Add(("Move up", at > 0 ? () => Edit(() => { places.RemoveAt(at); places.Insert(at - 1, place); }) : null));
        entries.Add(("Move down", at < places.Count - 1
            ? () => Edit(() => { places.RemoveAt(at); places.Insert(at + 1, place); })
            : null));
        entries.Add(("Add a line below", () => Edit(() => places.Insert(at + 1, PlaceEntry.Divider()))));
        entries.Add((place.IsDivider ? "Remove this line" : "Remove from this list", () => Edit(() => places.RemoveAt(at))));
        entries.Add((null, null));
        entries.Add(("Customize this list…", () => { _close(); _host.ShowSettings(SettingsPage.StartMenu); }));
        Ui.ShowMenu(target, entries.ToArray());
    }

    /// <summary>The gear opens Windows' settings, like the one in Windows' start menu.</summary>
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenWindowsSettings();

    /// <summary>ClearStart's own settings are a right-click away (and on a bar's right-click menu).</summary>
    private void Settings_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Ui.ShowMenu(SettingsButton,
            ("Windows Settings", OpenWindowsSettings),
            ("ClearStart settings", () => { _close(); _host.ShowSettings(SettingsPage.StartMenu); }));
    }

    private void OpenWindowsSettings()
    {
        _close();
        Launcher.Run("ms-settings:");
    }

    private void Power_Click(object sender, RoutedEventArgs e) => Ui.ShowMenu(PowerButton,
        ("Lock", () => Leave(() => ShellHelper.Lock())),
        ("Sign out", () => Leave(() => ShellHelper.Logoff())),
        (null, null),
        ("Sleep", () => Leave(() => PowerHelper.Sleep())),
        ("Restart", () => Leave(() => PowerHelper.Reboot())),
        ("Shut down", () => Leave(() => PowerHelper.Shutdown())));

    private void Leave(Action action)
    {
        _close();
        Ui.Safe(action, "Power action");
    }

    // ------------------------------------------------------------------ self-test support

    internal void SetQuery(string text) => SearchBox.Text = text;

    internal int VisibleAppCount => _apps.Count;

    internal List<string> TopResults(int count) =>
        _apps.Cast<object>().Take(count).Select(item => (item as ShellItem)?.DisplayName ?? "?").ToList();

    /// <summary>Best matches first while searching; otherwise alphabetical.</summary>
    private sealed class AppOrder : IComparer
    {
        private readonly StartMenuView _owner;

        public AppOrder(StartMenuView owner) => _owner = owner;

        public int Compare(object? x, object? y)
        {
            string a = (x as ShellItem)?.DisplayName ?? "";
            string b = (y as ShellItem)?.DisplayName ?? "";

            string query = _owner._query;
            if (query.Length > 0)
            {
                int difference = Rank(a, query) - Rank(b, query);
                if (difference != 0) return difference;
            }
            return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
