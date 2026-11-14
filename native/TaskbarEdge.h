#pragma once
// The Windows taskbar's edge, put on the bottom before Explorer starts.
//
// Windows 11 cannot auto-hide a taskbar on the top or a side, so ClearStart keeps it on the bottom. When it is on
// another edge, ClearStart writes its own edges down first (src\Interop\TaskbarEdge.cs, in the record file below), and
// puts them back when it closes for good. Explorer reads the edge only as it starts, and writes its own back over it
// while it runs, so under early startup the edge is written here, before the loader starts Explorer. Only while that
// record is there: without it, ClearStart could not put the edges back.
#include <windows.h>
#include <shlobj.h>
#include <string>

constexpr wchar_t ExplorerKey[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer";
// The edge in StuckRects3's value and in each of MMStuckRects3's (one per monitor): 0 left, 1 top, 2 right, 3 bottom.
// Kept in step with TaskbarEdge.cs.
constexpr DWORD TaskbarEdgeOffset = 12;
constexpr BYTE TaskbarBottom = 3;

// The record ClearStart keeps of the taskbar's own edges, or an empty path if the folder cannot be found.
inline std::wstring ClearStartRecordFile() {
    PWSTR local = nullptr;
    std::wstring path;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local)))
        path = std::wstring(local) + L"\\ClearStart\\explorer-taskbar-edges.txt";
    CoTaskMemFree(local);
    return path;
}

// Puts every edge under explorerKey on the bottom if recordFile is there. Returns how many values it changed.
inline int PutTaskbarOnBottom(HKEY root, const wchar_t* explorerKey, const wchar_t* recordFile) {
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
            data[TaskbarEdgeOffset] = TaskbarBottom;
            if (RegSetValueExW(key, value, 0, REG_BINARY, data, dataLength) == ERROR_SUCCESS) ++changed;
        }
        RegCloseKey(key);
    }
    return changed;
}
