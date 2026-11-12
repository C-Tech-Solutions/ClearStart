using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using ManagedShell.WindowsTray;

namespace ClearStart;

/// <summary>
/// The mouse on a tray icon, on the bar or in the panel of hidden icons. Mostly it goes straight to the app
/// that owns the icon. A left press is held back until it is clear what it is: let go on the icon and the
/// app gets the click; move away with the button down and the icon is being dragged instead, onto the bar
/// to keep it there or onto the arrow to hide it.
/// </summary>
internal static class TrayIconInput
{
    /// <summary>What a dragged tray icon carries, so only our own drop places take it.</summary>
    public const string DragFormat = "ClearStart.TrayIcon";

    // The icon the left button went down on and has not yet been let go of or dragged.
    private static FrameworkElement? _pressed;
    private static Point _pressedAt;

    /// <summary>The tray icon being dragged, while one is.</summary>
    public static NotifyIcon? Dragging { get; private set; }

    /// <summary>Raised when a drag has ended, whether it was dropped somewhere or given up.</summary>
    public static event Action? DragEnded;

    /// <summary>The tray icon a drag over a drop place carries, or null when it carries something else.</summary>
    public static NotifyIcon? DraggedIcon(DragEventArgs e) =>
        Dragging != null && e.Data.GetDataPresent(DragFormat) ? Dragging : null;

    private static int DoubleClickTime => (int)Native.GetDoubleClickTime();

    public static void Enter(object sender)
    {
        if (sender is not FrameworkElement element || element.DataContext is not NotifyIcon icon || Dragging != null) return;
        Ui.Safe(() =>
        {
            // Apps ask where their icon is (to place flyouts); tell the tray service.
            var origin = element.PointToScreen(new Point(0, 0));
            double scale = PresentationSource.FromVisual(element)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            icon.Placement = new NativeMethods.Rect
            {
                Left = (int)origin.X,
                Top = (int)origin.Y,
                Right = (int)(element.ActualWidth * scale),
                Bottom = (int)(element.ActualHeight * scale),
            };
            icon.IconMouseEnter(MouseHelper.GetCursorPositionParam());
        }, "Tray icon hover");
    }

    public static void Leave(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is NotifyIcon icon && Dragging == null)
        {
            Ui.Safe(() => icon.IconMouseLeave(MouseHelper.GetCursorPositionParam()), "Tray icon hover");
        }
    }

    public static void Move(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not NotifyIcon icon) return;

        if (ReferenceEquals(_pressed, element))
        {
            var now = e.GetPosition(element);
            if (e.LeftButton == MouseButtonState.Pressed &&
                (Math.Abs(now.X - _pressedAt.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                 Math.Abs(now.Y - _pressedAt.Y) >= SystemParameters.MinimumVerticalDragDistance))
            {
                _pressed = null;
                element.ReleaseMouseCapture();
                Drag(element, icon);
            }
            return;
        }

        if (Dragging == null) Ui.Safe(() => icon.IconMouseMove(MouseHelper.GetCursorPositionParam()), "Tray icon hover");
    }

    public static void Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not NotifyIcon icon) return;
        e.Handled = true;

        if (e.ChangedButton == MouseButton.Left)
        {
            // Held back: this may be the start of a drag rather than a click.
            _pressed = element;
            _pressedAt = e.GetPosition(element);
            element.CaptureMouse();
            return;
        }

        Ui.Safe(() => icon.IconMouseDown(e.ChangedButton, MouseHelper.GetCursorPositionParam(), DoubleClickTime),
            "Tray icon click");
    }

    public static void Up(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not NotifyIcon icon) return;
        e.Handled = true;

        if (e.ChangedButton != MouseButton.Left)
        {
            Ui.Safe(() => icon.IconMouseUp(e.ChangedButton, MouseHelper.GetCursorPositionParam(), DoubleClickTime),
                "Tray icon click");
            return;
        }

        if (!ReferenceEquals(_pressed, element)) return;
        _pressed = null;
        element.ReleaseMouseCapture();

        // As with any button, letting go somewhere else is not a click.
        var at = e.GetPosition(element);
        if (at.X < 0 || at.Y < 0 || at.X > element.ActualWidth || at.Y > element.ActualHeight) return;

        // The press the app was not told about, and then the release. A second click soon after the first
        // still reaches it as a double click: the tray service times the clicks itself.
        uint mouse = MouseHelper.GetCursorPositionParam();
        Ui.Safe(() =>
        {
            icon.IconMouseDown(MouseButton.Left, mouse, DoubleClickTime);
            icon.IconMouseUp(MouseButton.Left, mouse, DoubleClickTime);
        }, "Tray icon click");
    }

    public static void LostCapture(object sender)
    {
        if (ReferenceEquals(_pressed, sender)) _pressed = null;
    }

    /// <summary>Drags the icon until it is dropped or the drag is given up. The drop places do the moving.</summary>
    private static void Drag(FrameworkElement element, NotifyIcon icon)
    {
        // The app's own hover tip would otherwise stay up for the whole drag.
        Ui.Safe(() => icon.IconMouseLeave(MouseHelper.GetCursorPositionParam()), "Tray icon hover");

        Dragging = icon;
        double opacity = element.Opacity;
        element.Opacity = 0.4;
        double size = (element as Border)?.Child is Image image && image.ActualWidth > 0 ? image.ActualWidth : 16;
        var ghost = Ghost(icon.Icon, size);
        GiveFeedbackEventHandler follow = (_, _) => MoveGhost(ghost);
        element.GiveFeedback += follow;
        try
        {
            DragDrop.DoDragDrop(element, new DataObject(DragFormat, icon.Identifier), DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            Log.Error("Could not drag a tray icon", ex);
        }
        finally
        {
            element.GiveFeedback -= follow;
            element.Opacity = opacity;
            try { ghost?.Close(); } catch { /* already gone */ }
            Dragging = null;
            Ui.Safe(() => DragEnded?.Invoke(), "Tray icon drag");
        }
    }

    /// <summary>A see-through copy of the icon that follows the mouse during a drag, or null if it cannot be made.</summary>
    private static Window? Ghost(ImageSource? picture, double size)
    {
        if (picture == null) return null;
        try
        {
            var ghost = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                IsHitTestVisible = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                Content = new Image
                {
                    Source = picture,
                    Width = size,
                    Height = size,
                    Opacity = 0.85,
                    IsHitTestVisible = false,
                },
            };
            ghost.SourceInitialized += (_, _) => Native.MakeClickThrough(new WindowInteropHelper(ghost).Handle);
            ghost.Show();
            MoveGhost(ghost);
            return ghost;
        }
        catch (Exception ex)
        {
            Log.Warn("No picture for a tray icon drag: " + ex.Message);
            return null;
        }
    }

    private static void MoveGhost(Window? ghost)
    {
        if (ghost == null || !Native.GetCursorPos(out var cursor)) return;
        IntPtr handle = new WindowInteropHelper(ghost).Handle;
        if (handle == IntPtr.Zero) return;

        var dpi = VisualTreeHelper.GetDpi(ghost);
        int width = (int)Math.Round(ghost.ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Round(ghost.ActualHeight * dpi.DpiScaleY);
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, cursor.X - width / 2, cursor.Y - height / 2, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}
