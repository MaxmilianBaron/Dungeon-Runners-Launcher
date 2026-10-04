import argparse
from pathlib import Path
import shutil
import subprocess


def build(output, compiler=None):
    compiler = compiler or shutil.which('i686-w64-mingw32-gcc') or shutil.which('clang')
    if not compiler:
        raise RuntimeError('An i686 Windows C compiler is required.')
    here = Path(__file__).resolve().parent
    output.mkdir(parents=True, exist_ok=True)
    flags = [compiler, '-std=c11', '-O2', '-Wall', '-Wextra', '-Werror',
             '-Wno-unused-function', '-Wno-unused-parameter', '-fno-ident', '-static-libgcc',
             '-Wl,--no-insert-timestamp']
    if 'clang' in Path(compiler).name:
        flags += ['--target=i686-w64-windows-gnu']
    subprocess.run(flags + ['-shared', str(here / 'touch.c'), '-o', str(output / 'AardvarkTouch.dll'), '-luser32'], check=True)
    subprocess.run(flags + [str(here / 'host.c'), '-o', str(output / 'AardvarkInput.exe'), '-luser32', '-mwindows'], check=True)
    subprocess.run(flags + [str(here / 'runtime-check.c'), '-o', str(output / 'AardvarkRuntimeCheck.exe')], check=True)
    subprocess.run(flags + [str(here / 'graphics-check.c'), '-o', str(output / 'AardvarkGraphicsCheck.exe'), '-ld3d9', '-luser32'], check=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--compiler')
    args = parser.parse_args()
    build(args.output.resolve(), args.compiler)
