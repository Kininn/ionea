"""Builds every launcher UI sound, in both sound themes, from the same synth family as
frontend/tools/make_switch_sounds.py (same parts, key of G, loudness target and peak ceiling).
Pure synthesis, standard library only, no samples (no downloaded sound packs).

    python make_ui_sounds.py            writes ../assets/sounds/*.wav (theme "Ionea")
                                        and    ../assets/sounds/soft/*.wav (theme "Soft")
    python make_ui_sounds.py --check    only prints the table (length, loudness, peak, high-frequency share)
    python make_ui_sounds.py --only toast   builds (or checks) that one clip, in both themes

Theme "Ionea": today's wooden ticks and bell pops. The clips that already existed come out the same as
before (same functions, same seeds), except systembrowse and quicksysselect, which get a gentle top cut
(they had the most energy above 6 kHz of the set).
Theme "Soft": the same clips a fourth lower, with quieter overtones, a softer click and a lower top
cut: rounder and calmer, for late evenings or headphones.

New clips:
  tick       a value step (sliders, cover zoom); the launcher plays it at a pitch that follows the value
  type       a key typed into a text box: the softest, shortest tick
  toggleon   a switch turned on: two quick ticks going up
  toggleoff  a switch turned off: two quick ticks going down
  unfold     a container opening: air rising, landing on two soft wooden notes
  fold       a container closing: air falling, landing on one low wooden note
  toast      a pop-up appearing: one soft bell (lower, rounder, quieter and shorter; see s_toast)
  error      something refused or failed: two muted low notes going down
  profile    a profile switched: a soft three-note bell arpeggio going up
  badge      a badge earned: a quick bell arpeggio with a light shimmer
  gameclose  back home after a game: a soft bell falling, over a falling breath of air

Levels: every clip is levelled to -16 LUFS (BS.1770, ungated, over the clip) with the sample peak
under -3.5 dBFS, then the small frequent ones are trimmed (TRIM) so they sit under the actions.
"""
import math
import os
import random
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', '..', 'frontend', 'tools'))
import make_switch_sounds as m  # noqa: E402

OUT = os.path.join(HERE, '..', 'assets', 'sounds')
G4, A4, B4, D5, E5, G5, A5, B5 = 392.0, 440.0, 493.88, 587.33, 659.26, 783.99, 880.0, 987.77
G7 = 3135.96

# How much quieter than -16 LUFS each small clip ends up (linear). Not listed: 1.0.
TRIM = {'edge': 0.7, 'tick': 0.6, 'type': 0.5, 'toggleon': 0.8, 'toggleoff': 0.75, 'error': 0.8, 'toast': 0.55}
# Theme Ionea: extra low-pass (Hz) on clips that were a bit bright.
TOP_CUT = {'systembrowse': 7500, 'quicksysselect': 8000}


def air(f0, f1, dur, seed, shape=1.6):
    """Band-passed noise sliding from f0 to f1 under a soft hump (the whoosh part)."""
    rnd = random.Random(seed)
    n = int(dur * m.SR)
    w = m.sweep_bandpass([rnd.uniform(-1, 1) for _ in range(n)], f0, f1, 1.6)
    return [v * math.sin(math.pi * min(1.0, i / n)) ** shape for i, v in enumerate(w)]


def tick_at(f, dur=0.03, tau=0.006, click_fc=3200, click_amp=0.5, seed=40):
    t = m.body(f, dur, m.WOOD(tau), drop=0.04)
    return m.mix(t, m.click(click_fc, 1.2, 0.0013, dur=0.01, seed=seed), 0, click_amp)


# ---- the clips that already existed (unchanged recipes) ----
def s_edge():
    s = m.zeros(0.0)
    m.mix(s, m.body(D5, 0.07, m.WOOD(0.012), drop=0.08), 0.0, 0.9)
    m.mix(s, m.body(G4, 0.09, m.WOOD(0.016), drop=0.0), 0.012, 0.55)
    m.mix(s, m.click(1400, 1.0, 0.0012, dur=0.01, seed=31), 0.0, 0.25)
    return m.lowpass(m.highpass(s, 180), 4200)


def s_press():
    s = m.zeros(0.0)
    tock = m.body(m.G6, 0.05, m.WOOD(0.008), drop=0.05)
    m.mix(tock, m.click(3400, 1.2, 0.0014, dur=0.012, seed=21), 0, 0.6)
    m.mix(s, tock, 0.0, 0.75)
    m.mix(s, m.body(m.D7, 0.26, m.BELL(0.055), attack=0.0015, drop=0.02), 0.05, 1.0)
    m.mix(s, m.body(m.B6, 0.22, m.BRIGHT(0.045), attack=0.002, drop=0.0), 0.055, 0.35)
    m.mix(s, m.click(5200, 1.0, 0.001, dur=0.01, seed=22), 0.05, 0.12)
    return m.lowpass(m.highpass(s, 280), 9000)


