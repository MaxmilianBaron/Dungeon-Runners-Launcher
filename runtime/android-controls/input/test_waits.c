#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include "waits.h"

static LRESULT CALLBACK window_proc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    return DefWindowProcW(window, message, wparam, lparam);
}

typedef struct { HWND window; HANDLE stop; } TestWait;
static DWORD WINAPI capture(void *parameter) {
    TestWait *test = parameter;
    Sleep(500);
    report_waits(GetCurrentProcess(), test->window, stdout);
    SetEvent(test->stop);
    return 0;
}

int main(int argc, char **argv) {
    setvbuf(stdout, NULL, _IONBF, 0);
    if (argc == 2) {
        HANDLE stop = OpenEventA(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, argv[1]);
        if (!stop) return 10;
        WNDCLASSW type = {0}; type.lpfnWndProc = window_proc; type.hInstance = GetModuleHandleW(NULL);
        type.lpszClassName = L"RuntimeWaitFixture";
        if (!RegisterClassW(&type)) return 11;
        HWND window = CreateWindowW(type.lpszClassName, L"Wait fixture", WS_OVERLAPPEDWINDOW | WS_VISIBLE, 0, 0, 10, 10, NULL, NULL, type.hInstance, NULL);
        if (!window) return 12;
        TestWait test = {window, stop};
        HANDLE worker = CreateThread(NULL, 0, capture, &test, 0, NULL);
        if (!worker) return 14;
        puts("WAIT_FIXTURE_CHILD_READY");
        DWORD result = WaitForSingleObject(stop, 120000);
        WaitForSingleObject(worker, 10000); CloseHandle(worker);
        DestroyWindow(window); CloseHandle(stop);
        return result == WAIT_OBJECT_0 ? 0 : 13;
    }
    char name[96], path[32768], command[33000];
    snprintf(name, sizeof(name), "Local\\RuntimeWaitFixture-%lu", GetCurrentProcessId());
    HANDLE stop = CreateEventA(NULL, TRUE, FALSE, name);
    if (!stop || !GetModuleFileNameA(NULL, path, sizeof(path))) return 20;
    snprintf(command, sizeof(command), "\"%s\" %s", path, name);
    STARTUPINFOA startup = {0}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION child = {0};
    if (!CreateProcessA(path, command, NULL, NULL, FALSE, 0, NULL, NULL, &startup, &child)) return 21;
    DWORD ended = WaitForSingleObject(child.hProcess, 150000), result = 0;
    GetExitCodeProcess(child.hProcess, &result);
    CloseHandle(child.hThread); CloseHandle(child.hProcess); CloseHandle(stop);
    if (ended != WAIT_OBJECT_0 || result) {
        printf("WAIT_FIXTURE_FAILURE wait=%lu exit=%lu\n", ended, result);
        return 22;
    }
    puts("WAIT_FIXTURE_RESUMED");
    return 0;
}
