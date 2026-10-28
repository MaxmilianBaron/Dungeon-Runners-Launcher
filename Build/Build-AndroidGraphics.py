import argparse
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import platform
import shutil
import subprocess
import sys
import tarfile
import urllib.request


def download(entry, path):
    if path.is_file() and hashlib.sha256(path.read_bytes()).hexdigest() == entry['sha256']:
        return
    request = urllib.request.Request(entry['url'], headers={'User-Agent': 'Dungeon-Runners-Launcher-Build'})
    with urllib.request.urlopen(request, timeout=90) as stream:
        data = stream.read(32 * 1024 * 1024 + 1)
    if len(data) > 32 * 1024 * 1024 or hashlib.sha256(data).hexdigest() != entry['sha256']:
        raise ValueError('Graphics source checksum differs.')
    path.write_bytes(data)


def apply(source, patch, record):
    if record.is_file() and record.read_bytes() != patch.read_bytes():
        subprocess.run(['git', '-C', str(source), 'apply', '--reverse', '--check', str(record)], check=True)
        subprocess.run(['git', '-C', str(source), 'apply', '--reverse', str(record)], check=True)
    if subprocess.run(['git', '-C', str(source), 'apply', '--reverse', '--check', str(patch)], capture_output=True).returncode:
        subprocess.run(['git', '-C', str(source), 'apply', '--check', str(patch)], check=True)
        subprocess.run(['git', '-C', str(source), 'apply', str(patch)], check=True)
    record.write_bytes(patch.read_bytes())


def deb_data(path):
    data = path.read_bytes()
    if data[:8] != b'!<arch>\n':
        raise ValueError('Invalid graphics package.')
    offset = 8
    while offset + 60 <= len(data):
        header = data[offset:offset + 60]
        size = int(header[48:58])
        if size < 0 or offset + 60 + size > len(data):
            raise ValueError('Incomplete graphics package.')
        name = header[:16].decode().strip().rstrip('/')
        if name.startswith('data.tar.'):
            return data[offset + 60:offset + 60 + size]
        offset += 60 + size + size % 2
    raise ValueError('Graphics package data is missing.')


def build(cache, native, sdk):
    root = Path(__file__).resolve().parent.parent
    files = root / 'runtime/android-controls/gpu'
    manifest = json.loads((files / 'sources.json').read_text())
    cache.mkdir(parents=True, exist_ok=True)
    host = {'Windows': 'windows-x86_64', 'Linux': 'linux-x86_64', 'Darwin': 'darwin-x86_64'}[platform.system()]
    ndk = sdk / 'ndk/28.2.13676358/toolchains/llvm/prebuilt' / host
    suffix = '.exe' if os.name == 'nt' else ''
    if not (ndk / ('bin/clang' + suffix)).is_file():
        raise ValueError('Install Android NDK 28.2.13676358 before building the graphics runtime.')
    source = cache / 'virglrenderer'
    if not (source / '.git').exists():
        subprocess.run(['git', 'init', str(source)], check=True)
        subprocess.run(['git', '-C', str(source), 'fetch', '--depth=1', manifest['virgl']['repository'], manifest['virgl']['revision']], check=True)
        subprocess.run(['git', '-C', str(source), 'checkout', '--detach', 'FETCH_HEAD'], check=True)
    if subprocess.check_output(['git', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip() != manifest['virgl']['revision']:
        raise ValueError('Unsupported graphics source revision.')
    apply(source, files / 'virgl.patch', cache / 'virgl-applied.patch')
    epoxy = source / 'subprojects/epoxy'
    if not epoxy.is_dir():
        archive = cache / 'epoxy.tar.gz'
        download(manifest['epoxy'], archive)
        with tarfile.open(archive) as package:
            for entry in package:
                path = PurePosixPath(entry.name)
                if path.is_absolute() or '..' in path.parts or '\\' in entry.name or ':' in entry.name:
                    raise ValueError('Invalid graphics source path.')
                if len(path.parts) < 2 or not entry.isfile():
                    continue
                target = epoxy.joinpath(*path.parts[1:])
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(package.extractfile(entry).read())
    apply(epoxy, files / 'epoxy.patch', cache / 'epoxy-applied.patch')
    variants = [('x86_64', 'x86_64', 'x86_64-linux-android28'), ('arm64-v8a', 'aarch64', 'aarch64-linux-android28'), ('armeabi-v7a', 'arm', 'armv7a-linux-androideabi28')]
    for abi, cpu, triple in variants:
        cross = cache / (abi + '.ini')
        cross.write_text(f"""[binaries]
c = ['{ndk.as_posix()}/bin/clang{suffix}', '--target={triple}']
ar = '{ndk.as_posix()}/bin/llvm-ar{suffix}'
strip = '{ndk.as_posix()}/bin/llvm-strip{suffix}'
[host_machine]
system = 'android'
cpu_family = '{cpu}'
cpu = '{cpu}'
endian = 'little'
[properties]
needs_exe_wrapper = true
[built-in options]
c_args = ['-fPIC']
c_link_args = ['-Wl,-z,max-page-size=16384']
""", newline='\n')
        output = cache / ('build-' + abi)
        subprocess.run([sys.executable, '-m', 'mesonbuild.mesonmain', 'setup', str(output), str(source), '--cross-file', str(cross), '--buildtype=release', '-Ddefault_library=static', '-Dplatforms=egl', '-Dvenus=false', '-Ddrm-renderers=[]', '-Dtests=false'], check=True)
        subprocess.run(['ninja', '-C', str(output)], check=True)
        destination = native / abi
        destination.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(output / 'vtest/virgl_test_server', destination / 'libaardvark-gpu.so')
        subprocess.run([str(ndk / ('bin/llvm-strip' + suffix)), '--strip-unneeded', str(destination / 'libaardvark-gpu.so')], check=True)
        archive = cache / ('angle-' + abi + '.deb')
        download(manifest['angle'][abi], archive)
        with tarfile.open(fileobj=io.BytesIO(deb_data(archive))) as package:
            for name in ['libEGL_angle.so', 'libGLESv2_angle.so']:
                entry = next(item for item in package if item.name.endswith('/opt/angle-android/vulkan/' + name))
                (destination / name).write_bytes(package.extractfile(entry).read())


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--cache', type=Path, required=True)
    parser.add_argument('--native', type=Path, required=True)
    parser.add_argument('--sdk', type=Path, default=Path(os.environ.get('ANDROID_HOME', os.environ.get('ANDROID_SDK_ROOT', 'C:/Android'))))
    args = parser.parse_args()
    build(args.cache.resolve(), args.native.resolve(), args.sdk.resolve())
