using System.Windows.Threading;
using Connectivity = Windows.Networking.Connectivity;
using Nics = System.Net.NetworkInformation;

namespace ClearStart;

internal enum NetworkKind
{
    /// <summary>Not connected to any network.</summary>
    None,
    Wired,
    WiFi,
    Cellular,
    /// <summary>Connected through something else (a tunnel, a tethered phone).</summary>
    Other,
}

/// <summary>What the network glyph on the taskbar shows.</summary>
/// <param name="Internet">False when connected to a network that has no way out to the internet.</param>
/// <param name="Name">The network's name as Windows shows it ("Network 3", a Wi-Fi name).</param>
/// <param name="Bars">Wi-Fi signal strength, 0 to 5.</param>
internal sealed record NetworkState(NetworkKind Kind, bool Internet, string Name, int Bars)
{
    public static readonly NetworkState Disconnected = new(NetworkKind.None, false, "", 0);

    /// <summary>The character in the Segoe icon font for this state.</summary>
    public string Glyph => Kind switch
    {
        NetworkKind.None => "\uF384",                         // globe with a "no" sign
        NetworkKind.Wired => Internet ? "\uE839" : "\uEB56",  // PC with a cable; the same with a warning
        NetworkKind.WiFi when !Internet => "\uEB63",          // Wi-Fi with a warning
        NetworkKind.WiFi => Bars switch
        {
            <= 1 => "\uE872",
            2 => "\uE873",
            3 => "\uE874",
            _ => "\uE701",
        },
        NetworkKind.Cellular => "\uE870",                     // signal bars
        _ => "\uE774",                                        // globe
    };

    public string Description
    {
        get
        {
            if (Kind == NetworkKind.None) return "Not connected to a network";
            string kind = Kind switch
            {
                NetworkKind.Wired => "Wired network",
                NetworkKind.WiFi => "Wi-Fi",
                NetworkKind.Cellular => "Mobile network",
                _ => "Network",
            };
            string name = Name.Length > 0 ? $"{kind} ({Name})" : kind;
            return name + (Internet ? ": connected" : ": no internet");
        }
    }
}

/// <summary>
/// Keeps track of how this PC is connected: by cable, by Wi-Fi, or not at all. Windows announces changes;
/// a slow timer also looks again, which is what keeps the Wi-Fi signal strength current.
/// </summary>
internal sealed class NetworkWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _timer;
    private bool _started;
    private bool _disposed;
    private bool _reading;

    public NetworkWatcher()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => ReadSoon();
    }

    public NetworkState State { get; private set; } = NetworkState.Disconnected;

    /// <summary>Raised on the UI thread when the state changed.</summary>
    public event Action? Changed;

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        ReadNow();
        try
        {
            Connectivity.NetworkInformation.NetworkStatusChanged += OnStatusChanged;
        }
        catch (Exception ex)
        {
            Log.Warn("Network changes will only be noticed by the timer: " + ex.Message);
        }
        _timer.Start();
    }

    /// <summary>Looks right now, on the calling thread. Used before the first bar is drawn.</summary>
    public void ReadNow() => Apply(Read());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        if (!_started) return;
        try
        {
            Connectivity.NetworkInformation.NetworkStatusChanged -= OnStatusChanged;
        }
        catch
        {
            // Nothing to undo if Windows never accepted the subscription.
        }
    }

    // Windows calls this on one of its own threads.
    private void OnStatusChanged(object? sender) => _dispatcher.BeginInvoke(new Action(ReadSoon));

    /// <summary>Asking Windows can take a moment, so it is done off the UI thread.</summary>
    private async void ReadSoon()
    {
        if (_disposed || _reading) return;
        _reading = true;
        try
        {
            var state = await Task.Run(Read);
            if (!_disposed) Apply(state);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the network state: " + ex.Message);
        }
        finally
        {
            _reading = false;
        }
    }

    private void Apply(NetworkState state)
    {
        if (state == State) return;
        State = state;
        Log.Info("Network: " + state.Description);
        Changed?.Invoke();
    }

    private static NetworkState Read()
    {
        try
        {
            // The connection Windows itself uses to reach the internet, if there is one.
            var profile = Connectivity.NetworkInformation.GetInternetConnectionProfile();
            var level = profile?.GetNetworkConnectivityLevel() ?? Connectivity.NetworkConnectivityLevel.None;
            if (profile == null || level == Connectivity.NetworkConnectivityLevel.None) return WithoutInternet();

            bool internet = level is Connectivity.NetworkConnectivityLevel.InternetAccess
                or Connectivity.NetworkConnectivityLevel.ConstrainedInternetAccess;
            string name = profile.ProfileName ?? "";

            if (profile.IsWlanConnectionProfile)
            {
                return new NetworkState(NetworkKind.WiFi, internet, name, profile.GetSignalBars() ?? 4);
            }
            if (profile.IsWwanConnectionProfile) return new NetworkState(NetworkKind.Cellular, internet, name, 0);

            // 6 is the registered number for Ethernet.
            uint type = profile.NetworkAdapter?.IanaInterfaceType ?? 0;
            return new NetworkState(type == 6 ? NetworkKind.Wired : NetworkKind.Other, internet, name, 0);
        }
        catch (Exception ex)
        {
            Log.Warn("Windows would not say how this PC is connected: " + ex.Message);
            return WithoutInternet();
        }
    }

    /// <summary>
    /// No route to the internet. A cable or Wi-Fi link to a router still counts as connected (with a warning);
    /// virtual adapters, which are always "up" and lead nowhere, do not.
    /// </summary>
    private static NetworkState WithoutInternet()
    {
        try
        {
            foreach (var adapter in Nics.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != Nics.OperationalStatus.Up) continue;

                bool wifi = adapter.NetworkInterfaceType == Nics.NetworkInterfaceType.Wireless80211;
                bool wired = adapter.NetworkInterfaceType is Nics.NetworkInterfaceType.Ethernet
                    or Nics.NetworkInterfaceType.GigabitEthernet or Nics.NetworkInterfaceType.FastEthernetT;
                if (!wifi && !wired) continue;

                // A real network hands out a gateway; a virtual switch on this PC has none.
                if (adapter.GetIPProperties().GatewayAddresses.Count == 0) continue;
                return new NetworkState(wifi ? NetworkKind.WiFi : NetworkKind.Wired, false, adapter.Name, 0);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not list the network adapters: " + ex.Message);
        }
        return NetworkState.Disconnected;
    }
}
