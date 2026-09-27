# Surfaces, SurfaceMap, ripples, PathManager

Files: `Surface.cs`, `Surface.Collider.cs`, `Surface.Ripples.cs` (`Surface.Tendrils.cs` is in `Docs/head-genome.md`),
`SurfaceMap.cs/.hlsl`, `RippleField.cs/.hlsl`, `PathManager.cs`.

## Surface (`Surface.cs`)
Makes a MeshRenderer walkable.
- One A* NavMeshGraph per *mesh*, built on first use in mesh-local space (scaled up to 100 units for A*'s mm
  precision), shared by every Surface with that mesh; queries go through the renderer's Transform, so any pose / scale
  works without a rebuild. Creates the AstarPath if missing. Replaced `NavmeshGraphBinder` (same script GUID).
- **Meshes need Read/Write**, even in the editor (without it A* reads no triangles and nothing lands).
- Per corner: smoothed normal + directional curvature (least-squares fit, so a cylinder is straight along its axis).
- `SmoothNormal` (the crawler's normal): corner normals blended by distance (a (1 - d²/r²)² bump per vertex per
  smoothing group, r = its mean edge; ~15 samples per triangle, listed once per graph), not barycentric weights (those
  turn at a new rate per triangle: ±1.5° sway on the red cell that the camera copied; now ±0.3°).
- On Awake swaps the MeshFilter to a shared copy with a crack-free displacement direction in UV3 (angle-weighted
  average of all faces at a position); `BloodCellTriplanar` displaces along it.
- `isCell` (default on): off = walkable but not a cell (no `CellSignal.For`, not a command-mode "Cell", not a spawn
  anchor). Resource chunks and white cells use it.
- `Surface.Of(transform)` finds the Surface for an ISurfaceContact's `Surface` (its Space). `Map` = its SurfaceMap.
- `referenceSpeed` scales ripple strength (100 in the scene).

## Concave collider (`Surface.Collider.cs`)
A dynamic body only takes convex MeshColliders; the red cell's hull lidded both dimples (the body sank into the lid and
shoved its own cell = jitter). On Awake a convex MeshCollider over a concave mesh is replaced by child convex pieces:
the solid cut into boxes, the worst box halved (longest side) until each piece's hull sits within
`colliderTolerance` (x mesh size) of the surface, measured along the surface normal via the support function (an
incremental hull broke on coplanar cut faces; "every point behind every face plane" over-measured ~4x), capped at
`colliderPieces` (48). Built once per mesh (~0.1 s), shared + pre-baked; logs the count. Cell mesh (bloodcell.blend
"Icosphere", 1280 tris): 48 pieces, lid 15.8% -> 1.6% of its size.
- **Collider LOD:** the hull is kept; past `detailDistance` (50 m beyond its size) from every Organism a cell uses only
  the hull (no piece objects made); pieces made the first time a creature comes near and switched in (round-robin,
  1/15 of split surfaces a frame vs `Organism.All`). Ground forces detail on its surface (`UseDetailedCollider`).
- Still used by: landing contacts, rope anchors / wraps, camera, white cells (legs use SurfaceMap).
- `Colliders` = its solid colliders; `ClosestPoint` over them. **Anything iterating a cell's colliders treats pieces as
  one body:** PathManager merges them into one sphere, WhiteBloodCell one push per body and one pick per Surface; query
  buffers raised (rope 256 overlaps / 16 hits, WBC 256).

## SurfaceMap (`SurfaceMap.cs`, `SurfaceMap.hlsl`)
Nearest point on a mesh from anywhere around it, O(1), in mesh space (shared by every copy at any pose / scale).
- Grid over the padded bounds (40 cells on the longest side for ~1300 triangles, scaled by cbrt(triangles)); each cell
  within `Band` (3) cells of the surface lists every triangle within (nearest from its centre + the cell's diagonal),
  so it's exact there (~13% of the longest side, all a leg probe needs); farther cells borrow the nearest banded cell's
  list (nearby point, not exact).
- Queries return the point, the mesh's own normals interpolated (hard edges stay hard) and the face normal, with an
  optional facing filter.
- Built on a worker thread from `Surface.Start` (`Ready` when done; ~150 ms and ~1.7 MB for 1500 triangles).
  `GpuIndex` registers it into the shared GPU arrays (`SurfaceMap.Pack`, uploaded by LegRenderer when `Version` changes).
- Tested offline against brute force (cube, sphere, Evans-Fung red cell profile). Tried and dropped: tighter corner
  bound (cut lists ~10% at 4x the build); finer grids with a narrower band (lost exactness at probe distances).

## Ripples (`Surface.Ripples.cs`, `RippleField`)
`Surface.AddImpact` (per surface, 64 slots, fades dropped, weakest replaced, near-simultaneous impacts merged) feeds
both the cell shader and `RippleField`: a global list, grouped by cell, that lets *anything* ride the waves via
`RippleField.hlsl` (`positionWS += RippleFieldOffset(...)`), no per-object code. Legs / rope / body opt in with
`_FollowRipples`. The wave shape comes from the material on the surface's renderer (`_Ripple*`), which is why chunks
and white cells keep a hidden renderer carrying their material.
- **No `Update()` on Surface:** one static tick in the player loop's Update (`InstallTick` / `TickAll`) runs the collider
  LOD and drops faded ripples on the surfaces that have any (`s_rippling`). An Update on every Surface was ~1000
  script calls a frame; don't add one back.

## PathManager (`PathManager.cs`)
Gridless potential-flow field (sink + doublets), trap-free. `GetField/GetDirection(pos, target, ignoreA, ignoreB)`.
- Creatures are obstacles by default (`ignoreOrganisms = false`, the user wants this). `oneSpherePerCreature` merges a
  creature's colliders into one sphere; body poses read once per Rigidbody per step, offsets / radii baked at `Rescan()`.
  MeshCollider obstacles sized by bounds (a bit big for chunks).
- **Lazy:** `Rescan()` only flags a change; scan / per-step refresh run on the first query after it (at most once per
  physics step), so with no flying agents it costs nothing (the streamer's rescans were 60-90 ms hitches). The streamer
  calls it at most every `rescanInterval`.
- **Known weakness:** lookup grid sized to the largest obstacle (a cell, ~70 m), so creature-sized spheres share a few
  huge buckets and each query walks most of them. A two-tier grid (small buckets for creatures) is the next fix if the
  AI pass is hot.
- **Gathers locally:** a scan takes colliders within `scanRadius` (250 m) of creatures (`Organism.All`), one
  `OverlapSphereNonAlloc` per cluster (a creature within a third of the radius of an earlier centre shares it); any
  creature more than two thirds from every centre triggers a new gather (checked per physics step while queried). A
  whole-scene `FindObjectsByType<Collider>` was 12-17 ms per streamer change with ~1k streamed cells.

## Open items
- With legs off the colliders, landing could use SurfaceMap too, letting cells go back to one hull.
- Touching detailed cells collide piece-vs-piece (up to 48 x 48 pairs): pieces only need to meet creatures; cells could
  meet hull-vs-hull (collider include / exclude masks, but queries would then see the lidded hull).
