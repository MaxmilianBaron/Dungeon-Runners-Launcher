#include <stdio.h>
#include "waits.h"

static HMODULE waitLibrary;
static volatile LONG waitStarted;

static DWORD WINAPI observe_waits(void *parameter) {
    HWND window = parameter;
    DWORD owner = 0, threadId = GetWindowThreadProcessId(window, &owner);
    if (!threadId || owner != GetCurrentProcessId()) return 0;
    HANDLE thread = OpenThread(THREAD_QUERY_INFORMATION | SYNCHRONIZE, FALSE, threadId);
    if (!thread) return 0;
    wchar_t path[32768];
    DWORD length = GetModuleFileNameW(waitLibrary, path, 32768);
    wchar_t *separator = length && length < 32600 ? wcsrchr(path, L'\\') : NULL;
    if (!separator) { CloseHandle(thread); return 0; }
    wcscpy(separator + 1, L"AardvarkWaits.log");
    ULONGLONG lastCpu = ~(ULONGLONG)0, inactiveSince = GetTickCount64();
    unsigned captures = 0;
    while (captures < 2 && WaitForSingleObject(thread, 5000) == WAIT_TIMEOUT && IsWindow(window)) {
        FILETIME created, ended, kernel, user;
        if (!GetThreadTimes(thread, &created, &ended, &kernel, &user)) break;
        ULARGE_INTEGER k, u;
        k.LowPart = kernel.dwLowDateTime; k.HighPart = kernel.dwHighDateTime;
        u.LowPart = user.dwLowDateTime; u.HighPart = user.dwHighDateTime;
        ULONGLONG cpu = (k.QuadPart + u.QuadPart) / 10000, now = GetTickCount64();
        DWORD_PTR result;
        BOOL responds = SendMessageTimeoutW(window, WM_NULL, 0, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, 100, &result) != 0;
        if (cpu != lastCpu || responds) inactiveSince = now;
        else if (now - inactiveSince >= 60000) {
            FILE *output = _wfopen(path, L"w");
            if (output) { report_waits(GetCurrentProcess(), window, output); fclose(output); }
            inactiveSince = now;
            captures++;
        }
        lastCpu = cpu;
    }
    CloseHandle(thread);
    return 0;
}

static void start_wait_observer(HWND window) {
    DWORD owner = 0;
    wchar_t kind[32];
    GetWindowThreadProcessId(window, &owner);
    if (owner != GetCurrentProcessId() || !GetClassNameW(window, kind, 32) || lstrcmpW(kind, L"DRMainWindow")) return;
    if (InterlockedCompareExchange(&waitStarted, 1, 0)) return;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        (const wchar_t *)(uintptr_t)observe_waits, &waitLibrary)) return;
    HANDLE thread = CreateThread(NULL, 0, observe_waits, window, 0, NULL);
    if (thread) CloseHandle(thread);
}
