using System.Windows;
using System.Windows.Controls.Primitives;

namespace ClearStart;

// The button the main bar shows while a newer version of ClearStart is out (see Updates.cs).
public partial class TaskbarView
{
    private void UpdateButton_Loaded(object sender, RoutedEventArgs e)
    {
        _host.Updates.Changed -= RefreshUpdate;
        _host.Updates.Changed += RefreshUpdate;
        RefreshUpdate();
    }

    private void UpdateButton_Unloaded(object sender, RoutedEventArgs e) => _host.Updates.Changed -= RefreshUpdate;

    /// <summary>Shows the button on the main bar while there is a newer version, and says what is happening with it.</summary>
    private void RefreshUpdate()
    {
        var updates = _host.Updates;
        if (!_main || updates.Latest == null)
        {
            UpdateButton.Visibility = Visibility.Collapsed;
            return;
        }

        bool busy = updates.State is UpdateState.Downloading or UpdateState.Installing;
        // A download arrow while there is news, turning arrows while it is being acted on.
        UpdateGlyph.Text = busy ? "" : "";
        UpdateButton.ToolTip = busy ? updates.Status : updates.Status + "\nClick to install it or to read what is new";
        if (_vertical) UpdateButton.Padding = new Thickness(2, 6, 2, 6);
        UpdateButton.Visibility = Visibility.Visible;
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        _host.HidePreview();
        var entries = UpdateMenu();
        if (entries.Count > 0) Ui.ShowMenu(UpdateButton, PlacementMode.MousePoint, entries);
    }

    /// <summary>What clicking the button offers; nothing while there is no newer version.</summary>
    internal List<MenuEntry> UpdateMenu()
    {
        var updates = _host.Updates;
        var entries = new List<MenuEntry>();
        if (updates.Latest is not { } latest) return entries;
        string version = latest.VersionText;
        if (!updates.CanInstall)
        {
            // A copy run from a build folder: it says that there is news, but does not install it.
            entries.Add(new MenuEntry($"Get version {version} from GitHub", updates.OpenReleasePage));
        }
        else
        {
            if (updates.State == UpdateState.Downloading) entries.Add(new MenuEntry($"Downloading… {updates.Progress}%", null));
            if (updates.State == UpdateState.Installing) entries.Add(new MenuEntry("Installing…", null));
            entries.Add(new MenuEntry($"Install version {version} now", updates.CanStartInstall ? updates.Install : null));
            entries.Add(new MenuEntry($"What's new in version {version}", updates.OpenReleasePage));
        }
        entries.Add(new MenuEntry(null, null));
        entries.Add(new MenuEntry("Update settings…", () => _host.ShowSettings(SettingsPage.About)));
        return entries;
    }
}
