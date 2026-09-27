# Immune system: signals, antibodies, alarm motes

Files: `ImmuneSystem.cs`, `CellSignal.cs`, `Antibody.cs`, `Antibody.shader`, `AntibodyHold.cs`, `AntibodyMesh.cs`,
`BodyHull.cs`, `AlarmMotes.cs`, `AlarmMote.shader`. White blood cells: `Docs/white-blood-cells.md`.

The game's idea: you never attack directly, so evasion and defence matter.

## Signals
`ImmuneSystem` (manager, creates itself when the scene has Surfaces; the streamer also ensures it). `CellSignal` per
cell, added on demand by `CellSignal.For(transform)` (only `Surface.isCell`). Ten times a second every `Organism.All` on
a cell raises that cell's signal (`idleRate` / `walkRate` / `focusRate`, around a moving `Hotspot`); signals halve every
`halfLife`. `CellSignal.Pull` = gravity toward loud cells (strength / (1 + (d/falloff)^2)). `Silence()` (blight) stops
it for good. Noise also raises the vessel region's alert (`Vessel.Alert`).
- `ImmuneSystem.Alarm(cell, point, normal, signal)` raises + emits motes: **use it for new noisy actions.**
- `ImmuneSystem.Landed` (from the `LandAlarm` effect: `landSignal` x impact speed); `Deliver(cell, gene, site, normal)`
  (`Docs/head-genome.md`), wrong gene = `wrongGeneBurst`.

## Antibodies
Command-mode targets "Antibodies"; ticked in one loop by the manager: far / off-screen every 2..8 frames
(`tickDistance`), stuck ones every frame. `ambientCount` wander from the start; loud cells call more in from
`arriveDistance` (`reinforceRate`, capped). Immune density rises toward the vessel wall (margination).
- **Behaviour:** Drift along the pull + noise -> Patrol (circle over a loud cell's hotspot) -> Chase a virus they see
  (`stickChance`, else ignore it a while). Chase = `chaseBoost` x speed (14 m/s: faster than a crawl (8), not a flight
  (30)), darting (`chaseAcceleration`), aimed `chaseLead` s ahead along the virus's measured motion; within
  `diveDistance` they stop keeping clear of cells and dive in (else they hovered above a crawling virus and only stuck
  when it stopped) -> Stuck.
- Drift: `Carry(now)` (velocity + flow) runs every frame on screen, separate from the LOD'd `Tick`.
- **Look:** `AntibodyMesh.Build(detail)`: lumpy looped arms (closed tube loops with a hole), stem = two twisted chains,
  hinge blob; Worley beads displaced along the normal; vertex colours blue / magenta folds / teal patches, alpha = fold
  occlusion; uv0 = part + hinge-to-tip. Two details (near beaded, far ~500 verts, `lodDistance`).
- `Custom/Antibody` wiggles it in every pass: arms flap / clap / bend / twist about the hinge, stem sways, a wave crawls
  along the chains; grip opens the arms nearly flat (`_HugOpen`), flops the stem over (`_StemLean`), then `Wrap` bends
  the whole antibody round the virus's centre both ways (wiggle.w = hinge to centre in antibody sizes, from
  `AntibodyHold.Wrap`) so the arms follow its curve; free flapping damped. Glassy shading (wrap diffuse, back-light, rim).
- Drawn only by `ImmuneSystem.DrawAntibodies`: frustum + `drawDistance` culled, one `RenderMeshPrimitives` per LOD from
  one buffer (pose + `Antibody.Wiggle` = seed, agitation by state, grip, hug). Each antibody keeps a
  `forceRenderingOff` MeshRenderer with the far mesh only for Selectable's box.

## Holding on (`AntibodyHold`, one per creature with antibodies on it)
- Slots in latitude rings round the creature's *shown* body (`Organism.Shown`, so they ride its animation), top down to
  55° off its underside, each hinge just off the farthest *drawn* surface under the antibody (`BodyHull`: per-direction
  star hull of the meshes under `Organism.Shown`, a 6x12x12 cube map of farthest distances built once per creature from
  its triangles; needs Read/Write, else the collider sphere; also the stick distance). Rings an antibody's width apart,
  slots an arm span apart, alternate rings staggered, arms along the ring: no overlaps. Full -> a second / third shell
  (else it gives up on that virus).
- A stuck antibody takes the free slot nearest where it touched (they spread round from the approach side) and climbs
  there (`settleTime`). Posed in ImmuneSystem's LateUpdate (execution order 150, after the ticker) so they don't trail.
- **Shaking off:** the hold measures the shown body's turn rate and acceleration past `shakeTurn` / `shakeJolt`
  (low-passed ~0.1 s so jitter / single steps don't count; capped 1.5); each antibody has a random `gripHealth`
  (seconds of full shaking) that wears down and slowly comes back while not shaken (`regrip`). As it wears it loosens
  visibly (arms open, lifts, rattles), then is flung off (`flingSpeed` + the body's velocity) and ignores that virus for
  `shakenIgnore`. `StuckOn` = the hold's count (white cells sense it). `EatStuck` destroys them when swallowed.

## Alarm motes (`AlarmMotes`, replaced the green fumes)
Small glowing amber motes spurting out of the cell (a hormone / alarm) where a virus lands, walks, or drills in
(`Activity`, `perSignal` motes per unit of signal; sitting still is quiet), a big spurt for a wrong gene, and a trickle
round a loud cell's hotspot while it still calls (`calling`). GPU-animated: the CPU only writes new motes' birth data
into a ring buffer (one upload per frame); `Hidden/AlarmMote` moves them (exponential spurt + drift + wander),
stretches along motion, throbs and fades; one `RenderPrimitives`. Emission off screen / past `drawDistance` is dropped.

## Open items
- Antibodies are O(antibodies x organisms) in `Look` (every ~0.25 s) and O(antibodies x cells) in `Avoid` / `Patrolled`
  per tick; at thousands they need the spatial hash, and past that a data-only (Burst jobs) simulation instead of a
  GameObject each.
- Not saved: antibodies, cell signals / converted cells.
