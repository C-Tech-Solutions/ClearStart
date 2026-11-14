// Checks PutTaskbarOnBottom (TaskbarEdge.h) on made-up values under a test key of its own, with a made-up record
// file. Windows' own taskbar settings are not read or written. Built beside the loader, never shipped.
#include "TaskbarEdge.h"
#include <cstdio>

static const wchar_t TestKey[] = L"Software\\ClearStart.TaskbarEdgeCheck";
static const wchar_t TestExplorerKey[] = L"Software\\ClearStart.TaskbarEdgeCheck\\Explorer";
static int failures = 0;

static void Expect(bool ok, const char* what) {
    std::printf("%s: %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok) ++failures;
}

// A value of the given length whose bytes are their own index, with the edge byte set to edge.
static void Write(const wchar_t* sub, const wchar_t* name, BYTE edge, DWORD length = 48) {
    BYTE data[48]{};
    for (DWORD i = 0; i < length; ++i) data[i] = static_cast<BYTE>(i);
    if (length > TaskbarEdgeOffset) data[TaskbarEdgeOffset] = edge;
    HKEY key = nullptr;
    const std::wstring path = std::wstring(TestExplorerKey) + sub;
    RegCreateKeyExW(HKEY_CURRENT_USER, path.c_str(), 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr);
    RegSetValueExW(key, name, 0, REG_BINARY, data, length);
    RegCloseKey(key);
}

// The edge byte, or 255 if the other bytes are not as Write left them.
static BYTE Edge(const wchar_t* sub, const wchar_t* name, DWORD length = 48) {
    BYTE data[48]{};
    DWORD size = sizeof(data);
    const std::wstring path = std::wstring(TestExplorerKey) + sub;
    if (RegGetValueW(HKEY_CURRENT_USER, path.c_str(), name, RRF_RT_REG_BINARY, nullptr, data, &size) != ERROR_SUCCESS ||
        size != length) return 255;
    for (DWORD i = 0; i < length; ++i)
        if (i != TaskbarEdgeOffset && data[i] != i) return 255;
    return length > TaskbarEdgeOffset ? data[TaskbarEdgeOffset] : 0;
}

int wmain() {
    RegDeleteTreeW(HKEY_CURRENT_USER, TestKey);
    Write(L"\\StuckRects3", L"Settings", 1);         // top
    Write(L"\\MMStuckRects3", L"MONITOR1", 0);       // left
    Write(L"\\MMStuckRects3", L"MONITOR2", 2);       // right
    Write(L"\\MMStuckRects3", L"MONITOR3", 3);       // bottom already
    Write(L"\\MMStuckRects3", L"SHORT", 1, 8);       // too short to hold an edge

    wchar_t temp[MAX_PATH]{};
    GetTempPathW(MAX_PATH, temp);
    const std::wstring record = std::wstring(temp) + L"ClearStart.TaskbarEdgeCheck.txt";
    DeleteFileW(record.c_str());

    Expect(PutTaskbarOnBottom(HKEY_CURRENT_USER, TestExplorerKey, record.c_str()) == 0, "no record: nothing changed");
    Expect(Edge(L"\\StuckRects3", L"Settings") == 1, "no record: the main taskbar stays on the top");
    Expect(PutTaskbarOnBottom(HKEY_CURRENT_USER, TestExplorerKey, L"") == 0, "no record path: nothing changed");

    HANDLE file = CreateFileW(record.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    CloseHandle(file);
    Expect(PutTaskbarOnBottom(HKEY_CURRENT_USER, TestExplorerKey, record.c_str()) == 3, "record: three edges changed");
    Expect(Edge(L"\\StuckRects3", L"Settings") == 3, "record: the main taskbar goes on the bottom, nothing else changed");
    Expect(Edge(L"\\MMStuckRects3", L"MONITOR1") == 3, "record: a left edge goes on the bottom");
    Expect(Edge(L"\\MMStuckRects3", L"MONITOR2") == 3, "record: a right edge goes on the bottom");
    Expect(Edge(L"\\MMStuckRects3", L"MONITOR3") == 3, "record: a bottom edge stays");
    Expect(Edge(L"\\MMStuckRects3", L"SHORT", 8) == 0, "record: a value too short for an edge is left alone");
    Expect(PutTaskbarOnBottom(HKEY_CURRENT_USER, TestExplorerKey, record.c_str()) == 0, "again: nothing left to change");

    DeleteFileW(record.c_str());
    RegDeleteTreeW(HKEY_CURRENT_USER, TestKey);
    std::printf(failures ? "FAILED: %d\n" : "PASS\n", failures);
    return failures;
}
