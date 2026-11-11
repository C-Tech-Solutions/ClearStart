using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ClearStart;

/// <summary>The settings window: an ordinary, resizable window with a dark title bar.</summary>
internal sealed class SettingsWindow : Window
{
    private readonly SettingsView _view;

    public SettingsWindow(ShellHost host)
    {
        Title = "ClearStart settings";
        Width = 880;
        Height = 660;
        MinWidth = 700;
        MinHeight = 420;
        Background = Ui.Brush(Color.FromRgb(0x20, 0x20, 0x20));
        // The middle of the monitor the mouse is on, which is where the menu that opened it was.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _view = new SettingsView(host);
        Content = _view;

        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        // A bar's own menu may have changed something (the lock, a bar's position) while this was behind.
        Activated += (_, _) => _view.LoadValues();
    }

    /// <summary>Shows the window, or brings it forward; turned to the given page if one is named.</summary>
    public void Open(SettingsPage? page)
    {
        if (page != null) _view.Show(page.Value);
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>The settings file was changed by hand: show what it says now.</summary>
    public void Reload() => _view.LoadValues();
}
