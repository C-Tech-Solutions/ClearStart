using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ManagedShell.AppBar;
using Windows.System.Power;

namespace ClearStart;

/// <summary>
/// The panel that opens from Windows' battery icon in the tray: the charge, plugged in or not, the time left or
/// until full, and each battery on its own when the PC has more than one.
/// </summary>
internal static class BatteryFlyout
{
    private const string PowerSettings = "ms-settings:powersleep";

    private static readonly Brush LineBrush = Ui.Brush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    // The battery bars: fixed colours, as the accent colour can be too dark to see on the panel.
    private static readonly Brush ChargeBrush = Ui.Brush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ChargingBrush = Ui.Brush(Color.FromRgb(0x6C, 0xCB, 0x5F));
    private static readonly Brush LowBrush = Ui.Brush(Color.FromRgb(0xFF, 0xC0, 0x6A));

    private static Popup? _open;
    private static DateTime _closedAt = DateTime.MinValue;
    private static bool _reading;

    /// <summary>Opens the panel next to the icon, or closes it if that same click just closed it.</summary>
    public static async void Toggle(FrameworkElement icon, AppBarEdge edge)
    {
        // Clicking the icon while the panel is open closes it (the click lands outside the panel); without this
        // pause the same click would open it again.
        if (_open != null || (DateTime.UtcNow - _closedAt).TotalMilliseconds < 300)
        {
            Close();
            return;
        }
        if (_reading) return;

        _reading = true;
        try
        {
            var power = await Batteries.ReadAsync();
            // The bar, or the hidden icons, may have closed while the batteries were read.
            if (!icon.IsVisible) return;

            Log.Info("Battery panel: " + Batteries.Describe(power));
            var popup = new Popup
            {
                Child = Build(power),
                PlacementTarget = icon,
                AllowsTransparency = true,
                StaysOpen = false,
                PopupAnimation = PopupAnimation.Fade,
            };
            Ui.PlaceAgainstBar(popup, edge);
            popup.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_open, popup)) return;
                _open = null;
                _closedAt = DateTime.UtcNow;
            };
            _open = popup;
            popup.IsOpen = true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the battery panel", ex);
        }
        finally
        {
            _reading = false;
        }
    }

    public static void Close()
    {
        var popup = _open;
        _open = null;
        if (popup != null) popup.IsOpen = false;
    }

    /// <summary>The panel's contents (also drawn by the self-test).</summary>
    public static FrameworkElement Build(PowerReading power)
    {
        var column = new StackPanel { Width = 280 };
        column.Children.Add(Text(power.Batteries.Count > 1 ? "Batteries" : "Battery", 14, FontWeights.SemiBold));

        // All of them together: the charge, and what it is doing.
        var whole = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        whole.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        whole.ColumnDefinitions.Add(new ColumnDefinition());
        var percent = Text(power.Percent is int p ? p + "%" : "–", 36, FontWeights.Light);
        percent.VerticalAlignment = VerticalAlignment.Center;
        whole.Children.Add(percent);
        var details = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(Text(power.Percent == null ? "No battery found" : Batteries.Status(power), 13, FontWeights.SemiBold));
        if (Batteries.Estimate(power) is { } estimate) details.Children.Add(Text(estimate, 12, opacity: 0.75));
        Grid.SetColumn(details, 1);
        whole.Children.Add(details);
        column.Children.Add(whole);

        // Each on its own, when there is more than one.
        if (power.Batteries.Count > 1)
        {
            column.Children.Add(Line());
            var rows = new Grid();
            rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            rows.ColumnDefinitions.Add(new ColumnDefinition());
            rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            for (int i = 0; i < power.Batteries.Count; i++)
            {
                var battery = power.Batteries[i];
                rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
                Put(rows, i, 0, Text($"Battery {i + 1}", 13), HorizontalAlignment.Left);
                Put(rows, i, 1, Gauge(battery.Percent ?? 0, battery.Status == BatteryStatus.Charging), HorizontalAlignment.Stretch);
                Put(rows, i, 2, Text(battery.Percent is int bp ? bp + "%" : "–", 13), HorizontalAlignment.Right);
                Put(rows, i, 3, Text(Batteries.State(battery, power.PluggedIn), 12, opacity: 0.75), HorizontalAlignment.Right);
            }
            column.Children.Add(rows);
        }

        column.Children.Add(Line());
        var settings = new Button
        {
            Style = (Style)Application.Current.FindResource("FlatButton"),
            Content = Text("Power & battery settings", 12),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(-8, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        settings.Click += (_, _) => Ui.Safe(() =>
        {
            Close();
            Launcher.Run(PowerSettings);
        }, "Power settings");
        column.Children.Add(settings);

        return Ui.FlyoutPanel(column);
    }

    /// <summary>A bar filled as far as the battery is: green while it charges, amber when it is low.</summary>
    private static Grid Gauge(int percent, bool charging)
    {
        var gauge = new Grid { Height = 6, Margin = new Thickness(0, 0, 10, 0) };
        gauge.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(percent, GridUnitType.Star) });
        gauge.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - percent, GridUnitType.Star) });
        var track = new Border { Background = LineBrush, CornerRadius = new CornerRadius(3) };
        Grid.SetColumnSpan(track, 2);
        gauge.Children.Add(track);
        gauge.Children.Add(new Border
        {
            Background = charging ? ChargingBrush : percent <= 20 ? LowBrush : ChargeBrush,
            CornerRadius = new CornerRadius(3),
        });
        return gauge;
    }

    private static Border Line() => new() { Height = 1, Margin = new Thickness(0, 12, 0, 6), Background = LineBrush };

    private static TextBlock Text(string text, double size, FontWeight? weight = null, double opacity = 1) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Opacity = opacity,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static void Put(Grid grid, int row, int column, FrameworkElement element, HorizontalAlignment alignment)
    {
        element.HorizontalAlignment = alignment;
        element.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
