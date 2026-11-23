# Imports definitions from three MIT-licensed data sources:
#   Playnite   source/Playnite/Emulation (Emulators/*/emulator.yaml, Platforms.yaml)  -> definitions/emulators/playnite/
#   ES-DE      resources/systems/windows/es_systems.xml                              -> extra systems in systems.json
#   libretro-core-info  *_libretro.info                                               -> definitions/cores/libretro/
# Hand-made definitions (definitions/emulators/*.json, definitions/cores/*.json, the 20 hand-made systems) are never changed.
# Only data is copied (names, program-name patterns, command lines, extensions, firmware names and hashes).
# Usage: python -I import_defs.py <playnite Emulation dir> <es_systems.xml> <core-info dir> <launcher\definitions> <sources.json>
import json, os, re, sys, xml.etree.ElementTree as ET

pn_dir, esde_xml, info_dir, out, sources_file = sys.argv[1:6]
SRC = json.load(open(sources_file, encoding='utf-8'))   # {"playnite": "<commit>", "es-de": "<commit>", "core-info": "<commit>"}
log = []
def note(s): log.append(s)

# ---------- minimal YAML for Playnite's files (maps, "- " lists of maps, [flow, lists], quoted scalars) ----------
def scalar(v):
    v = v.strip()
    if v.startswith("'") and v.endswith("'") and len(v) >= 2: return v[1:-1].replace("''", "'")
    if v.startswith('"') and v.endswith('"') and len(v) >= 2: return json.loads(v)
    if v.startswith('[') and v.endswith(']'):
        items, cur, q = [], '', None
        for ch in v[1:-1]:
            if q:
                cur += ch
                if ch == q: q = None
            elif ch in "'\"": q = ch; cur += ch
            elif ch == ',': items.append(cur); cur = ''
            else: cur += ch
        if cur.strip(): items.append(cur)
        return [scalar(i) for i in items if i.strip()]
    if v in ('true', 'false'): return v == 'true'
    return v

def yaml_items(text):
    """Top level: a map (emulator.yaml) or a list of maps (Platforms.yaml). Nested: one list of maps (Profiles)."""
    text = text.lstrip('﻿')
    top, lst, cur, nested_key, nested = {}, None, None, None, None
    for raw in text.splitlines():
        if not raw.strip() or raw.lstrip().startswith('#'): continue
        ind = len(raw) - len(raw.lstrip(' ')); line = raw.strip()
        if ind == 0 and line.startswith('- '):              # top-level list item
            lst = lst if lst is not None else []
            cur = {}; lst.append(cur); line = line[2:]; ind = 2
        if ind == 0:
            k, _, v = line.partition(':'); nested_key = None
            if v.strip() == '': nested_key = k; top[k] = nested = []
            else: top[k] = scalar(v)
        elif nested_key is not None and line.startswith('- '):
            item = {}; nested.append(item); k, _, v = line[2:].partition(':'); item[k.strip()] = scalar(v)
        elif nested_key is not None and nested:
            k, _, v = line.partition(':'); nested[-1][k.strip()] = scalar(v)
        elif cur is not None:
            k, _, v = line.partition(':'); cur[k.strip()] = scalar(v)
    return lst if lst is not None else top

# ---------- systems ----------
systems_path = os.path.join(out, 'systems.json')
systems = json.load(open(systems_path, encoding='utf-8'))
hand = [s for s in systems if s.get('source') is None]          # the hand-made systems (kept as they are)
alias = {}                                                       # ES-DE system name -> Ionea system id
for s in hand:
    for a in s.get('esde', []) + [s['id']]: alias[a] = s['id']

SKIP_ESDE = {'windows', 'steam', 'epic', 'ports', 'kodi', 'desktop', 'moonlight', 'android', 'androidapps', 'androidgames',
             'chromeos', 'emulators', 'lutris', 'linux', 'macos', 'mugen', 'openbor', 'singe', 'solarus', 'triforce', 'ios'}

# Windows program and script types are never game files for an emulator (PC games come as .lnk in ES-DE's own systems).
PROGRAM_EXTS = {'.exe', '.dll', '.com', '.bat', '.cmd', '.lnk', '.url', '.ps1', '.vbs', '.js', '.msi', '.scr', '.pif', '.reg', '.sh', '.desktop'}
tree = ET.parse(esde_xml)
esde_cores = {}                                                  # core dll stem -> ES-DE systems naming it
extra = []
for sx in tree.getroot().findall('system'):
    name = (sx.findtext('name') or '').strip(); full = (sx.findtext('fullname') or '').strip()
    exts = []
    for e in (sx.findtext('extension') or '').split():
        e = e.lower()
        if e.startswith('.') and e not in exts and e not in PROGRAM_EXTS: exts.append(e)
    for c in sx.findall('command'):
        for m in re.finditer(r'%CORE_RETROARCH%[\\/]([A-Za-z0-9_\-]+)_libretro\.dll', c.text or ''):
            esde_cores.setdefault(m.group(1), []).append(name)
    if name in alias or name in SKIP_ESDE: continue
    if not exts: note(f'es-de: {name} has no extensions, skipped'); continue
    extra.append({'id': name, 'name': full or name, 'exts': exts, 'esde': [name], 'more': True, 'source': 'es-de'})
    alias[name] = name
