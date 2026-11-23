# Sets up (or resets) the test for moving games between places, test\e1-storage.txt.
#   python -I test\e1-storage-reset.py [--root <folder>] [--other <folder>]
# --root   the test's folder: data, the PC places and the fake emulator (default %TEMP%\Ionea-tests\e1-storage)
# --other  the "Other drive" place. Put it on another drive: a move inside one drive is a rename, so the
#          cross-drive copy and the cancelled move are only tested there (default <root>\other).
# Everything in both folders is made again from nothing on every run.
import argparse, json, os, shutil

here = os.path.dirname(os.path.abspath(__file__))
ap = argparse.ArgumentParser()
ap.add_argument('--root', default=os.path.join(os.environ.get('TEMP', here), 'Ionea-tests', 'e1-storage'))
ap.add_argument('--other')
ap.add_argument('--big-mb', type=int, default=2048, help='size of the game whose move is cancelled')
a = ap.parse_args()
T = os.path.abspath(a.root)
O = os.path.abspath(a.other or os.path.join(T, 'other'))
if os.path.splitdrive(T)[0].lower() == os.path.splitdrive(O)[0].lower():
    print('note: --other is on the same drive as --root, so the cancelled move will fail')

for d in (T, O):
    if os.path.isdir(d):
        shutil.rmtree(d)

def put(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'wb') as f:
        f.write(data)

pc = os.path.join(T, 'pc')
put(os.path.join(pc, 'gba', 'a.gba'), os.urandom(20_000_000))
put(os.path.join(pc, 'gba', 'a.sav'), b'save\n')
put(os.path.join(pc, 'gba', 'ab.gba'), b'other\n')   # same start of the name: must stay behind
put(os.path.join(pc, 'psx', 'b.cue'),
    b'FILE "b (Track 1).bin" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n'
    b'FILE "b (Track 2).bin" BINARY\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00\n')
put(os.path.join(pc, 'psx', 'b (Track 1).bin'), os.urandom(1_000_000))
put(os.path.join(pc, 'psx', 'b (Track 2).bin'), os.urandom(1_000_000))
put(os.path.join(pc, 'nds', 'c.nds'), os.urandom(1_000_000))
put(os.path.join(pc, 'gb', 'd.gb'), b'pc-d\n')
put(os.path.join(O, 'gb', 'd.gb'), b'other-d\n')   # a different file with the same name: the move stops
os.makedirs(os.path.join(T, 'pc2'), exist_ok=True)
big = os.path.join(pc, 'ps2', 'e.iso')   # big enough that the move is still running when it is cancelled
os.makedirs(os.path.dirname(big), exist_ok=True)
with open(big, 'wb') as f:
    chunk = os.urandom(1 << 20)
    for _ in range(a.big_mb):
        f.write(chunk)

emu = os.path.join(T, 'emus', 'test-emulator.exe')   # never started: the test only moves files
put(emu, b'')
data = os.path.join(T, 'data')
os.makedirs(data, exist_ok=True)
added = '2026-01-01T10:00:00+00:00'
with open(os.path.join(data, 'emulators.json'), 'w') as f:
    json.dump({'Version': 1, 'Emulators': [{'Id': 'e1test', 'Name': 'Test emulator', 'Preset': 'e1test', 'Exe': emu,
               'Args': '"{rom}"', 'Systems': ['gba', 'psx', 'nds', 'gb', 'ps2'],
               'Exts': ['.gba', '.cue', '.nds', '.gb', '.iso'], 'Added': added}]}, f, indent=1)
games = [('Game A', 'gba', 'a.gba'), ('Game B', 'psx', 'b.cue'), ('Game C', 'nds', 'c.nds'),
         ('Game D', 'gb', 'd.gb'), ('Game E', 'ps2', 'e.iso')]
with open(os.path.join(data, 'library.json'), 'w') as f:
    json.dump({'Games': [{'Id': 'id%d' % i, 'Name': n, 'Source': 'rom', 'Rom': os.path.join(pc, s, file),
                          'Emulator': 'e1test', 'System': s, 'Added': added, 'Removed': False}
                         for i, (n, s, file) in enumerate(games)]}, f, indent=1)
with open(os.path.join(data, 'settings.json'), 'w') as f:
    json.dump({'Name': 'Test', 'Fullscreen': False, 'Places': [
        {'Name': 'PC', 'Path': pc},
        {'Name': 'PC2', 'Path': os.path.join(T, 'pc2')},
        {'Name': 'Other drive', 'Path': O},
        {'Name': 'Network', 'Path': r'\\192.0.2.1\games\Ionea\ROMs'},   # a documentation address: never reachable
    ]}, f, indent=1)

script = os.path.join(here, 'e1-storage.txt')
print('ready:', T, 'and', O)
print('set IONEA_TESTROOT=' + T)
print('set IONEA_TESTROOT2=' + O)
print('Launcher.exe --data "%s" --no-import --windowed --mute --script "%s" --shots "%s"'
      % (data, script, os.path.join(data, 'shots')))
