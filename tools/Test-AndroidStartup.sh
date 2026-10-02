#!/usr/bin/env bash
set -euo pipefail
shopt -s nullglob
packages=(src/Client.Android/bin/Release/net10.0-android36.0/*-Signed.apk)
[[ ${#packages[@]} -eq 1 ]]
adb install -r "${packages[0]}"
adb logcat -c
for launch in 1 2; do
    adb shell am force-stop com.aardvarkland.dungeonrunners
    adb shell am start -W -n com.aardvarkland.dungeonrunners/.MainActivity
    ready=false
    for attempt in $(seq 1 12); do
        if adb shell uiautomator dump /sdcard/launcher-smoke.xml; then
            adb exec-out cat /sdcard/launcher-smoke.xml > launcher-smoke.xml
            if grep -q 'package="com.aardvarkland.dungeonrunners"' launcher-smoke.xml && grep -q 'text="Install"' launcher-smoke.xml; then
                ready=true
                break
            fi
        fi
        sleep 2
    done
    [[ "$ready" == true ]]
    adb logcat -d -s AndroidRuntime:E > launcher-smoke-crash.txt
    if grep -q 'Process: com.aardvarkland.dungeonrunners' launcher-smoke-crash.txt; then
        cat launcher-smoke-crash.txt
        exit 1
    fi
done
rm -f launcher-smoke.xml launcher-smoke-crash.txt
