using System.Windows;
using System.Windows.Controls;

namespace ClearStart;

// The General page's "Updates" lines (see Updates.cs).
internal sealed partial class SettingsView
{
    private void AddUpdateRows(Panel page)
    {
        var updates = _host.Updates;
        page.Children.Add(Group("Updates"));

        // This version, what the updater knows or is doing, and what can be done about it.
        var status = Small("");
        var notes = Button("What's new", updates.OpenReleasePage);
        var install = Button("Install", updates.Install);
        var check = Button("Check now", updates.CheckNow);
        var elsewhere = Hint("This copy of ClearStart was not installed with Setup, so it does not update itself. " +
            "It still says when a newer version is out, and Download opens its page on GitHub.");
        notes.Margin = install.Margin = new Thickness(0, 0, 8, 0);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(notes);
        buttons.Children.Add(install);
        buttons.Children.Add(check);

        var caption = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(new TextBlock { Text = "ClearStart " + VersionText(), TextWrapping = TextWrapping.Wrap });
        caption.Children.Add(status);

        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(caption);
        buttons.Margin = new Thickness(20, 0, 0, 0);
        Grid.SetColumn(buttons, 1);
        line.Children.Add(buttons);
        var card = Card(line);
        page.Children.Add(card);

        void Show()
        {
            bool news = updates.Latest != null;
            status.Text = updates.Status;
            // Download opens the same page in a copy that does not update itself.
            notes.Visibility = news && updates.CanInstall ? Visibility.Visible : Visibility.Collapsed;
            install.Visibility = news ? Visibility.Visible : Visibility.Collapsed;
            // A copy that does not update itself can still fetch the new version from the release page.
            install.Content = updates.CanInstall ? "Install" : "Download";
            install.IsEnabled = !updates.CanInstall || updates.CanStartInstall;
            check.IsEnabled = updates.State is UpdateState.Idle or UpdateState.Ready;
            elsewhere.Visibility = updates.CanInstall ? Visibility.Collapsed : Visibility.Visible;
        }
        _loaders.Add(Show);
        card.Loaded += (_, _) =>
        {
            updates.Changed -= Show;
            updates.Changed += Show;
        };
        card.Unloaded += (_, _) => updates.Changed -= Show;

        page.Children.Add(Row("Check for updates automatically",
            "Asks GitHub about once a day whether a newer version is out. Nothing about you or this PC is sent.",
            Toggle(() => Settings.Current.Updates.CheckAutomatically,
                value => Settings.Current.Updates.CheckAutomatically = value, SettingsChange.Updates)));
        page.Children.Add(Row("Install updates automatically",
            "Downloads a newer version as soon as it is found, and installs it when the PC has not been used for a while",
            Toggle(() => Settings.Current.Updates.InstallAutomatically,
                value => Settings.Current.Updates.InstallAutomatically = value, SettingsChange.Updates)));
        page.Children.Add(elsewhere);
    }
}