systems = hand + extra
with open(systems_path, 'w', encoding='utf-8', newline='\n') as f:
    f.write('[\n' + ',\n'.join('  ' + json.dumps(s, ensure_ascii=False) for s in systems) + '\n]\n')
sys_exts = {s['id']: s['exts'] for s in systems}
note(f'systems: {len(hand)} hand-made, {len(extra)} from es_systems.xml')

def extra_exts(sids, exts):
    have = {e for s in sids for e in sys_exts.get(s, [])}
    return [e for e in exts if e not in have and e not in PROGRAM_EXTS]

# ---------- Playnite emulators ----------
# Playnite platform id -> ES-DE system name (then -> Ionea id through alias). Written for this import.
PN = {'3do': '3do', 'adobe_flash': 'flash', 'amstrad_cpc': 'amstradcpc', 'apple_2': 'apple2', 'arcade': 'arcade',
      'arduboy': 'arduboy', 'atari_2600': 'atari2600', 'atari_5200': 'atari5200', 'atari_7800': 'atari7800',
      'atari_8bit': 'atari800', 'atari_jaguar': 'atarijaguar', 'atari_lynx': 'atarilynx', 'atari_st': 'atarist',
      'bandai_wonderswan_color': 'wonderswancolor', 'bandai_wonderswan': 'wonderswan', 'coleco_vision': 'colecovision',
      'commodore_64': 'c64', 'commodore_amiga_cd32': 'amigacd32', 'commodore_amiga': 'amiga', 'commodore_pet': 'pet',
      'commodore_plus4': 'plus4', 'commodore_vci20': 'vic20', 'fairchild_channelf': 'channelf', 'vectrex': 'vectrex',
      'macintosh': 'macintosh', 'magnavox_odyssey_2': 'odyssey2', 'mattel_intellivision': 'intellivision',
      'megaduck': 'megaduck', 'microsoft_msx': 'msx', 'microsoft_msx2': 'msx2', 'xbox360': 'xbox360', 'xbox': 'xbox',
      'nec_pc88': 'pc88', 'nec_pc98': 'pc98', 'nec_pcfx': 'pcfx', 'nec_supergrafx': 'supergrafx',
      'nec_turbografx_16': 'pcengine', 'nec_turbografx_cd': 'pcenginecd', 'nintendo_3ds': 'n3ds', 'nintendo_64': 'n64',
      'nintendo_ds': 'nds', 'nintendo_dsi': 'nds', 'nintendo_nes': 'nes', 'nintendo_famicom_disk': 'fds',
      'nintendo_gameandwatch': 'gameandwatch', 'nintendo_gameboyadvance': 'gba', 'nintendo_gameboycolor': 'gbc',
      'nintendo_gameboy': 'gb', 'nintendo_gamecube': 'gc', 'nintendo_super_nes': 'snes', 'nintendo_switch': 'switch',
      'nintendo_switch2': 'switch2', 'nintendo_virtualboy': 'virtualboy', 'nintendo_wiiu': 'wiiu', 'nintendo_wii': 'wii',
      'pc_dos': 'dos', 'sega_32x': 'sega32x', 'sega_cd': 'segacd', 'sega_dreamcast': 'dreamcast', 'sega_gamegear': 'gamegear',
      'sega_genesis': 'genesis', 'sega_mastersystem': 'mastersystem', 'sega_saturn': 'saturn', 'sega_sg1000': 'sg-1000',
      'sharp_x1': 'x1', 'sharp_x68000': 'x68000', 'sinclair_zxspectrum': 'zxspectrum', 'sinclair_zxspectrum3': 'zxspectrum',
      'sinclair_zx81': 'zx81', 'snk_neogeo_aes': 'neogeo', 'snk_neogeo_cd': 'neogeocd', 'snk_neogeopocket_color': 'ngpc',
      'snk_neogeopocket': 'ngp', 'sony_playstation2': 'ps2', 'sony_playstation3': 'ps3', 'sony_playstation4': 'ps4',
      'sony_psp': 'psp', 'sony_vita': 'psvita', 'sony_playstation': 'psx', 'thomson_mo5': 'moto', 'thomson_to7': 'moto',
      'tic_80': 'tic80', 'uzebox': 'uzebox', 'wasm4': 'wasm4', 'watara_supervision': 'supervision', 'philips_cdi': 'cdimono1',
      'pokemon_mini': 'pokemini'}