# ---- new clips ----
def s_tick():
    # The value step: a very short, light G6 tick (the launcher re-pitches it by the value).
    return m.lowpass(tick_at(m.G6, 0.028, 0.0055, 3000, 0.4, seed=41), 7000)


def s_type():
    # A key typed: shorter and duller than the scroll tick, so a fast typist doesn't hear a drum roll.
    return m.lowpass(tick_at(m.E6, 0.022, 0.0045, 2400, 0.35, seed=42), 5200)


def s_toggleon():
    s = m.zeros(0.0)
    m.mix(s, tick_at(m.D6, 0.04, 0.007, 2800, 0.4, seed=43), 0.0, 0.8)
    m.mix(s, tick_at(m.G6, 0.05, 0.009, 3200, 0.4, seed=44), 0.042, 1.0)
    return m.lowpass(s, 7000)


def s_toggleoff():
    s = m.zeros(0.0)
    m.mix(s, tick_at(m.G6, 0.04, 0.007, 3000, 0.35, seed=45), 0.0, 0.85)
    m.mix(s, tick_at(m.D6, 0.05, 0.009, 2600, 0.3, seed=46), 0.042, 0.9)
    return m.lowpass(s, 6000)


def s_unfold():
    s = [v * 0.55 for v in air(500, 2200, 0.16, seed=47)]
    m.mix(s, m.pop(m.G6, 0.03, partials=m.WOOD, click_fc=2600, click_amp=0.2, seed=48), 0.1, 0.8)
    m.mix(s, m.pop(m.B6, 0.04, partials=m.WOOD, click_fc=3000, click_amp=0.2, seed=49), 0.14, 0.7)
    return m.lowpass(s, 6500)


def s_fold():
    s = [v * 0.5 for v in air(2200, 450, 0.16, seed=50)]
    m.mix(s, m.pop(m.D6, 0.036, partials=m.WOOD, click_fc=1800, click_amp=0.18, seed=51), 0.12, 0.85)
    m.mix(s, m.body(G5, 0.08, m.WOOD(0.02), drop=0.0), 0.13, 0.3)
    return m.lowpass(s, 5000)


def s_toast():
    # Smaller and softer than the old high bell (B6 over G6), which could sound like a ping.
    # One low round note (G5) with a quiet fifth above (D6), a gentle attack, a short ring and a low top
    # cut: a pop-up is noticed without being announced. Played once per burst of pop-ups (Toasts.cs).
    D6 = 1174.66
    s = m.zeros(0.0)
    m.mix(s, m.body(G5, 0.26, [(1.0, 1.0, 0.07), (2.0, 0.1, 0.035), (3.0, 0.03, 0.02)], attack=0.012, drop=0.0), 0.0, 1.0)
    m.mix(s, m.body(D6, 0.2, [(1.0, 1.0, 0.05)], attack=0.016, drop=0.0), 0.0, 0.28)
    return m.lowpass(m.highpass(s, 250), 3800)


def s_error():
    s = m.zeros(0.0)
    m.mix(s, m.pop(E5, 0.04, partials=m.WOOD, click_fc=1500, click_amp=0.15, seed=52), 0.0, 0.9)
    m.mix(s, m.pop(B4, 0.05, partials=m.WOOD, click_fc=1200, click_amp=0.12, seed=53), 0.085, 0.9)
    return m.lowpass(m.highpass(s, 160), 3200)


def s_profile():
    s = m.zeros(0.0)
    for k, (f, at, g) in enumerate(((m.G6, 0.0, 0.7), (m.B6, 0.07, 0.8), (m.D7, 0.14, 1.0))):
        m.mix(s, m.body(f, 0.42, m.BELL(0.09 + 0.02 * k), attack=0.003, drop=0.0), at, g)
    m.mix(s, m.click(4200, 1.0, 0.001, dur=0.01, seed=54), 0.0, 0.1)
    return m.lowpass(m.highpass(s, 300), 8000)


