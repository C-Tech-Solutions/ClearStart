using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClearStart;

/// <summary>
/// Tells Windows where a window's button is on ClearStart's bars. Windows asks before it minimizes or restores a
/// window, and draws the window shrinking into that place or growing out of it. With no answer from ClearStart the
/// only answer was the Windows taskbar's: hidden, but still in its place, so every window flew to the bottom of
/// the screen whichever edge ClearStart's bar was on.
///
/// Windows does not document whose answer counts when several programs give one. This goes by what is known of it:
/// the question goes to every window registered for the shell's window news, in the order they registered, and
/// each answer replaces the one before. So ClearStart's has to come after Explorer's, and Explorer registers again
/// whenever it starts: <see cref="AnswerLast"/> moves ClearStart's window to the end of that line.
/// </summary>
internal sealed class MinimizeTarget : IDisposable
{
    /// <summary>The message Windows sends to the windows registered for the shell's window news.</summary>
    internal static readonly int ShellMessage = (int)Native.RegisterWindowMessage("SHELLHOOK");

    /// <summary>
    /// The question among that news (HSHELL_GETMINRECT). It comes with a pointer to the window's handle followed by
    /// its button's place as four 16-bit numbers: left, top, right, bottom, in screen pixels.
    /// </summary>
    internal const int Question = 5;

    // How many answers are written to the log, with what Windows had in the place before: enough to see on a PC
    // whether the question arrives and whether Explorer was asked first.
    private const int AnswersLogged = 3;

    private readonly Func<IntPtr, Native.RECT?> _placeOf;
    private HwndSource? _window;
    private bool _registered;
    private bool _moveQueued;
    private int _answers;
    private bool _failureLogged;

    /// <param name="placeOf">Where a window's button is on the screen; nothing for a window without one.</param>
    public MinimizeTarget(Func<IntPtr, Native.RECT?> placeOf)
    {
        _placeOf = placeOf;
        // Never shown. An ordinary hidden window and not a message-only one, which Windows keeps apart from the
        // windows of the desktop.
        _window = new HwndSource(new HwndSourceParameters("ClearStart window buttons")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, without WS_VISIBLE
            ExtendedWindowStyle = 0x00000080, // WS_EX_TOOLWINDOW
            PositionX = 0,
            PositionY = 0,
            Width = 0,
            Height = 0,
        });
        _window.AddHook(WndProc);
    }

    /// <summary>The window Windows asks. A self-test asks it the same way without registering it.</summary>
    internal IntPtr Window => _window?.Handle ?? IntPtr.Zero;

    /// <summary>
    /// Registers for the question, or registers again: that puts ClearStart's answer behind every answer registered
    /// so far, Explorer's among them.
    /// </summary>
    public bool AnswerLast()
    {
        if (_window == null) return false;
        if (_registered) Native.DeregisterShellHookWindow(_window.Handle);
        _registered = Native.RegisterShellHookWindow(_window.Handle);
        if (!_registered && !_failureLogged)
        {
            _failureLogged = true;
            Log.Warn("Windows would not take ClearStart's answers on where the windows' buttons are "
                + $"(error {Marshal.GetLastWin32Error()}); windows minimize towards the Windows taskbar");
        }
        return _registered;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != ShellMessage || wParam.ToInt64() != Question || lParam == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            IntPtr window = Marshal.ReadIntPtr(lParam);
            if (_placeOf(window) is not { } place || !Fits(place)) return IntPtr.Zero;

            int at = IntPtr.Size;
            if (_answers < AnswersLogged)
            {
                _answers++;
                Log.Info($"Windows asked where a window's button is (it had {Marshal.ReadInt16(lParam, at)},"
                    + $"{Marshal.ReadInt16(lParam, at + 2)},{Marshal.ReadInt16(lParam, at + 4)},"
                    + $"{Marshal.ReadInt16(lParam, at + 6)}); answered {place.Left},{place.Top},{place.Right},{place.Bottom}");
            }
            Marshal.WriteInt16(lParam, at, (short)place.Left);
            Marshal.WriteInt16(lParam, at + 2, (short)place.Top);
            Marshal.WriteInt16(lParam, at + 4, (short)place.Right);
            Marshal.WriteInt16(lParam, at + 6, (short)place.Bottom);
            handled = true;
            QueueMove();
            return new IntPtr(1);
        }
        catch (Exception ex)
        {
            if (!_failureLogged)
            {
                _failureLogged = true;
                Log.Warn("Could not tell Windows where a window's button is: " + ex.Message);
            }
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// After an answer, ClearStart goes to the end of the line again. A program that registered since then, such as
    /// an Explorer that started without ClearStart noticing, gets the last word once at most.
    /// </summary>
    private void QueueMove()
    {
        if (!_registered || _moveQueued || _window == null) return;
        _moveQueued = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _moveQueued = false;
            if (_registered) AnswerLast();
        }));
    }

    /// <summary>The answer holds 16-bit numbers, and a place without any size is no place.</summary>
    private static bool Fits(Native.RECT place) =>
        place.Width > 0 && place.Height > 0
        && place.Left >= short.MinValue && place.Top >= short.MinValue
        && place.Right <= short.MaxValue && place.Bottom <= short.MaxValue;

    public void Dispose()
    {
        if (_window == null) return;
        if (_registered) Native.DeregisterShellHookWindow(_window.Handle);
        _registered = false;
        _window.RemoveHook(WndProc);
        _window.Dispose();
        _window = null;
    }
}
