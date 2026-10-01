#!/bin/sh
set -eu
base=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
exec /usr/bin/open "$base/Dungeon Runners Launcher.app" --args "$@"
