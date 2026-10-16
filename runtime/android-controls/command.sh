#!/bin/bash
wine=${WINELOADER:-/usr/local/bin/aardvark-wine}
server=${WINESERVER:-/usr/local/bin/aardvark-wineserver}
trap '"$server" -k >/dev/null 2>&1 || true; "$server" -w >/dev/null 2>&1 || true' EXIT
"$wine" "$@"
result=$?
if [ "$result" -eq 0 ]; then
    "$server" -w
    result=$?
fi
exit "$result"
