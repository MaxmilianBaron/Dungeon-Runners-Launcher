#define COBJMACROS
#include <windows.h>
#include <d3d9.h>
#include <d3dx9.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stddef.h>

static LRESULT CALLBACK window_proc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    return DefWindowProcW(window, message, wparam, lparam);
}

typedef HRESULT (WINAPI *CreateEffect)(IDirect3DDevice9*, LPCVOID, UINT, const D3DXMACRO*, ID3DXInclude*, DWORD, ID3DXEffectPool*, ID3DXEffect**, ID3DXBuffer**);

static int check_effects(IDirect3DDevice9 *device, const char *folder) {
    if (!SetCurrentDirectoryA(folder)) return 46;
    char compiler[MAX_PATH];
    DWORD length = GetFullPathNameA("d3dx9_31.dll", MAX_PATH, compiler, NULL);
    if (!length || length >= MAX_PATH) return 47;
    HMODULE library = LoadLibraryExA(compiler, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    CreateEffect create = library ? (CreateEffect)GetProcAddress(library, "D3DXCreateEffect") : NULL;
    if (!create) { if (library) FreeLibrary(library); return 47; }
    FILE *list = fopen("sources.txt", "r");
    if (!list) { FreeLibrary(library); return 48; }
    struct vertex { float position[4], normal[3], uv[8][4], weights[4]; DWORD indices, color; float tangent[3], binormal[3]; } triangle[3] = {0};
    D3DVERTEXELEMENT9 declaration[] = {
        {0, offsetof(struct vertex, position), D3DDECLTYPE_FLOAT4, 0, D3DDECLUSAGE_POSITION, 0},
        {0, offsetof(struct vertex, normal), D3DDECLTYPE_FLOAT3, 0, D3DDECLUSAGE_NORMAL, 0},
        {0, offsetof(struct vertex, uv[0]), D3DDECLTYPE_FLOAT4, 0, D3DDECLUSAGE_TEXCOORD, 0},
        {0, offsetof(struct vertex, uv[1]), D3DDECLTYPE_FLOAT4, 0, D3DDECLUSAGE_TEXCOORD, 1},
        {0, offsetof(struct vertex, uv[2]), D3DDECLTYPE_FLOAT4, 0, D3DDECLUSAGE_TEXCOORD, 2},
        {0, offsetof(struct vertex, uv[3]), D3DDECLTYPE_FLOAT4, 0, D3DDECLUSAGE_TEXCOORD, 3},
        {0, offsetof(struct vertex, uv[4]), D3DDECLTYPE_FLOAT4, 0, D3DDECLUSAGE_TEXCOORD, 4},
        {0, offsetof(struct vertex, uv[5]), D3DDECLTYPE_FLOAT4, 0, D3DDECLUSAGE_TEXCOORD, 5},
        {0, offsetof(struct vertex, uv[6]), D3DDECLTYPE_FLOAT4, 0, D3DDECLUSAGE_TEXCOORD, 6},
        {0, offsetof(struct vertex, uv[7]), D3DDECLTYPE_FLOAT4, 0, D3DDECLUSAGE_TEXCOORD, 7},
        {0, offsetof(struct vertex, weights), D3DDECLTYPE_FLOAT4, 0, D3DDECLUSAGE_BLENDWEIGHT, 0},
        {0, offsetof(struct vertex, indices), D3DDECLTYPE_UBYTE4, 0, D3DDECLUSAGE_BLENDINDICES, 0},
        {0, offsetof(struct vertex, color), D3DDECLTYPE_D3DCOLOR, 0, D3DDECLUSAGE_COLOR, 0},
        {0, offsetof(struct vertex, tangent), D3DDECLTYPE_FLOAT3, 0, D3DDECLUSAGE_TANGENT, 0},
        {0, offsetof(struct vertex, binormal), D3DDECLTYPE_FLOAT3, 0, D3DDECLUSAGE_BINORMAL, 0}, D3DDECL_END()
    };
    IDirect3DVertexDeclaration9 *vertices = NULL;
    if (FAILED(IDirect3DDevice9_CreateVertexDeclaration(device, declaration, &vertices))) { fclose(list); FreeLibrary(library); return 53; }
    for (unsigned i = 0; i < 3; i++) {
        triangle[i].position[0] = i == 1 ? 0.5f : -0.5f;
        triangle[i].position[1] = i == 2 ? 0.5f : -0.5f;
        triangle[i].position[2] = 0.5f;
        triangle[i].position[3] = triangle[i].normal[2] = triangle[i].weights[0] = 1;
        triangle[i].color = 0xffffffff;
    }
    char source[256]; unsigned count = 0; int code = 0;
    while (fgets(source, sizeof(source), list)) {
        source[strcspn(source, "\r\n")] = 0;
        if (strncmp(source, "effects/2.0/", 12) || strstr(source, "..")
            || strspn(source, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_/.") != strlen(source) || ++count > 39) { code = 49; break; }
        char path[280];
        if (snprintf(path, sizeof(path), "compiled/%s", source) >= (int)sizeof(path)) { code = 49; break; }
        FILE *input = fopen(path, "rb");
        if (!input) { code = 50; break; }
        if (fseek(input, 0, SEEK_END)) { fclose(input); code = 50; break; }
        long bytes = ftell(input);
        if (bytes < 4 || bytes > 1048576 || fseek(input, 0, SEEK_SET)) { fclose(input); code = 50; break; }
        void *data = malloc((size_t)bytes);
        if (!data) { fclose(input); code = 50; break; }
        size_t read = fread(data, 1, (size_t)bytes, input);
        fclose(input);
        if (read != (size_t)bytes || memcmp(data, "\x01\x09\xff\xfe", 4)) { free(data); code = 50; break; }
        ID3DXEffect *effect = NULL; ID3DXBuffer *errors = NULL;
        printf("GRAPHICS_EFFECT_START %u\n", count);
        HRESULT result = create(device, data, (UINT)bytes, NULL, NULL, 0x10100, NULL, &effect, &errors);
        free(data);
        if (errors) ID3DXBuffer_Release(errors);
        D3DXHANDLE technique = NULL;
        if (SUCCEEDED(result) && effect) result = effect->lpVtbl->FindNextValidTechnique(effect, NULL, &technique);
        else if (SUCCEEDED(result)) result = E_FAIL;
        if (SUCCEEDED(result) && technique) result = effect->lpVtbl->SetTechnique(effect, technique);
        else if (SUCCEEDED(result)) result = E_FAIL;
        UINT passes = 0;
        if (SUCCEEDED(result)) {
            result = effect->lpVtbl->Begin(effect, &passes, 0);
            if (SUCCEEDED(result)) {
                if (!passes) result = E_FAIL;
                for (UINT pass = 0; SUCCEEDED(result) && pass < passes; pass++) {
                    result = effect->lpVtbl->BeginPass(effect, pass);
                    if (SUCCEEDED(result)) {
                        result = IDirect3DDevice9_BeginScene(device);
                        if (SUCCEEDED(result)) {
                            result = IDirect3DDevice9_SetVertexDeclaration(device, vertices);
                            if (SUCCEEDED(result)) result = IDirect3DDevice9_DrawPrimitiveUP(device, D3DPT_TRIANGLELIST, 1, triangle, sizeof(triangle[0]));
                            HRESULT ended = IDirect3DDevice9_EndScene(device);
                            if (SUCCEEDED(result)) result = ended;
                        }
                        HRESULT ended = effect->lpVtbl->EndPass(effect);
                        if (SUCCEEDED(result)) result = ended;
                    }
                }
                HRESULT ended = effect->lpVtbl->End(effect);
                if (SUCCEEDED(result)) result = ended;
            }
        }
        if (SUCCEEDED(result)) result = IDirect3DDevice9_Present(device, NULL, NULL, NULL, NULL);
        if (effect) effect->lpVtbl->Release(effect);
        if (FAILED(result)) { printf("GRAPHICS_EFFECT_FAILED %u %08lx\n", count, (unsigned long)result); code = 51; break; }
        printf("GRAPHICS_EFFECT_READY %u\n", count);
    }
    if (ferror(list) || (!code && count != 39)) code = 52;
    IDirect3DVertexDeclaration9_Release(vertices);
    fclose(list); FreeLibrary(library);
    if (!code) puts("GRAPHICS_EFFECTS_READY 39");
    return code;
}

int main(int argc, char **argv) {
    if (argc > 2) return 19;
    setvbuf(stdout, NULL, _IONBF, 0);
    puts("GRAPHICS_PROCESS_STARTED");
    HINSTANCE instance = GetModuleHandleW(NULL);
    WNDCLASSW type = {0}; type.lpfnWndProc = window_proc; type.hInstance = instance;
    type.lpszClassName = L"AardvarkGraphicsCheck";
    if (!RegisterClassW(&type)) return 20;
    HWND window = CreateWindowW(type.lpszClassName, L"Graphics check", WS_OVERLAPPEDWINDOW | WS_VISIBLE,
        0, 0, 640, 480, NULL, NULL, instance, NULL);
    if (!window) { printf("WINDOW_ERROR %lu\n", GetLastError()); return 21; }
    puts("GRAPHICS_WINDOW_READY");
    IDirect3D9 *d3d = Direct3DCreate9(D3D_SDK_VERSION);
    if (!d3d) return 22;
    D3DADAPTER_IDENTIFIER9 adapter;
    HRESULT result = IDirect3D9_GetAdapterIdentifier(d3d, 0, 0, &adapter);
    if (SUCCEEDED(result)) printf("ADAPTER %s %s\n", adapter.Driver, adapter.Description);
    D3DPRESENT_PARAMETERS parameters = {0}; parameters.BackBufferWidth = 640; parameters.BackBufferHeight = 480;
    parameters.BackBufferFormat = D3DFMT_X8R8G8B8; parameters.BackBufferCount = 1;
    parameters.SwapEffect = D3DSWAPEFFECT_DISCARD; parameters.hDeviceWindow = window; parameters.Windowed = TRUE;
    IDirect3DDevice9 *device = NULL;
    result = IDirect3D9_CreateDevice(d3d, 0, D3DDEVTYPE_HAL, window, D3DCREATE_SOFTWARE_VERTEXPROCESSING, &parameters, &device);
    printf("DEVICE %08lx\n", result);
    if (FAILED(result)) return 23;
    result = IDirect3DDevice9_Clear(device, 0, NULL, D3DCLEAR_TARGET, D3DCOLOR_XRGB(37, 113, 181), 1, 0);
    if (FAILED(result)) return 24;
    IDirect3DSurface9 *target = NULL, *copy = NULL;
    result = IDirect3DDevice9_GetRenderTarget(device, 0, &target);
    if (FAILED(result)) return 25;
    result = IDirect3DDevice9_CreateOffscreenPlainSurface(device, 640, 480, D3DFMT_X8R8G8B8, D3DPOOL_SYSTEMMEM, &copy, NULL);
    if (FAILED(result)) return 26;
    result = IDirect3DDevice9_GetRenderTargetData(device, target, copy);
    if (FAILED(result)) return 27;
    D3DLOCKED_RECT pixels;
    result = IDirect3DSurface9_LockRect(copy, &pixels, NULL, D3DLOCK_READONLY);
    if (FAILED(result)) return 28;
    unsigned color = *(const unsigned*)pixels.pBits & 0xffffff;
    IDirect3DSurface9_UnlockRect(copy);
    printf("PIXEL %06x\n", color);
    if (color != 0x2571b5) return 29;
    result = IDirect3DDevice9_Present(device, NULL, NULL, NULL, NULL);
    printf("PRESENT %08lx\n", result);
    if (FAILED(result)) return 30;
    IDirect3DSurface9_Release(target);
    if (FAILED(IDirect3DDevice9_GetRenderTarget(device, 0, &target))) return 39;
    IDirect3DTexture9 *texture = NULL;
    result = IDirect3DDevice9_CreateTexture(device, 4, 4, 1, 0, D3DFMT_DXT1, D3DPOOL_MANAGED, &texture, NULL);
    if (FAILED(result)) return 31;
    D3DLOCKED_RECT texels;
    if (FAILED(IDirect3DTexture9_LockRect(texture, 0, &texels, NULL, 0))) return 32;
    unsigned short *block = (unsigned short *)texels.pBits;
    block[0] = 0x8410; block[1] = 0; block[2] = 0; block[3] = 0;
    IDirect3DTexture9_UnlockRect(texture, 0);
    struct vertex { float x, y, z, rhw, u, v; } quad[] = {
        {0, 0, 0, 1, 0, 0}, {640, 0, 0, 1, 1, 0}, {0, 480, 0, 1, 0, 1}, {640, 480, 0, 1, 1, 1}
    };
    IDirect3DDevice9_SetRenderState(device, D3DRS_LIGHTING, FALSE);
    IDirect3DDevice9_SetRenderState(device, D3DRS_CULLMODE, D3DCULL_NONE);
    IDirect3DDevice9_SetRenderState(device, D3DRS_ZENABLE, FALSE);
    IDirect3DDevice9_SetFVF(device, D3DFVF_XYZRHW | D3DFVF_TEX1);
    IDirect3DDevice9_SetTexture(device, 0, (IDirect3DBaseTexture9 *)texture);
    IDirect3DDevice9_SetTextureStageState(device, 0, D3DTSS_COLOROP, D3DTOP_SELECTARG1);
    IDirect3DDevice9_SetTextureStageState(device, 0, D3DTSS_COLORARG1, D3DTA_TEXTURE);
    if (FAILED(IDirect3DDevice9_BeginScene(device))) return 33;
    result = IDirect3DDevice9_DrawPrimitiveUP(device, D3DPT_TRIANGLESTRIP, 2, quad, sizeof(quad[0]));
    IDirect3DDevice9_EndScene(device);
    if (FAILED(result)) return 34;
    if (FAILED(IDirect3DDevice9_GetRenderTargetData(device, target, copy))) return 35;
    if (FAILED(IDirect3DSurface9_LockRect(copy, &pixels, NULL, D3DLOCK_READONLY))) return 36;
    color = *(const unsigned *)((const char *)pixels.pBits + 240 * pixels.Pitch + 320 * sizeof(unsigned)) & 0xffffff;
    IDirect3DSurface9_UnlockRect(copy);
    printf("TEXTURE_PIXEL %06x\n", color);
    if (color != 0x848284) return 37;
    if (FAILED(IDirect3DDevice9_Present(device, NULL, NULL, NULL, NULL))) return 38;
    IDirect3DDevice9_SetTexture(device, 0, NULL);
    IDirect3DTexture9_Release(texture);
    const char *renderer = getenv("GALLIUM_DRIVER");
    if (renderer && strcmp(renderer, "virpipe") == 0) {
        IDirect3DSurface9 *front = NULL;
        D3DDISPLAYMODE mode;
        if (FAILED(IDirect3D9_GetAdapterDisplayMode(d3d, 0, &mode))) return 40;
        if (FAILED(IDirect3DDevice9_CreateOffscreenPlainSurface(device, mode.Width, mode.Height, D3DFMT_A8R8G8B8, D3DPOOL_SYSTEMMEM, &front, NULL))) return 41;
        result = IDirect3DDevice9_GetFrontBufferData(device, 0, front);
        printf("FRONT_RESULT %08lx\n", result);
        if (FAILED(result)) return 42;
        POINT point = {100, 100};
        if (!ClientToScreen(window, &point) || point.x < 0 || point.y < 0 || (unsigned)point.x >= mode.Width || (unsigned)point.y >= mode.Height) return 45;
        if (FAILED(IDirect3DSurface9_LockRect(front, &pixels, NULL, D3DLOCK_READONLY))) return 43;
        color = *(const unsigned *)((const char *)pixels.pBits + point.y * pixels.Pitch + point.x * sizeof(unsigned)) & 0xffffff;
        IDirect3DSurface9_UnlockRect(front); IDirect3DSurface9_Release(front);
        printf("FRONT_PIXEL %06x\n", color);
        if (color != 0x848284) return 44;
    }
    if (argc == 2) {
        int effects = check_effects(device, argv[1]);
        if (effects) return effects;
    }
    puts("GRAPHICS_READY");
    IDirect3DSurface9_Release(copy); IDirect3DSurface9_Release(target);
    IDirect3DDevice9_Release(device); IDirect3D9_Release(d3d); DestroyWindow(window);
    return 0;
}
