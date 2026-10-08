import argparse
import hashlib
import io
import json
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile
import zlib

REVISION = '0e1ebb4c180f4e8e7a14a80f7cd0db8301791b6d'
NATIVE_APK_SHA256 = '97e1c5d471cbb6af84267d7d2d3c475f3862ad2cc2aecebfe98ed0097a0164c0'


def digest(data):
    return hashlib.sha256(data).hexdigest()


def prepare(source, game, native_apk):
    from PIL import Image

    here = Path(__file__).resolve().parent
    revision = subprocess.check_output(['git', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip()
    if revision != REVISION:
        raise ValueError('Unsupported display runtime source revision')
    patch = str(here / 'termux-x11.patch')
    check = subprocess.run(['git', '-C', str(source), 'apply', '--reverse', '--check', patch], capture_output=True)
    if check.returncode:
        subprocess.run(['git', '-C', str(source), 'apply', '--check', patch], check=True)
        subprocess.run(['git', '-C', str(source), 'apply', patch], check=True)
    for name in ('GameControls.java', 'GameMouse.java', 'GameRuntimeActivity.java', 'GameRuntimeService.java', 'NativeRuntime.java', 'RuntimeProfile.java', 'RuntimeDiagnostics.java', 'RuntimeStatus.java', 'RuntimeReportStore.java', 'RuntimeReportActivity.java', 'RuntimeReportProvider.java', 'WinePrefix.java', 'RuntimeSession.java', 'GameStartup.java', 'RuntimeCheckCache.java', 'GameDisplay.java', 'GraphicsChoice.java', 'GraphicsRecovery.java', 'ShaderCache.java'):
        shutil.copyfile(here / name, source / 'lorie/src/main/java/com/termux/x11' / name)
    shutil.copyfile(here / 'AndroidManifest.xml', source / 'lorie/src/main/AndroidManifest.xml')
    shutil.copyfile(here / 'library.gradle', source / 'dungeon-runtime.gradle')
    build = source / 'lorie/build.gradle'
    include = 'apply from: rootProject.file("dungeon-runtime.gradle")'
    if include not in build.read_text():
        with build.open('a', encoding='utf-8', newline='\n') as output:
            output.write('\n' + include + '\n')
    runtime_assets = source / 'lorie/src/main/assets/dungeon-runtime'
    runtime_assets.mkdir(parents=True, exist_ok=True)
    subprocess.run([sys.executable, str(here / 'input/build.py'), '--output', str(runtime_assets)], check=True)
    for name in ('play.sh', 'command.sh', 'supervise.sh', 'session.sh'):
        (runtime_assets / name).write_text((here / name).read_text(), encoding='utf-8', newline='\n')
    runtime_patch = str(here / 'runtime.patch')
    applied = subprocess.run(['git', '-C', str(source), 'apply', '--reverse', '--check', runtime_patch], capture_output=True)
    if applied.returncode:
        subprocess.run(['git', '-C', str(source), 'apply', '--check', runtime_patch], check=True)
        subprocess.run(['git', '-C', str(source), 'apply', runtime_patch], check=True)
    manifest = json.loads((here / 'icons.json').read_text())
    if game is not None and digest((game / 'game.pki').read_bytes()) != manifest['pkiSha256']:
        raise ValueError('Unsupported game package index')
    assets = source / 'lorie/src/main/assets/dungeon-controls'
    assets.mkdir(parents=True, exist_ok=True)
    if game is None:
        for entry in manifest['entries']:
            shutil.copyfile(here / 'icons' / (entry['output'] + '.png'), assets / (entry['output'] + '.png'))
    else:
      with (game / 'game.pkg').open('rb') as package:
        for entry in manifest['entries']:
            package.seek(entry['package_offset'])
            stored = package.read(entry['stored_size'])
            if digest(stored) != entry['storedSha256']:
                raise ValueError('Game icon package checksum mismatch: ' + entry['name'])
            decoded = zlib.decompress(stored) if entry['flags'] & 1 else stored
            if digest(decoded) != entry['decodedSha256'] or len(decoded) != entry['decoded_size']:
                raise ValueError('Game icon texture checksum mismatch: ' + entry['name'])
            icon = Image.open(io.BytesIO(decoded)).convert('RGBA')
            if list(icon.size) != entry['size']:
                raise ValueError('Unexpected game icon dimensions')
            icon.save(assets / (entry['output'] + '.png'))
    if native_apk:
        if digest(native_apk.read_bytes()) != NATIVE_APK_SHA256:
            raise ValueError('Native libraries require the matching upstream APK')
        with zipfile.ZipFile(native_apk) as archive:
            for item in archive.infolist():
                parts = Path(item.filename).parts
                if len(parts) == 3 and parts[0] == 'lib' and parts[2].endswith('.so') and '..' not in parts:
                    target = source / 'prebuilt-native' / parts[1] / parts[2]
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(archive.read(item))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--game', type=Path)
    parser.add_argument('--native-apk', type=Path)
    args = parser.parse_args()
    prepare(args.source.resolve(), args.game.resolve() if args.game else None, args.native_apk.resolve() if args.native_apk else None)
