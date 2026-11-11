using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using ManagedShell.WindowsTasks;

namespace ClearStart;

internal enum AppState
{
    /// <summary>A pinned launcher with nothing open.</summary>
    Idle,
    Inactive,
    Active,
    Flashing,
}

/// <summary>The names under which a pin and a window are recognised as the same app.</summary>
internal static class AppKey
{
    public static string ForFile(string path) => "file:" + path;

    public static string ForAppId(string id) => "id:" + id;
}

/// <summary>
/// One button on a taskbar: a pinned launcher, an app with open windows, or both at once.
/// All of an app's windows sit behind its one button.
/// </summary>
internal sealed class AppButton : INotifyPropertyChanged
{
    private IReadOnlyList<ApplicationWindow> _windows = Array.Empty<ApplicationWindow>();
    private string _appName = "";
    private string _title;
    private string _label = "";
    private AppState _state;

    public AppButton(string key, PinnedItem? pin)
    {
        Key = key;
        Pin = pin;
        _title = pin?.Name ?? "";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }

    public PinnedItem? Pin { get; }

    public bool IsPinned => Pin != null;

    public IReadOnlyList<ApplicationWindow> Windows => _windows;

    /// <summary>The window whose icon stands for the app when it is not pinned.</summary>
    public ApplicationWindow? First => _windows.Count > 0 ? _windows[0] : null;

    public int Count => _windows.Count;

    public bool IsRunning => _windows.Count > 0;

    public bool IsMultiple => _windows.Count > 1;

    public AppState State => _state;

    /// <summary>Tooltip text.</summary>
    public string Title => _title;

    /// <summary>Text shown next to the icon when labels are switched on.</summary>
    public string Label => _label;

    public string AppName => _appName.Length > 0 ? _appName : _title;

    /// <summary>Brings the button up to date with the windows that now belong to it.</summary>
    public void Update(List<ApplicationWindow> windows)
    {
        if (!windows.SequenceEqual(_windows))
        {
            var oldFirst = First;
            int oldCount = Count;
            _windows = windows;

            if (!ReferenceEquals(oldFirst, First))
            {
                _appName = FindAppName();
                Raise(nameof(First));
            }
            if (oldCount != Count)
            {
                Raise(nameof(Count));
                Raise(nameof(IsRunning));
                Raise(nameof(IsMultiple));
            }
        }

        var state = AppState.Idle;
        foreach (var window in _windows)
        {
            if (window.State == ApplicationWindow.WindowState.Active)
            {
                state = AppState.Active;
                break;
            }
            if (window.State == ApplicationWindow.WindowState.Flashing)
            {
                state = AppState.Flashing;
            }
            else if (state == AppState.Idle)
            {
                state = AppState.Inactive;
            }
        }
        if (state != _state)
        {
            _state = state;
            Raise(nameof(State));
        }

        string title;
        string label;
        switch (_windows.Count)
        {
            case 0:
                title = Pin?.Name ?? "";
                label = "";
                break;
            case 1:
                title = Text(_windows[0].Title, AppName);
                label = title;
                break;
            default:
                label = AppName;
                title = $"{label} ({_windows.Count} windows)";
                break;
        }
        if (title != _title)
        {
            _title = title;
            Raise(nameof(Title));
        }
        if (label != _label)
        {
            _label = label;
            Raise(nameof(Label));
        }
    }

    private string FindAppName()
    {
        if (Pin != null) return Pin.Name;
        var first = First;
        if (first == null) return "";
        try
        {
            // Store apps all run inside the same host program, so its description says nothing about them.
            string? description = first.IsUWP ? null : first.WinFileDescription;
            return Text(description, first.Title ?? "");
        }
        catch
        {
            return first.Title ?? "";
        }
    }

