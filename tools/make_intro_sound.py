"""Builds the launcher's intro chime, in the same family as frontend/tools/make_switch_sounds.py
(same synth parts, key, loudness and peak ceiling).

    python make_intro_sound.py      writes ../assets/sounds/intro.wav

Shape (~1.9 s): a soft airy swell leads in, then a quick rising G-major arpeggio of bell tones
(G5 D6 G6 B6) lands on a held shimmer, over a warm low G4 bed. It plays as the icon settles, so it
reads as "the system is awake" rather than as a button sound.
"""
import math
import os
import random
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', '..', 'frontend', 'tools'))
import make_switch_sounds as m  # noqa: E402

G4, G5, D6, G6, B6, D7 = 392.00, 783.99, 1174.66, 1567.98, 1975.53, 2349.32
LEAD = 0.18   # the air swell before the first note; Intro.cs starts the clip this much before the reveal


def s_intro():
    s = m.zeros(0.0)
    # air swell leading in: band-passed noise sliding up, fading in, gone as the notes land
    rnd = random.Random(21)
    n = int(0.36 * m.SR)
    air = m.sweep_bandpass([rnd.uniform(-1, 1) for _ in range(n)], 900, 5200, 1.3)
    air = [v * (i / n) ** 2.2 * math.exp(-max(0, i - int(LEAD * m.SR)) / (0.06 * m.SR)) for i, v in enumerate(air)]
    m.mix(s, air, 0.0, 0.22)
    # the arpeggio: bell tones, each a little longer than the last
    for k, (f, at, g, tau) in enumerate([(G5, 0.00, 0.55, 0.32), (D6, 0.075, 0.55, 0.38), (G6, 0.15, 0.6, 0.48), (B6, 0.24, 0.5, 0.62)]):
        note = m.body(f, 1.5, m.BELL(tau), attack=0.004, drop=0.0, glide_from=0.985, glide_tau=0.02)
        m.mix(note, m.click(3800 + 400 * k, 1.1, 0.001, dur=0.008, seed=40 + k), 0, 0.18)
        m.mix(s, note, LEAD + at, g)
    # shimmer: a quiet high D7 that swells in after the last note and rings out
    sh = m.body(D7, 1.4, [(1.0, 1.0, 0.55), (2.0, 0.2, 0.3)], attack=0.12, drop=0.0)
    m.mix(s, sh, LEAD + 0.3, 0.16)
    # warm bed: low G4 sine, slow attack, long tail
    bed = m.body(G4, 1.6, [(1.0, 1.0, 0.7), (2.0, 0.25, 0.4)], attack=0.08, drop=0.0)
    m.mix(s, bed, LEAD, 0.28)
    return m.lowpass(s, 9000)


if __name__ == '__main__':
    x = m.finish(s_intro())
    # long fade-out so the tail never stops dead
    fo = int(0.35 * m.SR)
    for i in range(fo):
        x[len(x) - fo + i] *= 0.5 + 0.5 * math.cos(math.pi * i / fo)
    out = os.path.join(HERE, '..', 'assets', 'sounds', 'intro.wav')
    m.write_wav(out, x)
    pk = 20 * math.log10(max(abs(v) for v in x))
    print(f"intro {len(x) / m.SR * 1000:.0f} ms, {m.loudness(x):.1f} LUFS, peak {pk:.1f} dBFS -> {os.path.normpath(out)}")
