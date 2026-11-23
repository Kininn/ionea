"""Builds the launcher's "edge" sound (moving past the end of a row or list), in the same family as
frontend/tools/make_switch_sounds.py (same synth parts, key, loudness and peak ceiling).

    python make_feel_sounds.py      writes ../assets/sounds/edge.wav

Shape (~90 ms): a soft, low, muted wooden "thunk" (D5 then a lower G4 under it), duller than the
scroll tick so it reads as "nothing further this way" without being harsh.
"""
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', '..', 'frontend', 'tools'))
import make_switch_sounds as m  # noqa: E402

D5 = 587.33
G4 = 392.00


def s_edge():
    s = m.zeros(0.0)
    m.mix(s, m.body(D5, 0.07, m.WOOD(0.012), drop=0.08), 0.0, 0.9)
    m.mix(s, m.body(G4, 0.09, m.WOOD(0.016), drop=0.0), 0.012, 0.55)
    m.mix(s, m.click(1400, 1.0, 0.0012, dur=0.01, seed=31), 0.0, 0.25)
    return m.lowpass(m.highpass(s, 180), 4200)


if __name__ == '__main__':
    x = m.finish(s_edge())
    x = [v * 0.7 for v in x]   # quieter than the other clicks: it is a hint, not an action
    out = os.path.join(HERE, '..', 'assets', 'sounds', 'edge.wav')
    m.write_wav(out, x)
    pk = 20 * math.log10(max(abs(v) for v in x))
    print(f"edge {len(x) / m.SR * 1000:.0f} ms, {m.loudness(x):.1f} LUFS, peak {pk:.1f} dBFS -> {os.path.normpath(out)}")
