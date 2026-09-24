# "Exploring": the background track for a virus drifting through a living body.
# BotW exploring piano meets Spore's cell stage: curious, watery, a little alien, never pushy.
#
# Form (40 bars, ~2:20, loops seamlessly):
#   A  Drift   (8)  open fifths, a flowing harp figure fades in, piano fragments foreshadow the motif
#   B  Theme   (16) the piano theme, built on the focus sound's rising three (E F# A: the third
#                   steps up more), dotted rhythms and sighs, a varied left hand, contrabass
#   C  Wonder  (8)  modal shift to Bb lydian ("discovering something"): the harp states the motif,
#                   the piano answers; a glassy high violin line; back home through Em9 - Asus - A
#   D  Breath  (8)  near silence: pads, a few glassy high notes, a reversed piano swell into the loop
# Performance: gentle rubato (tempo drifts, slows into each section end), humanized timing and
# velocity, softer notes darker, rolled dyads, pedalled piano, violins with a slow watery wobble.
#
# Samples: Salamander Grand Piano (Alexander Holm, CC-BY 3.0); tonejs-instruments violin /
# contrabass / harp (Nicholaus Brosowsky, from VSCO2 Community, CC-BY 3.0).
#
# Setup (once): python -m venv venv && venv/Scripts/python -m pip install numpy miniaudio
# Samples go in samples/<instrument>/ next to this script:
#   violin, contrabass, harp: https://nbrosowsky.github.io/tonejs-instruments/samples/<inst>/<Note>.wav
#     (violin G3 A3 C4 E4 G4 A4 C5 E5 G5 A5 C6 E6; contrabass Fs1 G1 C2 D2 E2 Fs2 A2;
#      harp: all of them, B1..A6)
#   piano: https://tonejs.github.io/audio/salamander/<Note>.mp3 (C/Ds/Fs/A, octaves 2..6, and C7)
# Run: venv/Scripts/python compose.py ../Resources/Music/Exploring.wav
import glob, os, re, sys, wave
import numpy as np
import miniaudio

SR = 44100
HERE = os.path.dirname(os.path.abspath(__file__))
rng = np.random.default_rng(11)
PC = {'C': 0, 'Cs': 1, 'D': 2, 'Ds': 3, 'E': 4, 'F': 5, 'Fs': 6, 'G': 7, 'Gs': 8, 'A': 9, 'As': 10, 'B': 11}

# ---------------- samples ----------------

def midi_of(name):
    m = re.match(r'([A-G]s?)(-?\d)$', name)
    return 12 * (int(m.group(2)) + 1) + PC[m.group(1)] if m else None

