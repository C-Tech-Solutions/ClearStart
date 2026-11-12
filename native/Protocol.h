#pragma once
#include <windows.h>
#include <shellapi.h>
#include <string>
#include <cstddef>

// This layout is also used by EarlySession.cs. Names are random and session-local.
struct SharedState {
    LONG version;                     // 0
    volatile LONG enabled;            // 4: one-way transition to zero
    DWORD appPid;                     // 8
    volatile LONG failed;             // 12: a watchdog initiated recovery
    volatile LONG64 heartbeat;        // 16: awake milliseconds on the UI thread
    volatile LONG ready;              // 24
    LONG taskbarState;                // 28: recorded before changing auto-hide
    volatile LONG hasTaskbarState;    // 32
};
static_assert(offsetof(SharedState, heartbeat) == 16);
static_assert(sizeof(SharedState) == 40);
constexpr DWORD StartupTimeout = 30000;
constexpr DWORD HangTimeout = 15000;
constexpr wchar_t TokenVariable[] = L"CLEARSTART_EARLY_TOKEN";
constexpr wchar_t LoaderVariable[] = L"CLEARSTART_EARLY_OWNER";

inline ULONGLONG AwakeMilliseconds() {
    ULONGLONG time = 0;
    QueryUnbiasedInterruptTime(&time);
    return time / 10000;
}

inline bool HeartbeatExpired(SharedState* shared) {
    const auto beat = static_cast<ULONGLONG>(InterlockedCompareExchange64(&shared->heartbeat, 0, 0));
    const auto now = AwakeMilliseconds();
    return now >= beat && now - beat >= HangTimeout;
}

inline bool IsTaskbar(HWND window) {
    wchar_t name[64]{};
    GetClassNameW(window, name, 64);
    return wcscmp(name, L"Shell_TrayWnd") == 0 || wcscmp(name, L"Shell_SecondaryTrayWnd") == 0;
}

inline std::wstring ObjectName(const std::wstring& token, const wchar_t* suffix) {
    return L"Local\\ClearStart.Early." + token + suffix;
}

// Only the process handles opened for this launch are ever stopped. No process-name killing.
inline void StopApp(HANDLE app) {
    if (!app || WaitForSingleObject(app, 0) != WAIT_TIMEOUT) return;
    // The per-launch Release event asks this app to stop. Never signal a different copy's global exit event.
    if (WaitForSingleObject(app, 5000) == WAIT_TIMEOUT) {
        TerminateProcess(app, ERROR_PROCESS_ABORTED);
        WaitForSingleObject(app, 5000);
    }
}

inline BOOL CALLBACK ShowNativeBar(HWND window, LPARAM parameter) {
    if (!IsTaskbar(window)) return TRUE;
    DWORD pid = 0;
    GetWindowThreadProcessId(window, &pid);
    auto* state = reinterpret_cast<SharedState*>(parameter);
    if (state && pid == state->appPid) return TRUE; // ManagedShell has a tray window with the same class.
    wchar_t name[64]{};
    GetClassNameW(window, name, 64);
    if (state && state->hasTaskbarState && wcscmp(name, L"Shell_TrayWnd") == 0) {
        APPBARDATA data{};
        data.cbSize = sizeof(data);
        data.hWnd = window;
        data.lParam = state->taskbarState;
        SHAppBarMessage(ABM_SETSTATE, &data);
    }
    SetWindowPos(window, HWND_TOPMOST, 0, 0, 0, 0,
        SWP_SHOWWINDOW | SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    return TRUE;
}

inline void Restore(SharedState* state, HANDLE release, HANDLE app) {
    if (state) InterlockedExchange(&state->enabled, 0);
    if (release) SetEvent(release);
    StopApp(app);
    EnumWindows(ShowNativeBar, reinterpret_cast<LPARAM>(state));
    if (FindWindowW(L"Shell_TrayWnd", nullptr))
        SendNotifyMessageW(HWND_BROADCAST, RegisterWindowMessageW(L"TaskbarCreated"), 0, 0);
}
