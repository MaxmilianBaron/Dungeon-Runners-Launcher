#include <tlhelp32.h>
#include <string.h>
#include <stdlib.h>

typedef struct { DWORD base, size; char name[64]; DWORD codeStart[32], codeSize[32]; unsigned codes; } WaitModule;

static void wait_status(FILE *output, const char *stage, DWORD detail) {
    fprintf(output, "AARDVARK_GAME_WAIT_STATUS %s %lu\n", stage, detail);
    fflush(output);
}

static unsigned wait_modules(HANDLE process, WaitModule *modules, unsigned limit) {
    DWORD pid = GetProcessId(process);
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, pid == GetCurrentProcessId() ? 0 : pid);
    if (snapshot == INVALID_HANDLE_VALUE) return 0;
    MODULEENTRY32W entry = {0}; entry.dwSize = sizeof(entry);
    unsigned count = 0;
    if (Module32FirstW(snapshot, &entry)) do {
        if (count == limit) break;
        WaitModule value = {0};
        unsigned n = 0;
        for (; entry.szModule[n] && n + 1 < sizeof(value.name); n++) {
            WCHAR c = entry.szModule[n];
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-')) break;
            value.name[n] = (char)c;
        }
        if (!n || entry.szModule[n]) continue;
        value.base = (DWORD)(uintptr_t)entry.modBaseAddr; value.size = entry.modBaseSize;
        IMAGE_DOS_HEADER dos;
        IMAGE_NT_HEADERS32 pe;
        SIZE_T read = 0;
        if (!ReadProcessMemory(process, entry.modBaseAddr, &dos, sizeof(dos), &read) || read != sizeof(dos)
            || dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew < 0 || (DWORD)dos.e_lfanew > value.size
            || value.size - (DWORD)dos.e_lfanew < sizeof(pe)) continue;
        if (!ReadProcessMemory(process, entry.modBaseAddr + dos.e_lfanew, &pe, sizeof(pe), &read) || read != sizeof(pe)
            || pe.Signature != IMAGE_NT_SIGNATURE || pe.FileHeader.Machine != IMAGE_FILE_MACHINE_I386
            || pe.OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR32_MAGIC || pe.FileHeader.NumberOfSections > 32) continue;
        DWORD offset = (DWORD)dos.e_lfanew + sizeof(DWORD) + sizeof(IMAGE_FILE_HEADER) + pe.FileHeader.SizeOfOptionalHeader;
        for (unsigned i = 0; i < pe.FileHeader.NumberOfSections; i++, offset += sizeof(IMAGE_SECTION_HEADER)) {
            IMAGE_SECTION_HEADER section;
            if (offset > value.size || value.size - offset < sizeof(section)
                || !ReadProcessMemory(process, entry.modBaseAddr + offset, &section, sizeof(section), &read) || read != sizeof(section)) break;
            if (!(section.Characteristics & IMAGE_SCN_MEM_EXECUTE) || section.VirtualAddress >= value.size) continue;
            DWORD size = section.Misc.VirtualSize;
            if (size > value.size - section.VirtualAddress) size = value.size - section.VirtualAddress;
            value.codeStart[value.codes] = section.VirtualAddress; value.codeSize[value.codes++] = size;
        }
        if (value.codes) modules[count++] = value;
    } while (Module32NextW(snapshot, &entry));
    CloseHandle(snapshot);
    return count;
}

static BOOL wait_address(DWORD address, const WaitModule *modules, unsigned count, FILE *output) {
    for (unsigned i = 0; i < count; i++) {
        if (address < modules[i].base || address - modules[i].base >= modules[i].size) continue;
        DWORD offset = address - modules[i].base;
        for (unsigned s = 0; s < modules[i].codes; s++) {
            if (offset >= modules[i].codeStart[s] && offset - modules[i].codeStart[s] < modules[i].codeSize[s]) {
                fprintf(output, " %s+%08lx", modules[i].name, offset);
                return TRUE;
            }
        }
    }
    return FALSE;
}

