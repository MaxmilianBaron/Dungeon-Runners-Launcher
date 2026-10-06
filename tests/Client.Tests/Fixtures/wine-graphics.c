#define COBJMACROS
#define DIRECTDRAW_VERSION 0x0700
#include <windows.h>
#include <ddraw.h>
#include <d3d9.h>
#include <stdio.h>

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    for (int i = 0; i < 10; ++i)
    {
        IDirectDraw7 *draw = NULL;
        HRESULT result = DirectDrawCreateEx(NULL, (void **)&draw, &IID_IDirectDraw7, NULL);
        if (FAILED(result)) { printf("DirectDrawCreateEx: %08lx\n", result); return 1; }
        DDCAPS caps = {0};
        caps.dwSize = sizeof(caps);
        result = IDirectDraw7_GetCaps(draw, &caps, NULL);
        IDirectDraw7_Release(draw);
        if (FAILED(result)) return 2;
    }
    puts("PASS DirectDraw initialization");
    WNDCLASSW type = {0};
    type.lpfnWndProc = DefWindowProcW;
    type.hInstance = GetModuleHandleW(NULL);
    type.lpszClassName = L"WineGraphicsTest";
    if (!RegisterClassW(&type)) return 3;
    HWND window = CreateWindowW(type.lpszClassName, L"Graphics test", WS_OVERLAPPEDWINDOW,
        0, 0, 64, 64, NULL, NULL, type.hInstance, NULL);
    if (!window) return 4;
    IDirect3D9 *d3d = Direct3DCreate9(D3D_SDK_VERSION);
    if (!d3d) return 5;
    D3DPRESENT_PARAMETERS parameters = {0};
    parameters.BackBufferWidth = 64;
    parameters.BackBufferHeight = 64;
    parameters.BackBufferFormat = D3DFMT_X8R8G8B8;
    parameters.BackBufferCount = 1;
    parameters.SwapEffect = D3DSWAPEFFECT_DISCARD;
    parameters.hDeviceWindow = window;
    parameters.Windowed = TRUE;
    IDirect3DDevice9 *device = NULL;
    HRESULT result = IDirect3D9_CreateDevice(d3d, 0, D3DDEVTYPE_HAL, window,
        D3DCREATE_SOFTWARE_VERTEXPROCESSING, &parameters, &device);
    if (FAILED(result)) { printf("Direct3D device: %08lx\n", result); return 6; }
    if (FAILED(IDirect3DDevice9_Clear(device, 0, NULL, D3DCLEAR_TARGET, D3DCOLOR_XRGB(37, 113, 181), 1, 0))) return 7;
    IDirect3DSurface9 *target = NULL, *copy = NULL;
    if (FAILED(IDirect3DDevice9_GetRenderTarget(device, 0, &target))) return 8;
    if (FAILED(IDirect3DDevice9_CreateOffscreenPlainSurface(device, 64, 64, D3DFMT_X8R8G8B8, D3DPOOL_SYSTEMMEM, &copy, NULL))) return 9;
    if (FAILED(IDirect3DDevice9_GetRenderTargetData(device, target, copy))) return 10;
    D3DLOCKED_RECT pixels;
    if (FAILED(IDirect3DSurface9_LockRect(copy, &pixels, NULL, D3DLOCK_READONLY))) return 11;
    unsigned color = *(const unsigned*)pixels.pBits & 0xffffff;
    IDirect3DSurface9_UnlockRect(copy);
    if (color != 0x2571b5) { printf("Pixel: %06x\n", color); return 12; }
    if (FAILED(IDirect3DDevice9_Present(device, NULL, NULL, NULL, NULL))) return 13;
    IDirect3DSurface9_Release(copy);
    IDirect3DSurface9_Release(target);
    IDirect3DDevice9_Release(device);
    IDirect3D9_Release(d3d);
    DestroyWindow(window);
    puts("PASS Direct3D render, readback and present");
    return 0;
}
