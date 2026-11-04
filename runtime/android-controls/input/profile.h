#ifndef AARDVARK_INPUT_PROFILE_H
#define AARDVARK_INPUT_PROFILE_H
#include <windows.h>
#include <stdint.h>

#define TOUCH_MESSAGE L"Aardvark.Touch.1"
#define TOUCH_RESULT L"Aardvark.Touch.Result"
#define TOUCH_MAGIC 0x41524400

enum TouchResult { TouchApplied = 1, TouchUnavailable, TouchBusy, TouchUnsupported, TouchEmpty };

typedef struct { uint32_t address, size, hash; } CodeRange;
static const CodeRange profile[] = {
    {0x380e0, 364, 0x2306746f}, {0x37d60, 849, 0x4bb91080},
    {0x37cb0, 167, 0x844e57ec}, {0x39130, 136, 0xc9a0e4a1},
    {0x38880, 1669, 0x2620d68d}, {0x38f10, 261, 0xf272a873},
    {0x39020, 262, 0xca29ed56}
};

static int read_value(uintptr_t address, void *value, SIZE_T size) {
    SIZE_T read = 0;
    return address && ReadProcessMemory(GetCurrentProcess(), (const void*)address, value, size, &read) && read == size;
}

static uintptr_t pointer(uintptr_t address) {
    uint32_t value = 0;
    return read_value(address, &value, sizeof(value)) ? value : 0;
}

static int supported(uintptr_t base) {
    if (base != 0x400000 || (pointer(base) & 0xffff) != IMAGE_DOS_SIGNATURE) return 0;
    uintptr_t header = pointer(base + 0x3c);
    if (header > 4096 || pointer(base + header) != IMAGE_NT_SIGNATURE) return 0;
    WORD architecture;
    if (!read_value(base + header + 4, &architecture, 2) || architecture != IMAGE_FILE_MACHINE_I386) return 0;
    for (unsigned i = 0; i < sizeof(profile) / sizeof(profile[0]); i++) {
        uint8_t bytes[2048];
        if (profile[i].size > sizeof(bytes) || !read_value(base + profile[i].address, bytes, profile[i].size)) return 0;
        uint32_t hash = 2166136261u;
        for (unsigned j = 0; j < profile[i].size; j++) hash = (hash ^ bytes[j]) * 16777619u;
        if (hash != profile[i].hash) return 0;
    }
    return 1;
}

static uintptr_t find_icon(uintptr_t base, uintptr_t node, uintptr_t label, unsigned depth, unsigned *budget) {
    if (!node || !*budget) return 0;
    --*budget;
    if (depth > 24) return 0;
    if (pointer(node) == base + 0x44aeb0 && pointer(node + 0x174) == label) return node;
    for (uintptr_t child = pointer(node + 0x18); child && *budget; child = pointer(child + 0x20)) {
        if (pointer(child + 0x14) != node) return 0;
        uintptr_t found = find_icon(base, child, label, depth + 1, budget);
        if (found) return found;
    }
    return 0;
}

static enum TouchResult select_icon(uintptr_t base, unsigned action, uintptr_t *result) {
    *result = 0;
    if (action < 1 || action > 3) return TouchUnavailable;
    uintptr_t ui = pointer(base + 0x5314b0);
    if (!ui || !(pointer(ui + 0xb4) & 8)) return TouchUnavailable;
    uintptr_t zone = pointer(ui + 0x1b4), avatar = pointer(zone + 0xf8);
    if (pointer(zone) != base + 0x4ab1b8 || pointer(avatar) != base + 0x49b468 ||
        !pointer(zone + 0xf0) || !pointer(avatar + 0x80) || !pointer(avatar + 0x98)) return TouchUnavailable;
    if (pointer(ui + 0x180)) return TouchBusy;
    for (unsigned i = 0x1d8; i <= 0x1e0; i += 4) {
        uintptr_t panel = pointer(ui + i);
        if (panel && (pointer(panel + 0xb4) & 8)) return TouchBusy;
    }
    uintptr_t chat = pointer(ui + 0x21c), prompt = pointer(ui + 0x268);
    if ((chat && (pointer(chat + 0x20c) & 255)) || (prompt && (pointer(prompt + 0x179) & 255))) return TouchBusy;
    uintptr_t list = pointer(ui + 0x234);
    if (pointer(list) != base + 0x44b150) return TouchUnavailable;
    uintptr_t label = pointer(list + 0x1ac + (action - 1) * 4);
    unsigned budget = 256;
    uintptr_t icon = label ? find_icon(base, list, label, 0, &budget) : 0;
    if (!icon || !pointer(icon + 0x16c)) return TouchUnavailable;
    uintptr_t first = pointer(icon + 0x17c), end = pointer(icon + 0x180);
    if (!first || end <= first || end - first > 256 || ((end - first) & 3)) return TouchEmpty;
    *result = icon;
    return TouchApplied;
}
#endif
