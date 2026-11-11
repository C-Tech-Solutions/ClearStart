using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using ManagedShell.AppBar;

namespace ClearStart;

/// <summary>The panel that opens from the weather on the taskbar: the weather now and the next few days.</summary>
internal static class WeatherFlyout
{
    private static readonly Brush RainBrush = Ui.Brush(Color.FromRgb(0x7C, 0xC2, 0xFF));
    private static readonly Brush ProblemBrush = Ui.Brush(Color.FromRgb(0xFF, 0xC0, 0x6A));

    private static Popup? _open;
    private static DateTime _closedAt = DateTime.MinValue;

    /// <summary>Opens the panel next to the button, or closes it if that same click just closed it.</summary>
    public static void Toggle(UIElement target, AppBarEdge edge, WeatherReport report, string? problem)
    {
        // Clicking the button while the panel is open closes it (the click lands outside the panel);
        // without this pause the same click would open it again.
        if (_open != null || (DateTime.UtcNow - _closedAt).TotalMilliseconds < 300)
        {
            Close();
            return;
        }

        const double gap = 8;
        var popup = new Popup
        {
            Child = Build(report, problem),
            PlacementTarget = target,
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = PopupAnimation.Fade,
        };
        switch (edge)
        {
            case AppBarEdge.Top:
                popup.Placement = PlacementMode.Bottom;
                popup.VerticalOffset = gap;
                break;
            case AppBarEdge.Left:
                popup.Placement = PlacementMode.Right;
                popup.HorizontalOffset = gap;
                break;
            case AppBarEdge.Right:
                popup.Placement = PlacementMode.Left;
                popup.HorizontalOffset = -gap;
                break;
            default:
                popup.Placement = PlacementMode.Top;
                popup.VerticalOffset = -gap;
                break;
        }

        popup.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_open, popup)) return;
            _open = null;
            _closedAt = DateTime.UtcNow;
        };
        _open = popup;
        popup.IsOpen = true;
    }

    public static void Close()
    {
        var popup = _open;
        _open = null;
        if (popup != null) popup.IsOpen = false;
    }

    /// <summary>The panel's contents (also drawn by the self-test).</summary>
    public static FrameworkElement Build(WeatherReport report, string? problem)
    {
        var column = new StackPanel { Width = 300 };

        column.Children.Add(Text(report.Place.Length > 0 ? report.Place : "Weather", 14, FontWeights.SemiBold));

        // Now: picture, temperature, and the details beside them.
        var now = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        now.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        now.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        now.ColumnDefinitions.Add(new ColumnDefinition());

        now.Children.Add(Icon(report.Code, report.IsDay, 54));

        var temperature = Text(report.TemperatureText, 36, FontWeights.Light);
        temperature.Margin = new Thickness(12, 0, 0, 0);
        temperature.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(temperature, 1);
        now.Children.Add(temperature);

        var details = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(Text(report.Conditions, 13, FontWeights.SemiBold));
        details.Children.Add(Text("Feels like " + WeatherReport.Degrees(report.FeelsLike), 12, opacity: 0.75));
        details.Children.Add(Text($"Humidity {report.Humidity}%", 12, opacity: 0.75));
        details.Children.Add(Text("Wind " + report.WindText, 12, opacity: 0.75));
        Grid.SetColumn(details, 2);
        now.Children.Add(details);
        column.Children.Add(now);

        if (report.Days.Count > 0)
        {
            column.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 14, 0, 8),
                Background = Ui.Brush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            });

            var days = new Grid();
            days.ColumnDefinitions.Add(new ColumnDefinition());
            days.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            days.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            days.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });

            foreach (var day in report.Days)
            {
                int row = days.RowDefinitions.Count;
                days.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });

                string name = day.Date.Date == report.Updated.Date
                    ? "Today"
                    : day.Date.ToString("dddd", CultureInfo.CurrentCulture);
                Put(days, row, 0, Text(name, 13), HorizontalAlignment.Left);
                // A forecast for a whole day is shown with the daytime picture.
                Put(days, row, 1, Icon(day.Code, true, 22), HorizontalAlignment.Center);

                var rain = Text(day.RainChance is >= 20 ? day.RainChance + "%" : "", 12);
                rain.Foreground = RainBrush;
                Put(days, row, 2, rain, HorizontalAlignment.Right);

                var range = new TextBlock { FontSize = 13 };
                range.Inlines.Add(new Run(WeatherReport.Degrees(day.High)));
                range.Inlines.Add(new Run("  " + WeatherReport.Degrees(day.Low)) { Foreground = Ui.Brush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)) });
                Put(days, row, 3, range, HorizontalAlignment.Right);
            }
            column.Children.Add(days);
        }

        if (problem != null)
        {
            var warning = Text(problem + ". This is the last report.", 11);
            warning.Foreground = ProblemBrush;
            warning.Margin = new Thickness(0, 10, 0, 0);
            warning.TextWrapping = TextWrapping.Wrap;
            column.Children.Add(warning);
        }

        var footer = Text($"Updated {report.Updated.ToString("t", CultureInfo.CurrentCulture)}  ·  Weather data by Open-Meteo.com",
            11, opacity: 0.55);
        footer.Margin = new Thickness(0, 10, 0, 0);
        column.Children.Add(footer);

        var panel = new Border
        {
            Background = (Brush)Application.Current.FindResource("MenuBackgroundBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("MenuBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 16, 12),
            Child = column,
            SnapsToDevicePixels = true,
        };
        // A popup made in code is not inside any window, so nothing is inherited: say it all here.
        TextElement.SetForeground(panel, Brushes.White);
        TextElement.SetFontFamily(panel, (FontFamily)Application.Current.FindResource("UiFont"));
        TextElement.SetFontSize(panel, 13);
        return panel;
    }

    private static TextBlock Text(string text, double size, FontWeight? weight = null, double opacity = 1) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Opacity = opacity,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static Viewbox Icon(int code, bool day, double size) => new()
    {
        Width = size,
        Height = size,
        Child = WeatherIcons.Draw(code, day),
        VerticalAlignment = VerticalAlignment.Center,
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