    private static string Text(string? preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>
/// The buttons of one taskbar, kept in step with the pinned apps and the open windows:
/// pinned apps first, in their pinned order, then every other running app in the order it was opened.
/// </summary>
internal sealed class TaskbarApps : IDisposable
{
    private readonly ShellHost _host;
    private readonly IntPtr _monitor;
    private readonly bool _combine;
    private readonly bool _takeStrays;
    private readonly Func<PinnedItem, bool>? _showsPin;
    private readonly Dispatcher _dispatcher;
    private readonly HashSet<ApplicationWindow> _watched = new();
    private Dictionary<string, AppButton> _buttons = new(StringComparer.OrdinalIgnoreCase);
    private int _queued;
    private bool _disposed;

    /// <param name="monitor">Only windows on this monitor are listed; zero lists the windows of every monitor.</param>
    /// <param name="combine">One button per app (true) or one per window (false).</param>
    /// <param name="takeStrays">Also list the windows on monitors that have no bar (the main bar does).</param>
    /// <param name="showsPin">
    /// Says whether this bar shows a pinned app; left out, it shows them all. Asked again every time the
    /// buttons are worked out, so it can follow the settings. A pin that is not shown still has a button
    /// while its app has a window here, the same as any app that is not pinned.
    /// </param>
    public TaskbarApps(ShellHost host, IntPtr monitor, bool combine, bool takeStrays = false,
        Func<PinnedItem, bool>? showsPin = null)
    {
        _host = host;
        _monitor = monitor;
        _combine = combine;
        _takeStrays = takeStrays;
        _showsPin = showsPin;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _host.TaskView.CollectionChanged += Source_Changed;
        _host.Pins.Taskbar.CollectionChanged += Source_Changed;
        Reconcile();
    }

    public ObservableCollection<AppButton> Items { get; } = new();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.TaskView.CollectionChanged -= Source_Changed;
        _host.Pins.Taskbar.CollectionChanged -= Source_Changed;
        foreach (var window in _watched)
        {
            window.PropertyChanged -= Window_Changed;
        }
        _watched.Clear();
    }

    /// <summary>Looks again at what belongs here: a display gained or lost its bar, or the pins shown here changed.</summary>
    public void Refresh() => Queue();

    private void Source_Changed(object? sender, NotifyCollectionChangedEventArgs e) => Queue();

    private void Window_Changed(object? sender, PropertyChangedEventArgs e)
    {
        // Can arrive on a background thread (icons load there); only these affect the buttons.
        switch (e.PropertyName)
        {
            case nameof(ApplicationWindow.State):
            case nameof(ApplicationWindow.Title):
            case nameof(ApplicationWindow.HMonitor):
            case null:
            case "":
                Queue();
                break;
        }
    }

    /// <summary>Several changes usually arrive together (a focus switch changes two windows); handle them in one go.</summary>
    private void Queue()
    {
        if (_disposed || Interlocked.Exchange(ref _queued, 1) == 1) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Reconcile));
    }

    private void Reconcile()
    {
        Interlocked.Exchange(ref _queued, 0);
        if (_disposed) return;
        try
        {
            var windows = _host.TaskView.Cast<ApplicationWindow>().ToList();
            Watch(windows);

            var order = new List<AppButton>();
            var members = new Dictionary<AppButton, List<ApplicationWindow>>();
            var byAppKey = new Dictionary<string, AppButton>(StringComparer.OrdinalIgnoreCase);
            var next = new Dictionary<string, AppButton>(StringComparer.OrdinalIgnoreCase);

            AppButton Use(string key, PinnedItem? pin)
            {
                if (!next.TryGetValue(key, out var button))
                {
                    if (!_buttons.TryGetValue(key, out button) || !ReferenceEquals(button.Pin, pin))
                    {
                        button = new AppButton(key, pin);
                    }
                    next[key] = button;
                    order.Add(button);
                    members[button] = new List<ApplicationWindow>();
                }
                return button;
            }

            foreach (var pin in _host.Pins.Taskbar)
            {
                if (_showsPin != null && !_showsPin(pin)) continue;
                var button = Use("pin:" + pin.Path, pin);
                if (!_combine) continue;
                foreach (string key in pin.AppKeys)
                {
                    byAppKey.TryAdd(key, button);
                }
            }

            foreach (var window in windows)
            {
                if (_monitor != IntPtr.Zero)
                {
                    IntPtr on = window.HMonitor;
                    bool stray = _takeStrays && !_host.HasBarOn(on);
                    if (on != IntPtr.Zero && on != _monitor && !stray) continue;
                }

                AppButton? button = null;
                if (_combine)
                {
                    var keys = KeysOf(window);
                    foreach (string key in keys)
                    {
                        if (byAppKey.TryGetValue(key, out button)) break;
                    }
                    if (button == null)
                    {
                        button = Use("app:" + keys[0], null);
                        byAppKey[keys[0]] = button;
                    }
                }
                else
                {
                    button = Use("window:" + window.Handle, null);
                }
                members[button].Add(window);
            }

            _buttons = next;
            foreach (var button in order)
            {
                button.Update(members[button]);
            }

            for (int i = 0; i < order.Count; i++)
            {
                int at = Items.IndexOf(order[i]);
                if (at < 0)
                {
                    Items.Insert(i, order[i]);
                }
                else if (at != i)
                {
                    Items.Move(at, i);
                }
            }
            while (Items.Count > order.Count)
            {
                Items.RemoveAt(Items.Count - 1);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not update the taskbar buttons", ex);
        }
    }

    private void Watch(List<ApplicationWindow> windows)
    {
        var current = new HashSet<ApplicationWindow>(windows);
        foreach (var gone in _watched.Where(w => !current.Contains(w)).ToList())
        {
            gone.PropertyChanged -= Window_Changed;
            _watched.Remove(gone);
        }
        foreach (var window in windows)
        {
            if (_watched.Add(window)) window.PropertyChanged += Window_Changed;
        }
    }

    /// <summary>A window's program file and/or app id; the first one names its group when no pin claims it.</summary>
    internal static List<string> KeysOf(ApplicationWindow window)
    {
        var keys = new List<string>(2);
        try
        {
            string? file = window.IsUWP ? null : window.WinFileName;
            if (!string.IsNullOrEmpty(file)) keys.Add(AppKey.ForFile(file));

            string? id = window.AppUserModelID;
            if (!string.IsNullOrEmpty(id)) keys.Add(AppKey.ForAppId(id));
        }
        catch
        {
            // Some windows belong to processes that cannot be asked; they get a button of their own.
        }
        if (keys.Count == 0) keys.Add("window:" + window.Handle);
        return keys;
    }
}
