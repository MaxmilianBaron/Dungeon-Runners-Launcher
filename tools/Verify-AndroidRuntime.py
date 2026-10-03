import argparse
import hashlib
import io
import json
from pathlib import Path
import struct
import zipfile


def verify(apk):
    with zipfile.ZipFile(apk) as archive:
        entries = archive.namelist()
        if len(entries) != len(set(entries)):
            raise ValueError('The installer contains duplicate entries.')
        count = 0
        for abi, folder, elf_class, machine in [('armeabi-v7a', '', 1, 40), ('arm64-v8a', 'arm64-v8a/', 2, 183), ('x86_64', 'x86_64/', 2, 62)]:
            manifest = json.loads(archive.read('assets/dungeon-runtime/' + folder + 'files.json'))
            expected_native = {'lib/' + abi + '/' + item['file'] for item in manifest}
            actual_native = {name for name in entries if name.startswith('lib/' + abi + '/libguest-')}
            if expected_native != actual_native:
                raise ValueError('Packaged runtime library inventory differs: ' + abi)
            paths = set()
            for item in manifest:
                path = item['path']
                if path in paths or path.startswith('/') or '..' in path.split('/') or '\\' in path or ':' in path:
                    raise ValueError('Invalid runtime library path: ' + path)
                paths.add(path)
                content = archive.read('lib/' + abi + '/' + item['file'])
                if len(content) != item['size'] or hashlib.sha256(content).hexdigest() != item['sha256']:
                    raise ValueError('Packaged runtime library differs: ' + path)
                if content[:4] != b'\x7fELF' or content[4] != elf_class or int.from_bytes(content[18:20], 'little') != machine:
                    raise ValueError('Wrong runtime library architecture: ' + path)
                if abi == 'arm64-v8a':
                    offset = struct.unpack_from('<Q', content, 32)[0]
                    size, segments = struct.unpack_from('<HH', content, 54)
                    for segment in range(segments):
                        start = offset + segment * size
                        if struct.unpack_from('<I', content, start)[0] == 1 and struct.unpack_from('<Q', content, start + 48)[0] < 16384:
                            raise ValueError('ARM64 runtime requires 16 KiB segment alignment: ' + path)
            for name in ('libproot.so', 'libproot-loader.so', 'libtalloc.so', 'libandroid-shmem.so', 'libXlorie.so'):
                if 'lib/' + abi + '/' + name not in entries:
                    raise ValueError('Missing packaged runtime component: ' + abi + '/' + name)
            count += len(manifest)
        fixture = archive.read('assets/dungeon-runtime/guest-memory')
        if fixture[:5] != b'\x7fELF\x01' or fixture[18:20] != b'\x03\x00':
            raise ValueError('The runtime compatibility check is missing.')
        for name in ('AardvarkInput.exe', 'AardvarkTouch.dll', 'AardvarkRuntimeCheck.exe'):
            executable = archive.read('assets/dungeon-runtime/' + name)
            if executable[:2] != b'MZ':
                raise ValueError('The game input component is missing: ' + name)
            header = int.from_bytes(executable[60:64], 'little')
            if executable[header:header + 6] != b'PE\x00\x00\x4c\x01':
                raise ValueError('Expected a 32-bit game input component: ' + name)
        for folder in ('', 'wow64/', 'arm64-v8a/', 'x86_64/'):
            prefix = 'assets/dungeon-runtime/' + folder
            expected = json.loads(archive.read(prefix + 'data.json'))
            with archive.open(prefix + 'data.zip') as stream:
                checksum = hashlib.file_digest(stream, 'sha256').hexdigest()
            if checksum != expected['sha256']:
                raise ValueError('Packaged runtime data differs: ' + folder)
            with zipfile.ZipFile(io.BytesIO(archive.read(prefix + 'data.zip'))) as data:
                names = data.namelist()
                if len(names) != len(set(names)) or sum(item.file_size for item in data.infolist()) != expected['unpackedSize']:
                    raise ValueError('Runtime archive inventory differs: ' + folder)
                for name in names:
                    if name.startswith('/') or '..' in name.split('/') or '\\' in name or ':' in name:
                        raise ValueError('Invalid runtime data path: ' + name)
                    if name.startswith('opt/aardvark/directx/'):
                        raise ValueError('Microsoft requirements must be downloaded from their official source.')
        print(f'PASS: {count} runtime libraries across three profiles, runtime data and compatibility checks.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('apk', type=Path)
    args = parser.parse_args()
    verify(args.apk)