def s_badge():
    s = m.zeros(0.0)
    for k, (f, at) in enumerate(((m.G6, 0.0), (m.B6, 0.055), (m.D7, 0.11), (G7, 0.165))):
        m.mix(s, m.body(f, 0.55, m.BELL(0.11 + 0.03 * k), attack=0.002, drop=0.0), at, 0.6 + 0.12 * k)
    # the sparkle: light airy shimmer with a slow flutter, rising under the last note
    rnd = random.Random(55)
    n = int(0.5 * m.SR)
    sh = m.sweep_bandpass([rnd.uniform(-1, 1) for _ in range(n)], 3500, 6000, 2.5)
    sh = [v * math.sin(math.pi * i / n) ** 2 * (0.6 + 0.4 * math.sin(2 * math.pi * 14 * i / m.SR)) for i, v in enumerate(sh)]
    m.mix(s, sh, 0.12, 0.12)
    return m.lowpass(m.highpass(s, 300), 8500)


def s_gameclose():
    s = [v * 0.35 for v in air(2400, 600, 0.3, seed=56, shape=2.2)]
    m.mix(s, m.body(m.D7, 0.4, m.BELL(0.09), attack=0.004, drop=0.0), 0.04, 0.65)
    m.mix(s, m.body(m.G6, 0.5, m.BELL(0.13), attack=0.004, drop=0.0), 0.12, 0.85)
    return m.lowpass(m.highpass(s, 280), 7000)


CLIPS = {
    'scroll': m.s_scroll, 'select': m.s_select, 'back': m.s_back, 'systembrowse': m.s_systembrowse,
    'quicksysselect': m.s_quicksysselect, 'favorite': m.s_favorite, 'launch': m.s_launch, 'menuopen': m.s_menuopen,
    'edge': s_edge, 'press': s_press,
    'tick': s_tick, 'type': s_type, 'toggleon': s_toggleon, 'toggleoff': s_toggleoff, 'unfold': s_unfold, 'fold': s_fold,
    'toast': s_toast, 'error': s_error, 'profile': s_profile, 'badge': s_badge, 'gameclose': s_gameclose,
}


# ---- theme Soft: the same recipes through softer parts ----
class Soft:
    """Inside this block every body is a fourth lower with quieter, shorter overtones and a slightly
    longer ring, and every click is quieter and lower."""
    PITCH, OVERTONE, RING, CLICK_AMP, CLICK_FC = 0.75, 0.45, 1.15, 0.45, 0.65

    def __enter__(self):
        self.body, self.click = m.body, m.click
        body, click = m.body, m.click

        def soft_body(f, dur, partials, *a, **kw):
            p = [(r, amp if r == 1.0 else amp * Soft.OVERTONE, tau * Soft.RING) for r, amp, tau in partials]
            return body(f * Soft.PITCH, dur * Soft.RING, p, *a, **kw)

        def soft_click(fc, q, tau, dur=0.02, seed=1):
            return [v * Soft.CLICK_AMP for v in click(fc * Soft.CLICK_FC, q, tau, dur, seed)]

        m.body, m.click = soft_body, soft_click
        return self

    def __exit__(self, *e):
        m.body, m.click = self.body, self.click


def build(name, soft):
    if soft:
        with Soft():
            x = CLIPS[name]()
        x = m.lowpass(x, 5200)
    else:
        x = CLIPS[name]()
        if name in TOP_CUT:
            x = m.lowpass(x, TOP_CUT[name])
    x = m.finish(x)
    t = TRIM.get(name, 1.0)
    return [v * t for v in x]


def stats(x):
    pk = 20 * math.log10(max(abs(v) for v in x) + 1e-12)
    tot = sum(v * v for v in x) + 1e-12
    h = m.highpass(m.highpass(x, 6000), 6000)
    return len(x) / m.SR * 1000, m.loudness(x), pk, 100 * sum(v * v for v in h) / tot


def main():
    check = '--check' in sys.argv
    only = sys.argv[sys.argv.index('--only') + 1] if '--only' in sys.argv else None
    print(f"{'theme':6} {'clip':14}{'ms':>6}{'LUFS':>7}{'peak':>7}{'>6k %':>7}")
    for soft in (False, True):
        out = os.path.join(OUT, 'soft') if soft else OUT
        os.makedirs(out, exist_ok=True)
        for name in CLIPS:
            if only and name != only:
                continue
            x = build(name, soft)
            ms, lufs, pk, hi = stats(x)
            assert pk < -3.0, f"{name}: peak {pk:.1f} dBFS"
            print(f"{'soft' if soft else 'ionea':6} {name:14}{ms:6.0f}{lufs:7.1f}{pk:7.1f}{hi:7.2f}")
            if not check:
                m.write_wav(os.path.join(out, name + '.wav'), x)


if __name__ == '__main__':
    main()
