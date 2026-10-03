import argparse
import hashlib
import io
import json
from pathlib import Path
import zipfile


def verify(apk):
    with zipfile.ZipFile(apk) as archive:
        entries = archive.namelist()
        if len(entries) != len(set(entries)):
            raise ValueError('The installer contains duplicate entries.')
        manifest = json.loads(archive.read('assets/dungeon-runtime/files.json'))
        paths = set()
        for item in manifest:
            path = item['path']
            if path in paths or path.startswith('/') or '..' in path.split('/'):
                raise ValueError('Invalid runtime library path: ' + path)
            paths.add(path)
            content = archive.read('lib/armeabi-v7a/' + item['file'])
            if len(content) != item['size'] or hashlib.sha256(content).hexdigest() != item['sha256']:
                raise ValueError('Packaged runtime library differs: ' + path)
            if content[:5] != b'\x7fELF\x01' or content[18:20] != b'\x28\x00':
                raise ValueError('Expected an ARM runtime library: ' + path)
        for name in ('libproot.so', 'libproot-loader.so', 'libtalloc.so', 'libandroid-shmem.so', 'libXlorie.so'):
            if 'lib/armeabi-v7a/' + name not in entries:
                raise ValueError('Missing packaged runtime component: ' + name)
        fixture = archive.read('assets/dungeon-runtime/guest-memory')
        if fixture[:5] != b'\x7fELF\x01' or fixture[18:20] != b'\x03\x00':
            raise ValueError('The runtime compatibility check is missing.')
        for name in ('AardvarkInput.exe', 'AardvarkTouch.dll'):
            executable = archive.read('assets/dungeon-runtime/' + name)
            if executable[:2] != b'MZ':
                raise ValueError('The game input component is missing: ' + name)
            header = int.from_bytes(executable[60:64], 'little')
            if executable[header:header + 6] != b'PE\x00\x00\x4c\x01':
                raise ValueError('Expected a 32-bit game input component: ' + name)
        expected = json.loads(archive.read('assets/dungeon-runtime/data.json'))
        with archive.open('assets/dungeon-runtime/data.zip') as stream:
            checksum = hashlib.file_digest(stream, 'sha256').hexdigest()
        if checksum != expected['sha256']:
            raise ValueError('Packaged runtime data differs.')
        with zipfile.ZipFile(io.BytesIO(archive.read('assets/dungeon-runtime/data.zip'))) as data:
            for name in data.namelist():
                if name.startswith('opt/aardvark/directx/'):
                    raise ValueError('Microsoft requirements must be downloaded from their official source.')
        print(f'PASS: {len(manifest)} runtime libraries, runtime data and compatibility check.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('apk', type=Path)
    args = parser.parse_args()
    verify(args.apk)
