#include <psapi.h>

static void report_health(HANDLE process, HWND window) {
    FILETIME created, ended, kernel, user;
    long long cpu = -1, working = -1, committed = -1;
    int responds = -1;
    DWORD owner = 0;
    DWORD threadId = window ? GetWindowThreadProcessId(window, &owner) : 0;
    HANDLE thread = threadId && owner == GetProcessId(process) ? OpenThread(THREAD_QUERY_INFORMATION, FALSE, threadId) : NULL;
    if (thread) {
        if (GetThreadTimes(thread, &created, &ended, &kernel, &user)) {
            ULARGE_INTEGER k, u;
            k.LowPart = kernel.dwLowDateTime; k.HighPart = kernel.dwHighDateTime;
            u.LowPart = user.dwLowDateTime; u.HighPart = user.dwHighDateTime;
            cpu = (long long)((k.QuadPart + u.QuadPart) / 10000);
        }
        CloseHandle(thread);
    }
    PROCESS_MEMORY_COUNTERS_EX memory = {0};
    memory.cb = sizeof(memory);
    if (GetProcessMemoryInfo(process, (PROCESS_MEMORY_COUNTERS*)&memory, sizeof(memory))) {
        working = (long long)(memory.WorkingSetSize / 1024);
        if (memory.PrivateUsage) committed = (long long)(memory.PrivateUsage / 1024);
    }
    if (window && IsWindow(window)) {
        DWORD_PTR result;
        responds = SendMessageTimeoutW(window, WM_NULL, 0, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, 100, &result) ? 1 : 0;
    }
    printf("AARDVARK_GAME_HEALTH %lld %lld %lld %d\n", cpu, working, committed, responds);
    fflush(stdout);
}
