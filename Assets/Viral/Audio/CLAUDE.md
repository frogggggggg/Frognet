# Audio

## Direction (every sound and all music)
**Breath of the Wild meets Spore.** Sparse, airy piano (single notes and open arpeggios, lots of space, soft felt
attack), lydian / pentatonic colour, glassy shimmer and soft pads, big gentle reverb; organic sounds are wet, squishy
and underwater-muffled (this is inside a body). Transitions and UI moments are small musical gestures (a rising
arpeggio, a falling one back), not generic whooshes or beeps. Nothing harsh or aggressive; tension from sparseness and
dissonant intervals, not loudness. The user often finds sounds too loud / present / sharp / "satisfying": keep things
background, soft, throttled.

## Building blocks
- Clips are built once at load into float buffers and shared. Any clip can be replaced by assigning a recorded one.
- `Synth.cs`: seeded noise, sweepable biquads, `Swoosh`, `Bubble`, Freeverb-style `Reverb`, `Normalize`, `Loop`
  (seamless), `Clip` (mono for 3D), `Shimmer` (glassy tone), `Glide`, `Grain`, `Lowpass`. Build new effects from them.
- **Piano = real samples, never synthesized** (two additive piano models sounded synthetic). No effect uses it now;
  `Piano.cs` + `Resources/Piano` kept for later (Resources ship in builds: delete if unused).
  `Piano.Note(buf, start, midiNote, gain, pan, hold)`: nearest recorded note, resampled <= 1.5 semitones, damper after
  `hold`. Salamander Grand (CC-BY 3.0, Alexander Holm: credit him in the game; `CREDITS.txt`), from
  tonejs.github.io/audio/salamander/<note>.mp3, named C/Ds/Fs/A + octave; A4..A6 so far. `GetData` needs Load Type =
  Decompress On Load. Samples trimmed to onset at load (MP3 leading silence made rhythms late).
- `Sfx.cs`: pool of 24 3D voices for world one-shots. A call is dropped after one distance check against
  `SimulationTicker.CameraPosition` when out of range, over 8 starts this frame, or every voice busy with something louder.
- If Unity's audio device is stalled (mixer clock not advancing) it's reset once (`CheckDevice`); probably why nothing
  played at first (unconfirmed).
- Render clips to WAV outside Unity: see "Sounds" in the root CLAUDE.md. Piano only plays inside Unity.

