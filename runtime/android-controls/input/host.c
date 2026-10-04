#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <wchar.h>
#include "profile.h"

typedef struct { DWORD pid; HWND window; HANDLE process; UINT command; } Session;

static BOOL CALLBACK find_window(HWND window, LPARAM parameter) {
    Session *session = (Session*)parameter;
    DWORD pid; wchar_t name[32];
    GetWindowThreadProcessId(window, &pid);
    if (pid == session->pid && IsWindowVisible(window) && GetClassNameW(window, name, 32) && lstrcmpW(name, L"DRMainWindow") == 0) {
        session->window = window;
        return FALSE;
    }
    return TRUE;
}

static BOOL borderless(HWND window) {
    RECT client;
    if (!GetClientRect(window, &client) || client.right <= 0 || client.bottom <= 0) return FALSE;
    LONG style = GetWindowLongW(window, GWL_STYLE);
    LONG extended = GetWindowLongW(window, GWL_EXSTYLE);
    SetLastError(0);
    if (!SetWindowLongW(window, GWL_STYLE, style & ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU)) && GetLastError()) return FALSE;
    SetLastError(0);
    if (!SetWindowLongW(window, GWL_EXSTYLE, extended & ~(WS_EX_DLGMODALFRAME | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE | WS_EX_WINDOWEDGE)) && GetLastError()) {
        SetWindowLongW(window, GWL_STYLE, style);
        return FALSE;
    }
    if (SetWindowPos(window, NULL, 0, 0, client.right, client.bottom, SWP_FRAMECHANGED | SWP_NOZORDER | SWP_NOACTIVATE)) return TRUE;
    SetWindowLongW(window, GWL_STYLE, style);
    SetWindowLongW(window, GWL_EXSTYLE, extended);
    SetWindowPos(window, NULL, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    return FALSE;
}

static DWORD WINAPI input(void *parameter) {
    Session *session = (Session*)parameter;
    HANDLE pipe = GetStdHandle(STD_INPUT_HANDLE);
    uint8_t action; DWORD count;
    while (ReadFile(pipe, &action, 1, &count, NULL) && count == 1) {
        if (action < 1 || action > 3 || WaitForSingleObject(session->process, 0) != WAIT_TIMEOUT) continue;
        if (GetForegroundWindow() != session->window) {
            puts("AARDVARK_TOUCH_RESULT 2"); fflush(stdout); continue;
        }
        DWORD_PTR ignored;
        RemovePropW(session->window, TOUCH_RESULT);
#ifdef AARDVARK_INPUT_TEST_ATTACH
        POINT before, after; GetCursorPos(&before);
#endif
        if (!SendMessageTimeoutW(session->window, session->command, TOUCH_MAGIC | action, GetTickCount() + 750,
                                 SMTO_ABORTIFHUNG | SMTO_BLOCK, 2000, &ignored)) {
            puts("AARDVARK_TOUCH_UNAVAILABLE"); fflush(stdout);
            break;
        }
        unsigned result = (unsigned)(uintptr_t)GetPropW(session->window, TOUCH_RESULT);
        if (!result) { puts("AARDVARK_TOUCH_UNAVAILABLE"); fflush(stdout); break; }
        if (result == TouchUnsupported) { puts("AARDVARK_TOUCH_UNSUPPORTED"); fflush(stdout); break; }
        printf("AARDVARK_TOUCH_RESULT %u\n", result); fflush(stdout);
#ifdef AARDVARK_INPUT_TEST_ATTACH
        GetCursorPos(&after);
        printf("Touch action %u result %u cursor %s\n", action, result,
               before.x == after.x && before.y == after.y ? "unchanged" : "changed"); fflush(stdout);
#endif
    }
    return 0;
}

