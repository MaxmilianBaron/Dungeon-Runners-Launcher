#!/bin/sh
set -eu
installer=$(realpath "${1:-artifacts/Dungeon-Runners-Launcher-Linux.run}")
directory=$(dirname "$installer")
work=$(mktemp -d)
cleanup() {
    rm -f -- "$work/bin/uname" "$work/bin/getconf" "$work/damaged.run" "$work/truncated.run" "$work/error.txt"
    rmdir -- "$work/bin" "$work/scratch" "$work"
}
mkdir "$work/bin" "$work/scratch"
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
cat > "$work/bin/uname" <<'SH'
#!/bin/sh
case "$1" in
    -s) printf '%s\n' "${DR_TEST_KERNEL:-Linux}" ;;
    -m) printf '%s\n' "${DR_TEST_ARCH:-x86_64}" ;;
    *) exit 1 ;;
esac
SH
cat > "$work/bin/getconf" <<'SH'
#!/bin/sh
[ "$1" = LONG_BIT ] || exit 1
printf '%s\n' "${DR_TEST_BITS:-64}"
SH
chmod 700 "$work/bin/uname" "$work/bin/getconf"
export PATH="$work/bin:$PATH" TMPDIR="$work/scratch"
for pair in x86_64:x64 amd64:x64 aarch64:arm64 arm64:arm64; do
    DR_TEST_ARCH=${pair%:*}; export DR_TEST_ARCH
    [ "$(sh "$installer" --detect)" = "${pair#*:}" ]
done
for architecture in x64 arm64; do
    if [ "$architecture" = x64 ]; then
        DR_TEST_ARCH=x86_64
        name=Dungeon-Runners-Launcher-Linux.AppImage
    else
        DR_TEST_ARCH=aarch64
        name=Dungeon-Runners-Launcher-Linux-arm64.AppImage
    fi
    export DR_TEST_ARCH
    expected=$(sha256sum "$directory/$name"); expected=${expected%% *}
    [ "$(sh "$installer" --verify)" = "$expected  $name" ]
done
if DR_TEST_ARCH=armv7l sh "$installer" --detect > /dev/null 2>&1; then exit 1; fi
if DR_TEST_BITS=32 sh "$installer" --detect > /dev/null 2>&1; then exit 1; fi
if DR_TEST_KERNEL=Darwin sh "$installer" --detect > /dev/null 2>&1; then exit 1; fi
lines=$(awk '/^__DUNGEON_RUNNERS_PAYLOAD__$/ { print NR; exit }' "$installer")
head -n "$lines" "$installer" | sed -E 's/[a-f0-9]{64}/0000000000000000000000000000000000000000000000000000000000000000/g' > "$work/damaged.run"
tail -n +"$((lines + 1))" "$installer" >> "$work/damaged.run"
if sh "$work/damaged.run" --verify > "$work/error.txt" 2>&1; then exit 1; fi
grep -q 'checksum mismatch' "$work/error.txt"
bytes=$(wc -c < "$installer")
head -c "$((bytes - 4096))" "$installer" > "$work/truncated.run"
if sh "$work/truncated.run" --verify > "$work/error.txt" 2>&1; then exit 1; fi
[ -z "$(find "$work/scratch" -mindepth 1 -print -quit)" ]
printf '%s\n' 'PASS Linux architecture selection, both payload hashes, platform rejection, corrupt/truncated installer and cleanup.'
