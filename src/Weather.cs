using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace ClearStart;

/// <summary>One day of the forecast.</summary>
internal sealed record WeatherDay(DateTime Date, int Code, double High, double Low, int? RainChance);

/// <summary>The weather at the chosen place, as last fetched.</summary>
internal sealed record WeatherReport(string Place, double Temperature, double FeelsLike, int Humidity, double Wind,
    int Code, bool IsDay, bool Fahrenheit, DateTime Updated, IReadOnlyList<WeatherDay> Days)
{
    public string TemperatureText => Degrees(Temperature) + (Fahrenheit ? "F" : "C");

    public string Conditions => WeatherCodes.Describe(Code, IsDay);

    public string WindText => $"{Math.Round(Wind)} {(Fahrenheit ? "mph" : "km/h")}";

    public static string Degrees(double value) => Math.Round(value).ToString(CultureInfo.CurrentCulture) + "°";
}

/// <summary>A place the search found: "Springfield" and "Illinois, United States".</summary>
internal sealed record PlaceMatch(string Name, string Region, double Latitude, double Longitude)
{
    public string FullName => Region.Length == 0 ? Name : Name + ", " + Region;
}

internal enum WeatherKind
{
    Clear,
    MostlyClear,
    PartlyCloudy,
    Cloudy,
    Fog,
    Drizzle,
    Rain,
    Sleet,
    Snow,
    Thunder,
}

/// <summary>The weather codes the forecast uses (the WMO ones), in words and as a kind of picture.</summary>
internal static class WeatherCodes
{
    public static string Describe(int code, bool day) => code switch
    {
        0 => day ? "Sunny" : "Clear",
        1 => day ? "Mostly sunny" : "Mostly clear",
        2 => day ? "Partly sunny" : "Partly cloudy",
        3 => "Cloudy",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 => "Light snow",
        73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 => "Light showers",
        81 => "Showers",
        82 => "Heavy showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm, hail",
        _ => "Weather",
    };

    public static WeatherKind KindOf(int code) => code switch
    {
        0 => WeatherKind.Clear,
        1 => WeatherKind.MostlyClear,
        2 => WeatherKind.PartlyCloudy,
        45 or 48 => WeatherKind.Fog,
        51 or 53 or 55 => WeatherKind.Drizzle,
        61 or 63 or 65 or 80 or 81 or 82 => WeatherKind.Rain,
        56 or 57 or 66 or 67 => WeatherKind.Sleet,
        71 or 73 or 75 or 77 or 85 or 86 => WeatherKind.Snow,
        95 or 96 or 99 => WeatherKind.Thunder,
        _ => WeatherKind.Cloudy,
    };
}

/// <summary>
/// Fetches the weather for the place in the settings from Open-Meteo (free, no account or key) and keeps
/// it fresh. Nothing is asked of the service until a place has been chosen; after that, only the place's
/// coordinates and the units are sent.
/// </summary>
internal sealed class WeatherService : IDisposable
{
    private const string ForecastAddress = "https://api.open-meteo.com/v1/forecast";
    private const string SearchAddress = "https://geocoding-api.open-meteo.com/v1/search";

    private static readonly HttpClient Http = CreateClient();

    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _stop = new();
    private string _reportFor = "";
    private DateTime _lastAttempt = DateTime.MinValue;
    private bool _busy;
    private bool _disposed;

    public WeatherService()
    {
        // Looks once a minute whether the report has become old enough to fetch again; this also catches up
        // by itself after the PC has been asleep.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += (_, _) => Check();
    }

    /// <summary>The latest report for the place in the settings, or null while there is none.</summary>
    public WeatherReport? Report { get; private set; }

    /// <summary>Why the report could not be brought up to date, in a few words; null when all is well.</summary>
    public string? Problem { get; private set; }

    /// <summary>Raised on the UI thread when the report or the problem changed.</summary>
    public event Action? Changed;

    public void Start()
    {
        _timer.Start();
        Check();
    }

    /// <summary>Fetch now, whatever the age of the report.</summary>
    public void Refresh() => _ = FetchAsync();

    /// <summary>The place or the units may have changed: drop a report that no longer fits and fetch again.</summary>
    public void SettingsChanged()
    {
        if (_disposed) return;
        if (Report != null && Key(Settings.Current.Weather) != _reportFor)
        {
            Report = null;
            Changed?.Invoke();
        }
        Check();
    }

    private void Check()
    {
        if (_disposed || !_timer.IsEnabled) return;
        var settings = Settings.Current.Weather;
        if (!settings.Show || !settings.HasPlace) return;

        var wait = Problem != null ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(settings.RefreshMinutes);
        if (Key(settings) != _reportFor || DateTime.UtcNow - _lastAttempt >= wait) _ = FetchAsync();
    }

