#!/bin/bash
set -eu
wine=${WINELOADER:-/usr/local/bin/aardvark-wine}
server=${WINESERVER:-/usr/local/bin/aardvark-wineserver}
runtime=${AARDVARK_RUNTIME_DIR:-/runtime}
trap '"$server" -k >/dev/null 2>&1 || true; "$server" -w >/dev/null 2>&1 || true' EXIT
started=false
while IFS= read -r action; do
    result=0
    if [ "$action" != initialize ] && [ "$started" = false ]; then
        "$server" -p
        started=true
    fi
    case "$action" in
        initialize)
            if [ "$started" = true ]; then exit 64; fi
            "$wine" wineboot -i || result=$?
            if [ "$result" -eq 0 ]; then "$server" -w || result=$?; fi
            if [ "$result" -eq 0 ]; then
                "$server" -p || result=$?
                started=true
            fi ;;
        check)
            if [ "${1:-}" = legacy ]; then
                /usr/local/bin/box86 "$runtime/scripts/guest-memory" "$runtime/scripts/guest-code.bin" || result=$?
            else
                "$wine" "$runtime/scripts/AardvarkRuntimeCheck.exe" || result=$?
            fi ;;
        extract) "$wine" "$runtime/downloads/directx.exe" /Q '/T:C:\AardvarkRequirements' || result=$? ;;
        install) "$wine" 'C:\AardvarkRequirements\required\DXSETUP.exe' /silent || result=$? ;;
        graphics) "$wine" "$runtime/scripts/AardvarkGraphicsCheck.exe" || result=$? ;;
        play) /bin/bash "$runtime/scripts/play.sh" || result=$? ;;
        *) exit 64 ;;
    esac
    printf 'AARDVARK_SESSION_DONE %s %d\n' "$action" "$result"
    if [ "$result" -ne 0 ]; then exit "$result"; fi
    if [ "$action" = play ]; then exit 0; fi
done
