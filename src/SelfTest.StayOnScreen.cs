using System.Windows;
using System.Windows.Interop;

namespace ClearStart;

// A window that has to stay on screen, as the bars do: asked to minimize, and minimized without being asked. Tried on
// a window of the self-test's own, far off-screen and never the active one. No bar is opened, and no other window is
// touched.
internal static partial class SelfTest
{
    private static async Task<object> CheckStayOnScreenAsync(List<string> errors)
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
            var guard = new StayOnScreen(window.Dispatcher, () => "The self-test's window");
            HwndSource.FromHwnd(hwnd)!.AddHook((IntPtr handle, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                guard.Filter(handle, msg, wParam, ref handled);
                return IntPtr.Zero;
            });
            Native.GetWindowRect(hwnd, out var before);

            // Asked first, with the message a title bar's minimize button sends: refused.
            Native.SendMessage(hwnd, 0x0112 /* WM_SYSCOMMAND */, (IntPtr)0xF020 /* SC_MINIMIZE */, IntPtr.Zero);
            bool refused = !Native.IsIconic(hwnd);
            if (!refused) errors.Add("Staying on screen: a window that was asked to minimize did so");

            // Minimized without being asked, which no window can refuse: it comes back by itself, where it was.
            Native.ShowWindow(hwnd, 7 /* SW_SHOWMINNOACTIVE */);
            bool minimized = Native.IsIconic(hwnd);
            for (int waited = 0; Native.IsIconic(hwnd) && waited < 2000; waited += 50) await Task.Delay(50);
            bool cameBack = !Native.IsIconic(hwnd);
            Native.GetWindowRect(hwnd, out var after);
            bool samePlace = after.Left == before.Left && after.Top == before.Top &&
                after.Right == before.Right && after.Bottom == before.Bottom;

            if (!minimized) errors.Add("Staying on screen: the self-test's window would not minimize, so nothing was tried");
            else if (!cameBack) errors.Add("Staying on screen: a minimized window did not come back");
            else if (!samePlace) errors.Add("Staying on screen: a minimized window came back in another place");

            // Minimized again and again, more often than the log takes lines for: it comes back every time all the same.
            int again = 0;
            for (int round = 0; round < StayOnScreen.LinesAMinute + 3; round++)
            {
                Native.ShowWindow(hwnd, 7 /* SW_SHOWMINNOACTIVE */);
                for (int waited = 0; Native.IsIconic(hwnd) && waited < 2000; waited += 50) await Task.Delay(50);
                if (!Native.IsIconic(hwnd)) again++;
            }
            bool everyTime = again == StayOnScreen.LinesAMinute + 3;
            if (cameBack && !everyTime) errors.Add($"Staying on screen: minimized again and again, a window came back only {again} time(s)");
            return new { refused, minimized, cameBack, samePlace, everyTime };
        }
        finally
        {
            window.Close();
        }
    }
}