    private async Task FetchAsync()
    {
        var settings = Settings.Current.Weather;
        if (_busy || _disposed || !settings.HasPlace) return;

        _busy = true;
        _lastAttempt = DateTime.UtcNow;
        string key = Key(settings);
        try
        {
            bool fahrenheit = UsesFahrenheit(settings.Units);
            string address = ForecastUrl(settings.Latitude!.Value, settings.Longitude!.Value, fahrenheit);
            string json = await Http.GetStringAsync(address, _stop.Token);
            var report = Parse(json, settings.Place, fahrenheit, DateTime.Now);
            if (_disposed) return;

            if (Report == null || Problem != null) Log.Info("Weather is up to date");
            Report = report;
            _reportFor = key;
            Problem = null;
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            if (Problem == null) Log.Warn("Weather update failed: " + ex.Message);
            Problem = "Could not reach the weather service";
            // A report for another place or in other units would be wrong; an old one for this place is still useful.
            if (key != _reportFor) Report = null;
        }
        finally
        {
            _busy = false;
        }

        Changed?.Invoke();
        // The place may have been changed while this was on its way.
        if (Key(Settings.Current.Weather) != key) Check();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _stop.Cancel();
    }

    // ------------------------------------------------------------------ talking to the service

    /// <summary>Finds places by name or postal code. The text typed is sent to Open-Meteo's place search.</summary>
    public static async Task<IReadOnlyList<PlaceMatch>> FindPlacesAsync(string text, CancellationToken cancel)
    {
        string address = $"{SearchAddress}?name={Uri.EscapeDataString(text.Trim())}&count=8&language=en&format=json";
        string json = await Http.GetStringAsync(address, cancel);
        return ParsePlaces(json);
    }

    public static bool UsesFahrenheit(TemperatureUnits units) => units switch
    {
        TemperatureUnits.Fahrenheit => true,
        TemperatureUnits.Celsius => false,
        _ => !RegionInfo.CurrentRegion.IsMetric,
    };

    private static string Key(WeatherSettings settings) => FormattableString.Invariant(
        $"{settings.Latitude:0.###},{settings.Longitude:0.###},{UsesFahrenheit(settings.Units)}");

    private static string ForecastUrl(double latitude, double longitude, bool fahrenheit) => FormattableString.Invariant(
        $"{ForecastAddress}?latitude={latitude:0.###}&longitude={longitude:0.###}") +
        "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code,wind_speed_10m" +
        "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
        (fahrenheit ? "&temperature_unit=fahrenheit&wind_speed_unit=mph" : "") +
        "&timezone=auto&forecast_days=5";

    /// <summary>Reads a forecast reply. Throws if the reply is not a forecast.</summary>
    public static WeatherReport Parse(string json, string place, bool fahrenheit, DateTime now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var current = root.GetProperty("current");

        var days = new List<WeatherDay>();
        if (root.TryGetProperty("daily", out var daily) && daily.TryGetProperty("time", out var dates))
        {
            for (int i = 0; i < dates.GetArrayLength(); i++)
            {
                if (!DateTime.TryParseExact(dates[i].GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date))
                {
                    continue;
                }
                double? high = At(daily, "temperature_2m_max", i);
                double? low = At(daily, "temperature_2m_min", i);
                if (high == null || low == null) continue;
                double? rain = At(daily, "precipitation_probability_max", i);
                days.Add(new WeatherDay(date, (int)(At(daily, "weather_code", i) ?? 3), high.Value, low.Value,
                    rain == null ? null : (int)Math.Round(rain.Value)));
            }
        }

        double temperature = current.GetProperty("temperature_2m").GetDouble();
        return new WeatherReport(
            place,
            temperature,
            Number(current, "apparent_temperature") ?? temperature,
            (int)Math.Round(Number(current, "relative_humidity_2m") ?? 0),
            Number(current, "wind_speed_10m") ?? 0,
            (int)(Number(current, "weather_code") ?? 3),
            (Number(current, "is_day") ?? 1) != 0,
            fahrenheit,
            now,
            days);
    }

    public static IReadOnlyList<PlaceMatch> ParsePlaces(string json)
    {
        var places = new List<PlaceMatch>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var results)) return places;

        foreach (var result in results.EnumerateArray())
        {
            string? name = Text(result, "name");
            double? latitude = Number(result, "latitude");
            double? longitude = Number(result, "longitude");
            if (string.IsNullOrEmpty(name) || latitude == null || longitude == null) continue;

            var region = new[] { Text(result, "admin1"), Text(result, "country") }
                .Where(part => !string.IsNullOrEmpty(part) && part != name);
            // Two decimals is about a kilometre: plenty for a forecast, and no more exact than it needs to be.
            places.Add(new PlaceMatch(name, string.Join(", ", region),
                Math.Round(latitude.Value, 2), Math.Round(longitude.Value, 2)));
        }
        return places;
    }

    private static double? Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? At(JsonElement parent, string name, int index) =>
        parent.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array && index < list.GetArrayLength()
            && list[index].ValueKind == JsonValueKind.Number
            ? list[index].GetDouble()
            : null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClearStart/0.1");
        return client;
    }
}
