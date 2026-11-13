using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ManagedShell.Common.Helpers;
using ManagedShell.ShellFolders;

namespace ClearStart;

/// <summary>
/// The contents of the start menu: a search box, every installed app, the pinned tiles, and a
/// column of places and power actions. Typing filters the app list, and adds the Windows settings and
/// tools that match (see <see cref="WindowsFeatures"/>); Enter opens the best match. The pinned tiles can be dragged
/// into another order or onto one another to make a folder, and an app dragged from the list onto them is pinned.
/// </summary>
public partial class StartMenuView : UserControl
{
    private readonly ShellHost _host;
    private readonly Action _close;
    private readonly ListCollectionView _apps;
    private string _query = "";
    private object? _pressedApp;
    private bool _refreshing;
    private bool _resultsPending;

    /// <summary>Marks a drag as one of ours; what is dragged is kept in <see cref="_dragging"/>.</summary>
    private const string DragFormat = "ClearStart.StartTile";

    /// <summary>The folder shown in place of the pins, or null.</summary>
    private PinFolder? _openFolder;
    private Point _pressedAt;
    private IStartTile? _pressedTile;
    /// <summary>While dragging: a tile, or the pin path of an app from the list that is not pinned yet.</summary>
    private object? _dragging;

    internal StartMenuView(ShellHost host, Action close)
    {
        _host = host;
        _close = close;
        InitializeComponent();
        SetUpMotion();

        var settings = Settings.Current.StartMenu;
        Chrome.Background = Ui.Backdrop(settings.Appearance,
            Settings.ParseColor(settings.Tint, Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1C)));

