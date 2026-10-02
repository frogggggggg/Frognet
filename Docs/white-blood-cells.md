# White blood cells (+ ShrinkWrap)

Files: `WhiteBloodCell.cs` (per cell), `WhiteBloodCells.cs` (manager), `WhiteBloodCellMesh.cs`,
`WhiteBloodCell.shader/.hlsl` (`Custom/WhiteBloodCell`), `WhiteBloodCell.mat` (editable, assigned on the manager prefab),
`WhiteBloodCellBake.compute`, `ShrinkWrap.cs/.hlsl`, `ShrinkWrapCapture.shader`. Prefabs `WhiteBloodCell` (the cell) +
`WhiteBloodCells` (manager: count, sizes, shader); the editor auto-adds the manager prefab if the scene has none.
Audio: `Audio/WhiteBloodCellAudio.cs`.

## What it is
Phagocytes: they only eat pathogens. A walkable ball (Surface with `isCell` off; kinematic Rigidbody, sphere collider,
never turns so standing viruses don't spin; MeshRenderer `forceRenderingOff`, only for the walkable mesh + command box
"White Cell" + ripple material). Carried by the flow: `Carry(now)` every frame on screen, kept as `_drift` so grip /
swallow maths use its real velocity.

## The spot / arm
Exactly one absorbing **spot**, the tip of a pseudopod (`Spot`, `Reach`): swings over the body or out along a stretched
arm (pulls the arm in to swing far; won't extend through another cell). The arm is **sprung**, not tweened: angle and
length each follow a damped spring (`spotSpring` Hz, `spotDamping`), capped at `spotSpeed` / `extendSpeed`, sub-stepped
at 30 Hz for long dts; the spot's velocity (`Sway`) bows the arm behind it in the shader. Anything the mouth really
touches (`Touching`: within `mouthSize` across, up to its size + `touchMargin` in front) is swallowed: flying, on
another cell, or on this one (the spot then crawls across the body after you).

## States
Patrol (crawl to a nearby cell, louder CellSignal = likelier) -> Examine (spot feels over that cell) -> Hunt (a virus
within `senseRange` of its surface, + `antibodyRange` per antibody stuck on it, or touching it) -> **Grip** -> Engulf ->
Digest.
- Targeting in a crowd: commits to its prey for `commitTime`, then only switches for one `switchMargin` better (or one
  crawling on its body; it used to flip every think and the arm swung back and forth); skips viruses another cell grips;
  `sharePenalty` per other cell already after a virus (hunter counts kept by `Prey`'s setter, O(1)) spreads a group
  over a crowd.
- Crawl is amoeboid (surging speed, eased), pushed off other colliders (one overlap per think).

## Grip (physics, not an animation)
`WhiteBloodCells.Grip` plucks the catch off its surface and holds `Intent.Seized` on it; it stays a live body with its
own controls. Every physics step `WhiteBloodCell.Pull` accelerates it toward the reel point (`gripStiffness`, capped
`gripStrength`, damped mostly along the arm); the reel winds in at `reelSpeed` only while the catch keeps up; the mouth
stays on it (the arm stretches after it). Dragged `breakStretch` past the hold (a Burst can; plain Thrust at up to
80 m/s² can't) it tears free (`regrabDelay`). The catch keeps the orientation it was bitten in, relative to the arm's
frame (`ArmFrame`: reach + carried side), turning with the arm, and can only turn `gripTurn`° from that
(`Organism.Restrain`, cleared by `Release`). `Engulfing` fires at the grip (gulp sound on the grab). `Tension` for audio.

## Merge / swallow (`WhiteBloodCells.Capture/Hold/Finish`)
- At the body it's Captured and **merged**, like a drop into a pool (the user: not carried into the cell, no jumps,
  "dynamic merging"): picked up where and as fast as it is (`_held` / `_heldVel` in the cell's rotation frame), settles
  on a spring (`mergeSpring`) half out of the surface where it touched, then sinks under and shrinks over
  `swallowTime`; splats and jiggles on a squash spring (kicked by how hard it came in), applied through a temporary
  pivot it's parented to (`Hold(o, pos, scale, axis, height)`, volume kept; `Unpivot` at Finish), so its own mesh
  flattens. The arm just follows; the mouth never jumps onto a catch (`AimAt` eases `_aimSlack` away).
- Shader: `MergeShape` (instance `merge`: blob radius, centre distance along the reach, neck softness, squash) ->
  `MergeInto` in WhiteBloodCell.hlsl: surface = smooth union (polynomial smin) of the body and an ellipsoid blob a
  little inside the catch, found per vertex by sphere tracing inward along its ray from the centre (<= 16 steps, only
  near the blob), so the neck climbs its sides and widens until the body closes over it; maps shift with the surface.
  The lips' wrap melts away over the first quarter. (A tip-only wrap was a pinched, streaky bulge.)
- The victim is detached; its Organism, VirusAI, legs and colliders switched off; stuck antibodies destroyed
  (`ImmuneSystem.EatStuck`); settled in the mouth, the lips wrap round it (`Mood.y`, prey radius in `Mood.w`). Then an AI virus is destroyed and the player respawns at its start after `respawnDelay`.
  Camera: `UniversalCamera.StandIn` at the cell's surface. `Free` releases (used by load; a captor disabled mid-swallow
  finishes the job). Events `Noticed` / `Engulfing` / `Gulped` / `TornFree` / `Merging` / `Absorbed` / `Respawned`.

## Look (all in the vertex stage, every pass)
- Mesh = unit sphere in the *reach frame* (+Z = the spot, rings packed in the cap `CapAngle` = shader `CAP_ANGLE`).
  Membrane sampled in the *body* frame so it stays put while the spot slides: soft undulation + ruffles (ridged value
  noise in patches; Worley popcorn lumps were rejected as "balls"), **spikes** (Worley thorns, each growing / retracting
  on its own beat and curling along a drifting flow + trailing the crawl: "more spiky and flowy"; search the 8 nearest
  lattice cells, exact for `_SpikeWidth` <= 0.5), rolling swells, fine creases per pixel from the noise's analytic
  gradient; leading-edge lobes + tapered tail from its velocity (lobes wander round the lead by a body-frame noise
  vector, no basis). Spikes / ruffles fade toward `lodDistance` (`lod.x`) so the far mesh doesn't pop.
- The arm is the **body stretched** ("literally stretching a part of its body"): the cap becomes a round tip + a tube
  flaring back into the body (`_Flare`, meets it exactly at the cap rim), the body's front slides after it (`_Pull`).
  Bows behind the spot's motion (`_Lag`), meanders when slack, and under grip tension thins with swallowing waves
  running to the body (`_Peristalsis`).
- `side` (reach frame's x axis) is parallel-transported with the arm on the CPU (`WhiteBloodCell.Track`); picking it
  from a fixed body axis flipped it 90° near that axis and every arm-local feature jumped.
- **Material maps** (membrane geometry + pixel lumps) follow where the surface really is, never where a point started
  (that smeared the arm and wrap into streaks, rejected): `map` fixed to the body (the arm slides out through it),
  `mapTip` fixed to the tip, cross-faded along the arm by evaluating both and blending the *results* (contrast kept by
  1/sqrt(a²+(1-a)²) in the fragment); blending coordinates would stretch. Second Membrane / SurfaceHeight only in that band.
- **Lit as the red cells' family** (it looked flat otherwise): includes `BloodCellCore` (`_SPACE_WORLD`, lumps on the
  material maps, metres), shades with `CellShade`, white / lilac instead of red / navy.
- **Mouth inside = its own material** (`MouthShade`): wet flesh, radial folds wandering into a dark throat, swallowing
  rings running in, glossy highlight, wrapped flesh lighting in the same bands (`_MouthColor` / `_MouthDeepColor` /
  `_MouthWet` / `_MouthFolds` / `_MouthFoldDepth`); polar coords round the mouth from the instance; bump from screen
  derivatives (no branch). Its settings are plain uniforms outside `UnityPerMaterial` (the core owns it); LOD flag is
  `_LodDetail` (`_Detail` is the core's fine detail). Mouth at rest: irregular breathing rim, ruffled lip, drifting wisps.
- **The bite** (springs, "nice and satisfying"): `Bite()`: the mouth gapes as prey nears (`gapeRange`: lips peel back,
  mouth widens); on the grab the lip spring is flung shut (`lipSpring`, `lipDamping`) and bounces off closed; a squeeze
  spring is kicked (`biteSqueeze`); smaller gulp kicks every `gulpInterval` while reeling, a last one at the swallow;
  lips pucker into folds where they meet.
- **The wrap is a thin skin** (a double-layer ball on a swollen tip "made a big bulge"): the arm necks down to a
  throat just behind the catch (`tipD` from the hull's back); from it a skin (hull + ~12% of the catch's size) creeps
  forward over the catch's *real* shape (ShrinkWrap); rim = a rounded lip rolling onto the catch, ahead of it a lining
  tucked under the catch's uncovered front (the catch shows there); closes at uneven pace. Hull filtered like skin
  (`Hull`: five taps ~0.18 rad apart, each cut to the lowest + 15% of the catch's size; misses count as the lowest):
  thin parts (legs, crystal points) made sharp needles otherwise.
- Rides `RippleField`; normals by finite differences, ripples included.
- **Bake:** near cells are baked once a frame by `WhiteBloodCellBake.compute` (one Shape per vertex, then normals from
  the mesh grid's neighbours; reads the material's shape floats, copied each frame; `_WbcTime` = Time.time) into one
  buffer every pass reads (`_UseBaked`); before, each pass rebuilt it 3x per vertex in every pass incl. shadow
  cascades. Far cells shape in the vertex stage (no spikes / ruffles). Ripples go on in the draw vertex stage either way
  (compute doesn't see the global ripple field), normal bent by two taps. Baked record = 4 float4s (64 B: position +
  lump, map + mouth, normal + tendril, mapTip). Instance = 10 float4s (160 B: incl. `side`, `merge`, `sway` = spot
  velocity / tension, `extra` = LOD fade, gape, squeeze, `burst` = bursting (CellBurst, `Docs/head-genome.md`)).
- Gotcha: **`atan2(0, 0)` is NaN on D3D** (the mouth pole vanished); guard atan2 of directions on the axis.

## Cost
One `RenderMeshPrimitives` per LOD (near ~15.6k verts with spikes and ruffles, far ~1k without). Per cell: a think
every `thinkInterval` = `WhiteBloodCells.Near` (organism grid rebuilt at most once a frame, O(organisms)) + one overlap
query (buffer 256); idle far / off-screen cells tick every 2..8 frames.

## ShrinkWrap (`ShrinkWrap.cs`, `.hlsl`, `Hidden/ShrinkWrapCapture`)
Generic "wrap round a thing's real shape". Per slot (8), the farthest surface distance from a centre in every world
direction (star-shaped outer hull) as a dual-paraboloid pair in one RHalf Tex2DArray (48x48, slice slot*2 = +Z, +1 = -Z).
Capture draws the object's Mesh/SkinnedMeshRenderers (`maxExtent` filters out e.g. a trailing rope) with BlendOp Max,
placing vertices by the same `ShrinkWrapUV` the reader uses (orientation can't mismatch); triangles reaching 53° past a
hemisphere's edge are dropped there (the other has them). `Begin` / `Capture` / `Submit` each frame; unused slots freed.
Cost: 2 draws per renderer per captured object per frame.
