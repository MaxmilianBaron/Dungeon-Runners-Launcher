import os
from pathlib import Path
import subprocess
import tempfile


script = Path(__file__).resolve().parents[1] / 'session.sh'
with tempfile.TemporaryDirectory() as directory:
    root = Path(directory) / 'runtime with spaces'
    (root / 'scripts').mkdir(parents=True)
    wine = root / 'wine'
    server = root / 'wineserver'
    wine.write_text('''#!/bin/bash
printf 'wine %s\\n' "$*" >> "$FIXTURE/events"
if [ "$MODE" = failure ] && [[ "$1" = *GraphicsCheck.exe ]]; then exit 53; fi
''')
    server.write_text('''#!/bin/bash
printf 'server %s\\n' "$*" >> "$FIXTURE/events"
''')
    (root / 'scripts/play.sh').write_text('''#!/bin/bash
printf 'play\\n' >> "$FIXTURE/events"
python3 -c 'import os; print("INPUT " + os.read(0, 5).hex())'
''')
    wine.chmod(0o700)
    server.chmod(0o700)
    environment = dict(os.environ, WINELOADER=str(wine), WINESERVER=str(server),
                       AARDVARK_RUNTIME_DIR=str(root), FIXTURE=str(root), MODE='normal')
    commands = b'initialize\ncheck\nextract\ninstall\ngraphics\nplay\n\x01\x02\x03\x00\xff'
    result = subprocess.run(['bash', str(script), 'modern'], input=commands, env=environment, capture_output=True, timeout=5)
    assert result.returncode == 0, result
    lines = result.stdout.decode().splitlines()
    assert lines == ['AARDVARK_SESSION_DONE initialize 0', 'AARDVARK_SESSION_DONE check 0',
                     'AARDVARK_SESSION_DONE extract 0', 'AARDVARK_SESSION_DONE install 0',
                     'AARDVARK_SESSION_DONE graphics 0', 'INPUT 01020300ff', 'AARDVARK_SESSION_DONE play 0'], lines
    events = (root / 'events').read_text().splitlines()
    assert [line for line in events if line.startswith('server')] == [
        'server -w', 'server -p', 'server -k', 'server -w'], events
    assert events[0] == 'wine wineboot -i', events
    assert 'wine C:\\AardvarkRequirements\\required\\DXSETUP.exe /silent' in events
    assert f'wine {root}/downloads/directx.exe /Q /T:C:\\AardvarkRequirements' in events
    (root / 'events').unlink()
    result = subprocess.run(['bash', str(script)], input=b'graphics\nplay\n',
                            env=dict(environment, MODE='failure'), capture_output=True, timeout=5)
    assert result.returncode == 53 and result.stdout == b'AARDVARK_SESSION_DONE graphics 53\n', result
    assert 'play' not in (root / 'events').read_text().splitlines()
    (root / 'events').unlink()
    result = subprocess.run(['bash', str(script)], input=b'unknown; echo unsafe\n', env=environment, capture_output=True, timeout=5)
    assert result.returncode == 64 and result.stdout == b'', result
    assert (root / 'events').read_text().splitlines() == ['server -p', 'server -k', 'server -w']
    (root / 'events').unlink()
    result = subprocess.run(['bash', str(script)], input=b'check\ninitialize\n', env=environment, capture_output=True, timeout=5)
    assert result.returncode == 64 and result.stdout == b'AARDVARK_SESSION_DONE check 0\n', result
    assert 'wine wineboot -i' not in (root / 'events').read_text().splitlines()
print('PASS: persistent runtime, initialization flush, ordered setup, binary controls, failure cleanup and command validation')
