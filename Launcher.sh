#!/bin/sh
set -eu
base=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
case "$(uname -s)" in
    Darwin) platform=osx ;;
    Linux) platform=linux ;;
    *) printf '%s\n' 'Use DungeonRunnersLauncher.exe on Windows.' >&2; exit 1 ;;
esac
case "$(uname -m)" in
    arm64|aarch64) architecture=arm64 ;;
    x86_64|amd64) architecture=x64 ;;
    *) printf '%s\n' 'This package supports x64 and arm64.' >&2; exit 1 ;;
esac
launcher="$base/bin/$platform-$architecture/DungeonRunnersLauncher"
if [ ! -f "$launcher" ]; then
    printf '%s\n' 'The launcher application is incomplete. Download it again.' >&2
    exit 1
fi
exec "$launcher" "$@"
