#!/usr/bin/env python3
"""Small publication guard: do not print potentially sensitive matches."""
import re,sys
from pathlib import Path
root=Path(__file__).resolve().parents[1]
errors=[]
for p in root.rglob('*'):
    if not p.is_file() or '.git' in p.parts: continue
    relative=p.relative_to(root).as_posix()
    if p.suffix in {'.dll','.png'} or 'MuseBleNative.bundle/Contents/MacOS/' in relative: continue
    try: s=p.read_text()
    except UnicodeDecodeError: continue
    if p.name == 'audit.py': continue
    if re.search(r'(?i)lynook|lumomobile|MinimalVoiceChat|QwenTTS|MiSans|/Users/',s): errors.append(relative+': private host reference')
    if re.search(r'(?:sk_[A-Za-z0-9]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})',s): errors.append(relative+': possible credential')
    for token in re.findall(r'mgst_[A-Za-z0-9_-]{20,}',s):
        if set(token[5:]) != {'A'}: errors.append(relative+': unexpected SDK token')
if errors:
    print('\n'.join(sorted(set(errors))));sys.exit(1)
print('PASS publication guard: no private host references or non-fixture credentials found')
