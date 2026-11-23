"""Builds the launcher's "press" sound (A on a game or an options button), in the same family as
frontend/tools/make_switch_sounds.py (same synth parts, key, loudness and peak ceiling).

    python make_press_sound.py      writes ../assets/sounds/press.wav

Shape (~280 ms): a crisp click with a short wooden G6 "tock" (the press going down), then a bright
D7 bell pop with a light B6 under it and a short ring (the press coming back up).
"""
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', '..', 'frontend', 'tools'))
import make_switch_sounds as m  # noqa: E402


def s_press():
    s = m.zeros(0.0)
    tock = m.body(m.G6, 0.05, m.WOOD(0.008), drop=0.05)
    m.mix(tock, m.click(3400, 1.2, 0.0014, dur=0.012, seed=21), 0, 0.6)
    m.mix(s, tock, 0.0, 0.75)
    m.mix(s, m.body(m.D7, 0.26, m.BELL(0.055), attack=0.0015, drop=0.02), 0.05, 1.0)
    m.mix(s, m.body(m.B6, 0.22, m.BRIGHT(0.045), attack=0.002, drop=0.0), 0.055, 0.35)
    m.mix(s, m.click(5200, 1.0, 0.001, dur=0.01, seed=22), 0.05, 0.12)
    return m.lowpass(m.highpass(s, 280), 9000)


if __name__ == '__main__':
    x = m.finish(s_press())
    out = os.path.join(HERE, '..', 'assets', 'sounds', 'press.wav')
    m.write_wav(out, x)
    pk = 20 * math.log10(max(abs(v) for v in x))
    print(f"press {len(x) / m.SR * 1000:.0f} ms, {m.loudness(x):.1f} LUFS, peak {pk:.1f} dBFS -> {os.path.normpath(out)}")