int main(void) {
    setvbuf(stdout, NULL, _IONBF, 0);
    puts("AARDVARK_GAME_HOST_READY");
    wchar_t module[32768], dll[32768], directory[32768];
    DWORD length = GetModuleFileNameW(NULL, module, 32768);
    if (!length || length >= 32768) return 10;
    wchar_t *separator = wcsrchr(module, L'\\');
    if (!separator || separator - module > 32600) return 10;
    separator[1] = 0;
    wcscpy(dll, module); wcscat(dll, L"AardvarkTouch.dll");
    HMODULE library = LoadLibraryExW(dll, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!library) { printf("AARDVARK_GAME_ERROR %lu\n", GetLastError()); return 11; }
    HOOKPROC callback = (HOOKPROC)GetProcAddress(library, "AardvarkTouchHook");
    if (!callback) callback = (HOOKPROC)GetProcAddress(library, "AardvarkTouchHook@12");
    if (!callback) { FreeLibrary(library); return 11; }
    Session session = {0};
    PROCESS_INFORMATION child = {0};
#ifdef AARDVARK_INPUT_TEST_ATTACH
    session.window = FindWindowW(L"DRMainWindow", NULL);
    if (!session.window) return 12;
    GetWindowThreadProcessId(session.window, &session.pid);
    session.process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, session.pid);
    if (!session.process) { FreeLibrary(library); return 12; }
#else
    length = GetCurrentDirectoryW(32768, directory);
    if (!length || length > 32600) { FreeLibrary(library); return 12; }
    wchar_t executable[32768], arguments[] = L"\"DungeonRunners.exe\" ran_from_launcher";
    wcscpy(executable, directory); wcscat(executable, L"\\DungeonRunners.exe");
    STARTUPINFOW startup = {0}; startup.cb = sizeof(startup);
    if (!CreateProcessW(executable, arguments, NULL, NULL, FALSE, 0, NULL, directory, &startup, &child)) {
        printf("AARDVARK_GAME_ERROR %lu\n", GetLastError());
        FreeLibrary(library); return 12;
    }
    session.pid = child.dwProcessId; session.process = child.hProcess;
    CloseHandle(child.hThread);
#endif
    puts("AARDVARK_GAME_PROCESS_READY");
    session.command = RegisterWindowMessageW(TOUCH_MESSAGE);
    while (!session.window && WaitForSingleObject(session.process, 100) == WAIT_TIMEOUT)
        EnumWindows(find_window, (LPARAM)&session);
    if (session.window) {
        puts(borderless(session.window) ? "AARDVARK_GAME_BORDERLESS_READY" : "AARDVARK_GAME_BORDERLESS_UNAVAILABLE");
        puts("AARDVARK_GAME_WINDOW_READY");
    }
    HHOOK hook = NULL; HANDLE reader = NULL;
    if (session.window && session.command) {
        DWORD thread = GetWindowThreadProcessId(session.window, NULL);
        hook = SetWindowsHookExW(WH_CALLWNDPROC, callback, library, thread);
        if (hook) reader = CreateThread(NULL, 0, input, &session, 0, NULL);
    }
    puts(reader ? "AARDVARK_TOUCH_READY" : "AARDVARK_TOUCH_UNAVAILABLE"); fflush(stdout);
#ifdef AARDVARK_INPUT_TEST_ATTACH
    if (reader) {
        HANDLE handles[] = {session.process, reader};
        WaitForMultipleObjects(2, handles, FALSE, INFINITE);
    }
#else
    WaitForSingleObject(session.process, INFINITE);
#endif
    DWORD result = 0; GetExitCodeProcess(session.process, &result);
    printf("AARDVARK_GAME_EXITED %lu\n", result);
    if (reader) { CancelSynchronousIo(reader); WaitForSingleObject(reader, 3000); CloseHandle(reader); }
    if (hook) UnhookWindowsHookEx(hook);
    if (session.window) RemovePropW(session.window, TOUCH_RESULT);
#ifdef AARDVARK_INPUT_TEST_ATTACH
    if (result == STILL_ACTIVE) result = 0;
#endif
    CloseHandle(session.process); FreeLibrary(library);
    return (int)result;
}
