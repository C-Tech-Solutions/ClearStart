#include "Protocol.h"
#include "detours.h"

static SharedState* state;
static HANDLE releaseEvent, readyEvent, attachedEvent, desktopEvent, owner, app;
static volatile LONG desktopPending;
static ATOM primaryAtom, secondaryAtom;
static WNDPROC primaryProc, secondaryProc;
static decltype(&CreateWindowExW) realCreate = CreateWindowExW;
static decltype(&RegisterClassExW) realRegisterEx = RegisterClassExW;
static decltype(&RegisterClassW) realRegister = RegisterClassW;
static decltype(&ShowWindow) realShow = ShowWindow;
static decltype(&ShowWindowAsync) realShowAsync = ShowWindowAsync;
static decltype(&SetWindowPos) realPosition = SetWindowPos;
static decltype(&DeferWindowPos) realDefer = DeferWindowPos;
static decltype(&SetWindowLongPtrW) realStyle = SetWindowLongPtrW;
static decltype(&SetEvent) realSetEvent = SetEvent;

static bool Enabled() { return state && InterlockedCompareExchange(&state->enabled, 1, 1) == 1; }
static bool NamedClass(LPCWSTR name, LPCWSTR expected) {
    return !IS_INTRESOURCE(name) && wcscmp(name, expected) == 0;
}
static bool TrayClass(LPCWSTR name) {
    if (IS_INTRESOURCE(name)) {
        auto atom = static_cast<ATOM>(reinterpret_cast<ULONG_PTR>(name));
        return atom != 0 && (atom == primaryAtom || atom == secondaryAtom);
    }
    return NamedClass(name, L"Shell_TrayWnd") || NamedClass(name, L"Shell_SecondaryTrayWnd");
}
static void FilterPosition(WINDOWPOS* position) {
    if (Enabled()) position->flags = (position->flags & ~SWP_SHOWWINDOW) | SWP_HIDEWINDOW;
}
static LRESULT CALLBACK TrayProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    wchar_t name[64]{};
    GetClassNameW(window, name, 64);
    WNDPROC original = wcscmp(name, L"Shell_TrayWnd") == 0 ? primaryProc : secondaryProc;
    if (message == WM_WINDOWPOSCHANGING) FilterPosition(reinterpret_cast<WINDOWPOS*>(lParam));
    if (message == WM_STYLECHANGING && wParam == static_cast<WPARAM>(GWL_STYLE) && Enabled())
        reinterpret_cast<STYLESTRUCT*>(lParam)->styleNew &= ~WS_VISIBLE;
    LRESULT result = CallWindowProcW(original, window, message, wParam, lParam);
    // Explorer's handler can edit the structure too. Enforce this before user32 applies it.
    if (message == WM_WINDOWPOSCHANGING) FilterPosition(reinterpret_cast<WINDOWPOS*>(lParam));
    if (message == WM_STYLECHANGING && wParam == static_cast<WPARAM>(GWL_STYLE) && Enabled())
        reinterpret_cast<STYLESTRUCT*>(lParam)->styleNew &= ~WS_VISIBLE;
    return result;
}
template<class T> static void WrapClass(T& value) {
    if (NamedClass(value.lpszClassName, L"Shell_TrayWnd")) {
        value.lpfnWndProc = TrayProc;
    } else if (NamedClass(value.lpszClassName, L"Shell_SecondaryTrayWnd")) {
        value.lpfnWndProc = TrayProc;
    }
}
static void RememberClass(LPCWSTR name, WNDPROC proc, ATOM atom) {
    if (!atom || proc == TrayProc) return;
    if (NamedClass(name, L"Shell_TrayWnd")) { primaryAtom = atom; primaryProc = proc; }
    if (NamedClass(name, L"Shell_SecondaryTrayWnd")) { secondaryAtom = atom; secondaryProc = proc; }
}
static ATOM WINAPI RegisterEx(const WNDCLASSEXW* input) {
    auto value = *input;
    WrapClass(value);
    ATOM atom = realRegisterEx(&value);
    RememberClass(input->lpszClassName, input->lpfnWndProc, atom);
    return atom;
}
static ATOM WINAPI Register(const WNDCLASSW* input) {
    auto value = *input;
    WrapClass(value);
    ATOM atom = realRegister(&value);
    RememberClass(input->lpszClassName, input->lpfnWndProc, atom);
    return atom;
}
static HWND WINAPI Create(DWORD ex, LPCWSTR cls, LPCWSTR title, DWORD style, int x, int y,
    int width, int height, HWND parent, HMENU menu, HINSTANCE instance, LPVOID parameter) {
    if (Enabled() && TrayClass(cls)) style &= ~WS_VISIBLE;
    return realCreate(ex, cls, title, style, x, y, width, height, parent, menu, instance, parameter);
}
static BOOL WINAPI Show(HWND window, int command) {
    return realShow(window, Enabled() && IsTaskbar(window) ? SW_HIDE : command);
}
static BOOL WINAPI ShowAsync(HWND window, int command) {
    return realShowAsync(window, Enabled() && IsTaskbar(window) ? SW_HIDE : command);
}
static UINT PositionFlags(HWND window, UINT flags) {
    return Enabled() && IsTaskbar(window) ? (flags & ~SWP_SHOWWINDOW) | SWP_HIDEWINDOW : flags;
}
static BOOL WINAPI Position(HWND window, HWND after, int x, int y, int width, int height, UINT flags) {
    return realPosition(window, after, x, y, width, height, PositionFlags(window, flags));
}
static HDWP WINAPI Defer(HDWP batch, HWND window, HWND after, int x, int y, int width, int height, UINT flags) {
    return realDefer(batch, window, after, x, y, width, height, PositionFlags(window, flags));
}
static LONG_PTR WINAPI Style(HWND window, int index, LONG_PTR value) {
    if (index == GWL_STYLE && Enabled() && IsTaskbar(window)) value &= ~static_cast<LONG_PTR>(WS_VISIBLE);
    return realStyle(window, index, value);
}
static BOOL WINAPI Signal(HANDLE event) {
    // Never block Explorer's UI thread waiting for ClearStart (docking needs that thread).
    // The worker forwards this signal after the bars render, or immediately on recovery.
    if (Enabled() && !state->ready && desktopEvent && CompareObjectHandles(event, desktopEvent)) {
        InterlockedExchange(&desktopPending, 1);
        // Recovery/readiness may have raced the first check. Never strand a deferred signal.
        if ((!Enabled() || state->ready) && InterlockedExchange(&desktopPending, 0))
            return realSetEvent(event);
        return TRUE;
    }
    return realSetEvent(event);
}

