#include "Protocol.h"
#include "detours.h"
#include <objbase.h>
#include <vector>

static std::wstring modulePath, explorerPath;
constexpr wchar_t ShellKey[] = L"Software\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon";

static void DisableOwnStartup() {
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, ShellKey, 0, KEY_QUERY_VALUE | KEY_SET_VALUE, &key) != ERROR_SUCCESS) return;
    wchar_t value[32768]{};
    DWORD bytes = sizeof(value), type = 0;
    const std::wstring expected = L"\"" + modulePath + L"\" --shell";
    if (RegQueryValueExW(key, L"Shell", nullptr, &type, reinterpret_cast<BYTE*>(value), &bytes) == ERROR_SUCCESS
        && type == REG_SZ && _wcsicmp(value, expected.c_str()) == 0) RegDeleteValueW(key, L"Shell");
    RegCloseKey(key);
}

static bool Start(const std::wstring& path, DWORD flags, PROCESS_INFORMATION& process, LPCSTR dll = nullptr) {
    STARTUPINFOW info{};
    info.cb = sizeof(info);
    std::wstring command = L"\"" + path + L"\"";
    const std::wstring folder = path.substr(0, path.find_last_of(L'\\'));
    if (dll) {
        if (DetourCreateProcessWithDllExW(path.c_str(), command.data(), nullptr, nullptr, FALSE,
            flags, nullptr, folder.c_str(), &info, &process, dll, nullptr)) return true;
        // Detours closes its handles when injection fails.
        process = {};
        return false;
    }
    return CreateProcessW(path.c_str(), command.data(), nullptr, nullptr, FALSE, flags,
        nullptr, folder.c_str(), &info, &process) != FALSE;
}
static void CloseProcess(PROCESS_INFORMATION& process) {
    if (process.hThread) CloseHandle(process.hThread);
    if (process.hProcess) CloseHandle(process.hProcess);
    process = {};
}
static void NormalExplorer() {
    SetEnvironmentVariableW(TokenVariable, nullptr);
    SetEnvironmentVariableW(LoaderVariable, nullptr);
    if (GetShellWindow()) return;
    PROCESS_INFORMATION process{};
    if (Start(explorerPath, 0, process)) CloseProcess(process);
}
static std::string DllPath(const std::wstring& path) {
    // Detours' import path is ANSI. Use the short path if the install path cannot round-trip.
    auto convert = [](const std::wstring& input) {
        char output[32768]{};
        BOOL usedDefault = FALSE;
        int length = WideCharToMultiByte(CP_ACP, WC_NO_BEST_FIT_CHARS, input.c_str(), -1,
            output, sizeof(output), nullptr, &usedDefault);
        return length > 0 && !usedDefault ? std::string(output) : std::string();
    };
    std::string result = convert(path);
    if (!result.empty()) return result;
    wchar_t shortPath[32768]{};
    DWORD length = GetShortPathNameW(path.c_str(), shortPath, 32768);
    return length > 0 && length < 32768 ? convert(shortPath) : std::string();
}

