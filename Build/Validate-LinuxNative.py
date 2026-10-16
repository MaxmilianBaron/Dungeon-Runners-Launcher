import argparse
import gzip
import hashlib
import io
import stat
import struct
import tarfile
from pathlib import Path


EXECUTABLE = "usr/lib/dungeon-runners-launcher/DungeonRunnersLauncher"
DESKTOP = "usr/share/applications/dungeon-runners-launcher.desktop"
ICON = "usr/share/pixmaps/dungeon-runners-launcher.png"
COPYRIGHT = "usr/share/doc/dungeon-runners-launcher/copyright"


def require(value, message):
    if not value:
        raise ValueError(message)


def tar_members(data):
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:*") as archive:
        members = {}
        for entry in archive.getmembers():
            name = entry.name.removeprefix("./").lstrip("/").rstrip("/")
            require(".." not in name.split("/"), "unsafe package path")
            if not entry.isfile():
                require(entry.isdir(), "unsupported package file type")
                continue
            require(name not in members, "duplicate package file")
            with archive.extractfile(entry) as source:
                members[name] = (entry.mode, entry.uid, entry.gid, source.read())
        return members


def read_deb(path):
    data = path.read_bytes()
    require(data.startswith(b"!<arch>\n"), "wrong DEB signature")
    position = 8
    members = {}
    while position < len(data):
        header = data[position:position + 60]
        require(len(header) == 60 and header[58:] == b"`\n", "invalid ar entry")
        size = int(header[48:58])
        name = header[:16].decode().strip().rstrip("/")
        position += 60
        members[name] = data[position:position + size]
        position += size + size % 2
    require(members.get("debian-binary") == b"2.0\n", "wrong DEB format")
    control = tar_members(members["control.tar.gz"])
    fields = {}
    for line in control["control"][3].decode().splitlines():
        if line and not line.startswith(" ") and ":" in line:
            key, value = line.split(":", 1)
            fields[key] = value.strip()
    return fields, tar_members(members["data.tar.gz"])


def rpm_header(data, start):
    require(data[start:start + 4] == b"\x8e\xad\xe8\x01", "invalid RPM header")
    entries, length = struct.unpack_from(">II", data, start + 8)
    require(entries < 100000 and length < 10000000, "oversized RPM header")
    base = start + 16 + entries * 16
    require(base + length <= len(data), "truncated RPM header")
    values = {}
    for number in range(entries):
        tag, kind, offset, count = struct.unpack_from(">4I", data, start + 16 + number * 16)
        position = base + offset
        if kind in (6, 8, 9):
            strings = []
            for _ in range(count):
                end = data.index(0, position, base + length)
                strings.append(data[position:end].decode())
                position = end + 1
            values[tag] = strings[0] if kind == 6 else strings
        elif kind in (3, 4):
            code = "H" if kind == 3 else "I"
            values[tag] = list(struct.unpack_from(">" + str(count) + code, data, position))
    return values, base + length


def read_rpm(path):
    data = path.read_bytes()
    require(data[:4] == b"\xed\xab\xee\xdb", "wrong RPM signature")
    _, start = rpm_header(data, 96)
    fields, start = rpm_header(data, (start + 7) & ~7)
    require(fields[1125] == "gzip", "unsupported RPM payload compression")
    payload = gzip.decompress(data[start:])
    position = 0
    files = {}
    while position < len(payload):
        require(payload[position:position + 6] in (b"070701", b"070702"), "unsupported CPIO format")
        words = [int(payload[position + 6 + number * 8:position + 14 + number * 8], 16) for number in range(13)]
        mode, uid, gid, size, name_size = words[1], words[2], words[3], words[6], words[11]
        position += 110
        name = payload[position:position + name_size - 1].decode().removeprefix("./").lstrip("/")
        position = (position + name_size + 3) & ~3
        contents = payload[position:position + size]
        require(len(contents) == size and ".." not in name.split("/"), "invalid CPIO entry")
        position = (position + size + 3) & ~3
        if name == "TRAILER!!!":
            break
        if stat.S_ISREG(mode):
            require(name not in files, "duplicate RPM file")
            files[name] = (stat.S_IMODE(mode), uid, gid, contents)
        else:
            require(stat.S_ISDIR(mode), "unsupported RPM file type")
    return {"Package": fields[1000], "Version": fields[1001], "Architecture": fields[1022],
            "Depends": " ".join(fields.get(1049, []))}, files


def validate(path, binary, architecture):
    fields, files = read_deb(path) if path.suffix == ".deb" else read_rpm(path)
    expected_arch = {(".deb", "x64"): "amd64", (".rpm", "x64"): "x86_64",
                     (".deb", "arm64"): "arm64", (".rpm", "arm64"): "aarch64"}[(path.suffix, architecture)]
    require(fields["Package"] == "dungeon-runners-launcher" and fields["Architecture"] == expected_arch, "wrong package identity")
    for name in (EXECUTABLE, DESKTOP, ICON, COPYRIGHT):
        require(name in files, "missing installed file: " + name)
    for name, (mode, uid, gid, _) in files.items():
        require(name.startswith("usr/") and uid == gid == 0, "package writes private data or wrong ownership")
        require(mode & 0o022 == 0, "package file is writable by other users")
    mode, _, _, payload = files[EXECUTABLE]
    require(mode == 0o755, "launcher is not executable")
    require(hashlib.sha256(payload).digest() == hashlib.sha256(binary.read_bytes()).digest(), "launcher payload changed")
    require(payload[:6] == b"\x7fELF\x02\x01", "not a 64-bit Linux executable")
    require(struct.unpack_from("<H", payload, 18)[0] == (62 if architecture == "x64" else 183), "wrong payload architecture")
    desktop = files[DESKTOP][3].decode()
    require("Exec=/" + EXECUTABLE + "\n" in desktop and "Terminal=false\n" in desktop, "menu launches a shell or wrong application")
    require("Icon=dungeon-runners-launcher\n" in desktop, "menu icon missing")
    require(not any(name.endswith((".sh", ".run")) for name in files), "native installer contains a shell entry point")
    require(b"MIT License" in files[COPYRIGHT][3], "product license missing")
    require("libssl" in fields["Depends"] and ("libx11" in fields["Depends"] or "libX11" in fields["Depends"]), "runtime dependencies missing")
    require("wmctrl" in fields["Depends"] and ("libxrandr" in fields["Depends"] or "libXrandr" in fields["Depends"]), "monitor support dependencies missing")
    print("PASS", path.name, fields["Version"], expected_arch, "native executable, menu, icon, permissions, ownership, libraries and exact payload")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    for architecture, suffix in (("x64", ""), ("arm64", "-arm64")):
        for extension in ("deb", "rpm"):
            validate(args.directory / f"Dungeon-Runners-Launcher-Linux{suffix}.{extension}",
                     args.directory / f"DungeonRunnersLauncher-linux-{architecture}", architecture)


if __name__ == "__main__":
    main()
