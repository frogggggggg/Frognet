# Pathogens: wild viruses (and later bacteria) that live their own lives

Files: `RoundVirusAI.cs`, `CellInfection.cs`; prefab `Prefabs/RoundVirus.prefab` (model `virusRound.blend`, materials
`virusRound.mat` (crystal shell, acid green) / `virusRoundCore.mat` (cel core), remapped in the blend's import settings,
Read/Write on for BodyHull). Idea (user): a bustling world you're not the centre of; other pathogens threaten you or
fight the immune system, which deals with them. **Growth must never be exponential** (it would break the game).

## Round virus (`RoundVirusAI`, the Organism's Brain)
- Prefab: root (Rigidbody, SphereCollider r 0.6, Organism, RoundVirusAI) -> `Body` (Organism.visual: BodyOffset,
  antibodies ride it) -> `Ball` (rolled) -> `Model` (scale 0.6). No legs. Thrust 16 (player kind 30), Burst 40, Crawl 6
  with speed curves (rolls up / down to speed), hover 0.6, tether / drill off.
- **Roll:** `Ball`'s world rotation, turned by the distance moved over the cell under it (cell-local, so the cell's
  drift doesn't spin it), the spin carried into the air and eased to a slow tumble (`airSpin`). On landing the
  tangential speed it came in with is added to Move, fading over `rollOut`. Shown = `Body`, not the ball: a rolling
  Shown would read as shaking to AntibodyHold (and roll the slots into the cell).
- Modes: **Drift** (wander round obstacles via PathManager at `driftThrottle`; every ~`decideInterval` maybe roam or
  infect) -> **Roam** (land on a cell within `senseRange`, roll about `roamTime`, take off; landing anywhere while
  drifting roams there) -> **Infect** (land on a free cell, then **Enter**: holds `Intent.Focus` (Halt + the immune
  system hears drilling), BodyOffset sinks it ~2.2 r + hover over `enterTime`, spinning; at the bottom
  `CellInfection.Begin` and the virus is destroyed) -> **Shake** (antibodies on it: jump off, `Charge` jinks every
  `shakeInterval` to shake them loose).
- **Can't enter with an antibody on it** (`AntibodyHold.CountOn`): checked to start and every think while sinking
  (aborts, jumps off). Gripped / captured by a white cell: stops.
- Cell choice: one `Physics.OverlapSphereNonAlloc` (256) per decision, nearest x random(1..2); infect skips cells
  already infected, bursting or converted (yours).
- `IWorldState`: `readyAt` (vessel clock). Pinned while entering. Command-mode target "Round Virus".
- `Spawn(key, pos, vel)`: `WorldStreamer.SpawnNew(key)` (streamed + saved), else `ViralBuildAssets.pathogens` by
  name. Newborns get `bornCooldown`, world-generated ones `firstCooldown`.

## Infection (`CellInfection`, on the cell's Surface object while infected)
`Strain` (carried by the virus, copied in, saved): key, `sacrificeDelay` (inside, before it gives itself up),
`incubation`, `yield`, `maxPopulation`, `signal`, `burstSpeed`, tendril `color`. Inside -> Infected (Surface.Infect
tendrils in the strain colour, front timed to wrap the cell by the burst; `ImmuneSystem.Alarm` signal/s every 0.5 s:
antibodies are called and patrol it, white cells come) -> Burst (`ImmuneSystem.Lysed` + `CellBurst.Kill`; the new ones
come out at swell + half the break, from the middle, biased out of the entry side). Times on the vessel clock: a cell
streamed out keeps incubating, bursts >= 1 s after it's back. A cell killed / bursting otherwise yields nothing.

## Population (why it stays bounded)
- Go-to-infect needs: cooldown over, no antibodies, `CellInfection.All` < `maxInfections` (3), live + infected <
  `strain.maxPopulation` (24); chance `infectChance` x (1 - crowd).
- `CellInfection.Yield`: yield x (1 - crowd²), clamped to the room left under `maxPopulation`: nothing at the cap.
- Counts are what's loaded round the player (`RoundVirusAI.All`, `CellInfection.All`); stored ones don't simulate.
- The immune system does the rest: they're Organisms, so antibodies chase / stick (then they can't infect) and white
  cells eat them; rolling / entering on a cell raises its signal like any virus.

## Cost
Per virus: a think every `thinkInterval` (0.2 s; one PathManager query in the air), the roll per tick, an overlap per
decision (~4 s) when looking for a cell. Per infected cell: one Update (<= `maxInfections`). Not crowd-separated in
the air (VirusAI's hash isn't shared): they collide physically; fine at tens, revisit at hundreds.

## Open items (nothing run in play mode)
- Prefab and materials hand-written (YAML): check the model sits centred in the collider, its look / colours, and that
  the blend reimported with the material remap. Tuning (speeds, cooldowns, yields, signal) guessed.
- No sounds of their own yet (landing / dash use CreatureAudio; the burst has `CellBurst.Burst`). No threat to the
  player beyond bursting cells and drawing the immune system (they don't attack you).
- The "inside" stage is a timer on the cell (the virus is gone once in); if the player should later control or see
  viruses inside, give CellInfection a visible occupant.
