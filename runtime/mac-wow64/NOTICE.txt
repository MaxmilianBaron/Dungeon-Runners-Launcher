# Wine WoW64 compatibility

`wow64cpu.dll` is built from Wine 11.0 with the local `wow64cpu.patch`, under LGPL-2.1-or-later. Copyright 2021 Alexandre Julliard and the Wine contributors. The license is in `COPYING.LIB`.

The patch uses a far call followed by stack cleanup and an indirect jump for both 32-to-64-bit transitions. This applies the Rosetta SIGUSR1 race workaround from CodeWeavers CW 20760, available in [the macOS Wine source](https://github.com/Gcenx/game-porting-toolkit/blob/main/dlls/wow64cpu/cpu.c), to Wine 11.0's syscall and Unix-call thunks. It preserves the exported interface and works on x86-64 processors as well as under Rosetta. It does not change game files or rendering settings.

The launcher updates only its managed Wine 11.0_1 installation and the corresponding prefix copy. It checks the entire input and output file hashes, backs up the original, and preserves unrecognized or explicitly selected runtimes. Fresh prefixes inherit the corrected library before initialization.

## Sources and rebuild

Download [Wine 11.0 sources](https://dl.winehq.org/wine/source/11.0/wine-11.0.tar.xz), SHA-256 `c07a6857933c1fc60dff5448d79f39c92481c1e9db5aa628db9d0358446e0701`. The unmodified `dlls/wow64cpu/cpu.c` has SHA-256 `088246b519ef984b06b9278047ac894ede9fdd060e73264ee59aa7815bddd972`.

On Linux, configure and build Wine with x86-64 PE support, GNU make, GCC and MinGW-w64. For example, from an empty build directory:

```sh
../wine-11.0/configure --enable-archs=i386,x86_64
make -j4
```

Then run:

```sh
sh runtime/mac-wow64/build.sh /path/to/wine-11.0 /path/to/wine-build /path/to/output
```

The script checks the original source, applies the patch to an output copy, and builds only the replacement DLL. The bundled build used x86_64-w64-mingw32 GCC 13-win32 and GNU Binutils 2.41.90.20240122. Debug symbols are stripped; the PE timestamp and optional checksum are zeroed for reproducible output. Python 3 is required for this final normalization. Other compiler versions can produce different hashes; a replacement must be reviewed and its hash updated in `MacWineCompatibility.cs`.

Bundled DLL SHA-256: `3c60da938c7c7572d5c91c1f0e3e491a47425a16ac760ed96fa3245821289a15`.

Original DLL SHA-256: `cbbf4d054d5488e19d82ea24e7c280c3408ee43b00fce708d1b559acada9d21e`, from [Wine 11.0_1 for macOS](https://github.com/Gcenx/macOS_Wine_builds/releases/tag/11.0_1). An unchanged copy is retained as `tests/Client.Tests/Fixtures/mac-wow64-original.dll` for migration tests and remains under the same license.
