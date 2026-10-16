#!/system/bin/sh
child=
stop() {
    trap '' TERM INT HUP
    child=${child:-$!}
    if [ -n "$child" ]; then
        kill -QUIT "$child" 2>/dev/null || true
        wait "$child" 2>/dev/null || true
    fi
    exit 143
}
trap stop TERM INT HUP
exec 3<&0
(
    export LD_PRELOAD="$1" LD_LIBRARY_PATH="$2"
    shift 2
    exec "$@"
) <&3 3<&- &
child=$!
exec 3<&-
wait "$child"
result=$?
trap - TERM INT HUP
exit "$result"
