using System.Windows;
using System.Windows.Interop;

namespace ClearStart;

// A window made see-through and solid again, as the Windows taskbar is while ClearStart keeps it hidden. Tried on a
// window of the self-test's own, far off-screen and never the active one. The Windows taskbar is not touched.
internal static partial class SelfTest
{
    private static object CheckSeeThrough(List<string> errors)
    {
        var window = new Window
        {
            Title = "ClearStart self-test",
            Left = -30000,
            Top = -30000,
            Width = 300,
            Height = 40,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        window.Show();
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            bool solidAtFirst = !Native.IsLayered(hwnd) && !Native.IsSeeThrough(hwnd);
            if (!solidAtFirst) errors.Add("See-through window: the self-test's window was not an ordinary one, so nothing was tried");

            // Made see-through, and asked again: the second time finds it done.
            bool made = Native.MakeSeeThrough(hwnd) && Native.IsSeeThrough(hwnd) && Native.MakeSeeThrough(hwnd);
            if (solidAtFirst && !made) errors.Add("See-through window: a window was not made see-through");

            // It stays so when it is hidden and shown again, as Windows does with its taskbar.
            Native.ShowWindow(hwnd, 0 /* SW_HIDE */);
            Native.ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */);
            bool kept = Native.IsSeeThrough(hwnd);
            if (made && !kept) errors.Add("See-through window: a window did not stay see-through when hidden and shown again");

            // Made solid again, it is an ordinary window once more, and there is nothing left to undo.
            bool undone = Native.MakeSolid(hwnd) && !Native.IsSeeThrough(hwnd) && !Native.IsLayered(hwnd);
            if (made && !undone) errors.Add("See-through window: a see-through window was not made solid again");
            bool onlyOnce = !Native.MakeSolid(hwnd);
            if (!onlyOnce) errors.Add("See-through window: a solid window was made solid a second time");

            // A window that is layered for a reason of its own is left alone both ways.
            Native.MakeClickThrough(hwnd);
            bool othersLeftAlone = Native.IsLayered(hwnd) && !Native.MakeSeeThrough(hwnd) && !Native.MakeSolid(hwnd) &&
                Native.IsLayered(hwnd);
            if (!othersLeftAlone) errors.Add("See-through window: a window that was layered already was not left alone");

            return new { solidAtFirst, made, kept, undone, onlyOnce, othersLeftAlone };
        }
        finally
        {
            window.Close();
        }
    }
}
