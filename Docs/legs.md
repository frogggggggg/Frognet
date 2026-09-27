# Legs: SpiderLegWalker + LegRenderer + LegSimulation.compute + BloodCellLegs

Files: `Movement/SpiderLegWalker.cs`, `Movement/LegRenderer.cs`, `LegSimulation.compute`, `LegData.hlsl`,
`BloodCellLegs.shader/.hlsl`. Feet use `SurfaceMap` (`Docs/surfaces.md`).

**The legs run on the GPU.** Nothing per leg happens on the CPU.

## SpiderLegWalker (per walker, CPU)
Keeps only per-walker state: ring heading / phase / mirror, velocity relative to the support, turn, cadence (`_rate`,
swing / stance time, max span), flight pose (air axis, orbit, trail, spin), landing spot (one raycast a frame in the
air; walkers without an ISurfaceContact also raycast for ground), spin-wiggle amount, the support's turn since last
frame (carry), and mode changes as flags (init, enter ground / from air, enter air, support changed, snap knees).
Each frame fills one `LegRenderer.Walker` record (27 float4s: those + the support's local<->world matrices + its
SurfaceMap index) and `Submit`s it. Ticked by SimulationTicker.
- Off screen (by more than `offscreenMargin`) or past `cullDistance`: submits nothing, re-plants when seen again. The
  shared view is sampled before the camera's LateUpdate, so it trails a frame: visibility checks are padded. Bounds are
  conservative (MaxSpan leg reach), not from the tips.
- Past `lodDistance`: a lighter tube (about half the rings, fewer sides), its own group.
- `debugLog` logs what a walker sends and what the GPU holds for its first leg (async readback).

## LegRenderer (GPU state owner)
- Per walker a slot (`Allocate` / `Free` on disable): a walker slot (gait cycle + hub height) and a block of
  `legCount` leg states (364 B each), grown by copy kernels.
- `LateUpdate`: sorts the frame's records by draw group (material, tube resolution, shadows; undrawn = simulate only),
  dispatches `Simulate` (one thread per walker), then one `DrawMeshInstancedProcedural` per group (`_LegBase` = the
  group's first leg in the shared `_Legs` output).
- Footsteps: the kernel writes per walker which legs planted and where; read back with `AsyncGPUReadback` (polled, a
  frame or two late) -> `SpiderLegWalker.Planted` -> `CreatureAudio.Step`.
- `Generation` changes on a script reload so walkers re-allocate. Copies the leg material every frame only in the editor.
- Uploads `SurfaceMap.Pack` when its `Version` changes.

## LegSimulation.compute
The old per-leg C# moved over line for line: gait (phase-locked, two alternating groups, feet aimed where they'll
sit at mid-stance, catch-up steps), swing arcs, air pose, takeoff / landing reach, spin wiggle, rootDir / bendUp /
hipUp easing, hub height, knee springs, CornerSink, the LegData records.
- Anchors (feet, step ends, aim) are stored in the walker's **support's** local space (identity in the air); on a
  support change they're re-anchored from their last world position.
- **Feet are placed with the support's SurfaceMap, never raycasts:** `Probe` = nearest point over faces turned within
  60° of the probe direction (`MinProbeFacing`), accepted when the point is what a ray would meet (offset along the
  surface normal there), within the probe's height range and the leg's reach. Past a hard edge the nearest facing
  point is the edge itself and the offset points sideways: `Wrap` then probes back in from beyond the edge, as far down
  as the step overshot (a foot stepping off a cube's top lands on its side). No map (unreadable mesh, map still
  building, no ISurfaceContact) = feet step in the body's plane. Feet plant only on the surface the walker stands on.
- Swing: travel starts 10% after the lift and the arc peaks early (peel up, reach, set down).
- Edges: upper leg arches off the body's surface (`hipUp`), lower comes down onto the foot's (`bendUp`), both lifted by
  exactly how deep the straight root-to-foot line sinks into the corner (`CornerSink`: body plane from hub height,
  learned from feet on the body's face; foot plane), else not at all. (A guessed tan(half angle) lift arched every edge
  leg and read as stretching.)
- A foot steps out of turn past rest reach + the planned lead at this speed + `SpanMargin` (capped `MaxSpan`), not a
  flat 2x leg length.
- HLSL has no short-circuit `||`: probe fallbacks are explicit `if (!ok)` chains.
- **fxc dropped RWStructuredBuffer stores placed right before a `continue`** in the leg loop (legs froze mid-step).
  Each leg's work is a function (`StepLeg`) with early `return`s and one unconditional store after the call; keep
  loops over UAV state that way.

## Shader (BloodCellLegs)
Builds geometry in the vertex stage from the leg buffer; shares BloodCellCore / BloodCellForward with the cells
(`Docs/rendering.md`). `_TessMax = 1`. Foot and root caps pushed out into rounded nubs, tip tapered; the noise sample is
reused by DepthNormals; inside the focus sweep legs go plain (`LegSweepCover`, reads global `_InvertSweep`); legs
never enable `INVERT_BACKFACES`. `_FollowRipples` rides RippleField. `LegData.hlsl` = the record shared by compute
and shader. `_SHADING_*` is `multi_compile` (runtime-copied material).

## Cost
CPU O(walkers in view), a record each; GPU one thread per walker, SurfaceMap lookups (~15-50 triangles each) only on
frames a foot lifts or plants.

## Open items
Unverified in play mode (compiled: C#, all leg passes, every kernel; SurfaceMap tested offline). Watch: feet on small
chunks (probes can fall past the exact band), footstep timing (events a frame or two late), legs on non-Organism
walkers (plane only).