static int Run() {
    const std::wstring folder = modulePath.substr(0, modulePath.find_last_of(L'\\'));
    const std::wstring appPath = folder + L"\\ClearStart.exe";
    const std::wstring guardPath = folder + L"\\ClearStart.ExplorerGuard.dll";
    const std::string guardAnsi = DllPath(guardPath);
    if (guardAnsi.empty() || GetFileAttributesW(appPath.c_str()) == INVALID_FILE_ATTRIBUTES
        || GetFileAttributesW(guardPath.c_str()) == INVALID_FILE_ATTRIBUTES) {
        DisableOwnStartup(); NormalExplorer(); return 1;
    }
    GUID id{};
    wchar_t guid[40]{};
    if (FAILED(CoCreateGuid(&id)) || !StringFromGUID2(id, guid, 40)) {
        DisableOwnStartup(); NormalExplorer(); return 1;
    }
    const std::wstring token(guid + 1, 36);
    HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
        sizeof(SharedState), ObjectName(token, L".State").c_str());
    auto* state = mapping ? static_cast<SharedState*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(SharedState))) : nullptr;
    HANDLE attached = CreateEventW(nullptr, TRUE, FALSE, ObjectName(token, L".Attached").c_str());
    HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, ObjectName(token, L".Ready").c_str());
    HANDLE release = CreateEventW(nullptr, TRUE, FALSE, ObjectName(token, L".Release").c_str());
    if (!state || !attached || !ready || !release) {
        DisableOwnStartup(); NormalExplorer(); return 1;
    }
    *state = {};
    state->version = 1;
    state->enabled = 1;
    SetEnvironmentVariableW(TokenVariable, token.c_str());
    SetEnvironmentVariableW(LoaderVariable, std::to_wstring(GetCurrentProcessId()).c_str());
    PROCESS_INFORMATION application{}, explorer{};
    bool success = false, resumed = false;
    // Establish the exact app PID before Explorer can load the guard. The app waits suspended.
    if (Start(appPath, CREATE_SUSPENDED, application)) {
        state->appPid = application.dwProcessId;
        if (Start(explorerPath, 0, explorer, guardAnsi.c_str())) {
            HANDLE loaded[] = { attached, explorer.hProcess };
            if (WaitForMultipleObjects(2, loaded, FALSE, 10000) == WAIT_OBJECT_0 && state->enabled) {
                resumed = ResumeThread(application.hThread) != static_cast<DWORD>(-1);
                if (resumed) {
                    HANDLE startup[] = { ready, application.hProcess, explorer.hProcess, release };
                    success = WaitForMultipleObjects(4, startup, FALSE, StartupTimeout) == WAIT_OBJECT_0;
                }
            }
        }
    }
    SetEnvironmentVariableW(TokenVariable, nullptr);
    SetEnvironmentVariableW(LoaderVariable, nullptr);
    if (success) {
        HANDLE running[] = { release, application.hProcess, explorer.hProcess };
        while (WaitForMultipleObjects(3, running, FALSE, 1000) == WAIT_TIMEOUT) {
            if (AwakeMilliseconds() - static_cast<ULONGLONG>(InterlockedCompareExchange64(&state->heartbeat, 0, 0)) >= HangTimeout) {
                success = false;
                break;
            }
        }
        DWORD code = 0;
        if (GetExitCodeProcess(application.hProcess, &code) && code != 0 && code != STILL_ACTIVE) success = false;
        if (WaitForSingleObject(explorer.hProcess, 0) == WAIT_OBJECT_0) success = false;
    }
    // No more suppression, even if the app cannot service its exit request.
    InterlockedExchange(&state->enabled, 0);
    SetEvent(release);
    if (!resumed && application.hProcess) TerminateProcess(application.hProcess, ERROR_PROCESS_ABORTED);
    Restore(state, release, application.hProcess);
    if (!success) DisableOwnStartup();
    // If the guarded shell failed during startup or crashed later, start plain Explorer once.
    if (explorer.hProcess && WaitForSingleObject(explorer.hProcess, 0) == WAIT_TIMEOUT && !GetShellWindow()) {
        TerminateProcess(explorer.hProcess, ERROR_PROCESS_ABORTED);
        WaitForSingleObject(explorer.hProcess, 5000);
    }
    NormalExplorer();
    // A newly started plain Explorer needs time to recreate its bars before we can restore auto-hide.
    if (!success) {
        for (int attempt = 0; attempt < 50 && !FindWindowW(L"Shell_TrayWnd", nullptr); ++attempt) Sleep(200);
        EnumWindows(ShowNativeBar, reinterpret_cast<LPARAM>(state));
    }
    CloseProcess(application);
    CloseProcess(explorer);
    UnmapViewOfFile(state);
    CloseHandle(mapping); CloseHandle(attached); CloseHandle(ready); CloseHandle(release);
    return success ? 0 : 1;
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR command, int) {
    // Double-clicking this binary must not start or replace a running desktop.
    if (wcscmp(command, L"--shell") != 0 || GetShellWindow()) return 2;
    wchar_t path[32768]{}, windows[MAX_PATH]{};
    DWORD length = GetModuleFileNameW(nullptr, path, 32768);
    if (!length || length == 32768 || !GetWindowsDirectoryW(windows, MAX_PATH)) return 1;
    modulePath = path;
    explorerPath = std::wstring(windows) + L"\\explorer.exe";
    HANDLE single = CreateMutexW(nullptr, TRUE, L"Local\\ClearStart.EarlyLoader");
    if (!single || GetLastError() == ERROR_ALREADY_EXISTS) return 2;
    int result = 1;
    try { result = Run(); }
    catch (...) { DisableOwnStartup(); NormalExplorer(); }
    ReleaseMutex(single);
    CloseHandle(single);
    return result;
}