def trim(x):
    mono = np.abs(x).max(axis=1)
    idx = np.argmax(mono > mono[:SR // 2].max() * 0.05)
    return x[max(0, idx - SR // 1000):]

def load(inst):
    out = {}
    for f in glob.glob(os.path.join(HERE, 'samples', inst, '*')):
        n = midi_of(os.path.splitext(os.path.basename(f))[0])
        if n is None: continue
        if f.endswith('.mp3'):
            d = miniaudio.decode_file(f, output_format=miniaudio.SampleFormat.FLOAT32, nchannels=2, sample_rate=SR)
            x = np.frombuffer(d.samples, dtype=np.float32).reshape(-1, 2).astype(np.float64)
        else:
            w = wave.open(f)
            x = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float64) / 32768.0
            x = x.reshape(-1, w.getnchannels())
            if x.shape[1] == 1: x = np.repeat(x, 2, axis=1)
        out[n] = trim(x)
    if not out: sys.exit(f'no samples in samples/{inst}')
    return out

S = {k: load(k) for k in ['piano', 'violin', 'contrabass', 'harp']}

# ---------------- time: bars/beats -> seconds, with rubato ----------------

BEATS_PER_BAR = 4
SECTIONS = [('A', 8), ('B', 16), ('C', 8), ('D', 8)]
BARS = sum(n for _, n in SECTIONS)
TOTAL_BEATS = BARS * BEATS_PER_BAR
BPM = 68
ENDS = np.cumsum([n * BEATS_PER_BAR for _, n in SECTIONS])  # section-end beats

grid = np.arange(0, TOTAL_BEATS + 8, 1 / 64)
bpm = BPM * (1 + 0.018 * np.sin(2 * np.pi * grid / 24))
for e in ENDS:  # ease into each section end over its last 3 beats, back after
    w = np.clip((grid - (e - 3)) / 3, 0, 1) * (grid < e)
    bpm *= 1 - 0.14 * w ** 1.5
    bpm *= 1 - 0.06 * np.clip(1 - (grid - e), 0, 1) * (grid >= e)
secs = np.concatenate([[0], np.cumsum(60 / bpm[:-1] / 64)])

def T(bar, beat=0.0):
    return float(np.interp(bar * BEATS_PER_BAR + beat, grid, secs))

LOOP = T(BARS)
TAIL = 10.0
N = int((LOOP + TAIL) * SR)
stems = {k: np.zeros((N, 2)) for k in S}

# ---------------- playing a note ----------------

def darken(y, cutoff):
    Y = np.fft.rfft(y, axis=0)
    f = np.fft.rfftfreq(len(y), 1 / SR)
    Y *= (1 / np.sqrt(1 + (f / cutoff) ** 2))[:, None]
    return np.fft.irfft(Y, n=len(y), axis=0)

def render(inst, midi, gain, pan=0.0, hold=None, attack=0.0, release=0.3, cents=0.0, wobble=0.0, tone=None):
    bank = S[inst]
    src = min(bank, key=lambda k: abs(k - midi))
    x = bank[src]
    ratio = 2 ** ((midi - src + cents / 100) / 12)
    n_out = int((len(x) - 1) / ratio)
    if hold is not None: n_out = min(n_out, int((hold + release * 1.5) * SR))
    if wobble > 0:  # slow watery pitch wobble
        t = np.arange(n_out) / SR
        step = ratio * (1 + wobble * np.sin(2 * np.pi * rng.uniform(0.18, 0.3) * t + rng.uniform(0, 6.28)))
        pos = np.concatenate([[0], np.cumsum(step[:-1])])
        pos = pos[pos < len(x) - 1]
        n_out = len(pos)
    else:
        pos = np.arange(n_out) * ratio
    y = np.stack([np.interp(pos, np.arange(len(x)), x[:, c]) for c in range(2)], axis=1)
    env = np.ones(n_out)
    if attack > 0:
        a = min(n_out, int(attack * SR))
        env[:a] = np.sin(np.linspace(0, np.pi / 2, a)) ** 2
    if hold is not None:
        h = int(hold * SR)
        if h < n_out: env[h:] *= np.exp(-np.arange(n_out - h) / SR * 5.0 / release)
    y *= env[:, None]
    if tone: y = darken(y, tone)
    y[:, 0] *= gain * np.sqrt(1 - pan)
    y[:, 1] *= gain * np.sqrt(1 + pan)
    return y

def place(stem, t, y):
    i = int(t * SR)
    if i < 0: y, i = y[-i:], 0
    j = min(N, i + len(y))
    if j > i: stems[stem][i:j] += y[:j - i]

def piano(bar, beat, midi, vel, hold_beats=3.0, pan=None):
    """vel 0..1: loudness and brightness together (soft = dark), humanized."""
    vel = float(np.clip(vel * rng.uniform(0.92, 1.06), 0.05, 1))
    t = T(bar, beat) + rng.normal(0, 0.007)
    hold = T(bar, beat + hold_beats) - T(bar, beat)
    if pan is None: pan = float(np.interp(midi, [36, 96], [-0.4, 0.4]))
    place('piano', t, render('piano', midi, 0.55 * vel, pan, hold=hold, release=0.7, tone=900 + 5200 * vel ** 1.5))

def roll(bar, beat, notes, vel, hold_beats=4.0, spread=0.035):
    for k, m in enumerate(sorted(notes)):
        piano(bar, beat + k * spread / (60 / BPM), m, vel * (1 - 0.08 * k), hold_beats)

def harp(bar, beat, midi, vel, pan=None):
    t = T(bar, beat) + rng.normal(0, 0.005)
    if pan is None: pan = float(np.interp(midi, [40, 90], [-0.5, 0.5]))
    place('harp', t, render('harp', midi, 0.5 * vel * rng.uniform(0.9, 1.05), pan, tone=1200 + 3500 * vel))

def pad(bar, bars, notes, gain, attack=1.8):
    t0, t1 = T(bar), T(bar + bars)
    for k, m in enumerate(notes):
        for side, cents, dt in ((-1, -6, 0.0), (1, 7, 0.05)):
            place('violin', t0 + dt, render('violin', m, gain, side * (0.2 + 0.1 * k), hold=t1 - t0 + 0.4,
                                            attack=attack, release=2.0, cents=cents, wobble=0.0015))

def bass(bar, bars, midi, gain):
    t0, t1 = T(bar), T(bar + bars)
    place('contrabass', t0, render('contrabass', midi, gain, -0.05, hold=t1 - t0 + 0.2, attack=0.6, release=1.8, wobble=0.0008))

# ---------------- harmony ----------------
# per chord: bass, pad voicing (violins), harp figure notes (low -> high), left-hand piano notes
CH = {
    'D5':   dict(bass=38, pad=[57, 64, 69],     harp=[50, 57, 64, 66, 69], lh=[38, 45, 57, 64]),
    'D':    dict(bass=38, pad=[57, 64, 66, 69], harp=[50, 57, 64, 66, 69], lh=[38, 45, 57, 66]),
    'E/D':  dict(bass=38, pad=[56, 64, 68, 71], harp=[50, 56, 64, 68, 71], lh=[38, 50, 56, 64]),
    'Bm9':  dict(bass=47, pad=[57, 62, 66, 73], harp=[47, 54, 61, 62, 66], lh=[35, 42, 57, 61]),
    'G':    dict(bass=43, pad=[59, 62, 66, 73], harp=[43, 50, 59, 61, 62], lh=[43, 50, 59, 66]),
    'F#m':  dict(bass=42, pad=[57, 61, 64, 69], harp=[42, 49, 57, 61, 64], lh=[42, 49, 57, 64]),
    'Asus': dict(bass=45, pad=[57, 62, 64, 69], harp=[45, 52, 57, 62, 64], lh=[45, 52, 57, 64]),
    'A':    dict(bass=45, pad=[57, 61, 64, 69], harp=[45, 52, 57, 61, 64], lh=[45, 52, 57, 61]),
    'Bb':   dict(bass=46, pad=[57, 62, 65, 69], harp=[46, 53, 57, 62, 64], lh=[34, 41, 57, 64]),
    'C/Bb': dict(bass=46, pad=[55, 60, 64, 67], harp=[46, 55, 60, 64, 67], lh=[34, 46, 55, 64]),
    'Em9':  dict(bass=40, pad=[55, 62, 66, 71], harp=[40, 47, 55, 62, 66], lh=[40, 47, 55, 66]),
}
# one chord per bar
BAR_CHORDS = (['D5'] * 4 + ['E/D'] * 4 +                                                 # A
              ['D', 'D', 'E/D', 'E/D', 'Bm9', 'Bm9', 'G', 'G',                            # B
               'D', 'D', 'E/D', 'E/D', 'F#m', 'F#m', 'Asus', 'A'] +
              ['Bb', 'Bb', 'C/Bb', 'C/Bb', 'Em9', 'Em9', 'Asus', 'A'] +                   # C
              ['D5'] * 4 + ['G', 'G', 'Asus', 'Asus'])                                    # D
assert len(BAR_CHORDS) == BARS

# pads + bass: merge repeated bars into one held chord
b = 0
while b < BARS:
    name = BAR_CHORDS[b]
    n = 1
    while b + n < BARS and BAR_CHORDS[b + n] == name: n += 1
    ch = CH[name]
    section_d = b >= 32
    gain = 0.045 if b < 8 else 0.05 if b < 24 else 0.06 if b < 32 else 0.035
    pad(b, n, ch['pad'], gain * (0.7 if b == 0 else 1.0), attack=3.0 if b in (0, 32) else 1.8)
    if 8 <= b < 32: bass(b, n, ch['bass'] - (12 if ch['bass'] >= 45 else 0), 0.15)
    elif b in (0, 32): bass(b, n, 38, 0.09)
    b += n

# glassy high violin line in the Wonder section
place('violin', T(24), render('violin', 76, 0.03, 0.3, hold=T(28) - T(24), attack=2.5, release=2.0, wobble=0.002))
place('violin', T(28), render('violin', 78, 0.03, 0.3, hold=T(30) - T(28), attack=1.5, release=2.0, wobble=0.002))

# flowing harp figure (Spore's water): eighths, the shape varying bar to bar
FIGS = [[0, 1, 2, 1, 3, 1, 2, 4], [0, 2, 1, 3, 2, 4, 3, 1], [0, 1, 3, 2, 4, 2, 3, 1]]
for bar in range(2, 36):
    ch = CH[BAR_CHORDS[bar]]
    if bar < 8: level = 0.06 + 0.04 * (bar - 2) / 5
    elif bar < 24: level = 0.08
    elif bar < 32: level = 0.1
    else: level = 0.08 * (36 - bar) / 4
    fig = FIGS[(bar // 2) % len(FIGS)]
    for e, idx in enumerate(fig):
        accent = 1.0 if e == 0 else 0.8 if e == 4 else 0.62
        harp(bar, e * 0.5, ch['harp'][idx], level * accent)

# ---------------- piano ----------------

# A: Drift. Open fifths low, then fragments that hint at the motif.
roll(0, 0, [38, 45], 0.32, 8)
piano(0, 2.5, 76, 0.22, 4)
piano(1, 1, 69, 0.2, 4)
piano(2, 0, 64, 0.22, 3)
piano(2, 2, 76, 0.25, 3)
piano(2, 3.5, 78, 0.2, 4)
roll(4, 0, [38, 50], 0.3, 8)
piano(4, 1.5, 80, 0.22, 3)
piano(5, 0, 83, 0.25, 4)
piano(5, 3, 76, 0.2, 4)
piano(6, 0, 76, 0.3, 1)           # the rising three, first time: E F# B
piano(6, 1, 78, 0.32, 1)
piano(6, 2, 83, 0.36, 5)
piano(7, 2.5, 80, 0.22, 3)

# B: Theme (per two bars: right hand (beat, midi, length), left hand figure)
THEME = [
    [(0, 76, 1.5), (1.5, 78, 0.5), (2, 81, 2.5), (4.5, 80, 0.5), (5, 81, 1), (6, 76, 2)],
    [(0, 83, 1), (1, 80, 0.5), (1.5, 78, 0.5), (2, 76, 3), (5, 71, 0.5), (5.5, 73, 0.5), (6, 76, 2)],
    [(0, 78, 1.5), (1.5, 81, 0.5), (2, 85, 2.5), (4.5, 83, 0.5), (5, 81, 1), (6, 78, 2)],
    [(0, 74, 0.5), (0.5, 78, 0.5), (1, 81, 1), (2, 85, 3), (5, 83, 1), (6, 81, 2)],
    [(0, 76, 1.5), (1.5, 78, 0.5), (2, 81, 1), (3, 88, 3), (6, 86, 1), (7, 85, 1)],
    [(0, 83, 1), (1, 85, 0.5), (1.5, 83, 0.5), (2, 80, 3), (5, 78, 1), (6, 76, 2)],
    [(0, 81, 1.5), (1.5, 80, 0.5), (2, 76, 2), (4, 73, 1), (5, 76, 1), (6, 78, 2)],
    [(0, 74, 2), (2, 76, 1), (3, 74, 1), (4, 73, 4)],
]
LH = [  # (beat, index into lh, dyad with next?)
    [(0, 0, True), (3, 2, False), (5, 3, False)],
    [(0, 0, False), (1.5, 1, False), (2, 2, False), (6, 3, False)],
    [(0, 0, True), (4, 2, True)],
]
for k, phrase in enumerate(THEME):
    bar0 = 8 + 2 * k
    peak = max(m for _, m, _ in phrase)
    for i, (beat, m, length) in enumerate(phrase):
        shape = 0.85 + 0.2 * np.sin(np.pi * (i + 0.5) / len(phrase))   # swell through the phrase
        vel = (0.36 if k < 4 else 0.4) * shape * (1.08 if m == peak else 1.0)
        piano(bar0, beat, m, vel, max(length * 1.3, 1.0))
        if k in (4, 5) and m == peak: piano(bar0, beat, m - 12, vel * 0.55, length)  # octave warmth
    ch = CH[BAR_CHORDS[bar0]]
    for beat, idx, dyad in LH[k % len(LH)]:
        notes = [ch['lh'][idx]] + ([ch['lh'][idx + 1]] if dyad and idx + 1 < len(ch['lh']) else [])
        roll(bar0, beat, notes, 0.24 if beat == 0 else 0.18, 4)
    if BAR_CHORDS[bar0 + 1] != BAR_CHORDS[bar0]:   # 'Asus' -> 'A': the left hand follows the resolve
        roll(bar0 + 1, 0, CH[BAR_CHORDS[bar0 + 1]]['lh'][2:], 0.2, 4)

# C: Wonder. Bb lydian; the harp states the motif, the piano answers.
CALL = [(24, [(0, 77), (1, 79), (2, 84)]), (26, [(0, 79), (1, 81), (2, 86)])]
for bar0, notes in CALL:
    for beat, m in notes:
        harp(bar0, beat, m, 0.3 if m == notes[-1][1] else 0.24)
ANSWER = [
    (24, [(4.5, 86, 0.5), (5, 84, 1), (6, 81, 2)]),
    (26, [(4.5, 88, 0.5), (5, 86, 1), (6, 84, 3)]),
    (28, [(0, 83, 1.5), (1.5, 81, 0.5), (2, 78, 2), (5, 79, 1), (6, 81, 2)]),
    (30, [(0, 76, 2), (2, 74, 2)]),
    (31, [(0, 73, 4)]),
]
for bar0, notes in ANSWER:
    for beat, m, length in notes:
        piano(bar0, beat, m, 0.33, max(length * 1.3, 1.0))
for bar0 in (24, 26, 28, 30, 31):
    ch = CH[BAR_CHORDS[bar0]]
    roll(bar0, 0, ch['lh'][:2], 0.24, 6)
    if bar0 < 30: roll(bar0, 3, ch['lh'][2:], 0.16, 4)

# D: Breath. Pads, a few glassy high notes, then a reversed swell back into the start.
roll(32, 0, [38, 45], 0.2, 8)
piano(33, 2, 88, 0.14, 6)
piano(35, 0, 93, 0.11, 6)
roll(36, 0, [43, 50], 0.17, 8)
piano(37, 1, 85, 0.13, 6)
roll(38, 0, [45, 52], 0.16, 8)

# ---------------- mix ----------------

def spectral(x, fn):
    X = np.fft.rfft(x, axis=0)
    X *= fn(np.fft.rfftfreq(len(x), 1 / SR))[:, None]
    return np.fft.irfft(X, n=len(x), axis=0)

lowpass = lambda fc, order=2: (lambda f: 1 / np.sqrt(1 + (f / fc) ** (2 * order)))
highpass = lambda fc: (lambda f: (f / fc) ** 2 / np.sqrt(1 + (f / fc) ** 4))

def make_ir(seconds=6.5):
    n = int(seconds * SR)
    t = np.arange(n) / SR
    ir = np.zeros((n, 2))
    f = np.fft.rfftfreq(n, 1 / SR)
    for c in range(2):
        F = np.fft.rfft(np.random.default_rng(100 + c).normal(0, 1, n))
        for lo, hi, rt in [(0, 400, 5.2), (400, 2500, 3.8), (2500, 8000, 2.0), (8000, SR, 0.8)]:
            ir[:, c] += np.fft.irfft(np.where((f >= lo) & (f < hi), F, 0), n=n) * np.exp(-6.9 * t / rt)
    pre = int(0.035 * SR)
    ir = np.concatenate([np.zeros((pre, 2)), ir])
    ir[pre:pre + int(0.1 * SR)] *= np.linspace(0, 1, int(0.1 * SR))[:, None]
    return ir / np.sqrt((ir ** 2).sum(axis=0).mean())

def convolve(x, ir):
    n = len(x) + len(ir) - 1
    size = 1 << (n - 1).bit_length()
    return np.fft.irfft(np.fft.rfft(x, size, axis=0) * np.fft.rfft(ir, size, axis=0), size, axis=0)[:n]

IR = make_ir()

# reversed piano swell (D5 + A5 through the hall, backwards) rising into the loop point
sw = render('piano', 74, 0.3, -0.1, hold=3.0, tone=2500) + render('piano', 81, 0.22, 0.1, hold=3.0, tone=2500)
sw = convolve(sw, IR)[: int(4.5 * SR)][::-1]
sw *= np.linspace(0, 1, len(sw))[:, None] ** 1.5
place('piano', LOOP - len(sw) / SR, sw * 0.5)

stems['violin'] = spectral(stems['violin'], lambda f: lowpass(2200)(f) * highpass(150)(f))
stems['contrabass'] = spectral(stems['contrabass'], lowpass(650))
stems['piano'] = spectral(stems['piano'], lowpass(3400, 1))
stems['harp'] = spectral(stems['harp'], lambda f: lowpass(2800, 1)(f) * highpass(120)(f))

SEND = {'piano': 0.5, 'violin': 0.6, 'contrabass': 0.2, 'harp': 0.6}   # far away: mostly room
LEVEL = {'piano': 1.0, 'violin': 1.0, 'contrabass': 0.9, 'harp': 0.9}
dry = sum(stems[k] * LEVEL[k] for k in stems)
wet = convolve(sum(stems[k] * LEVEL[k] * SEND[k] for k in stems), IR)[:N]
mix = dry * 0.7 + wet * 0.7

# seamless loop: wrap everything after the loop point back onto the start
L = int(LOOP * SR)
out = mix[:L].copy()
out[:N - L] += mix[L:]
out *= 0.7 / np.abs(out).max()
out = np.tanh(out * 1.1) / np.tanh(1.1) * 0.95

def save(path, x):
    pcm = (np.clip(x, -1, 1) * 32767).astype(np.int16)
    w = wave.open(path, 'wb'); w.setnchannels(2); w.setsampwidth(2); w.setframerate(SR)
    w.writeframes(pcm.tobytes()); w.close()

for p in sys.argv[1:]:
    save(p, out)
    print('wrote', p, f'{len(out) / SR:.1f} s')
