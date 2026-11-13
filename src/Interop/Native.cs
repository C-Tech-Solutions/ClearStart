using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;

namespace ClearStart;

/// <summary>Win32 declarations and thin helpers. Nothing in here keeps state.</summary>
internal static class Native
{
    // ------------------------------------------------------------------ structs

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int Width;
        public int Height;
    }

    /// <summary>Where and how the compositor draws a live picture of another window inside one of ours.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    internal delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint eventThread, uint eventTime);

    // ------------------------------------------------------------------ constants

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_HIDEWINDOW = 0x0080;
    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal static readonly IntPtr HWND_BOTTOM = new(1);

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TRANSPARENT = 0x00000020;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_LAYERED = 0x00080000;
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    internal const uint DWM_TNP_RECTDESTINATION = 0x1;
    internal const uint DWM_TNP_OPACITY = 0x4;
    internal const uint DWM_TNP_VISIBLE = 0x8;
    internal const uint DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    internal const ushort VK_MENU = 0x12;

    /// <summary>An unassigned virtual key. Tapping it while Win is held stops the shell opening its own Start menu.</summary>
    internal const ushort VK_MASK = 0xE8;

    internal const ushort VK_LWIN = 0x5B;
    internal const ushort VK_S = 0x53;
    internal const ushort VK_K = 0x4B;

    // ------------------------------------------------------------------ imports

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmUnregisterThumbnail(IntPtr thumbnail);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out SIZE size);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref DWM_THUMBNAIL_PROPERTIES properties);

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    [DllImport("shell32.dll")]
    internal static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? name);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    internal static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    internal static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    internal static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool SendNotifyMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint milliseconds, IntPtr callback);

    [DllImport("user32.dll")]
    internal static extern bool KillTimer(IntPtr hwnd, UIntPtr id);

    // ------------------------------------------------------------------ helpers

    internal static string ClassNameOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// Sets what the compositor draws behind a window: nothing, or a blur of whatever is behind it. The
    /// window's own content, including its colour and the frosted grain, is drawn by WPF on top, so if the
    /// compositor ignores this call the window still looks right, just without blur.
    /// </summary>
    internal static void ApplyBackdrop(IntPtr hwnd, Appearance appearance)
    {
        // 0 = off, 3 = blur behind. Asking for a colour as well (flag 2, or the "acrylic" state 4 with one)
        // makes Windows 11 lay a nearly solid layer of its own over the blur, whatever alpha the colour has,
        // which is how a bar set fully see-through still came out dark. So: blur only, colour from WPF.
        int state = appearance is Appearance.Blur or Appearance.Acrylic ? 3 : 0;

        var policy = new AccentPolicy
        {
            AccentState = state,
            AccentFlags = 0,
            GradientColor = 0,
        };

        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WindowCompositionAttributeData { Attribute = 19, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch (Exception ex)
        {
            Log.Warn("Backdrop could not be applied: " + ex.Message);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>Asks Windows 11 to round the window's corners (ignored on older builds).</summary>
    internal static void RoundCorners(IntPtr hwnd)
    {
        try
        {
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        }
        catch
        {
            // Cosmetic only.
        }
    }

    /// <summary>Gives an ordinary window a dark title bar, to go with dark contents.</summary>
    internal static void UseDarkTitleBar(IntPtr hwnd)
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        }
        catch
        {
            // Cosmetic only.
        }
    }

    /// <summary>Keeps a window out of Alt+Tab and the task list.</summary>
    internal static void MakeToolWindow(IntPtr hwnd)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_TOOLWINDOW));
    }

    /// <summary>For a popup that must never take the focus from the window the user is working in.</summary>
    internal static void MakeNoActivateToolWindow(IntPtr hwnd)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    /// <summary>
    /// For a picture that follows the mouse: the mouse goes straight through it to whatever is underneath,
    /// so a drag still finds the place it is over.
    /// </summary>
    internal static void MakeClickThrough(IntPtr hwnd)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE,
            new IntPtr(style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT));
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>The folder Windows knows by this id (Program Files, System32, ...), or null.</summary>
    internal static string? KnownFolderPath(Guid id)
    {
        if (SHGetKnownFolderPath(in id, 0, IntPtr.Zero, out IntPtr path) != 0) return null;
        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    internal static IntPtr MonitorFromCursor()
    {
        GetCursorPos(out var point);
        return MonitorFromPoint(point, 2 /* MONITOR_DEFAULTTONEAREST */);
    }

    internal static bool TryGetMonitor(IntPtr monitor, out RECT bounds, out RECT workArea)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            bounds = info.rcMonitor;
            workArea = info.rcWork;
            return true;
        }

        bounds = default;
        workArea = default;
        return false;
    }

    /// <summary>Scale factor of a monitor (1.0 = 100 %).</summary>
    internal static double ScaleOf(IntPtr monitor)
    {
        try
        {
            if (GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch
        {
            // Fall through to 100 %.
        }
        return 1.0;
    }

    internal static void TapKey(ushort virtualKey)
    {
        var inputs = new INPUT[2];
        inputs[0].type = 1; // INPUT_KEYBOARD
        inputs[0].U.ki = new KEYBDINPUT { wVk = virtualKey };
        inputs[1].type = 1;
        inputs[1].U.ki = new KEYBDINPUT { wVk = virtualKey, dwFlags = 0x0002 /* KEYEVENTF_KEYUP */ };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Types a two-key shortcut such as Win+S: the first key held while the second is tapped.</summary>
    internal static void TapShortcut(ushort heldKey, ushort tappedKey)
    {
        const uint keyUp = 0x0002; // KEYEVENTF_KEYUP
        var inputs = new INPUT[4];
        for (int i = 0; i < inputs.Length; i++) inputs[i].type = 1; // INPUT_KEYBOARD
        inputs[0].U.ki = new KEYBDINPUT { wVk = heldKey };
        inputs[1].U.ki = new KEYBDINPUT { wVk = tappedKey };
        inputs[2].U.ki = new KEYBDINPUT { wVk = tappedKey, dwFlags = keyUp };
        inputs[3].U.ki = new KEYBDINPUT { wVk = heldKey, dwFlags = keyUp };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Brings a window of ours to the foreground even though another app currently has it.
    /// Windows refuses plain SetForegroundWindow calls from background processes, so escalate step by step.
    /// </summary>
    internal static void ForceForeground(IntPtr hwnd)
    {
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return;

        IntPtr foreground = GetForegroundWindow();
        uint foregroundThread = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, out _) : 0;
        uint thisThread = GetCurrentThreadId();
        if (foregroundThread != 0 && foregroundThread != thisThread)
        {
            AttachThreadInput(thisThread, foregroundThread, true);
            SetForegroundWindow(hwnd);
            AttachThreadInput(thisThread, foregroundThread, false);
            if (GetForegroundWindow() == hwnd) return;
        }

        // Last resort: a synthetic Alt tap lifts the foreground lock.
        TapKey(VK_MENU);
        SetForegroundWindow(hwnd);
    }
}
