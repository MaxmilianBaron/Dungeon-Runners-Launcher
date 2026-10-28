import argparse
import concurrent.futures
import hashlib
import io
import json
from pathlib import Path
import re
import subprocess
import tarfile
import zipfile


def verified(path, digest):
    data = path.read_bytes()
    if hashlib.sha256(data).hexdigest() != digest:
        raise ValueError("Source checksum mismatch: " + path.name)
    return data


def dependency(data, output):
    if data[:8] != b"!<arch>\n":
        raise ValueError("Invalid dependency archive")
    position = 8
    while position < len(data):
        header = data[position:position + 60]
        size = int(header[48:58])
        name = header[:16].decode().strip().rstrip("/")
        position += 60
        if name.startswith("data.tar"):
            with tarfile.open(fileobj=io.BytesIO(data[position:position + size])) as archive:
                for entry in archive:
                    if not entry.isfile():
                        continue
                    if "/include/" in entry.name:
                        relative = entry.name.split("/include/", 1)[1]
                        if ".." in Path(relative).parts:
                            raise ValueError("Invalid dependency header")
                        target = output / "include" / relative
                    elif Path(entry.name).name.startswith("libtalloc.so"):
                        target = output / "lib/libtalloc.so"
                    elif Path(entry.name).name == "libandroid-shmem.so":
                        target = output / "lib/libandroid-shmem.so"
                    else:
                        continue
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(archive.extractfile(entry).read())
            return
        position += size + size % 2
    raise ValueError("Dependency payload missing")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--ndk", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--packages", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    here = Path(__file__).resolve().parent
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    source = verified(args.source, "75f654fe60dea92dabff2bf083ae8bfe4f91baa6a1a374786a6bf391015eebaa")
    with zipfile.ZipFile(io.BytesIO(source)) as archive:
        archive.extractall(output)
    root = output / "proot-5.1.107.96"
    for patch in ["proot-wow64-abi.patch", "proot-sigsys.patch"]:
        subprocess.run(["git", "apply", str(here / patch)], cwd=root, check=True)
    metadata = json.loads((here / "dependencies/termux64.json").read_text())["x86_64"]
    for name in ["libtalloc", "libandroid-shmem"]:
        dependency(verified(args.packages / (name + "-x86_64.deb"), metadata[name]["sha256"]), output)
    (output / "build.h").write_text("#define HAVE_PROCESS_VM\n#define HAVE_SECCOMP_FILTER\n")
    toolchains = args.ndk / "toolchains/llvm/prebuilt"
    compiler = next(toolchains.glob("*/bin/clang.exe"), None) or next(toolchains.glob("*/bin/clang"))
    src = root / "src"
    flags = [str(compiler.resolve()), "--target=x86_64-linux-android28", "-O2", "-include", "string.h",
             "-ffile-prefix-map=" + str(output) + "=/build/proot",
             "-D_FILE_OFFSET_BITS=64", "-D_GNU_SOURCE", "-DARG_MAX=131072", '-DVERSION="5.1.107.96"',
             "-DWITH_LIBANDROID_SHMEM", '-DPROOT_UNBUNDLE_LOADER="/runtime/loader"',
             "-I" + str(src), "-I" + str(output), "-I" + str(output / "include")]
    makefile = (src / "GNUmakefile").read_text()
    objects = re.findall(r"([a-zA-Z0-9_/-]+)\.o", makefile.split("OBJECTS +=", 1)[1].split("define define_from_arch", 1)[0])

    def compile_one(name):
        target = output / (name.replace("/", "_") + ".o")
        subprocess.run(flags + ["-c", str(src / (name + ".c")), "-o", str(target)], check=True)
        return str(target)

    with concurrent.futures.ThreadPoolExecutor(4) as pool:
        compiled = list(pool.map(compile_one, objects))
    subprocess.run(flags + compiled + ["-L" + str(output / "lib"), "-ltalloc", "-landroid-shmem",
                   "-Wl,-z,noexecstack,-z,max-page-size=16384", "-o", str(output / "libproot.so")], check=True)
    print("Built " + str(output / "libproot.so"))


if __name__ == "__main__":
    main()