        _apps = new ListCollectionView(host.Apps.Apps)
        {
            CustomSort = new AppOrder(this),
            Filter = item => item is ShellItem app && Rank(app.DisplayName, _query) >= 0,
        };
        AppList.ItemTemplateSelector = new TwoTemplates(
            (DataTemplate)Resources["AppTemplate"], (DataTemplate)Resources["FeatureTemplate"], item => item is WindowsFeature);
        AppList.ItemsSource = _apps;
        ((INotifyCollectionChanged)_apps).CollectionChanged += Apps_Changed;
        PinList.ItemTemplateSelector = new TwoTemplates(
            (DataTemplate)Resources["TileTemplate"], (DataTemplate)Resources["FolderTileTemplate"], item => item is PinFolder);
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
        EndMotion();
        _pinsOffset = 0;
        OpenFolder(null);
    }

    internal void FocusSearch()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    /// <summary>Called when the menu window closes, so the shared app list does not keep this view alive.</summary>
    internal void Detach()
    {
        EndMotion();
        CollectionChangedEventManager.RemoveHandler(_host.Pins.Start, Pins_Changed);
        ((INotifyCollectionChanged)_apps).CollectionChanged -= Apps_Changed;
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

        _refreshing = true;
        try
        {
            _apps.Refresh();
        }
        finally
        {
            _refreshing = false;
        }
        ShowResults(keepSelection: false);
    }, "Search");

    /// <summary>
    /// Every app; or while searching, the apps and the Windows settings and tools that match, best first. At the same
    /// rank an app comes first, and a setting or tool with the same name as an app in the list is left out.
    /// </summary>
    private void ShowResults(bool keepSelection)
    {
        object? selected = keepSelection ? AppList.SelectedItem : null;
        bool searching = _query.Length > 0;
        if (!searching)
        {
            if (!ReferenceEquals(AppList.ItemsSource, _apps)) AppList.ItemsSource = _apps;
        }
        else
        {
            var apps = _apps.OfType<ShellItem>().ToList();
            var names = new HashSet<string>(apps.Select(app => app.DisplayName ?? ""), StringComparer.OrdinalIgnoreCase);
            // One letter brings up apps only; the list of settings is long, and a second letter narrows it.
            var features = _query.Length < 2
                ? new List<(object Item, int Rank)>()
                : WindowsFeatures.All
                    .Where(feature => !names.Contains(feature.Name))
                    .Select(feature => (Item: (object)feature, Rank: feature.Rank(_query)))
                    .Where(match => match.Rank >= 0)
                    .OrderBy(match => match.Rank)
                    .ThenBy(match => ((WindowsFeature)match.Item).Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            // The apps are in order already; a stable sort by rank alone keeps them so, and ahead of the features.
            AppList.ItemsSource = apps.Select(app => (Item: (object)app, Rank: Rank(app.DisplayName, _query)))
                .Concat(features)
                .OrderBy(match => match.Rank)
                .Select(match => match.Item)
                .ToList();
        }

        int count = AppList.Items.Count;
        NoResults.Visibility = searching && count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selected != null && AppList.Items.Contains(selected))
        {
            AppList.SelectedItem = selected;
            return;
        }
        AppList.SelectedIndex = searching && count > 0 ? 0 : -1;
        ScrollToTop();
    }

    /// <summary>Apps installed or removed while searching: the results are made again, once the list has changed.</summary>
    private void Apps_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_refreshing || _resultsPending || _query.Length == 0) return;
        _resultsPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _resultsPending = false;
            if (_query.Length > 0 && AppList.ItemsSource != null) Ui.Safe(() => ShowResults(keepSelection: true), "Search");
        }));
    }

    private void ScrollToTop()
    {
        if (AppList.Items.Count > 0) AppList.ScrollIntoView(AppList.Items[0]);
    }

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Renaming a folder: Enter keeps the name, Esc puts the old one back. The other keys are the text box's.
        if (FolderName.IsKeyboardFocusWithin)
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                e.Handled = true;
                if (e.Key == Key.Escape) FolderName.Text = _openFolder?.Name ?? "";
                else CommitFolderName();
                FocusSearch();
            }
            return;
        }

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
                else if (_openFolder != null) OpenFolder(null, animate: true);
                else _close();
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        int count = AppList.Items.Count;
        if (count == 0) return;

        int index = AppList.SelectedIndex < 0
            ? (delta > 0 ? 0 : count - 1)
            : Math.Clamp(AppList.SelectedIndex + delta, 0, count - 1);
        AppList.SelectedIndex = index;
        if (AppList.SelectedItem != null) AppList.ScrollIntoView(AppList.SelectedItem);
    }

    /// <summary>Enter: the highlighted result, else the best match, else run the text as a command.</summary>
    private void OpenSelection()
    {
        bool elevated = Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift);

        object? item = AppList.SelectedItem;
        if (item == null && _query.Length > 0 && AppList.Items.Count > 0) item = AppList.Items[0];

        if (item != null)
        {
            Open(item, elevated);
        }
        else if (_query.Length > 0)
        {
            string command = _query;
            _close();
            Launcher.Run(command, elevated: elevated);
        }
    }

    // ------------------------------------------------------------------ apps and pins

    /// <summary>Opens an app, or a Windows setting or tool from the search results.</summary>
    private void Open(object item, bool elevated = false)
    {
        switch (item)
        {
            case ShellItem app:
                Launcher.Launch(app, elevated);
                break;
            case WindowsFeature feature:
                // A settings page is not a program, and cannot run as administrator.
                Launcher.Run(feature.Target, elevated: elevated && feature.CanElevate);
                break;
            default:
                return;
        }
        _close();
    }

    private void App_Press(object sender, MouseButtonEventArgs e)
    {
        _pressedApp = (sender as FrameworkElement)?.DataContext;
        _pressedAt = e.GetPosition(this);
    }

    /// <summary>An app dragged from the list: dropped on the pins, it is pinned there. One pinned already moves.</summary>
    private void App_Move(object sender, MouseEventArgs e)
    {
        if (_pressedApp == null || e.LeftButton != MouseButtonState.Pressed || !DraggedFar(e.GetPosition(this))) return;
        var pressed = _pressedApp;
        // A drag is not a click, whatever it was dragged.
        _pressedApp = null;
        if (pressed is not ShellItem app || AppCatalog.PinPath(app) is not { Length: > 0 } path) return;
        object dragged = (object?)_host.Pins.FindStart(path) ?? path;
        Ui.Safe(() => Drag((FrameworkElement)sender, dragged), "Drag");
    }

    private void App_Click(object sender, MouseButtonEventArgs e)
    {
        // Only a press and release on the same row counts, so ending a scrollbar drag over a row opens nothing.
        var pressed = _pressedApp;
        _pressedApp = null;
        if ((sender as FrameworkElement)?.DataContext is { } item && ReferenceEquals(item, pressed))
        {
            Ui.Safe(() => Open(item), "Open");
        }
    }

    private void App_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (element.DataContext is ShellItem app)
        {
            e.Handled = true;
            Ui.Safe(() => ShowItemMenu(element, app, AppCatalog.PinPath(app)), "App menu");
        }
        else if (element.DataContext is WindowsFeature feature)
        {
            e.Handled = true;
            Ui.Safe(() => ShowFeatureMenu(element, feature), "Feature menu");
        }
    }

    /// <summary>Right-click on a Windows setting or tool: open it, or keep it in the column of places.</summary>
    private void ShowFeatureMenu(UIElement target, WindowsFeature feature)
    {
        var places = Settings.Current.StartMenu.Places;
        var place = places.FirstOrDefault(entry =>
            string.Equals(entry.Target?.Trim(), feature.Target, StringComparison.OrdinalIgnoreCase));

        void Edit(Action change)
        {
            change();
            ShowPlaces();
            _host.Edited(SettingsChange.None);
        }

        var entries = new List<(string? Header, Action? Action)>
        {
            ("Open", () => Open(feature)),
            ("Run as administrator", feature.CanElevate ? () => Open(feature, elevated: true) : null),
            (null, null),
        };
        entries.Add(place == null
            ? ("Add to places", () => Edit(() =>
                places.Add(new PlaceEntry { Name = feature.Name, Target = feature.Target, Glyph = feature.Glyph })))
            : ("Remove from places", () => Edit(() => places.Remove(place))));
        Ui.ShowMenu(target, entries.ToArray());
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.DataContext)
        {
            case PinnedItem pin:
                bool elevated = Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
                Ui.Safe(() => Open(pin.Item, elevated), "Open");
                break;
            case PinFolder folder:
                Ui.Safe(() => OpenFolder(folder, animate: true), "Open folder");
                break;
        }
    }

    private void Pin_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        switch (element.DataContext)
        {
            case PinnedItem pin:
                e.Handled = true;
                Ui.Safe(() => ShowItemMenu(element, pin.Item, pin.Path, pin), "Pin menu");
                break;
            case PinFolder folder:
                e.Handled = true;
                Ui.Safe(() => ShowFolderMenu(element, folder), "Folder menu");
                break;
        }
    }

    /// <param name="path">What a pin of this item stores: a file path, or the app's id in the apps folder.</param>
    /// <param name="tile">The pinned tile right-clicked, which can also be moved; null for an app in the list.</param>
    private void ShowItemMenu(UIElement target, ShellItem item, string? path, PinnedItem? tile = null)
    {
        bool hasPath = !string.IsNullOrEmpty(path);
        string? file = AppCatalog.FileOf(path);
        var pins = _host.Pins;

        var entries = new List<MenuEntry>
        {
            new("Open", () => Open(item)),
            new("Run as administrator", () => Open(item, elevated: true)),
            new(null, null),
        };

        if (pins.IsPinnedToStart(path)) entries.Add(new("Unpin from Start", () => Rearrange(() => pins.UnpinFromStart(path!))));
        else entries.Add(new("Pin to Start", hasPath ? () => Rearrange(() => pins.PinToStart(path!)) : null));

        if (pins.IsPinnedToTaskbar(path)) entries.Add(new("Unpin from taskbar", () => pins.UnpinFromTaskbar(path!)));
        else entries.Add(new("Pin to taskbar", hasPath ? () => pins.PinToTaskbar(path!) : null));

        if (tile != null) entries.AddRange(TileEntries(tile));

        entries.Add(new(null, null));
        entries.Add(new("Open file location", file != null ? () => { Launcher.ShowInExplorer(file); _close(); } : null));

        Ui.ShowMenu(target, PlacementMode.MousePoint, entries);
    }

    /// <summary>Moving a pinned tile, for those who would rather not drag: to the front, or into or out of a folder.</summary>
    private List<MenuEntry> TileEntries(PinnedItem pin)
    {
        var pins = _host.Pins;
        var folder = pins.FolderOf(pin);
        IList list = folder == null ? pins.Start : folder.Items;
        int at = list.IndexOf(pin);

        var folders = pins.Start.OfType<PinFolder>().Where(other => !ReferenceEquals(other, folder))
            .Select(other => new MenuEntry(other.Name, () => Rearrange(() => pins.MoveStart(pin, other, -1))))
            .ToList();
        if (folders.Count > 0) folders.Add(new(null, null));
        folders.Add(new("New folder", () =>
        {
            if (pins.NewFolder(pin) is { } made) RenameFolder(made);
        }));

        var entries = new List<MenuEntry>
        {
            new(null, null),
            new("Move to front", at > 0 ? () => Rearrange(() => pins.MoveStart(pin, folder, 0)) : null),
            new("Move to folder", null, Children: folders),
        };
        if (folder != null) entries.Add(new("Take out of folder", () => Rearrange(() => pins.MoveOutOfFolder(pin))));
        entries.Add(new("Sort A to Z", list.Count > 1 ? () => Rearrange(() => pins.SortStart(folder)) : null));
        return entries;
    }

    private void ShowFolderMenu(UIElement target, PinFolder folder)
    {
        var pins = _host.Pins;
        int at = pins.Start.IndexOf(folder);
        if (at < 0) return;
        Ui.ShowMenu(target,
            ("Open", () => OpenFolder(folder, animate: true)),
            ("Rename", () => RenameFolder(folder)),
            (null, null),
            ("Move to front", at > 0 ? () => Rearrange(() => pins.MoveStart(folder, null, 0)) : null),
            ("Sort its apps A to Z", folder.Items.Count > 1 ? () => pins.SortStart(folder) : null),
            (null, null),
            ("Ungroup apps", () => Rearrange(() => pins.Ungroup(folder))));
    }

    /// <summary>Right-click between the tiles.</summary>
    private void Pins_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        var folder = _openFolder;
        var pins = _host.Pins;
        IList list = folder == null ? pins.Start : folder.Items;
        if (folder == null)
        {
            Ui.ShowMenu(PinsArea, ("Sort A to Z", list.Count > 1 ? () => Rearrange(() => pins.SortStart(null)) : null));
        }
        else
        {
            Ui.ShowMenu(PinsArea,
                ("Back to the pins", () => OpenFolder(null, animate: true)),
                ("Sort A to Z", list.Count > 1 ? () => Rearrange(() => pins.SortStart(folder)) : null));
        }
    }

    private void Pins_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // The open folder was ungrouped, emptied, or read again from the settings.
        if (_openFolder != null && !_host.Pins.Start.Contains(_openFolder)) OpenFolder(null);
        UpdatePinHint();
    }

    private void UpdatePinHint() =>
        PinHint.Visibility = _openFolder == null && _host.Pins.Start.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ------------------------------------------------------------------ folders

    /// <summary>
    /// Shows a folder's pins in place of the start menu's own (null: back to those). With <paramref name="animate"/>,
    /// the folder grows out of its tile, and closing, goes back into it.
    /// </summary>
    private void OpenFolder(PinFolder? folder, bool animate = false)
    {
        CommitFolderName();
        if (folder != null && !_host.Pins.Start.Contains(folder)) folder = null;
        if (ReferenceEquals(folder, _openFolder) && PinList.ItemsSource != null) return;
        var from = _openFolder;
        StopFolderMotion();
        BitmapSource? before = null;
        Rect? tile = null;
        if (animate && Motion.On && IsVisible)
        {
            UpdateLayout();
            before = Snapshot(PinStage);
            if (folder != null) tile = TileBounds(folder);
        }

        if (from == null) _pinsOffset = PinScroller.VerticalOffset;
        _openFolder = folder;
        PinList.ItemsSource = folder == null ? _host.Pins.Start : folder.Items;
        PinCard.Background = folder == null ? null : FolderCardBrush;
        PinCard.Padding = new Thickness(folder == null ? 0 : 4);
        PinsHeader.Visibility = folder == null ? Visibility.Visible : Visibility.Hidden;
        FolderHeader.Visibility = folder == null ? Visibility.Collapsed : Visibility.Visible;
        FolderName.Text = folder?.Name ?? "";
        // Back at the pins, they are where they were left.
        PinScroller.ScrollToVerticalOffset(folder == null ? _pinsOffset : 0);
        UpdatePinHint();
        if (before == null) return;

        UpdateLayout();
        FrameworkElement? closedInto = null;
        if (folder == null && from != null)
        {
            tile = TileBounds(from);
            closedInto = TileOf(from);
        }
        Motion.To(folder == null ? PinsHeader : FolderHeader, OpacityProperty, 1, 200, Motion.Out, from: 0);
        PlayFolderMotion(before, opening: folder != null, tile, closedInto);
    }

    /// <summary>Opens the folder and puts the cursor in its name, all of it chosen, ready to be typed over.</summary>
    private void RenameFolder(PinFolder folder)
    {
        OpenFolder(folder, animate: true);
        if (_openFolder != folder) return;
        FolderName.Focus();
        Keyboard.Focus(FolderName);
        FolderName.SelectAll();
    }

    private void CommitFolderName()
    {
        if (_openFolder == null) return;
        if (FolderName.Text != _openFolder.Name) _host.Pins.RenameFolder(_openFolder, FolderName.Text);
        // A blank name comes back as the usual one.
        FolderName.Text = _openFolder.Name;
    }

    private void FolderName_LostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        Ui.Safe(CommitFolderName, "Rename folder");

    private void FolderBack_Click(object sender, RoutedEventArgs e) =>
        Ui.Safe(() => OpenFolder(null, animate: true), "Close folder");

    // ------------------------------------------------------------------ dragging

    private void Tile_Press(object sender, MouseButtonEventArgs e)
    {
        _pressedTile = (sender as FrameworkElement)?.DataContext as IStartTile;
        _pressedAt = e.GetPosition(this);
    }

    private void Tile_Move(object sender, MouseEventArgs e)
    {
        if (_pressedTile == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pressedTile = null;
            return;
        }
        if (!DraggedFar(e.GetPosition(this))) return;
        var tile = _pressedTile;
        _pressedTile = null;
        Ui.Safe(() => Drag((FrameworkElement)sender, tile), "Drag");
    }

    private bool DraggedFar(Point now) =>
        Math.Abs(now.X - _pressedAt.X) >= SystemParameters.MinimumHorizontalDragDistance
        || Math.Abs(now.Y - _pressedAt.Y) >= SystemParameters.MinimumVerticalDragDistance;

    /// <summary>
    /// Drags a tile, or an app's pin path, until it is dropped. Its icon floats under the pointer meanwhile, and the
    /// one dragged stays faint in its place; let go anywhere else, the icon goes back there.
    /// </summary>
    private void Drag(FrameworkElement source, object dragged)
    {
        _dragging = dragged;
        _pressedApp = null;
        _pressedTile = null;
        _dropped = false;
        try
        {
            // The button or list keeps the mouse otherwise, and would take the drop for a click.
            Mouse.Capture(null);
            LiftGhost(source, dragged);
            Motion.Set(source, OpacityProperty, source.DataContext is IStartTile ? 0.25 : 0.6);
            DragDrop.DoDragDrop(source, new DataObject(DragFormat, "ClearStart"),
                dragged is string ? DragDropEffects.Copy : DragDropEffects.Move);
        }
        finally
        {
            _dragging = null;
            HideDropMarks();
            if (_dropped) Restore(source);
            else ReturnGhost(source);
        }
    }

    /// <summary>
    /// Where a drop would go: before or after a tile (<paramref name="Index"/>, counting the tiles as they are now), or
    /// onto one (<paramref name="Onto"/>), into a folder or to make a folder of the two. <paramref name="Moves"/> is false
    /// for a tile dropped where it already is.
    /// </summary>
    private readonly record struct PinDrop(int Index, IStartTile? Onto, Rect Mark, bool Moves);

    private PinDrop DropPlace(object dragged, Point at)
    {
        var items = PinList.Items;
        var cells = new List<(int Index, IStartTile Tile, Rect Bounds)>();
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is IStartTile tile && PinList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement cell
                && cell.IsVisible && cell.ActualWidth > 0)
            {
                cells.Add((i, tile, cell.TransformToVisual(DropLayer).TransformBounds(new Rect(cell.RenderSize))));
            }
        }
        int own = dragged is IStartTile draggedTile ? items.IndexOf(draggedTile) : -1;
        if (cells.Count == 0) return new PinDrop(items.Count, null, Rect.Empty, own < 0);

        int index;
        Rect mark;
        if (at.Y > cells.Max(cell => cell.Bounds.Bottom))
        {
            // Below the tiles: after the last one.
            var last = cells[^1].Bounds;
            index = items.Count;
            mark = LineAt(last.Right, last);
        }
        else
        {
            var (near, tile, bounds) = cells.OrderBy(cell => Distance(cell.Bounds, at)).First();
            double across = (at.X - bounds.Left) / bounds.Width;
            double down = (at.Y - bounds.Top) / bounds.Height;
            if (across is >= 0.25 and <= 0.75 && down is >= 0.2 and <= 0.8 && CanDropOnto(dragged, tile))
            {
                return new PinDrop(-1, tile, bounds, true);
            }
            bool after = across > 0.5;
            index = after ? near + 1 : near;
            mark = LineAt(after ? bounds.Right : bounds.Left, bounds);
        }
        return new PinDrop(index, null, mark, own < 0 || (index != own && index != own + 1));

        static Rect LineAt(double x, Rect cell) => new(x - 1.5, cell.Top + 8, 3, Math.Max(0, cell.Height - 16));

        static double Distance(Rect bounds, Point point)
        {
            double x = Math.Max(Math.Max(bounds.Left - point.X, 0), point.X - bounds.Right);
            double y = Math.Max(Math.Max(bounds.Top - point.Y, 0), point.Y - bounds.Bottom);
            return x * x + y * y;
        }
    }

    /// <summary>A folder takes any pin not in it already; two pins make a folder, but not inside a folder.</summary>
    private bool CanDropOnto(object dragged, IStartTile target)
    {
        if (dragged is PinFolder || ReferenceEquals(dragged, target)) return false;
        return target switch
        {
            PinFolder folder => dragged is not PinnedItem pin || !folder.Items.Contains(pin),
            PinnedItem onto => _openFolder == null && !(dragged is string path
                && string.Equals(path, onto.Path, StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    private void Pins_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_dragging is not { } dragged || !e.Data.GetDataPresent(DragFormat))
        {
            e.Effects = DragDropEffects.None;
            HideDropMarks();
            return;
        }
        e.Effects = dragged is string ? DragDropEffects.Copy : DragDropEffects.Move;

        // Near the top or the bottom, the tiles scroll.
        var at = e.GetPosition(PinsArea);
        const double edge = 28;
        if (at.Y < edge) PinScroller.ScrollToVerticalOffset(PinScroller.VerticalOffset - 12);
        else if (at.Y > PinsArea.ActualHeight - edge) PinScroller.ScrollToVerticalOffset(PinScroller.VerticalOffset + 12);

        ShowDropMark(DropPlace(dragged, e.GetPosition(DropLayer)));
    }

    private void Pins_DragLeave(object sender, DragEventArgs e)
    {
        // Raised too as the mouse goes from one tile to the next, still over the pins.
        var at = e.GetPosition(PinsArea);
        if (new Rect(PinsArea.RenderSize).Contains(at)) return;
        HideDropMarks();
    }

    private void Pins_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        HideDropMarks();
        if (_dragging is not { } dragged || !e.Data.GetDataPresent(DragFormat)) return;
        var drop = DropPlace(dragged, e.GetPosition(DropLayer));
        var folder = _openFolder;
        AfterDrop(dragged, () => Place(dragged, drop, folder));
    }

    /// <summary>Puts what was dropped where the drop showed it would go, in the folder that was open then (or none).</summary>
    private void Place(object dragged, PinDrop drop, PinFolder? folder)
    {
        if (!drop.Moves) return;
        var pins = _host.Pins;
        if (dragged is string path)
        {
            switch (drop.Onto)
            {
                case PinFolder into:
                    pins.PinToStart(path, into, -1);
                    break;
                case PinnedItem onto:
                    if (pins.PinToStart(path, null, -1) is { } pin) pins.Group(pin, onto);
                    break;
                default:
                    pins.PinToStart(path, folder, drop.Index);
                    break;
            }
            return;
        }
        if (dragged is not IStartTile tile) return;
        switch (drop.Onto)
        {
            case PinFolder into:
                pins.MoveStart(tile, into, -1);
                break;
            case PinnedItem onto when tile is PinnedItem pin:
                pins.Group(pin, onto);
                break;
            default:
                pins.MoveStart(tile, folder, drop.Index);
                break;
        }
    }

    private void ShowDropMark(PinDrop drop)
    {
        if (!drop.Moves || drop.Mark.IsEmpty)
        {
            HideDropMarks();
            return;
        }
        HoverOnto(drop.Onto);
        var shown = drop.Onto != null ? DropTarget : DropLine;
        var hidden = drop.Onto != null ? DropLine : DropTarget;
        var mark = drop.Onto != null ? Rect.Inflate(drop.Mark, -2, -2) : drop.Mark;
        Canvas.SetLeft(shown, mark.Left);
        Canvas.SetTop(shown, mark.Top);
        shown.Width = Math.Max(0, mark.Width);
        shown.Height = Math.Max(0, mark.Height);
        shown.Visibility = Visibility.Visible;
        hidden.Visibility = Visibility.Collapsed;
    }

    private void HideDropMarks()
    {
        DropLine.Visibility = Visibility.Collapsed;
        DropTarget.Visibility = Visibility.Collapsed;
        HoverOnto(null);
    }

    /// <summary>A pin dropped on the arrow back to the pins comes out of the open folder.</summary>
    private void FolderBack_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = _dragging is PinnedItem pin && _openFolder != null && _openFolder.Items.Contains(pin)
            ? DragDropEffects.Move
            : DragDropEffects.None;
    }

    private void FolderBack_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_dragging is not PinnedItem pin || _openFolder == null || !_openFolder.Items.Contains(pin)) return;
        AfterDrop(pin, () => _host.Pins.MoveOutOfFolder(pin));
    }

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

    internal void ShowFolder(PinFolder? folder, bool animate = false) => OpenFolder(folder, animate);

    internal PinFolder? ShownFolder => _openFolder;

    /// <summary>
    /// Drops a tile, or an app's pin path, on a tile shown as a drag would: that far across it (0 to 1), halfway down.
    /// With <paramref name="lift"/>, the dragged tile's icon floats there first, and flies to where it lands.
    /// </summary>
    internal void DropOnTile(object dragged, int tile, double across, bool lift = false)
    {
        UpdateLayout();
        if (PinList.ItemContainerGenerator.ContainerFromIndex(tile) is not FrameworkElement cell) return;
        var bounds = cell.TransformToVisual(DropLayer).TransformBounds(new Rect(cell.RenderSize));
        var at = new Point(bounds.Left + bounds.Width * across, bounds.Top + bounds.Height / 2);
        if (lift && TileOf(dragged) is { } source) LiftGhost(source, dragged, DropLayer.TranslatePoint(at, MotionLayer));
        var drop = DropPlace(dragged, at);
        Settle(dragged, () => Place(dragged, drop, _openFolder));
    }

    internal bool Floating => Ghost.Visibility == Visibility.Visible;

    /// <summary>What is still moved, scaled, faint or shown once every animation should be over; null when nothing.</summary>
    internal string? Unsettled()
    {
        if (Ghost.Visibility == Visibility.Visible) return "the dragged icon is still floating";
        if (PinsBefore.Visibility == Visibility.Visible) return "the picture of the pins before is still shown";
        if (_stageScale.ScaleX != 1 || _stageScale.ScaleY != 1 || PinStage.Opacity != 1) return "the pins are not back at full size";
        foreach (var item in PinList.Items)
        {
            if (TileOf(item) is not { } tile) continue;
            if (tile.Opacity != 1) return $"a tile is at opacity {tile.Opacity}";
            if (!tile.RenderTransform.Value.IsIdentity) return "a tile is still moved or scaled";
        }
        return null;
    }

    internal List<string> TopResults(int count) =>
        AppList.Items.Cast<object>().Take(count).Select(item => item switch
        {
            ShellItem app => app.DisplayName ?? "?",
            WindowsFeature feature => feature.Name,
            _ => "?",
        }).ToList();

    /// <summary>The usual look for an item, or another for the other kind: a Windows setting's row, a folder's tile.</summary>
    private sealed class TwoTemplates : DataTemplateSelector
    {
        private readonly DataTemplate _usual;
        private readonly DataTemplate _other;
        private readonly Func<object, bool> _isOther;

        public TwoTemplates(DataTemplate usual, DataTemplate other, Func<object, bool> isOther)
        {
            _usual = usual;
            _other = other;
            _isOther = isOther;
        }

        public override DataTemplate SelectTemplate(object item, DependencyObject container) =>
            _isOther(item) ? _other : _usual;
    }

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
