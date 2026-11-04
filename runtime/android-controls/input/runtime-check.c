#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

int main(void) {
    if (sizeof(void*) != 4) return 10;
    SYSTEM_INFO system;
    GetSystemInfo(&system);
    SIZE_T size = system.dwAllocationGranularity;
    uint8_t *memory = VirtualAlloc((void*)0x18000000, size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (memory != (void*)0x18000000) return 11;
    const uint8_t code[] = {0xb8, 0x27, 0x41, 0, 0, 0xc3};
    memcpy(memory, code, sizeof(code));
    DWORD previous;
    if (!VirtualProtect(memory, size, PAGE_EXECUTE_READ, &previous)) return 12;
    if (!FlushInstructionCache(GetCurrentProcess(), memory, sizeof(code))) return 13;
    if (((int(__cdecl*)(void))memory)() != 0x4127) return 14;
    if (!VirtualFree(memory, 0, MEM_RELEASE)) return 15;
    HMODULE controls = LoadLibraryW(L"comctl32.dll");
    if (!controls) {
        printf("AARDVARK_RUNTIME_LIBRARY_FAILED comctl32.dll %lu\n", (unsigned long)GetLastError());
        return 16;
    }
    if (!GetProcAddress(controls, "InitCommonControlsEx")) {
        printf("AARDVARK_RUNTIME_LIBRARY_FAILED comctl32.dll %lu\n", (unsigned long)GetLastError());
        FreeLibrary(controls);
        return 17;
    }
    FreeLibrary(controls);
    puts("AARDVARK_RUNTIME_LIBRARY_READY comctl32.dll");
    puts("AARDVARK_RUNTIME_READY");
    return 0;
}
