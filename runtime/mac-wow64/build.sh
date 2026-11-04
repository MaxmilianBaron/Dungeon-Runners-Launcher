#!/bin/sh
set -eu
source_dir=$(cd "$1" && pwd)
build_dir=$(cd "$2" && pwd)
mkdir -p "$3"
output_dir=$(cd "$3" && pwd)
patch_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
printf '%s  %s\n' 088246b519ef984b06b9278047ac894ede9fdd060e73264ee59aa7815bddd972 "$source_dir/dlls/wow64cpu/cpu.c" | sha256sum -c -
mkdir -p "$output_dir/source/dlls/wow64cpu"
cp "$source_dir/dlls/wow64cpu/cpu.c" "$output_dir/source/dlls/wow64cpu/cpu.c"
patch -d "$output_dir/source" -p1 < "$patch_dir/wow64cpu.patch"
cd "$build_dir"
x86_64-w64-mingw32-gcc -O2 -c -o "$output_dir/cpu.o" "$output_dir/source/dlls/wow64cpu/cpu.c" \
    -Iinclude -I"$source_dir/include" -I"$source_dir/include/msvcrt" \
    -D_MSVCR_VER=0 -D__WINESRC__ -D__WINE_PE_BUILD -fno-strict-aliasing -ffunction-sections -mcx16 -mcmodel=small
tools/winegcc/winegcc -o "$output_dir/wow64cpu.dll" --wine-objdir . -b x86_64-w64-mingw32 \
    -Wl,--wine-builtin -Wl,--no-insert-timestamp -Wl,--image-base,0x7a400000 -shared -nodefaultlibs \
    "$source_dir/dlls/wow64cpu/wow64cpu.spec" "$output_dir/cpu.o" \
    dlls/wow64/x86_64-windows/libwow64.a dlls/ntdll/x86_64-windows/libntdll.a dlls/winecrt0/x86_64-windows/libwinecrt0.a
x86_64-w64-mingw32-strip "$output_dir/wow64cpu.dll"
python3 - "$output_dir/wow64cpu.dll" <<'PY'
from pathlib import Path
import struct
import sys

path = Path(sys.argv[1])
data = bytearray(path.read_bytes())
pe = struct.unpack_from('<I', data, 0x3c)[0]
if data[pe:pe + 4] != b'PE\0\0' or struct.unpack_from('<H', data, pe + 4)[0] != 0x8664:
    raise SystemExit('Expected an x86-64 PE library')
struct.pack_into('<I', data, pe + 8, 0)
struct.pack_into('<I', data, pe + 88, 0)
path.write_bytes(data)
PY
sha256sum "$output_dir/wow64cpu.dll"
