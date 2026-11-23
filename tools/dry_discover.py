# Read-only: which programs in the emulator folders a definition set would claim, the way Emulators.Discover does
# (root, its folders and their folders; .exe/.dll; cores first, then own > hand-made > imported).
# Usage: python -I dry_discover.py <definitions dir> <emulators.json> <folder> [<folder> ...]
import json, os, re, sys
defs_dir, emus_file, folders = sys.argv[1], sys.argv[2], sys.argv[3:]
defs = []
for sub in ('cores', 'emulators'):
    root = os.path.join(defs_dir, sub)
    files = sorted((os.path.join(dp, f) for dp, _, fs in os.walk(root) for f in fs if f.endswith('.json')), key=str.lower)
    for f in files:
        d = json.load(open(f, encoding='utf-8'))
        progs = d.get('detect', {}).get('programs', [])
        if not progs:
            continue
        defs.append((d['id'], progs, any(p.lower().endswith('_libretro.dll') for p in progs), 2 if d.get('source') else 1))
defs.sort(key=lambda t: (0 if t[2] else 1, t[3]))
def match(name):
    for did, progs, _, rank in defs:
        for p in progs:
            if p.startswith('^'):
                if re.match(p, name, re.I): return did, rank
            elif p.lower() == name.lower(): return did, rank
    return None
data = json.load(open(emus_file, encoding='utf-8'))
have = {os.path.normcase(e['Exe']) for e in data.get('Emulators', [])}
dismissed = {os.path.normcase(x) for x in data.get('Dismissed', []) or []}
seen, new, known = 0, [], []
for root in folders:
    if not os.path.isdir(root): continue
    dirs = [root]
    for d in sorted(os.listdir(root)):
        p = os.path.join(root, d)
        if os.path.isdir(p):
            dirs.append(p)
            try: dirs += [os.path.join(p, x) for x in os.listdir(p) if os.path.isdir(os.path.join(p, x))]
            except OSError: pass
    for d in dirs:
        try: fs = sorted(f for f in os.listdir(d) if f.lower().endswith(('.exe', '.dll')) and os.path.isfile(os.path.join(d, f)))
        except OSError: continue
        for f in fs:
            seen += 1
            full = os.path.normcase(os.path.join(d, f))
            m = match(f)
            if not m: continue
            if full in have: known.append(f'{f} -> {m[0]} ({"imported" if m[1] == 2 else "hand"})')
            elif full not in dismissed: new.append(f'{os.path.join(d, f)} -> {m[0]} ({"imported" if m[1] == 2 else "hand"})')
print(f'{len(defs)} definitions, {seen} programs looked at')
print('Known emulators:'); print('  ' + '\n  '.join(known))
print('New finds:'); print('  ' + ('\n  '.join(new) or '(none)'))
