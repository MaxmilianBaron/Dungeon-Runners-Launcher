#!/bin/sh
set -eu
base=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
exec /bin/sh "$base/Launcher.sh" "$@"
