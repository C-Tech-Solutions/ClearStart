// ClearStart.WinKey.exe: watches the Windows key for ClearStart, with UI Access.
//
// While the keyboard belongs to a window of a program running as administrator (a game started that way, for one),
// Windows does not call the keyboard hook of a program that is not. ClearStart is not, so there its own hook
// (src/Interop/WinKeyHook.cs) misses the Windows key and the Windows Start menu opens. A program with UI Access is
// called over every window. Windows gives UI Access to a program that asks for it in its manifest (WinKey.manifest),
// is signed, and is in a folder under Program Files.
//
// ClearStart cannot be that program itself. Windows starts one only through the shell, never suspended, which is how
// the early-startup loader starts ClearStart. It also runs above the user's other programs: every app ClearStart
// started would run there too, and nothing could be dragged from an ordinary window onto its bars. So this program
// holds the hook and nothing else. It follows the rules in WinKey.h, tells ClearStart of each tap, and ends when
// ClearStart ends or asks it to (src/Interop/WinKeyHelper.cs has ClearStart's side).
//
//   ClearStart.WinKey.exe --watch <ClearStart's process id> <one of its windows, as a number> <name of its stop event>
//
// All it ever says is "the key was tapped", and only to a window of the process it was given.
#include "WinKey.h"
#include <shellapi.h>
#include <cwchar>

namespace {

// The exit codes ClearStart reads (WinKeyHelper.cs).
enum Exit : int { Stopped = 0, BadArguments = 1, NoUiAccess = 2, NoClearStart = 3, NoHook = 4 };

// What the helper and ClearStart say to each other: this message, with Hello or Tap, and the version of these rules.
constexpr wchar_t MessageName[] = L"ClearStart.WinKey";
constexpr WPARAM Hello = 1, Tap = 2;
constexpr LPARAM Protocol = 1;
// A key that does nothing. Typed while the Windows key is still down, it turns the tap into a shortcut for Windows,
// which then leaves its own Start menu closed.
constexpr WORD MaskKey = 0xE8;

HHOOK hook;
HWND target;
DWORD targetProcess;
UINT message;
WinKeyWatch watch;

bool IsDown(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }

void TellClearStart() {
    // This process got the key, so only it may hand the foreground on: ClearStart's menu has to take the keyboard.
    AllowSetForegroundWindow(targetProcess);
    PostMessageW(target, message, Tap, Protocol);
}

LRESULT CALLBACK KeyboardProc(int code, WPARAM wParam, LPARAM lParam) {
    if (code == HC_ACTION) {
        const auto& key = *reinterpret_cast<const KBDLLHOOKSTRUCT*>(lParam);
        if (!(key.flags & LLKHF_INJECTED)) {
            const bool down = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
            switch (watch.Key(key.vkCode, down, IsDown(VK_CONTROL), IsDown(VK_SHIFT), IsDown(VK_MENU))) {
            case KeyAction::Tap: {
                INPUT keys[2]{};
                keys[0].type = keys[1].type = INPUT_KEYBOARD;
                keys[0].ki.wVk = keys[1].ki.wVk = MaskKey;
                keys[1].ki.dwFlags = KEYEVENTF_KEYUP;
                SendInput(2, keys, sizeof(INPUT));
                TellClearStart();
                break;
            }
            case KeyAction::TapAndSwallow:
                TellClearStart();
                return 1;
            case KeyAction::Pass:
                break;
            }
        }
    }
    return CallNextHookEx(hook, code, wParam, lParam);
}

bool HasUiAccess() {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
    DWORD granted = 0, size = 0;
    const bool ok = GetTokenInformation(token, TokenUIAccess, &granted, sizeof(granted), &size) && granted != 0;
    CloseHandle(token);
    return ok;
}

} // namespace

int APIENTRY wWinMain(HINSTANCE instance, HINSTANCE, LPWSTR, int) {
    int count = 0;
    LPWSTR* arguments = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!arguments || count != 5 || std::wcscmp(arguments[1], L"--watch") != 0) return BadArguments;
    targetProcess = std::wcstoul(arguments[2], nullptr, 10);
    target = reinterpret_cast<HWND>(static_cast<UINT_PTR>(_wcstoui64(arguments[3], nullptr, 10)));

    // Without UI Access this hook would see no more than ClearStart's own: leave the key to that one.
    if (!HasUiAccess()) return NoUiAccess;

    DWORD owner = 0;
    GetWindowThreadProcessId(target, &owner);
    HANDLE waits[2] = {
        OpenProcess(SYNCHRONIZE, FALSE, targetProcess),
        OpenEventW(SYNCHRONIZE, FALSE, arguments[4]),
    };
    if (!targetProcess || owner != targetProcess || !waits[0] || !waits[1]) return NoClearStart;

    // Every key press waits for this thread's answer, in whatever program it is typed.
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    message = RegisterWindowMessageW(MessageName);
    hook = SetWindowsHookExW(WH_KEYBOARD_LL, KeyboardProc, instance, 0);
    if (!hook) return NoHook;
    PostMessageW(target, message, Hello, Protocol);

    // Windows calls the hook while this thread reads its messages. Anything else wakes the wait: ClearStart has
    // ended, it has set its stop event, or the wait itself failed.
    while (MsgWaitForMultipleObjects(2, waits, FALSE, INFINITE, QS_ALLINPUT) == WAIT_OBJECT_0 + 2) {
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
    UnhookWindowsHookEx(hook);
    return Stopped;
}
