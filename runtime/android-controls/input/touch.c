#include "profile.h"
#include "wait-observer.h"

static uintptr_t image;
static UINT command;

__declspec(dllexport) LRESULT CALLBACK AardvarkTouchHook(int code, WPARAM sent, LPARAM parameter) {
    if (code >= 0) {
        const CWPSTRUCT *message = (const CWPSTRUCT*)parameter;
        if (!waitStarted) start_wait_observer(message->hwnd);
        unsigned action = message->wParam & 15;
        if (message->message == command && (message->wParam & ~15u) == TOUCH_MAGIC && action >= 1 && action <= 3) {
            DWORD process = 0;
            wchar_t kind[32];
            GetWindowThreadProcessId(message->hwnd, &process);
            int result = TouchUnavailable;
            if (process == GetCurrentProcessId() && GetForegroundWindow() == message->hwnd &&
                GetClassNameW(message->hwnd, kind, 32) && lstrcmpW(kind, L"DRMainWindow") == 0) {
                result = TouchUnsupported;
                if (supported(image)) {
                    uintptr_t icon;
                    result = select_icon(image, action, &icon);
                    int32_t remaining = (int32_t)((uint32_t)message->lParam - GetTickCount());
                    if (remaining <= 0 || remaining > 1000) result = TouchBusy;
                    if (result == TouchApplied) {
                        typedef void (__attribute__((thiscall)) *UseItem)(void*);
                        ((UseItem)(image + 0x380e0))((void*)icon);
                    }
                }
            }
            SetPropW(message->hwnd, TOUCH_RESULT, (HANDLE)(uintptr_t)result);
        }
    }
    return CallNextHookEx(NULL, code, sent, parameter);
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, void *reserved) {
    if (reason == DLL_PROCESS_ATTACH) {
        image = (uintptr_t)GetModuleHandleW(NULL);
        command = RegisterWindowMessageW(TOUCH_MESSAGE);
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}