## FocusSound (creates itself, follows the player's focus events)
Entering: a wet, muffled underwater inject; after `transitionDelay`, **no piano** (user asked): a soft underwater bloom
= broad noise wash swelling 300 Hz -> 1.6 kHz, 16 small bubbles rising and thinning, faint glassy `Shimmer` (D6 + A6,
detuned sine pairs, 0.12 s ease-in, quiet) in a big soft reverb. Leaving: the wash falling, a few sinking bubbles, a
soft A5 shimmer. Rejected: pad + bass + loud swoosh ("too synth and epic"), runs ("too much / too positive / too much
flourish"), 12 three-note gestures. So: organic, soft, glassy only as a faint colour, never a melody.

## CreatureAudio (creates itself)
- **Steps** ("spider on a slime ball", 6 variants, pitched by leg size): `SpiderLegWalker` calls `Step` when a foot
  plants. Every player step plays, plus every step of the `voicedWalkers` (2) nearest other creatures crawling within
  `nearStepRange` (whole gaits keep rhythm; random single legs from many sounded erratic). A soft round "bloo" (`Bloo`:
  low tone gliding down into its note, 8 ms onset, breath of noise, 1.2 kHz low-pass); a bright click and a thumpy
  squelch were "too sharp and loud".
- **Crowd bed** (everyone else): one smooth 2D loop (low brown-noise wash + 450 quiet low "bloo"s, too dense to pick out;
  sparse pitched blips sounded like popcorn). Level follows *motion*: a scan of `Organism.All` every `scanInterval` sums
  crawl pace (`Crawl.CurrentSpeed / speed`, ignoring < 0.15) x distance falloff (driving it by steps trailed off after a
  crowd stopped: feet keep settling). O(creatures) per scan.
- **Impact** (`ImpactSound` on Landing): a *trampoline* bounce: a stretchy "sproing" gliding up with a spring wobble,
  rebounds quicker (gap x0.68) and quieter. 4 strength tiers by `HitSpeed / fullImpactSpeed`: harder = lower, bigger
  glide, 2..6 bounces, longer gaps and rings, a membrane thump; volume scales too. (Bouncier than a jelly "bwoing"; the
  sound itself changes with strength.) Also the Pump slam (`Pump.soundSpeed`).
- **Burst** (`BurstSound` on Charging): underwater whoosh + bubbles.
- **Flight loop**: *water, not air*, pleasant for a long time: smooth deep slowly swelling rush (brown noise low-passed
  + a broad quiet flow band). Nothing narrow or bright in a loop (bubbles shrill, gurgle band unpleasant). Louder and a
  little clearer with speed (low-pass 250..1750 Hz); pitch 0.92..1.08 only. Must stay background: volume 0.15 x
  speed^`flightCurve` (1.8, faint at cruise), rumble under 140 Hz cut.

## RopeAudio (creates itself when there's a VirusRope; listens to its events, rope has no audio code)
- `Anchored`: wet sticky attach (soft slap + suction "plup" gliding up + glue stretch + jelly wobble). A crisp "ch"
  didn't fit: keep contact sounds organic.
- `Spooled` (+ out / - in per physics step, auto payout included): two *different* loops ("in like a slurp, out like a
  line being cast"). Out (`BuildCast`): soft mid swish (broad 850 Hz band drifting ±18% twice a loop), warm 280-1300 Hz
  body, faint 2.1 kHz sheen, capped 2.8 kHz (a bright line hiss was "unpleasant and hissy"). In (`BuildSlurp`): wet "aw"
  (650 / 1100 Hz soft formants) in irregular gulps (level = slowed noise squared, ~10-20 surges/s), low bubbles on the
  surges, a little sucked air. Pitch / low-pass ranges in Update (cast 1.5..3.5 kHz, slurp 0.9..2.7 kHz). A soft
  "squeeze" as it starts from rest.
- `Stowed`: a wet "plp".
- Rejected: clicker + zip; low brown-noise loop ("sounded like flying"); sparse random squelch grains ("too much
  variation"); impulse train through vowels ("lawnmower"); narrow swept resonance + falling "plip" ("lasergun"); one
  shared recipe both ways. So: nothing tonal, no pulse trains, no fast pitch drops, no narrow filters.

## AmbientMusic (creates itself)
Loops `Resources/Music/Exploring.wav` (streamed, Vorbis) at `volume` 0.18 with a fade in. Must stay *background*:
piano soft and dark (low-pass 2.8 kHz), melody quiet, lots of (mostly room) reverb. A real composition rendered offline
by `MusicSource~/compose.py` (numpy, real samples: Salamander piano; tonejs-instruments violin / contrabass / harp,
CC-BY: `Resources/Music/CREDITS.txt`; FFT convolution reverb; tail wrapped onto the start to loop seamlessly). 68 bpm
with rubato, ~140 s, four sections: **Drift** (open fifths, flowing harp figure fades in, piano hints at the motif),
**Theme** (piano melody on the focus sound's rising three, dotted rhythms and sighs, varied left hand, contrabass),
**Wonder** (shift to Bb lydian, harp states the motif, piano answers, glassy high violin), **Breath** (near silence,
glassy high notes, reversed piano swell back into the loop). Humanized timing / velocity, softer notes darker, rolled
dyads, watery wobble on strings. To change it: edit PROG / CH / MEL in compose.py, run with a venv holding `numpy` +
`miniaudio`, samples in `samples/<instrument>/` next to it (download commands at the top), output path as argument.
Rejected: one broken-chord pattern per chord ("too simple"); generated "random sparse" piano over a noise bed (they
wanted "an actual good sounding background").

## WhiteBloodCellAudio (creates itself; listens to WhiteBloodCells' events)
Tritone swell when one hunts the player (`Noticed`), deep gulp on the grab (`Engulfing`), small gulps while reeling
(`Gulped`), a "thwup" + rising bubbles + faint E6/B6 glint on tearing free, a drop-into-a-pool plop + jelly wobble on
merging (louder / lower the harder, + inward speed), a sinking wash + dark tritone when absorbed, a rising wash + D6/A6
shimmer on respawn. Loops: the 2 nearest cells within `presenceRange` churn (low surging brown noise, breath, deep
blubs; louder hunting / holding, lower for bigger cells); a gripping cell adds a rubbery strain loop scaled by
`Tension` (dark, <= 950 Hz, slow tugs). Throttled (the user found some obnoxious): hunt swell at most every
`noticeCooldown` (12 s, never twice in a row from one cell), reeling gulps at most every `smallGulpGap` (2.2 s, mostly
wet swallow, tone just a hint), anything happening to a catch that isn't the player at `otherPreyVolume`. Scan
O(cells) every `scanInterval`.

## InjectionAudio (creates itself, 2D)
The strand being *pulled through*, not a reward (per-stage plups / gulps / rising shimmers were "too much unlockable
satisfying"). Two loops follow `GenomeView.InjectSpeed` so the sound starts, rushes and stops with the strand: a dry
stick-slip friction rub, and soft muffled rung ticks whose loop pitch (= tick rate) tracks speed; past `IntoTube` both
tighten. One-shots only for mechanical moments: an unclip off the mount (`Started`), a muffled stop at the tip
(`Delivered`). The slam is the normal landing thump (the user wants it). No notes.

## CraftAudio (creates itself, 2D)
Listens to `GenomeView.Synthesis` (`CraftStage`: MenuOpened / Closed, Pointed, Refused, PageTurned, Started, Docked,
Dispensing, Made) + `GenomeView.Drawing`: rising / falling D6 E6 A6 glassy arpeggio for the menu, a soft pluck per
recipe (pentatonic step by name hash), a dull tritone "bup-bup" when refused, a suction plup on start, a wet latch click
per store, a straw-suck loop while drawing, a squeeze on dispense, a bloo + two-note shimmer when made.

## HoldTickAudio (creates itself)
Dense quiet ratchet of tiny dry clicks while a `WorldButton` (static `All`) is held, ~22/s -> ~45/s (interval 0.045 ->
0.022 s, ±15% uneven), pitch 1 -> 1.25 as it fills (a slow tick / tock read as a time bomb). Scheduled on the DSP clock
(PlayScheduled, 0.12 s ahead) so the rhythm is even; stops (scheduled ticks cancelled) on let-go or completion.

## ResourceAudio
Start "plup", drain loop, breathy bubbly poof with a faint E6/B6 shimmer.
