using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ManagedShell.WindowsTray;

namespace ClearStart;

/// <summary>
/// The panel of tray icons that are not on the bar: what the arrow beside the tray opens. Also drawn on its
/// own by the self-test.
/// </summary>
public partial class TrayOverflowView : UserControl
{
    // As many to a row as the Windows panel has.
    private const int PerRow = 5;

    private readonly Action<NotifyIcon>? _hide;

    /// <param name="icons">The hidden icons (or stand-ins with Icon and Title).</param>
    /// <param name="iconSize">How big the icons are on the bar; they are the same size here.</param>
    /// <param name="hide">Takes an icon dropped here off the bar; null where nothing can be dropped.</param>
    internal TrayOverflowView(IEnumerable? icons, double iconSize, Action<NotifyIcon>? hide)
    {
        _hide = hide;
        InitializeComponent();

        double cell = iconSize + 16;
        Resources["TrayIconSize"] = iconSize;
        Resources["TrayCellSize"] = cell;
        Icons.MaxWidth = PerRow * cell;

        ((INotifyCollectionChanged)Icons.Items).CollectionChanged += (_, _) => ShowWhetherEmpty();
        Icons.ItemsSource = icons;
        ShowWhetherEmpty();
    }

    /// <summary>Raised when a tray icon has been dropped here.</summary>
    internal event Action? Dropped;

    /// <summary>How many icons the panel shows: for the self-test.</summary>
    internal int IconCount => Icons.Items.Count;

    /// <summary>Lets go of the icon list, which outlives the panel.</summary>
    internal void Detach() => Icons.ItemsSource = null;

    private void ShowWhetherEmpty()
    {
        bool empty = Icons.Items.Count == 0;
        Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Icons.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Icon_MouseEnter(object sender, MouseEventArgs e) => TrayIconInput.Enter(sender);

    private void Icon_MouseLeave(object sender, MouseEventArgs e) => TrayIconInput.Leave(sender);

    private void Icon_MouseMove(object sender, MouseEventArgs e) => TrayIconInput.Move(sender, e);

    private void Icon_MouseDown(object sender, MouseButtonEventArgs e) => TrayIconInput.Down(sender, e);

    private void Icon_MouseUp(object sender, MouseButtonEventArgs e) => TrayIconInput.Up(sender, e);

    private void Icon_LostCapture(object sender, MouseEventArgs e) => TrayIconInput.LostCapture(sender);

    private void Panel_DragOver(object sender, DragEventArgs e)
    {
        // Only an icon from the bar can be hidden: one from here is hidden already.
        e.Effects = _hide != null && TrayIconInput.DraggedIcon(e) is { IsPinned: true }
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Panel_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_hide == null || TrayIconInput.DraggedIcon(e) is not { IsPinned: true } icon) return;
        Dropped?.Invoke();
        // Once the drag is over: the icon on the bar it came from is still the drag's source until then.
        Dispatcher.InvokeAsync(() => _hide(icon));
    }
}