alias['switch2'] = 'switch2'

hand_programs, hand_ids = set(), set()
for folder in ('emulators', 'cores'):
    for fn in os.listdir(os.path.join(out, folder)):
        if fn.endswith('.json'):
            d = json.load(open(os.path.join(out, folder, fn), encoding='utf-8'))
            hand_ids.add(d['id'])
            for p in d.get('detect', {}).get('programs', []): hand_programs.add(p.lower())

# Programs that sit next to emulators and must never be taken for one.
NEVER = ['Launcher.exe', 'azahar-room.exe', 'DolphinTool.exe', 'eden-cli.exe', 'mgba-sdl.exe', 'lichii-updater.exe',
         'setup.exe', 'uninstall.exe', 'unins000.exe', 'updater.exe', 'crashpad_handler.exe', 'QtWebEngineProcess.exe',
         'vc_redist.x64.exe', 'maintenancetool.exe', 'Updater.exe', 'dxwebsetup.exe']
SKIP_PN = {'retroarch', 'ralibretro'}                          # cores come from libretro-core-info

def pattern_ok(p, who):
    try: rx = re.compile(p, re.I)
    except re.error as e: note(f'playnite: {who}: bad pattern {p}: {e}'); return False
    if not (p.startswith('^') and p.endswith('$')): note(f'playnite: {who}: pattern {p} is not anchored, skipped'); return False
    bad = [n for n in NEVER if rx.match(n)]
    if bad: note(f'playnite: {who}: pattern {p} matches {bad}, skipped'); return False
    hit = [h for h in hand_programs if rx.match(h)]
    if hit: note(f'playnite: {who}: pattern {p} matches hand-made {hit}, skipped'); return False
    return True

pn_out = os.path.join(out, 'emulators', 'playnite'); os.makedirs(pn_out, exist_ok=True)
for fn in os.listdir(pn_out):
    if fn.endswith('.json'): os.remove(os.path.join(pn_out, fn))
emu_root = os.path.join(pn_dir, 'Emulators')
made = 0
# A program pattern already taken by an imported definition: a second one with the same program could never be
# picked (the first wins), so it is left out. Folders are read A to Z, so the result is the same every import.
# (Playnite e2e6efcda311: DOSBox and DOSBox Staging both ship dosbox.exe, ^dosbox\.exe$; DOSBox is kept.)
taken = {}                                                       # pattern (lower case) -> definition id
def literal(p): return p.strip('^$').replace(chr(92), '')
def clashes(p):
    rx = re.compile(p, re.I)
    for q, who in taken.items():
        if q == p.lower() or rx.match(literal(q)) or re.compile(q, re.I).match(literal(p)): return who
    return None
for folder in sorted(os.listdir(emu_root)):
    yml = os.path.join(emu_root, folder, 'emulator.yaml')
    if not os.path.isfile(yml): continue
    y = yaml_items(open(yml, encoding='utf-8').read())
    if not isinstance(y, dict) or not y.get('Id'): note(f'playnite: {folder}: unreadable'); continue
    pid = str(y['Id']).lower()
    if pid in SKIP_PN: continue
    if pid in hand_ids: note(f'playnite: {pid}: hand-made definition kept'); continue
    # One definition per program pattern; its profiles' platforms are merged. Profiles that need what Ionea
    # can't give (script import, other placeholders than the game path) are left out.
    by_exe = {}
    for pr in y.get('Profiles', []):
        args = str(pr.get('StartupArguments', '"{ImagePath}"'))
        if pr.get('ScriptGameImport'): continue
        if re.search(r'\{(?!ImagePath\})[A-Za-z]+\}', args): note(f'playnite: {pid}/{pr.get("Name")}: needs {args}, skipped'); continue
        exe = str(pr.get('StartupExecutable', '')).strip()
        if not exe: continue
        g = by_exe.setdefault(exe, {})                         # args -> its platforms and extensions
        a = args.replace('{ImagePath}', '{rom}')
        h = g.setdefault(a, {'platforms': [], 'exts': []})
        for p in pr.get('Platforms', []) or []:
            if p not in h['platforms']: h['platforms'].append(p)
        for e in pr.get('ImageExtensions', []) or []:
            e = '.' + str(e).lower().lstrip('.')
            if e not in h['exts']: h['exts'].append(e)
    for i, (exe, byargs) in enumerate(by_exe.items()):
        # A definition has one command line: keep the platforms whose profiles use the most common one
        # (per-platform options such as --system would start the others wrongly).
        args, g = max(byargs.items(), key=lambda kv: len(kv[1]['platforms']))
        if len(byargs) > 1:
            dropped = [p for a, h in byargs.items() if a != args for p in h['platforms'] if p not in g['platforms']]
            note(f'playnite: {pid}: one command line {args}; platforms needing another left out: {dropped}')
        did = pid if len(by_exe) == 1 else f'{pid}-{i + 1}'
        name = str(y.get('Name', pid)) if len(by_exe) == 1 else f'{y.get("Name", pid)} ({exe.strip("^$").replace(chr(92), "")})'
        if not pattern_ok(exe, did): continue
        other = clashes(exe)
        if other: note(f'playnite: {did}: pattern {exe} is the same program as {other}, skipped (can\'t be told apart by file name)'); continue
        sids, unmapped = [], []
        for p in g['platforms']:
            s = alias.get(PN.get(p, ''))
            if s and s not in sids: sids.append(s)
            elif not s: unmapped.append(p)
        if unmapped: note(f'playnite: {did}: platforms without a system: {unmapped}')
        if not sids: note(f'playnite: {did}: no known system, skipped'); continue
        d = {'id': did, 'name': name, 'source': f'playnite@{SRC["playnite"][:12]}',
             'detect': {'programs': [exe], **({'archive': pid} if len(pid) >= 5 else {})}, 'start': {'args': args}, 'systems': sids}
        ee = extra_exts(sids, g['exts'])
        if ee: d['extraExts'] = ee
        if did in hand_ids: note(f'playnite: {did}: id taken by a hand-made definition, skipped'); continue
        with open(os.path.join(pn_out, did + '.json'), 'w', encoding='utf-8', newline='\n') as f:
            json.dump(d, f, indent=2, ensure_ascii=False); f.write('\n')
        taken[exe.lower()] = did
        made += 1
