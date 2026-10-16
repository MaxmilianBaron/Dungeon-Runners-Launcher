#define COBJMACROS
#include <windows.h>
#include <d3dx9.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef HRESULT (WINAPI *CreateCompiler)(LPCVOID, UINT, const D3DXMACRO*, ID3DXInclude*, DWORD, ID3DXEffectCompiler**, ID3DXBuffer**);

static void *read_source(const char *path, UINT *size) {
    FILE *input = fopen(path, "rb");
    if (!input) return NULL;
    if (fseek(input, 0, SEEK_END)) { fclose(input); return NULL; }
    long bytes = ftell(input);
    if (bytes < 1 || bytes > 1048576 || fseek(input, 0, SEEK_SET)) { fclose(input); return NULL; }
    char *source = malloc((size_t)bytes + 1);
    if (!source) { fclose(input); return NULL; }
    size_t count = fread(source, 1, (size_t)bytes, input);
    fclose(input);
    if (count != (size_t)bytes) { free(source); return NULL; }
    source[count] = 0;
    *size = (UINT)count;
    return source;
}

static HRESULT WINAPI include_open(ID3DXInclude *self, D3DXINCLUDE_TYPE kind, LPCSTR name, LPCVOID parent, LPCVOID *data, UINT *size) {
    if (!name || strstr(name, "..") || strchr(name, ':') || strchr(name, '/') || strchr(name, '\\')) return E_FAIL;
    char path[512];
    if (snprintf(path, sizeof(path), "effects/2.0/%s", name) >= (int)sizeof(path)) return E_FAIL;
    *data = read_source(path, size);
    return *data ? S_OK : E_FAIL;
}

static HRESULT WINAPI include_close(ID3DXInclude *self, LPCVOID data) { free((void*)data); return S_OK; }

int main(int argc, char **argv) {
    setvbuf(stdout, NULL, _IONBF, 0);
    if (argc > 2 || (argc == 2 && !SetCurrentDirectoryA(argv[1]))) return 9;
    WCHAR compilerPath[MAX_PATH];
    DWORD compilerLength = GetFullPathNameW(L"d3dx9_31.dll", MAX_PATH, compilerPath, NULL);
    if (!compilerLength || compilerLength >= MAX_PATH) return 10;
    HMODULE library = LoadLibraryExW(compilerPath, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    CreateCompiler create = library ? (CreateCompiler)GetProcAddress(library, "D3DXCreateEffectCompiler") : NULL;
    if (!create) return 10;
    FILE *list = fopen("sources.txt", "r");
    if (!list) { FreeLibrary(library); return 11; }
    ID3DXIncludeVtbl callbacks = {include_open, include_close};
    ID3DXInclude includes = {&callbacks};
    char path[256]; unsigned count = 0; DWORD started = GetTickCount(); int exitCode = 0;
    while (fgets(path, sizeof(path), list)) {
        path[strcspn(path, "\r\n")] = 0;
        if (strncmp(path, "effects/2.0/", 12) || strspn(path, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_/.") != strlen(path)
            || strstr(path, "..") || ++count > 64) { exitCode = 12; break; }
        UINT size = 0; void *source = read_source(path, &size);
        if (!source) { exitCode = 13; break; }
        ID3DXEffectCompiler *compiler = NULL; ID3DXBuffer *binary = NULL, *errors = NULL;
        HRESULT result = create(source, size, NULL, &includes, 0x100, &compiler, &errors);
        free(source);
        if (errors) { ID3DXBuffer_Release(errors); errors = NULL; }
        if (SUCCEEDED(result)) result = compiler->lpVtbl->CompileEffect(compiler, 0x100, &binary, &errors);
        if (SUCCEEDED(result) && binary) {
            char output[300];
            snprintf(output, sizeof(output), "compiled/%s", path);
            FILE *file = fopen(output, "wb");
            size = ID3DXBuffer_GetBufferSize(binary);
            if (!file || size < 4 || size > 1048576) result = E_FAIL;
            else if (fwrite(ID3DXBuffer_GetBufferPointer(binary), 1, size, file) != size) result = E_FAIL;
            if (file && fclose(file)) result = E_FAIL;
        } else if (SUCCEEDED(result)) result = E_FAIL;
        if (errors) ID3DXBuffer_Release(errors);
        if (binary) ID3DXBuffer_Release(binary);
        if (compiler) compiler->lpVtbl->Release(compiler);
        if (FAILED(result)) { printf("AARDVARK_SHADERS_FAILED %u %08lx\n", count, (unsigned long)result); exitCode = 14; break; }
        printf("AARDVARK_SHADERS_COMPILED %u\n", count);
    }
    if (ferror(list) || !count) exitCode = 15;
    fclose(list); FreeLibrary(library);
    if (!exitCode) printf("AARDVARK_SHADERS_READY %u %lu\n", count, (unsigned long)(GetTickCount() - started));
    return exitCode;
}
