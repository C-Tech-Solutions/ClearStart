#pragma once
// The Windows taskbar's edge, put on the bottom before Explorer starts.
//
// Windows 11 cannot auto-hide a taskbar on the top or a side, so ClearStart keeps it on the bottom. When it is on
// another edge, ClearStart writes its own edges down first (src\Interop\TaskbarEdge.cs, in the record file below), and
// puts them back when it closes for good. Explorer reads the edge only as it starts, and writes its own back over it
// while it runs, so under early startup the edge is written here, before the loader starts Explorer. Only while that
// record is there: without it, ClearStart could not put the edges back.
//
// Windows 11 25H2 with 2026's updates lets the user choose the edge in Settings, and keeps that choice as a number of
// its own, which Explorer goes by over the older values. It is put on the bottom here too, where Windows has it.
//
// When early startup fails, the loader puts the values back as they were before it starts plain Explorer: ClearStart
// may not have put the taskbar back on its own edge, and does not start at the next sign-in.
#include <windows.h>
#include <shlobj.h>
#include <string>
#include <vector>

constexpr wchar_t ExplorerKey[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer";
// The edge in StuckRects3's value and in each of MMStuckRects3's (one per monitor): 0 left, 1 top, 2 right, 3 bottom.
// Kept in step with TaskbarEdge.cs.
constexpr DWORD TaskbarEdgeOffset = 12;
constexpr BYTE TaskbarBottom = 3;
// The edge chosen in Settings, as a number with the same meaning, under the Advanced key. Missing on a Windows without
// that choice, and until it is first changed.
constexpr wchar_t TaskbarLocationValue[] = L"TaskbarLocation";

// The record ClearStart keeps of the taskbar's own edges, or an empty path if the folder cannot be found.
inline std::wstring ClearStartRecordFile() {
    PWSTR local = nullptr;
    std::wstring path;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local)))
        path = std::wstring(local) + L"\\ClearStart\\explorer-taskbar-edges.txt";
    CoTaskMemFree(local);
    return path;
}

// A value PutTaskbarOnBottom changed, as it was before, for PutTaskbarBack.
struct TaskbarValue {
    std::wstring key, name;
    DWORD type;
    std::vector<BYTE> data;
};

// Puts every edge under explorerKey on the bottom if recordFile is there. Returns how many values it changed, and
// adds each one as it was to saved, if given.
inline int PutTaskbarOnBottom(HKEY root, const wchar_t* explorerKey, const wchar_t* recordFile,
                              std::vector<TaskbarValue>* saved = nullptr) {
    if (!recordFile || !*recordFile || GetFileAttributesW(recordFile) == INVALID_FILE_ATTRIBUTES) return 0;
    int changed = 0;
    for (const wchar_t* name : { L"\\StuckRects3", L"\\MMStuckRects3" }) {
        const std::wstring path = std::wstring(explorerKey) + name;
        HKEY key = nullptr;
        if (RegOpenKeyExW(root, path.c_str(), 0, KEY_QUERY_VALUE | KEY_SET_VALUE, &key) != ERROR_SUCCESS) continue;
        for (DWORD index = 0;; ++index) {
            wchar_t value[256]{};
            BYTE data[256]{};
            DWORD valueLength = 256, dataLength = sizeof(data), type = 0;
            const LONG status = RegEnumValueW(key, index, value, &valueLength, nullptr, &type, data, &dataLength);
            if (status == ERROR_NO_MORE_ITEMS) break;
            if (status != ERROR_SUCCESS || type != REG_BINARY || dataLength <= TaskbarEdgeOffset) continue;
            // On the bottom already, or not an edge at all.
            if (data[TaskbarEdgeOffset] >= TaskbarBottom) continue;
            std::vector<BYTE> before(data, data + dataLength);
            data[TaskbarEdgeOffset] = TaskbarBottom;
            if (RegSetValueExW(key, value, 0, REG_BINARY, data, dataLength) != ERROR_SUCCESS) continue;
            ++changed;
            if (saved) saved->push_back({ path, value, REG_BINARY, std::move(before) });
        }
        RegCloseKey(key);
    }

    // The edge chosen in Settings, only where it is there already: it is never made here.
    const std::wstring advanced = std::wstring(explorerKey) + L"\\Advanced";
    HKEY key = nullptr;
    if (RegOpenKeyExW(root, advanced.c_str(), 0, KEY_QUERY_VALUE | KEY_SET_VALUE, &key) == ERROR_SUCCESS) {
        DWORD location = 0, size = sizeof(location), type = 0;
        if (RegQueryValueExW(key, TaskbarLocationValue, nullptr, &type, reinterpret_cast<BYTE*>(&location), &size) == ERROR_SUCCESS &&
            type == REG_DWORD && size == sizeof(location) && location < TaskbarBottom) {
            const BYTE* before = reinterpret_cast<const BYTE*>(&location);
            std::vector<BYTE> was(before, before + sizeof(location));
            const DWORD bottom = TaskbarBottom;
            if (RegSetValueExW(key, TaskbarLocationValue, 0, REG_DWORD, reinterpret_cast<const BYTE*>(&bottom),
                               sizeof(bottom)) == ERROR_SUCCESS) {
                ++changed;
                if (saved) saved->push_back({ advanced, TaskbarLocationValue, REG_DWORD, std::move(was) });
            }
        }
        RegCloseKey(key);
    }
    return changed;
}

// Puts back the values PutTaskbarOnBottom changed, as they were.
inline void PutTaskbarBack(HKEY root, const std::vector<TaskbarValue>& saved) {
    for (const TaskbarValue& value : saved)
        RegSetKeyValueW(root, value.key.c_str(), value.name.c_str(), value.type, value.data.data(),
                        static_cast<DWORD>(value.data.size()));
}