note(f'playnite: {made} emulator definitions')

# ---------- libretro cores ----------
def info(path):
    d = {}
    for line in open(path, encoding='utf-8', errors='replace'):
        m = re.match(r'\s*([A-Za-z0-9_]+)\s*=\s*"?(.*?)"?\s*$', line)
        if m: d[m.group(1)] = m.group(2)
    return d

core_out = os.path.join(out, 'cores', 'libretro'); os.makedirs(core_out, exist_ok=True)
for fn in os.listdir(core_out):
    if fn.endswith('.json'): os.remove(os.path.join(core_out, fn))
made, names = 0, {}
for fn in sorted(os.listdir(info_dir)):
    if not fn.endswith('_libretro.info'): continue
    stem = fn[:-len('_libretro.info')]
    did = stem + '-core'
    if did in hand_ids or (stem + '_libretro.dll') in hand_programs: continue
    if stem not in esde_cores: continue                          # only cores ES-DE uses for a system
    i = info(os.path.join(info_dir, fn))
    sids = []
    for n in esde_cores[stem]:
        s = alias.get(n)
        if s and s not in sids: sids.append(s)
    if not sids: note(f'core: {stem}: its ES-DE systems were skipped'); continue
    name = (i.get('corename') or stem) + ' core'
    if name in names: name = f'{name} ({stem})'
    names[name] = stem
    d = {'id': did, 'name': name, 'source': f'libretro-core-info@{SRC["core-info"][:12]}',
         'detect': {'programs': [stem + '_libretro.dll'], 'archive': stem + '_libretro'}, 'systems': sids}
    exts = ['.' + e.lower() for e in (i.get('supported_extensions') or '').split('|') if e]
    ee = extra_exts(sids, exts)
    if ee: d['extraExts'] = ee
    md5 = {}
    for part in (i.get('notes') or '').split('|'):
        m = re.match(r'\s*\(!\)\s*(.+?)\s*\(md5\):\s*([0-9a-fA-F]{32})', part)
        if m: md5.setdefault(m.group(1).strip().lower(), m.group(2).lower())
    files = []
    for k in range(int(i.get('firmware_count') or 0)):
        p = i.get(f'firmware{k}_path')
        if not p: continue
        base = p.replace('\\', '/').split('/')[-1]
        f = {'name': base, 'path': p.replace('/', '\\')}
        h = md5.get(p.lower()) or md5.get(base.lower())
        if h: f['md5'] = h
        if (i.get(f'firmware{k}_opt') or '').lower() == 'true': f['optional'] = True
        if i.get(f'firmware{k}_desc'): f['desc'] = i[f'firmware{k}_desc']
        files.append(f)
    if files: d['requiredFiles'] = files
    with open(os.path.join(core_out, did + '.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump(d, f, indent=2, ensure_ascii=False); f.write('\n')
    made += 1
note(f'cores: {made} core definitions (ES-DE names {len(esde_cores)} cores)')
print('\n'.join(log))
