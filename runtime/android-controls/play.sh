#!/bin/bash
set -eu
export DISPLAY=:7 WINEPREFIX=/root/.wine
loader=b
if [ -f /root/game/d3d9.dll ] && [ -f /root/game/Addons/Runtime/Addons.dll ]; then loader=n,b; fi
export WINEDLLOVERRIDES="mscoree,mshtml=;winemenubuilder.exe=d;d3d9=$loader;d3dx9_31,d3dx9_40=n,b"
export WINEDEBUG=-all,err+all
export WINELOADER=/usr/local/bin/aardvark-wine WINESERVER=/usr/local/bin/aardvark-wineserver
trap '/usr/local/bin/aardvark-wineserver -k >/dev/null 2>&1 || true; /usr/local/bin/aardvark-wineserver -w >/dev/null 2>&1 || true' EXIT
cd /root/game
/usr/local/bin/aardvark-wine /runtime/scripts/AardvarkInput.exe
