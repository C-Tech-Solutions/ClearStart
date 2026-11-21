using System.Windows.Threading;

namespace ClearStart;

/// <summary>
/// Keeps a window that belongs on screen from staying minimized. A taskbar has no button to bring it back with, and
/// a program that minimizes every window on the desktop takes ClearStart's bars with the rest.
///
/// A request to minimize that arrives as a message (the system command) is refused. Windows gives a window no way
/// to refuse ShowWindow from another program: when the window hears of it, it is minimized already. Such a window
/// is brought back as soon as the message that told it so has been dealt with, without making it the active one,
/// so the app in front keeps the keyboard.
/// </summary>
internal sealed class StayOnScreen
{
    private const int WindowPosChanged = 0x0047; // WM_WINDOWPOSCHANGED
    private const int SystemCommand = 0x0112; // WM_SYSCOMMAND
    private const int Minimize = 0xF020; // SC_MINIMIZE

    private readonly Dispatcher _dispatcher;
    private readonly Func<string> _name;
    private readonly Func<bool>? _closing;
    private bool _queued;

    /// <param name="name">What the log calls the window, as the start of a sentence.</param>
    /// <param name="closing">True once the window is on its way out, and is left alone.</param>
    public StayOnScreen(Dispatcher dispatcher, Func<string> name, Func<bool>? closing = null)
    {
        _dispatcher = dispatcher;
        _name = name;
        _closing = closing;
    }

    /// <summary>
    /// Looks at a message before the window's own code does. True when that code must not see it: a request to
    /// minimize, which is refused here, or news of the window's place while it is minimized. Code that keeps a
    /// window in its place would take that for a move, and chase the window to where Windows keeps minimized ones.
    /// </summary>
    public bool Filter(IntPtr hwnd, int msg, IntPtr wParam, ref bool handled)
    {
        // The low four bits of a system command are Windows' own.
        if (msg == SystemCommand && (wParam.ToInt64() & 0xFFF0) == Minimize)
        {
            handled = true;
            Log.Info($"{_name()} was asked to minimize and stayed where it is");
            return true;
        }

        if (msg != WindowPosChanged || !Native.IsIconic(hwnd)) return false;
        if (!_queued && _closing?.Invoke() != true)
        {
            _queued = true;
            _dispatcher.BeginInvoke(DispatcherPriority.Send, () => BringBack(hwnd));
        }
        return true;
    }

    private void BringBack(IntPtr hwnd)
    {
        _queued = false;
        if (_closing?.Invoke() == true || !Native.IsIconic(hwnd)) return;

        Native.ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */);
        if (Native.IsIconic(hwnd)) Log.Warn($"{_name()} was minimized by another program and could not be brought back");
        else Log.Info($"{_name()} was minimized by another program; brought it back");
    }
}
