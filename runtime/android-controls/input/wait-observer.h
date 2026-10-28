#include <stdio.h>
#include <wchar.h>
#include "waits.h"

typedef struct { HANDLE process; HWND window; DWORD interval; ULONGLONG delay; } WaitObserver;

static DWORD WINAPI observe_waits(void *parameter) {
    WaitObserver state = *(WaitObserver*)parameter;
    free(parameter);
    wchar_t path[32768];
    DWORD length = GetModuleFileNameW(NULL, path, 32768);
    wchar_t *separator = length && length < 32600 ? wcsrchr(path, L'\\') : NULL;
    if (!separator) {
        printf("AARDVARK_GAME_WAIT_STATUS failed %lu\n", (DWORD)ERROR_BAD_PATHNAME); fflush(stdout);
        CloseHandle(state.process); return 0;
    }
    wcscpy(separator + 1, L"AardvarkWaits.log");
    FILE *output = _wfopen(path, L"w");
    if (!output) {
        printf("AARDVARK_GAME_WAIT_STATUS failed %lu\n", (DWORD)ERROR_OPEN_FAILED); fflush(stdout);
        CloseHandle(state.process); return 0;
    }
    setvbuf(output, NULL, _IONBF, 0);
    DWORD owner = 0, threadId = GetWindowThreadProcessId(state.window, &owner);
    HANDLE thread = threadId && owner == GetProcessId(state.process)
        ? OpenThread(THREAD_QUERY_INFORMATION | SYNCHRONIZE, FALSE, threadId) : NULL;
    if (!thread) {
        wait_status(output, "failed", GetLastError());
        fclose(output); CloseHandle(state.process); return 0;
    }
    wait_status(output, "monitoring", 0);
    ULONGLONG lastCpu = ~(ULONGLONG)0, inactiveSince = GetTickCount64();
    unsigned captures = 0;
    while (captures < 2 && WaitForSingleObject(state.process, state.interval) == WAIT_TIMEOUT && IsWindow(state.window)) {
        FILETIME created, ended, kernel, user;
        if (!GetThreadTimes(thread, &created, &ended, &kernel, &user)) {
            wait_status(output, "failed", GetLastError()); break;
        }
        ULARGE_INTEGER k, u;
        k.LowPart = kernel.dwLowDateTime; k.HighPart = kernel.dwHighDateTime;
        u.LowPart = user.dwLowDateTime; u.HighPart = user.dwHighDateTime;
        ULONGLONG cpu = (k.QuadPart + u.QuadPart) / 10000, now = GetTickCount64();
        DWORD_PTR result;
        BOOL responds = SendMessageTimeoutW(state.window, WM_NULL, 0, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, 100, &result) != 0;
        if (cpu != lastCpu || responds) inactiveSince = now;
        else if (now - inactiveSince >= state.delay) {
            if (captures) {
                fclose(output);
                output = _wfopen(path, L"w");
                if (!output) {
                    printf("AARDVARK_GAME_WAIT_STATUS failed %lu\n", (DWORD)ERROR_OPEN_FAILED); fflush(stdout);
                    break;
                }
                setvbuf(output, NULL, _IONBF, 0);
            }
            report_waits(state.process, state.window, output);
            inactiveSince = GetTickCount64();
            captures++;
        }
        lastCpu = cpu;
    }
    if (output) fclose(output);
    CloseHandle(thread);
    CloseHandle(state.process);
    return 0;
}

static HANDLE start_wait_observer(HANDLE process, HWND window, DWORD interval, ULONGLONG delay) {
    DWORD owner = 0;
    if (!GetWindowThreadProcessId(window, &owner) || owner != GetProcessId(process)
        || owner == GetCurrentProcessId() || !interval || !delay) { SetLastError(ERROR_INVALID_PARAMETER); return NULL; }
    WaitObserver *state = calloc(1, sizeof(WaitObserver));
    if (!state) { SetLastError(ERROR_NOT_ENOUGH_MEMORY); return NULL; }
    if (!DuplicateHandle(GetCurrentProcess(), process, GetCurrentProcess(), &state->process,
        PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | SYNCHRONIZE, FALSE, 0)) {
        DWORD error = GetLastError(); free(state); SetLastError(error); return NULL;
    }
    state->window = window; state->interval = interval; state->delay = delay;
    HANDLE observer = CreateThread(NULL, 0, observe_waits, state, 0, NULL);
    if (!observer) { DWORD error = GetLastError(); CloseHandle(state->process); free(state); SetLastError(error); }
    return observer;
}
