#!/bin/bash
set -euo pipefail

source_root=$(realpath "${1:?source directory required}")
work=$(realpath -m "${2:?build directory required}")
recipe=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$work/source" "$work/wine-build"
printf '%s  %s\n' c07a6857933c1fc60dff5448d79f39c92481c1e9db5aa628db9d0358446e0701 "$source_root/wine-11.0.tar.xz" | sha256sum -c -
box64_digest=$(sha256sum "$source_root/box64-v0.4.4.tar.gz")
box64_digest=${box64_digest%% *}
[[ "$box64_digest" == 99c6de4f509e46ab1de15df740d0e0ea338a7790efa3f67510dfbb975cc24029 || "$box64_digest" == 8bd2236676f3805486fc420d40e74fccc2256fbf9c36569c4b842c17b00c6ce9 ]]
tar -xf "$source_root/wine-11.0.tar.xz" -C "$work/source"
tar -xf "$source_root/box64-v0.4.4.tar.gz" -C "$work/source"
patch -d "$work/source/wine-11.0" -p1 < "$recipe/wine-android-image-copy.patch"
patch -d "$work/source/wine-11.0" -p1 < "$recipe/wine-unix-path.patch"
cd "$work/wine-build"
../source/wine-11.0/configure --prefix=/opt/aardvark/wine --enable-archs=i386,x86_64 \
    --without-cups --without-dbus --without-gphoto --without-gstreamer --without-kerberos \
    --without-netapi --without-oss --without-pcap --without-sane --without-smartcard \
    --without-usb --without-v4l2 --without-wayland
make -j"${BUILD_JOBS:-3}"
make install DESTDIR="$work/wine-root"
cmake -S "$work/source/box64-0.4.4" -B "$work/box64-build" -DARM64=ON -DBAD_SIGNAL=ON -DNOGIT=ON \
    -DCMAKE_SYSTEM_NAME=Linux -DCMAKE_SYSTEM_PROCESSOR=aarch64 \
    -DCMAKE_C_COMPILER=aarch64-linux-gnu-gcc -DCMAKE_BUILD_TYPE=Release
cmake --build "$work/box64-build" --parallel "${BUILD_JOBS:-3}"
