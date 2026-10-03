import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time


script = Path(__file__).resolve().parents[1] / 'supervise.sh'
for status in (0, 53):
    result = subprocess.run(['sh', str(script), '', '', sys.executable, '-c',
        'import sys; print(sys.stdin.buffer.read().hex()); print(repr(sys.argv[1])); sys.exit(int(sys.argv[2]))',
        'path with spaces', str(status)], input=b'\x01\x02\x03\x00\xff', capture_output=True, timeout=5)
    assert result.returncode == status, result
    assert result.stdout.splitlines() == [b'01020300ff', b"'path with spaces'"], result.stdout

with tempfile.TemporaryDirectory() as directory:
    root = Path(directory)
    executable = root / 'native=fixture'
    executable.symlink_to(sys.executable)
    child = root / 'child.py'
    child.write_text('''import os, signal, subprocess, sys, time
from pathlib import Path
child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'])
def stop(signum, frame):
    child.kill()
    child.wait()
    sys.exit(0)
signal.signal(signal.SIGQUIT, stop)
Path(sys.argv[1]).write_text(str(os.getpid()) + ' ' + str(child.pid))
while True:
    time.sleep(1)
''')
    ready = root / 'ready'
    process = subprocess.Popen(['sh', str(script), '', '', str(executable), str(child), str(ready)])
    try:
        for _ in range(100):
            if ready.exists():
                break
            time.sleep(.02)
        assert ready.exists(), 'child did not start'
        pids = [int(value) for value in ready.read_text().split()]
        process.terminate()
        assert process.wait(timeout=5) == 143
        for pid in pids:
            try:
                os.kill(pid, 0)
            except ProcessLookupError:
                continue
            raise AssertionError('runtime process survived cancellation')
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
        if ready.exists():
            for pid in map(int, ready.read_text().split()):
                try:
                    os.kill(pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
print('PASS: exit codes, binary input, quoted arguments and cancellation of runtime children')
