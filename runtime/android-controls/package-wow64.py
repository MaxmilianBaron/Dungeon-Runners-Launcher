import hashlib,io,json,pathlib,posixpath,shutil,subprocess,zipfile
from elftools.elf.elffile import ELFFile
import argparse,urllib.request
parser=argparse.ArgumentParser()
parser.add_argument('--work',type=pathlib.Path,required=True)
parser.add_argument('--output',type=pathlib.Path,required=True)
args=parser.parse_args()
base=args.work.resolve()
output=args.output.resolve()
output.mkdir(parents=True,exist_ok=True)
metadata=pathlib.Path(__file__).resolve().parent/'dependencies'
seed=json.loads((metadata/'guest-files.json').read_text())
hosts=json.loads((metadata/'ubuntu64.json').read_text())
for architecture,packages in hosts.items():
    host=base/('host-'+architecture)
    downloads=host/'packages'
    downloads.mkdir(parents=True,exist_ok=True)
    root=host/'root'
    root.mkdir(exist_ok=True)
    for info in packages.values():
        target=downloads/pathlib.PurePosixPath(info['url']).name
        def valid():
            return target.is_file() and target.stat().st_size==info['size'] and hashlib.file_digest(target.open('rb'),'sha256').hexdigest()==info['sha256']
        if not valid():
            pending=target.with_suffix('.pending')
            try:
                with urllib.request.urlopen(info['url'],timeout=90) as source,pending.open('wb') as stream:
                    total=0
                    while chunk:=source.read(1024*1024):
                        total+=len(chunk)
                        if total>info['size']:raise ValueError('Package too large')
                        stream.write(chunk)
                pending.replace(target)
                if not valid():raise ValueError('Package checksum mismatch')
            finally:pending.unlink(missing_ok=True)
        subprocess.run(['dpkg-deb','-x',str(target),str(root)],check=True)
    (host/'packages.json').write_text(json.dumps(packages,indent=2))
wine=base/'wine-root'
config=(base/'wine-build/include/config.h').read_text()
import re
extra=re.findall(r'^#define SONAME_\w+ "([^"]+)"',config,re.M)
def content(root,path):
    parts=path.split('/');resolved=[];hops=0
    while parts:
        resolved.append(parts.pop(0));p=root.joinpath(*resolved)
        if p.is_symlink():
            hops+=1
            if hops>32:raise ValueError('Link cycle '+path)
            target=str(p.readlink())
            target=target.lstrip('/') if target.startswith('/') else posixpath.join('/'.join(resolved[:-1]),target)
            normalized=posixpath.normpath(target)
            if normalized.startswith('../'):raise ValueError('Link escaped '+path)
            parts=normalized.split('/')+parts;resolved=[]
    return root.joinpath(*resolved).read_bytes()
def datazip(folder,values):
    folder.mkdir(parents=True,exist_ok=True)
    archive=folder/'data.zip'
    with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
        for name,value in sorted(values.items()):
            info=zipfile.ZipInfo(name,(2026,10,3,0,0,0));info.compress_type=zipfile.ZIP_DEFLATED;info.external_attr=0o100644<<16
            z.writestr(info,value)
    (folder/'data.json').write_text(json.dumps({'sha256':hashlib.file_digest(archive.open('rb'),'sha256').hexdigest(),'unpackedSize':sum(map(len,values.values())),'fileCount':len(values)},indent=2))
winefiles={};wineelf={}
for path in sorted(wine.rglob('*')):
    if not path.is_file():continue
    name=path.relative_to(wine).as_posix()
    if '/include/' in name or name.startswith('opt/aardvark/wine/deps/') or name.endswith(('.a','.man','.desktop','.pc')):continue
    data=content(wine,name)
    if data.startswith(b'MZ') or data.startswith(b'\x7fELF'):
        temp=base/'strip-stage';temp.write_bytes(data)
        if data.startswith(b'MZ'):
            header=int.from_bytes(data[60:64],'little');machine=int.from_bytes(data[header+4:header+6],'little')
            tool=('i686-w64-mingw32-strip' if machine==0x14c else 'x86_64-w64-mingw32-strip') if data[header:header+4]==b'PE\x00\x00' and machine in [0x14c,0x8664] else None
        else:tool='strip'
        if tool:
            subprocess.run([tool,'--strip-debug',str(temp)],check=True)
            data=temp.read_bytes()
    winefiles[name]=data
    if data.startswith(b'\x7fELF'):wineelf[name]=data
