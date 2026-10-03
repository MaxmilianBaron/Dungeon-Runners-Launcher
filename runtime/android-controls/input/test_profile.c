#include <stdio.h>
#include <string.h>
#include "profile.h"

static uintptr_t base, ui, zone, avatar, list, icons[3];
static unsigned checks;

static void set(uintptr_t address, uint32_t value) { memcpy((void*)address, &value, 4); }
static void check(int success, const char *name) {
    ++checks;
    if (!success) { fprintf(stderr, "FAIL: %s\n", name); ExitProcess(1); }
}
static void reset(void) {
    memset((void*)base, 0, 0x600000);
    ui = base + 0x10000; zone = base + 0x11000; avatar = base + 0x12000; list = base + 0x13000;
    set(base + 0x5314b0, (uint32_t)ui); set(ui + 0xb4, 8);
    set(ui + 0x1b4, (uint32_t)zone); set(zone, (uint32_t)base + 0x4ab1b8);
    set(zone + 0xf0, 1); set(zone + 0xf8, (uint32_t)avatar);
    set(avatar, (uint32_t)base + 0x49b468); set(avatar + 0x80, 1); set(avatar + 0x98, 1);
    set(ui + 0x234, (uint32_t)list); set(list, (uint32_t)base + 0x44b150);
    for (unsigned i = 0; i < 3; i++) {
        icons[i] = base + 0x14000 + i * 0x1000;
        set(list + 0x1ac + i * 4, i + 100);
        set(icons[i], (uint32_t)base + 0x44aeb0); set(icons[i] + 0x174, i + 100);
        set(icons[i] + 0x14, (uint32_t)list); set(icons[i] + 0x16c, (uint32_t)avatar);
        set(icons[i] + 0x17c, (uint32_t)base + 0x20000 + i * 256);
        set(icons[i] + 0x180, (uint32_t)base + 0x20000 + i * 256 + 12);
        if (i) set(icons[i - 1] + 0x20, (uint32_t)icons[i]);
    }
    set(list + 0x18, (uint32_t)icons[0]);
}

int main(void) {
    base = (uintptr_t)VirtualAlloc((void*)0x10000000, 0x600000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    check(base == 0x10000000, "fixture allocation");
    uintptr_t result;
    reset();
    for (unsigned i = 1; i <= 3; i++) check(select_icon(base, i, &result) == TouchApplied && result == icons[i - 1], "native control selection");
    check(select_icon(base, 0, &result) == TouchUnavailable, "invalid action");
    set(ui + 0x180, 1); check(select_icon(base, 1, &result) == TouchBusy, "modal input blocked");
    reset(); set(ui + 0x21c, (uint32_t)base + 0x21000); set(base + 0x2120c, 1);
    check(select_icon(base, 2, &result) == TouchBusy, "chat input blocked");
    reset(); set(ui + 0x268, (uint32_t)base + 0x22000); set(base + 0x22179, 1);
    check(select_icon(base, 3, &result) == TouchBusy, "text prompt blocked");
    for (unsigned i = 0x1d8; i <= 0x1e0; i += 4) {
        reset(); set(ui + i, (uint32_t)base + 0x23000); set(base + 0x230b4, 8);
        check(select_icon(base, 1, &result) == TouchBusy, "menu input blocked");
    }
    reset(); set(zone + 0xf0, 0); check(select_icon(base, 1, &result) == TouchUnavailable, "loading blocked");
    reset(); set(icons[0] + 0x180, pointer(icons[0] + 0x17c));
    check(select_icon(base, 1, &result) == TouchEmpty, "empty item types");
    reset(); set(icons[0] + 0x180, pointer(icons[0] + 0x17c) + 256);
    check(select_icon(base, 1, &result) == TouchApplied, "multiple item variants");
    set(icons[0] + 0x180, pointer(icons[0] + 0x17c) + 260);
    check(select_icon(base, 1, &result) == TouchEmpty, "invalid type vector");
    reset(); set(list + 0x1ac, 12345); set(icons[2] + 0x20, (uint32_t)icons[0]);
    check(select_icon(base, 1, &result) == TouchUnavailable, "cyclic siblings bounded");
    reset(); unsigned budget = 256;
    set(icons[0] + 0x20, (uint32_t)icons[0]);
    check(find_icon(base, list, 12345, 24, &budget) == 0 && budget == 0, "depth-limit cycle bounded");
    reset(); set(icons[0] + 0x14, 0);
    check(select_icon(base, 1, &result) == TouchUnavailable, "foreign parent rejected");
    reset(); set(ui + 0x234, 0x11111111);
    check(select_icon(base, 1, &result) == TouchUnavailable, "invalid pointer rejected");
    check(!supported(base), "unsupported image rejected");
    VirtualFree((void*)base, 0, MEM_RELEASE);
    printf("PASS: %u game input checks\n", checks);
    return 0;
}
