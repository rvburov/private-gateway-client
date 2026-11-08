#!/usr/bin/env python3
"""Воспроизводимая геобаза и изображения. Входы скачиваются отдельно, проверяются до чтения.
python3 scripts/gen-country-assets.py /tmp/dbip.csv.gz /tmp/flag-icons.tar.gz
Нужен CairoSVG 2.8.2 (только генерация; в приложение не входит).
"""
import csv
import gzip
import hashlib
import io
import ipaddress
import json
import shutil
from pathlib import Path
import struct
import sys
import tarfile

ROOT = Path(__file__).resolve().parent.parent
META = ROOT / 'shared/countries/sources.json'
meta = json.loads(META.read_text())
inputs = [Path(p) for p in sys.argv[1:]]
if len(inputs) != 2:
    sys.exit('Нужны CSV.gz DB-IP и архив flag-icons.tar.gz')
for path, source in zip(inputs, (meta['database'], meta['flags'])):
    if hashlib.sha256(path.read_bytes()).hexdigest() != source['sha256']:
        sys.exit('SHA256 не совпадает: ' + path.name)

import cairosvg  # noqa: E402
if cairosvg.__version__ != '2.8.2':
    sys.exit('Для повторяемых изображений нужен CairoSVG 2.8.2')

out = ROOT / 'shared/countries'
flags = out / 'flags'
flags.mkdir(parents=True, exist_ok=True)
svgdir = out / 'svg'
svgdir.mkdir(exist_ok=True)
known = set()
with tarfile.open(inputs[1]) as archive:
    for member in archive.getmembers():
        name = Path(member.name)
        if '/flags/1x1/' not in member.name or name.suffix != '.svg' or len(name.stem) != 2:
            continue
        code = name.stem.upper()
        if not code.isascii() or not code.isalpha() or code in ('XX', 'EU', 'UN'):
            continue
        source = archive.extractfile(member).read()
        (svgdir / (code + '.svg')).write_bytes(source)
        cairosvg.svg2png(bytestring=source, write_to=str(flags / (code + '.png')), output_width=112, output_height=112)
        known.add(code)
    license_file = next(m for m in archive.getmembers() if m.name.endswith('/LICENSE'))
    (out / 'LICENSE-flags.txt').write_bytes(archive.extractfile(license_file).read())
cairosvg.svg2png(url=str(svgdir / 'UNKNOWN.svg'), write_to=str(flags / 'UNKNOWN.png'), output_width=112, output_height=112)

rows = []
with gzip.open(inputs[0], 'rt', encoding='utf-8') as source:
    for start, end, country in csv.reader(source):
        if country not in known:
            continue
        a, b = ipaddress.ip_address(start), ipaddress.ip_address(end)
        if a.version != b.version or int(a) > int(b):
            raise ValueError('Некорректный диапазон')
        # IPv4 занимает пространство ::ffff:0:0/96; семейства не пересекаются.
        pack = lambda ip: b'\0' * 10 + b'\xff\xff' + ip.packed if ip.version == 4 else ip.packed
        rows.append(pack(a) + pack(b) + country.encode('ascii'))
rows.sort(key=lambda row: row[:16])
for previous, current in zip(rows, rows[1:]):
    if previous[16:32] >= current[:16]:
        raise ValueError('Перекрывающиеся диапазоны')
raw = b'PGCTRY1\0' + struct.pack('>I', len(rows)) + b''.join(rows)
with (out / 'country.bin.gz').open('wb') as file:
    with gzip.GzipFile(filename='', mode='wb', fileobj=file, mtime=0, compresslevel=9) as compressed:
        compressed.write(raw)
print(f'Стран: {len(known)}; диапазонов: {len(rows)}; база: {len(raw)} байт')

preview = ROOT / 'docs/design/html/assets/countries'
preview.mkdir(parents=True, exist_ok=True)
for image in flags.glob('*.png'):
    shutil.copyfile(image, preview / image.name)