static DWORD WINAPI Watch(LPVOID) {
    const ULONGLONG started = AwakeMilliseconds();
    HANDLE events[] = { releaseEvent, owner, app };
    while (Enabled()) {
        if (WaitForMultipleObjects(3, events, FALSE, 250) != WAIT_TIMEOUT) break;
        const ULONGLONG now = AwakeMilliseconds();
        if ((!state->ready && now - started >= StartupTimeout)
            || (state->ready && now - static_cast<ULONGLONG>(InterlockedCompareExchange64(&state->heartbeat, 0, 0)) >= HangTimeout)) break;
        if (state->ready && InterlockedExchange(&desktopPending, 0)) realSetEvent(desktopEvent);
    }
    InterlockedExchange(&state->enabled, 0);
    realSetEvent(releaseEvent);
    // Release the sign-in screen before any cleanup that might be slow.
    if (InterlockedExchange(&desktopPending, 0)) realSetEvent(desktopEvent);
    Restore(state, releaseEvent, app);
    return 0;
}

static bool Initialize() {
    wchar_t token[40]{}, ownerText[16]{};
    if (GetEnvironmentVariableW(TokenVariable, token, 40) != 36
        || !GetEnvironmentVariableW(LoaderVariable, ownerText, 16)) return false;
    SetEnvironmentVariableW(TokenVariable, nullptr);
    SetEnvironmentVariableW(LoaderVariable, nullptr);
    const std::wstring prefix(token);
    HANDLE mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, ObjectName(prefix, L".State").c_str());
    if (!mapping) return false;
    state = static_cast<SharedState*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(SharedState)));
    CloseHandle(mapping);
    if (!state || state->version != 1 || !state->appPid) return false;
    owner = OpenProcess(SYNCHRONIZE, FALSE, wcstoul(ownerText, nullptr, 10));
    app = OpenProcess(SYNCHRONIZE | PROCESS_TERMINATE, FALSE, state->appPid);
    releaseEvent = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, ObjectName(prefix, L".Release").c_str());
    readyEvent = OpenEventW(SYNCHRONIZE, FALSE, ObjectName(prefix, L".Ready").c_str());
    attachedEvent = OpenEventW(EVENT_MODIFY_STATE, FALSE, ObjectName(prefix, L".Attached").c_str());
    desktopEvent = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, L"ShellDesktopSwitchEvent");
    // A missing sign-in gate means this is not the supported startup path. Leave Explorer unmodified.
    if (!owner || !app || !releaseEvent || !readyEvent || !attachedEvent || !desktopEvent) return false;
    if (DetourTransactionBegin() != NO_ERROR) return false;
    DetourUpdateThread(GetCurrentThread());
    DetourAttach(&(PVOID&)realRegisterEx, RegisterEx);
    DetourAttach(&(PVOID&)realRegister, Register);
    DetourAttach(&(PVOID&)realCreate, Create);
    DetourAttach(&(PVOID&)realShow, Show);
    DetourAttach(&(PVOID&)realShowAsync, ShowAsync);
    DetourAttach(&(PVOID&)realPosition, Position);
    DetourAttach(&(PVOID&)realDefer, Defer);
    DetourAttach(&(PVOID&)realStyle, Style);
    DetourAttach(&(PVOID&)realSetEvent, Signal);
    if (DetourTransactionCommit() != NO_ERROR) return false;
    // Do not wait for this thread under the loader lock. It begins after DllMain returns.
    HANDLE worker = CreateThread(nullptr, 0, Watch, nullptr, 0, nullptr);
    if (!worker) { InterlockedExchange(&state->enabled, 0); return false; }
    CloseHandle(worker);
    realSetEvent(attachedEvent);
    return true;
}

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID) {
    if (DetourIsHelperProcess()) return TRUE;
    if (reason == DLL_PROCESS_ATTACH) {
        DetourRestoreAfterWith();
        // Failure must not prevent Explorer itself from loading.
        try { if (!Initialize() && state) InterlockedExchange(&state->enabled, 0); }
        catch (...) { if (state) InterlockedExchange(&state->enabled, 0); }
    }
    // Hooks and class procedures live for Explorer's lifetime. Do not unload this DLL at runtime.
    return TRUE;
}
