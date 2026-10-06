# Native fixtures

`mac-wow64-original.dll` is the unmodified Wine 11.0_1 library described in `runtime/mac-wow64/README.md`, under LGPL-2.1-or-later.

`wine-graphics.exe` exercises 32-bit DirectDraw initialization and Direct3D 9 render, pixel readback and presentation. Rebuild it from this directory with MinGW-w64:

```sh
i686-w64-mingw32-gcc -O2 -s -Wl,--no-insert-timestamp wine-graphics.c -lddraw -ld3d9 -ldxguid -o wine-graphics.exe
```

The runtime integration test runs it twice to cover closing and reopening the graphics client with the same Wine prefix.