static void report_wait(HANDLE process, DWORD threadId, const char *kind, const WaitModule *modules, unsigned count, FILE *output) {
    HANDLE thread = OpenThread(THREAD_GET_CONTEXT | THREAD_SUSPEND_RESUME | THREAD_QUERY_INFORMATION, FALSE, threadId);
    if (!thread || GetProcessIdOfThread(thread) != GetProcessId(process)) { if (thread) CloseHandle(thread); return; }
    CONTEXT context = {0}; context.ContextFlags = CONTEXT_CONTROL;
    DWORD stack[2048]; SIZE_T read = 0;
    BOOL captured = FALSE;
    wait_status(output, "suspend", threadId);
    if (SuspendThread(thread) != (DWORD)-1) {
        wait_status(output, "context", threadId);
        captured = GetThreadContext(thread, &context);
        if (captured) {
            wait_status(output, "stack", threadId);
            MEMORY_BASIC_INFORMATION region;
            if (VirtualQueryEx(process, (void*)(uintptr_t)context.Esp, &region, sizeof(region)) == sizeof(region)
                && region.State == MEM_COMMIT && !(region.Protect & (PAGE_GUARD | PAGE_NOACCESS))) {
                uintptr_t end = (uintptr_t)region.BaseAddress + region.RegionSize;
                SIZE_T size = end > context.Esp ? end - context.Esp : 0;
                if (size > sizeof(stack)) size = sizeof(stack);
                if (size && !ReadProcessMemory(process, (void*)(uintptr_t)context.Esp, stack, size, &read)) read = 0;
            }
        }
        wait_status(output, "resume", threadId);
        ResumeThread(thread);
    }
    CloseHandle(thread);
    fprintf(output, "AARDVARK_GAME_WAIT %s %08lx ip", kind, threadId);
    if (!captured || !wait_address(context.Eip, modules, count, output)) fprintf(output, " unavailable");
    fputc('\n', output);
    if (!captured || read < 4) return;
    fprintf(output, "AARDVARK_GAME_WAIT %s %08lx stack_candidates", kind, threadId);
    DWORD previous[12]; unsigned printed = 0;
    for (SIZE_T i = 0; i < read / 4 && printed < 12; i++) {
        BOOL duplicate = FALSE;
        for (unsigned p = 0; p < printed; p++) if (previous[p] == stack[i]) duplicate = TRUE;
        if (!duplicate && wait_address(stack[i], modules, count, output)) previous[printed++] = stack[i];
    }
    if (!printed) fprintf(output, " unavailable");
    fputc('\n', output);
}

static void report_waits(HANDLE process, HWND window, FILE *output) {
    DWORD owner = 0, main = GetWindowThreadProcessId(window, &owner);
    if (!main || owner != GetProcessId(process)) { wait_status(output, "failed", ERROR_INVALID_WINDOW_HANDLE); return; }
    wait_status(output, "modules", 0);
    WaitModule *modules = calloc(128, sizeof(WaitModule));
    if (!modules) { wait_status(output, "failed", ERROR_NOT_ENOUGH_MEMORY); return; }
    unsigned count = wait_modules(process, modules, 128);
    if (!count) { free(modules); fputs("AARDVARK_GAME_WAIT unavailable\n", output); wait_status(output, "failed", GetLastError()); return; }
    report_wait(process, main, "main", modules, count, output);
    wait_status(output, "threads", 0);
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snapshot != INVALID_HANDLE_VALUE) {
        THREADENTRY32 entry = {0}; entry.dwSize = sizeof(entry);
        unsigned captured = 0;
        if (Thread32First(snapshot, &entry)) do {
            if (entry.th32OwnerProcessID == owner && entry.th32ThreadID != main && entry.th32ThreadID != GetCurrentThreadId()) {
                report_wait(process, entry.th32ThreadID, "worker", modules, count, output);
                if (++captured == 8) break;
            }
        } while (Thread32Next(snapshot, &entry));
        CloseHandle(snapshot);
    }
    free(modules);
    wait_status(output, "complete", 0);
}