winefiles['opt/aardvark/licenses/Wine.txt']=(base/'source/wine-11.0/COPYING.LIB').read_bytes()
fallback=base/'host-amd64/root'
queue=list(wineelf.values());seen=set()
while queue:
    elf=ELFFile(io.BytesIO(queue.pop()))
    dynamic=elf.get_section_by_name('.dynamic')
    if not dynamic:continue
    for tag in dynamic.iter_tags():
        if tag.entry.d_tag!='DT_NEEDED' or tag.needed in seen:continue
        seen.add(tag.needed)
        if any(pathlib.PurePosixPath(name).name==tag.needed for name in wineelf):continue
        dependency=fallback/'usr/lib/x86_64-linux-gnu'/tag.needed
        if not dependency.is_file():
            choices=[p for p in fallback.rglob(tag.needed) if p.is_file()]
            if len(choices)!=1:raise ValueError('Missing WoW64 dependency '+tag.needed)
            dependency=choices[0]
        value=content(fallback,dependency.relative_to(fallback).as_posix())
        if ELFFile(io.BytesIO(value))['e_machine']!='EM_X86_64':raise ValueError('Wrong WoW64 library '+tag.needed)
        winefiles['opt/aardvark/wine/deps/'+tag.needed]=value
        queue.append(value)
datazip(output/'assets/wow64',winefiles)
for arch,abi,triplet,loader,machine in [('amd64','x86_64','x86_64-linux-gnu','ld-linux-x86-64.so.2','EM_X86_64'),('arm64','arm64-v8a','aarch64-linux-gnu','ld-linux-aarch64.so.1','EM_AARCH64')]:
    host=base/('host-'+arch);root=host/'root';native=output/'native'/abi;native.mkdir(parents=True,exist_ok=True)
    values={};paths={};mapping={}
    def find(name):
        preferred=root/'usr/lib'/triplet/name
        if preferred.is_file():return preferred.relative_to(root).as_posix()
        candidates=[p for p in root.rglob(name) if p.is_file()]
        if len(candidates)!=1:raise ValueError(f'Library lookup {arch} {name}: {candidates}')
        return candidates[0].relative_to(root).as_posix()
    pending=[]
    for entry in seed:
        path=entry['path'].replace('arm-linux-gnueabihf',triplet).replace('ld-linux-armhf.so.3',loader)
        if path=='usr/local/bin/box86':continue
        if not (root/path).is_file():
            if path.endswith('libusb-1.0.so.0'):continue
            if '/libgallium-' in path:path=find('libgallium-*.so')
            else:raise ValueError('Missing seed '+path)
        pending.append(path)
    for name in extra:
        if name=='libodbc.so':
            paths['usr/lib/'+triplet+'/libodbc.so']=content(root,find('libodbc.so.2'))
        else:pending.append(find(name))
    if abi=='arm64-v8a':
        paths['usr/local/bin/box64']=(base/'box64-build/box64').read_bytes()
        values['opt/aardvark/licenses/Box64.txt']=(base/'source/box64-0.4.4/LICENSE').read_bytes()
    else:paths.update(wineelf)
    pending=list(dict.fromkeys(pending))
    checked=set()
    while pending or set(paths)-checked:
        path=pending.pop(0) if pending else next(iter(set(paths)-checked))
        if path in checked:continue
        data=paths.get(path) or content(root,path)
        elf=ELFFile(io.BytesIO(data))
        if elf['e_machine']!=machine:raise ValueError('Wrong host ELF '+path)
        paths[path]=data;checked.add(path)
        dynamic=elf.get_section_by_name('.dynamic')
        if dynamic:
            for tag in dynamic.iter_tags():
                if tag.entry.d_tag=='DT_NEEDED':
                    owned=[key for key in paths if pathlib.PurePosixPath(key).name==tag.needed]
                    dependency=owned[0] if len(owned)==1 else find(tag.needed)
                    if dependency not in checked:pending.append(dependency)
    for path,data in sorted(paths.items()):
        digest=hashlib.sha256(data).hexdigest();name='libguest-'+digest[:20]+'.so'
        (native/name).write_bytes(data)
        mapping[path]={'path':path,'file':name,'sha256':digest,'size':len(data)}
    expected={item['file'] for item in mapping.values()}
    for stale in native.glob('libguest-*.so'):
        if stale.name not in expected:stale.unlink()
    for path in sorted(root.rglob('*')):
        if not path.is_file():continue
        name=path.relative_to(root).as_posix()
        if name.startswith(('etc/fonts/','usr/share/fontconfig/','usr/share/fonts/truetype/dejavu/','usr/share/alsa/')):values[name]=content(root,name)
        if name.startswith('usr/share/doc/') and name.endswith('/copyright'):values['opt/aardvark/licenses/'+name.split('/')[3]+'.txt']=content(root,name)
    assets=output/'assets'/abi;assets.mkdir(parents=True,exist_ok=True)
    (assets/'files.json').write_text(json.dumps(list(mapping.values()),indent=2))
    datazip(assets,values)
    print(abi,len(paths),sum(map(len,paths.values())),flush=True)
shutil.copy(base/'host-amd64/packages.json',output/'ubuntu-amd64.json')
shutil.copy(base/'host-arm64/packages.json',output/'ubuntu-arm64.json')
print('Payload ready',flush=True)
