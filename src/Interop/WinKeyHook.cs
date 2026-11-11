using System.Runtime.InteropServices;

namespace ClearStart;

/// <summary>
/// Watches the keyboard for a lone tap of the Windows key (or Ctrl+Esc) and reports it,
/// while stopping the system Start menu from opening for that tap.
///
/// The hook runs on its own thread with its own message loop. Windows silently removes a
/// low-level hook whose thread is slow to respond, so it must never share the UI thread.
/// Win+letter shortcuts are left completely alone.
/// </summary>
internal sealed class WinKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const uint WM_QUIT = 0x0012;
    private const uint LLKHF_INJECTED = 0x10;

    private const uint VK_SHIFT = 0x10;
    private const uint VK_CONTROL = 0x11;
    private const uint VK_ALT = 0x12;
    private const uint VK_ESCAPE = 0x1B;
    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;

    private readonly Action _onTap;
    private readonly Native.LowLevelKeyboardProc _proc; // kept alive for the lifetime of the hook
    private readonly ManualResetEventSlim _started = new(false);
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private bool _winDown;
    private bool _chord;

    /// <param name="onTap">Called on the hook thread. It must return immediately (post to the UI thread).</param>
    public WinKeyHook(Action onTap)
    {
        _onTap = onTap;
        _proc = HookProc;
    }

    public bool Start()
    {
        if (_thread != null) return _hook != IntPtr.Zero;

        _thread = new Thread(Run) { IsBackground = true, Name = "ClearStart Win key hook" };
        _thread.Start();
        _started.Wait(2000);
        return _hook != IntPtr.Zero;
    }

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        _hook = Native.SetWindowsHookEx(WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        int error = Marshal.GetLastWin32Error();
        _started.Set();

        if (_hook == IntPtr.Zero)
        {
            // Not fatal: the Start button still works, only the Win key stays with the system menu.
            Log.Error($"Could not install the Win key hook (error {error}); the Win key keeps opening the system Start menu");
            return;
        }

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                bool injected = (data.flags & LLKHF_INJECTED) != 0;
                if (!injected)
                {
                    int message = wParam.ToInt32();
                    bool down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
                    bool isWin = data.vkCode == VK_LWIN || data.vkCode == VK_RWIN;

                    if (isWin)
                    {
                        if (down)
                        {
                            if (!_winDown)
                            {
                                _winDown = true;
                                // Ctrl+Win, Shift+Win and Alt+Win are never a plain "open Start".
                                _chord = IsDown(VK_CONTROL) || IsDown(VK_SHIFT) || IsDown(VK_ALT);
                            }
                        }
                        else
                        {
                            bool tap = _winDown && !_chord;
                            _winDown = false;
                            if (tap)
                            {
                                // The system sees "Win + some key" and therefore does not open its own menu.
                                Native.TapKey(Native.VK_MASK);
                                _onTap();
                            }
                        }
                    }
                    else if (down)
                    {
                        if (_winDown)
                        {
                            _chord = true;
                        }
                        else if (data.vkCode == VK_ESCAPE && IsDown(VK_CONTROL) && !IsDown(VK_SHIFT) && !IsDown(VK_ALT))
                        {
                            // Ctrl+Esc is the other way to open Start.
                            _onTap();
                            return new IntPtr(1);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // An exception must never escape into the system's input pipeline.
            Log.Error("Win key hook failed", ex);
        }

        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool IsDown(uint virtualKey) => (Native.GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        if (_thread == null) return;
        if (_threadId != 0)
        {
            Native.PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        _thread.Join(1000);
        _thread = null;
    }
}
