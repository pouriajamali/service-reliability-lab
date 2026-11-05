"""Package tracked-style source members and a standalone HTML demo; no binaries."""
from pathlib import Path
import hashlib,shutil,zipfile
ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'artifacts/release'
OUT.mkdir(parents=True,exist_ok=True)
members=[]
for f in ROOT.rglob('*'):
    if f.is_file() and not any(x in {'bin','obj','.git','artifacts','__pycache__'} for x in f.relative_to(ROOT).parts):
        members.append(f)
archive=OUT/'service-reliability-lab-v0.1.0-preview.1.zip'
with zipfile.ZipFile(archive,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=9) as z:
    for f in sorted(members):
        info=zipfile.ZipInfo(f.relative_to(ROOT).as_posix(),date_time=(2026,1,1,0,0,0))
        info.compress_type=zipfile.ZIP_DEFLATED
        z.writestr(info,f.read_bytes())
html=OUT/'service-reliability-demo.html'
shutil.copyfile(ROOT/'demo/index.html',html)
lines=[]
for p in [archive,html]:
    digest=hashlib.sha256(p.read_bytes()).hexdigest()
    lines.append(f'{digest}  {p.name}')
(OUT/'SHA256SUMS.txt').write_text('\n'.join(lines)+'\n',encoding='ascii',newline='\n')
print(f'Packaged {len(members)} source members and standalone HTML; checksums in {OUT}')
