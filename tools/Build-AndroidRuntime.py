import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import urllib.request
import zipfile


def download(package, destination):
    if destination.is_file() and destination.stat().st_size == package['size']:
        with destination.open('rb') as stream:
            if hashlib.file_digest(stream, 'sha256').hexdigest() == package['sha256']:
                return
    destination.parent.mkdir(parents=True, exist_ok=True)
    pending = destination.with_suffix('.pending')
    checksum = hashlib.sha256()
    size = 0
    try:
        request = urllib.request.Request(package['url'], headers={'User-Agent': 'Dungeon-Runners-Launcher-Build'})
        with urllib.request.urlopen(request, timeout=90) as response, pending.open('wb') as output:
            while block := response.read(1024 * 1024):
                size += len(block)
                if size > package['size']:
                    raise ValueError('Runtime download is too large.')
                checksum.update(block)
                output.write(block)
        if size != package['size'] or checksum.hexdigest() != package['sha256']:
            raise ValueError('Runtime download checksum differs.')
        pending.replace(destination)
    finally:
        pending.unlink(missing_ok=True)


def unpack(archive, destination, manifest):
    expected = manifest['files']
    with zipfile.ZipFile(archive) as package:
        names = package.namelist()
        if len(names) != len(set(names)) or set(names) != set(expected):
            raise ValueError('Runtime archive inventory differs.')
        for name in names:
            path = PurePosixPath(name)
            if path.is_absolute() or '..' in path.parts or '\\' in name or ':' in name:
                raise ValueError('Invalid runtime archive path.')
            info = package.getinfo(name)
            item = expected[name]
            if info.file_size != item['size'] or info.file_size > 400 * 1024 * 1024:
                raise ValueError('Runtime entry is too large.')
            target = destination.joinpath(*path.parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            pending = target.with_name(target.name + '.pending')
            checksum = hashlib.sha256()
            try:
                with package.open(info) as source, pending.open('wb') as output:
                    while block := source.read(1024 * 1024):
                        checksum.update(block)
                        output.write(block)
                if checksum.hexdigest() != item['sha256']:
                    raise ValueError('Runtime entry checksum differs: ' + name)
                pending.replace(target)
            finally:
                pending.unlink(missing_ok=True)
    root = destination.resolve()
    stale = [path for path in destination.rglob('*') if path.is_file() and path.relative_to(destination).as_posix() not in expected]
    if any(not path.resolve().is_relative_to(root) for path in stale):
        raise ValueError('Runtime cache contains an external link.')
    for path in stale:
        path.unlink()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--cache', type=Path, default=Path('artifacts/android-runtime/build'))
    parser.add_argument('--source', type=Path)
    parser.add_argument('--library', type=Path, default=Path('artifacts/android-runtime/library'))
    parser.add_argument('--payload', type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    runtime = root / 'runtime/android-controls'
    manifest = json.loads((runtime / 'payload.json').read_text())
    cache = args.cache.resolve()
    cache.mkdir(parents=True, exist_ok=True)
    archive = args.payload.resolve() if args.payload else cache / 'runtime.zip'
    if args.payload:
        with archive.open('rb') as stream:
            if archive.stat().st_size != manifest['package']['size'] or hashlib.file_digest(stream, 'sha256').hexdigest() != manifest['package']['sha256']:
                raise ValueError('Local runtime payload checksum differs.')
    else:
        download(manifest['package'], archive)
    unpack(archive, cache / 'payload', manifest)
    source = args.source.resolve() if args.source else cache / 'display'
    revision = manifest['displayRevision']
    if not (source / '.git').is_dir():
        if source.exists() and any(source.iterdir()):
            raise ValueError('Display source directory is not empty.')
        subprocess.run(['git', 'init', str(source)], check=True)
        subprocess.run(['git', '-C', str(source), 'remote', 'add', 'origin', 'https://github.com/termux/termux-x11.git'], check=True)
        subprocess.run(['git', '-C', str(source), 'fetch', '--depth=1', 'origin', revision], check=True)
        subprocess.run(['git', '-C', str(source), 'checkout', '--detach', revision], check=True)
    subprocess.run([sys.executable, str(runtime / 'prepare.py'), '--source', str(source)], check=True)
    assets = source / 'lorie/src/main/assets/dungeon-runtime'
    shutil.copytree(cache / 'payload/assets', assets, dirs_exist_ok=True)
    native = cache / 'payload/native'
    args.library.mkdir(parents=True, exist_ok=True)
    gradle = source / ('gradlew.bat' if os.name == 'nt' else 'gradlew')
    if os.name != 'nt':
        gradle.chmod(gradle.stat().st_mode | 0o100)
    subprocess.run([str(gradle), ':lorie:exportRuntime', '-PprebuiltNativeDir=' + native.as_posix(),
                    '-PruntimePackage=com.aardvarkland.dungeonrunners',
                    '-PruntimeOutput=' + args.library.resolve().as_posix(), '--no-daemon'], cwd=source, check=True)


if __name__ == '__main__':
    main()
