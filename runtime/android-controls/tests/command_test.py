import os
from pathlib import Path
import subprocess
import tempfile


script = Path(__file__).resolve().parents[1] / 'command.sh'
with tempfile.TemporaryDirectory() as directory:
    root = Path(directory)
    wine = root / 'wine'
    server = root / 'wineserver'
    wine.write_text('''#!/bin/bash
printf '%s\\n' "$@" > "$FIXTURE/arguments"
if [ "$MODE" = failure ]; then exit 23; fi
(sleep 0.2; touch "$FIXTURE/saved") &
echo $! > "$FIXTURE/pid"
''')
    server.write_text('''#!/bin/bash
printf '%s\\n' "$1" >> "$FIXTURE/events"
if [ "$1" = -k ]; then
    if [ -f "$FIXTURE/pid" ] && [ ! -f "$FIXTURE/saved" ]; then
        touch "$FIXTURE/interrupted"
        kill "$(cat "$FIXTURE/pid")" 2>/dev/null || true
    fi
elif [ "$MODE" != failure ]; then
    for i in {1..100}; do
        [ -f "$FIXTURE/saved" ] && break
        sleep 0.01
    done
    [ -f "$FIXTURE/saved" ] || exit 19
    [ "$MODE" != wait_failure ] || exit 31
fi
''')
    wine.chmod(0o700)
    server.chmod(0o700)
    for mode, status in [('success', 0), ('failure', 23), ('wait_failure', 31)]:
        for name in ('saved', 'pid', 'events', 'interrupted'):
            (root / name).unlink(missing_ok=True)
        environment = dict(os.environ, WINELOADER=str(wine), WINESERVER=str(server), FIXTURE=str(root), MODE=mode)
        result = subprocess.run(['bash', str(script), 'path with spaces', '/silent'], env=environment, timeout=5)
        assert result.returncode == status, (mode, result.returncode)
        assert (root / 'arguments').read_text().splitlines() == ['path with spaces', '/silent']
        assert not (root / 'interrupted').exists(), mode
        events = (root / 'events').read_text().splitlines()
        assert events == (['-k', '-w'] if mode == 'failure' else ['-w', '-k', '-w']), (mode, events)
print('PASS: asynchronous prefix save, setup and wait failures, cleanup and quoted arguments')
